#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;
using PackageManagerClient = UnityEditor.PackageManager.Client;
using PackageManagerPackageInfo = UnityEditor.PackageManager.PackageInfo;


namespace Chievfx.Mcp.Editor
{
    internal sealed partial class ProfilerBridgeService
    {
        /// <summary>
        /// "Which draw call wrote this pixel?" — the right first question for a wrong-looking pixel, and
        /// previously answerable only by eyeballing draw calls one at a time. Binary-searches the frame
        /// debugger event limit for the first event whose output at (x,y) matches the finished frame, so
        /// it costs ~log2(eventCount) captures instead of a linear sweep.
        /// </summary>
        public object PickFrameDebuggerPixel(JToken args)
        {
            var searchStep = ReadString(args, "searchStep");
            if (string.Equals(searchStep, "start", StringComparison.OrdinalIgnoreCase))
            {
                return StartPixelSearch(args);
            }

            if (string.Equals(searchStep, "next", StringComparison.OrdinalIgnoreCase))
            {
                return ContinuePixelSearch(args);
            }

            return PickFrameDebuggerPixelInOneCall(args);
        }

        private object StartPixelSearch(JToken args)
        {
            var diagnostics = new List<string>();
            var window = PrepareFrameDebuggerForInspection(args, diagnostics, selectEventIndex: null);
            var state = CreateFrameDebuggerState(window, diagnostics);
            var eventCount = state.TryGetValue("eventCount", out var countValue) ? countValue as int? : null;
            if (!eventCount.HasValue || eventCount.Value <= 0)
            {
                throw new InvalidOperationException("Frame Debugger has no captured events. Enable it on a rendered frame first (frame-debugger-control enabled:true).");
            }

            var normalizedX = ReadNormalized(args, "x") ?? throw new ArgumentException("frame-debugger-pick-pixel requires x (0..1, from the left edge of the capture).", nameof(args));
            var normalizedY = ReadNormalized(args, "y") ?? throw new ArgumentException("frame-debugger-pick-pixel requires y (0..1, from the TOP edge of the capture).", nameof(args));
            if (normalizedX < 0f || normalizedX > 1f || normalizedY < 0f || normalizedY > 1f)
            {
                throw new ArgumentOutOfRangeException(nameof(args), "x and y must be normalized 0..1 of the capture (top-left origin).");
            }

            var search = new PixelSearch
            {
                EventCount = eventCount.Value,
                FinalEventIndex = eventCount.Value - 1,
                NormalizedX = normalizedX,
                NormalizedY = normalizedY,
                MaxDimension = Math.Max(128, Math.Min(ReadInt(args, "maxDimension", 4096), 8192)),
                Tolerance = Math.Max(0, Math.Min(ReadInt(args, "tolerance", 2), 255)) / 255f,
                Low = 0,
                High = eventCount.Value - 1,
            };
            search.Diagnostics.AddRange(diagnostics);
            _pixelSearch = search;
            SelectPixelSearchEvent(window, search, search.FinalEventIndex);
            return new { success = true, pending = true, eventCount = search.EventCount };
        }

        private object ContinuePixelSearch(JToken args)
        {
            var search = _pixelSearch ?? throw new InvalidOperationException("No frame-debugger-pick-pixel search is running: call frame-debugger-pick-pixel with x and y.");
            var diagnostics = new List<string>();
            var window = PrepareFrameDebuggerForInspection(args, diagnostics, selectEventIndex: null);
            var eventCount = GetFrameDebuggerEventCount(diagnostics);
            if (eventCount.HasValue && eventCount.Value != search.EventCount)
            {
                search.FrameChanged = true;
            }

            if (search.IsReadingDetails)
            {
                return FinishPixelSearch(search);
            }

            var color = SamplePixelSearchCapture(search);
            if (!search.HasFinalColor)
            {
                search.FinalColor = color;
                search.HasFinalColor = true;
            }
            else if (search.IsSamplingBefore)
            {
                search.BeforeColor = color;
                return SelectPixelSearchWriter(window, search);
            }
            else if (ColorsMatch(color, search.FinalColor, search.Tolerance))
            {
                search.High = search.SelectedEventIndex;
            }
            else
            {
                search.Low = search.SelectedEventIndex + 1;
            }

            if (search.Low < search.High)
            {
                SelectPixelSearchEvent(window, search, search.Low + ((search.High - search.Low) / 2));
                return new { success = true, pending = true, eventCount = search.EventCount, low = search.Low, high = search.High };
            }

            if (search.Low > 0)
            {
                search.IsSamplingBefore = true;
                SelectPixelSearchEvent(window, search, search.Low - 1);
                return new { success = true, pending = true, eventCount = search.EventCount, low = search.Low, high = search.High };
            }

            return SelectPixelSearchWriter(window, search);
        }

        // Leave the frame debugger showing the culprit so the Unity window matches the answer; its details come next step.
        private static object SelectPixelSearchWriter(EditorWindow window, PixelSearch search)
        {
            SelectPixelSearchEvent(window, search, search.Low);
            search.IsReadingDetails = true;
            return new { success = true, pending = true, eventCount = search.EventCount, low = search.Low, high = search.High };
        }

        private static void SelectPixelSearchEvent(EditorWindow window, PixelSearch search, int eventIndex)
        {
            if (!TrySelectFrameDebuggerEventIndex(window, eventIndex + 1, out var error))
            {
                _pixelSearch = null;
                throw new NotSupportedException($"Frame Debugger event selection is unsupported in this Unity version. {error}");
            }

            search.SelectedEventIndex = eventIndex;
            window.Repaint();
            EditorApplication.QueuePlayerLoopUpdate();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }

        private static Color SamplePixelSearchCapture(PixelSearch search)
        {
            var texture = DecodeCapture(new ScreenshotBridgeService().CaptureGameView(search.MaxDimension));
            try
            {
                if (search.PixelX < 0)
                {
                    search.CaptureWidth = texture.width;
                    search.CaptureHeight = texture.height;
                    search.PixelX = Mathf.Clamp(Mathf.RoundToInt(search.NormalizedX * (texture.width - 1)), 0, texture.width - 1);
                    search.PixelY = Mathf.Clamp(Mathf.RoundToInt((1f - search.NormalizedY) * (texture.height - 1)), 0, texture.height - 1);
                }

                search.Captures++;
                return texture.GetPixel(search.PixelX, search.PixelY);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private object FinishPixelSearch(PixelSearch search)
        {
            _pixelSearch = null;
            var writerIndex = search.Low;
            var diagnostics = search.Diagnostics;
            if (search.FrameChanged)
            {
                diagnostics.Add($"The captured frame changed during the search (it had {search.EventCount} events): writerEventIndex may point to another draw call. Check it with frame-debugger-event-get, or exit Play Mode.");
            }

            var frameEvent = CreateFrameDebuggerEventSummary(writerIndex, includeDetails: true, diagnostics);
            // Before a full-screen pass the back buffer still holds the previous frame's picture, so the same colour there
            // says nothing about an earlier writer.
            if (search.BeforeColor.HasValue && ColorsMatch(search.BeforeColor.Value, search.FinalColor, search.Tolerance) && !IsFullScreenPassEvent(frameEvent))
            {
                diagnostics.Add("The pixel already had its final colour before this event, so an earlier event may be the real writer (the colour likely changed away and back). Re-run with a nearby maxDimension or inspect neighbouring events.");
            }

            if (IsFullScreenPassEvent(frameEvent))
            {
                var copied = frameEvent.TryGetValue("copiesScreenTexture", out var copiedValue) && copiedValue is string copiedName ? $" (it copies {copiedName})" : string.Empty;
                diagnostics.Add($"Event {writerIndex} is a clear, blit or full-screen copy{copied}, not an object's draw: the picture reaches the screen through a full-screen pass "
                    + "(the camera renders into its own target, and post-processing in the final blit changes the colours), so the screen colour first appears here. "
                    + "The object was drawn earlier into the camera target: look through the groups before the final blit with frame-debugger-drawcall-screenshot.");
            }

            return new
            {
                success = true,
                pixel = new Dictionary<string, object?>
                {
                    ["x"] = search.PixelX,
                    ["y"] = search.PixelY,
                    ["captureWidth"] = search.CaptureWidth,
                    ["captureHeight"] = search.CaptureHeight,
                },
                writerEventIndex = writerIndex,
                capturesTaken = search.Captures,
                eventCount = search.EventCount,
                finalColor = DescribeColor(search.FinalColor),
                colorBefore = search.BeforeColor.HasValue ? DescribeColor(search.BeforeColor.Value) : null,
                frameEvent,
                diagnostics = diagnostics.Distinct(StringComparer.Ordinal).ToArray()
            };
        }

        private static bool IsFullScreenPassEvent(Dictionary<string, object?> frameEvent)
        {
            var type = frameEvent.TryGetValue("type", out var typeValue) ? typeValue as string : null;
            var shader = frameEvent.TryGetValue("shader", out var shaderValue) ? shaderValue as string : null;
            var name = frameEvent.TryGetValue("name", out var nameValue) ? nameValue as string : null;
            return (type != null && (type.StartsWith("Clear", StringComparison.Ordinal) || type.StartsWith("Resolve", StringComparison.Ordinal)))
                || (shader != null && shader.IndexOf("Blit", StringComparison.OrdinalIgnoreCase) >= 0)
                || (name != null && name.IndexOf("BlitFinal", StringComparison.OrdinalIgnoreCase) >= 0)
                || frameEvent.ContainsKey("copiesScreenTexture");
        }

        private object PickFrameDebuggerPixelInOneCall(JToken args)
        {
            var diagnostics = new List<string>();
            var window = PrepareFrameDebuggerForInspection(args, diagnostics, selectEventIndex: null);
            var state = CreateFrameDebuggerState(window, diagnostics);
            var eventCount = state.TryGetValue("eventCount", out var countValue) ? countValue as int? : null;
            if (!eventCount.HasValue || eventCount.Value <= 0)
            {
                throw new InvalidOperationException("Frame Debugger has no captured events. Enable it on a rendered frame first (frame-debugger-control enabled:true).");
            }

            var maxDimension = Math.Max(128, Math.Min(ReadInt(args, "maxDimension", 4096), 8192));
            var screenshots = new ScreenshotBridgeService();
            var tolerance = Math.Max(0, Math.Min(ReadInt(args, "tolerance", 2), 255)) / 255f;

            // Sample the finished frame first: it fixes both the target colour and the capture size the
            // caller's normalized coordinates resolve against.
            SelectFrameDebuggerEventForCapture(window, eventCount.Value - 1, diagnostics);
            var finalTexture = DecodeCapture(screenshots.CaptureGameView(maxDimension));
            int pixelX, pixelY, captureWidth, captureHeight;
            Color finalColor;
            try
            {
                captureWidth = finalTexture.width;
                captureHeight = finalTexture.height;
                ResolvePickPixel(args, captureWidth, captureHeight, out pixelX, out pixelY);
                finalColor = finalTexture.GetPixel(pixelX, pixelY);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(finalTexture);
            }

            Color SampleAtLimit(int eventIndex)
            {
                SelectFrameDebuggerEventForCapture(window, eventIndex, diagnostics);
                var texture = DecodeCapture(screenshots.CaptureGameView(maxDimension));
                try
                {
                    return texture.GetPixel(pixelX, pixelY);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
            }

            // Smallest event index whose output already matches the final colour. Monotonic in the common
            // case; a pixel that changes away and back is reported with a caveat below.
            var low = 0;
            var high = eventCount.Value - 1;
            var captures = 1;
            var everyCaptureMatched = true;
            while (low < high)
            {
                var mid = low + ((high - low) / 2);
                if (ColorsMatch(SampleAtLimit(mid), finalColor, tolerance))
                {
                    high = mid;
                }
                else
                {
                    low = mid + 1;
                    everyCaptureMatched = false;
                }

                captures++;
            }

            if (everyCaptureMatched && low == 0 && eventCount.Value > 1)
            {
                diagnostics.Add("Every capture matched the finished frame, event 0 included: the Game view was not re-rendered between selections within this one call, so writerEventIndex 0 is not an answer. The MCP server runs this search over several editor ticks - restart it if it is older than the Unity package.");
            }

            var writerIndex = low;
            var beforeColor = writerIndex > 0 ? SampleAtLimit(writerIndex - 1) : (Color?)null;
            if (writerIndex > 0)
            {
                captures++;
            }

            // Leave the frame debugger showing the culprit so the Unity window matches the answer.
            SelectFrameDebuggerEventForCapture(window, writerIndex, diagnostics);

            // In Play the game can render a different frame on some repaints (another camera every other frame): the
            // search then compared captures of different frames.
            var eventCountAfter = GetFrameDebuggerEventCount(diagnostics);
            if (eventCountAfter.HasValue && eventCountAfter.Value != eventCount.Value)
            {
                diagnostics.Add($"The captured frame changed during the search ({eventCount.Value} -> {eventCountAfter.Value} events): writerEventIndex may point to another draw call. Check it with frame-debugger-event-get, or exit Play Mode.");
            }

            var frameEvent = CreateFrameDebuggerEventSummary(writerIndex, includeDetails: true, diagnostics);
            if (beforeColor.HasValue && ColorsMatch(beforeColor.Value, finalColor, tolerance))
            {
                diagnostics.Add("The pixel already had its final colour before this event, so an earlier event may be the real writer (the colour likely changed away and back). Re-run with a nearby maxDimension or inspect neighbouring events.");
            }

            return new
            {
                success = true,
                pixel = new Dictionary<string, object?>
                {
                    ["x"] = pixelX,
                    ["y"] = pixelY,
                    ["captureWidth"] = captureWidth,
                    ["captureHeight"] = captureHeight,
                },
                writerEventIndex = writerIndex,
                capturesTaken = captures,
                eventCount = eventCount.Value,
                finalColor = DescribeColor(finalColor),
                colorBefore = beforeColor.HasValue ? DescribeColor(beforeColor.Value) : null,
                frameEvent,
                diagnostics = diagnostics.Distinct(StringComparer.Ordinal).ToArray()
            };
        }

        private static void ResolvePickPixel(JToken args, int width, int height, out int pixelX, out int pixelY)
        {
            // Normalized 0..1 with a top-left origin, matching screenshot PNG space (what the caller is
            // looking at). Texture2D sampling is bottom-left, so flip Y here.
            var normalizedX = ReadNormalized(args, "x") ?? throw new ArgumentException("frame-debugger-pick-pixel requires x (0..1, from the left edge of the capture).", nameof(args));
            var normalizedY = ReadNormalized(args, "y") ?? throw new ArgumentException("frame-debugger-pick-pixel requires y (0..1, from the TOP edge of the capture).", nameof(args));
            if (normalizedX < 0f || normalizedX > 1f || normalizedY < 0f || normalizedY > 1f)
            {
                throw new ArgumentOutOfRangeException(nameof(args), "x and y must be normalized 0..1 of the capture (top-left origin).");
            }

            pixelX = Mathf.Clamp(Mathf.RoundToInt(normalizedX * (width - 1)), 0, width - 1);
            pixelY = Mathf.Clamp(Mathf.RoundToInt((1f - normalizedY) * (height - 1)), 0, height - 1);
        }

        private static float? ReadNormalized(JToken args, string name)
        {
            var token = args?[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
            {
                return token.Value<float>();
            }

            return token.Type == JTokenType.String
                && float.TryParse(token.Value<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        private static Texture2D DecodeCapture(ImageResult capture)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(Convert.FromBase64String(capture.Base64)))
            {
                UnityEngine.Object.DestroyImmediate(texture);
                throw new InvalidOperationException("Could not decode the Frame Debugger capture for pixel sampling.");
            }

            return texture;
        }

        private static bool ColorsMatch(Color left, Color right, float tolerance)
        {
            return Mathf.Abs(left.r - right.r) <= tolerance
                && Mathf.Abs(left.g - right.g) <= tolerance
                && Mathf.Abs(left.b - right.b) <= tolerance
                && Mathf.Abs(left.a - right.a) <= tolerance;
        }

        private static string DescribeColor(Color color)
        {
            return $"#{Mathf.RoundToInt(color.r * 255):X2}{Mathf.RoundToInt(color.g * 255):X2}{Mathf.RoundToInt(color.b * 255):X2}{Mathf.RoundToInt(color.a * 255):X2}";
        }

        // The Frame Debugger applies a new event limit and re-renders the Game view only on a later editor tick, so
        // captures taken inside one call all show the same picture. The MCP server runs the search in steps instead:
        // each step samples what the Game view shows now and selects the next event to look at.
        private sealed class PixelSearch
        {
            public int EventCount;
            public int FinalEventIndex;
            public float NormalizedX;
            public float NormalizedY;
            public int MaxDimension;
            public float Tolerance;
            public int PixelX = -1;
            public int PixelY = -1;
            public int CaptureWidth;
            public int CaptureHeight;
            public Color FinalColor;
            public bool HasFinalColor;
            public Color? BeforeColor;
            public int Low;
            public int High;
            public int SelectedEventIndex;
            public bool IsSamplingBefore;
            // The writer is selected and the next step reads its details: Unity gives an event's data a tick after selection.
            public bool IsReadingDetails;
            public int Captures;
            public bool FrameChanged;
            public List<string> Diagnostics = new List<string>();
        }

        private static PixelSearch? _pixelSearch;
    }
}
