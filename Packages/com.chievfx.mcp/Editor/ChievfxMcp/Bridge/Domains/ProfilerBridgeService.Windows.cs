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
    internal sealed partial class ProfilerBridgeService : BridgeDomainServiceBase
    {
        public object ControlWindow(JToken args)
        {
            var diagnostics = new List<string>();
            var window = ResolveKnownEditorWindow(
                new[]
                {
                    "UnityEditor.Profiling.ProfilerWindow",
                    "UnityEditorInternal.ProfilerWindow",
                    "UnityEditor.ProfilerWindow"
                },
                "Window/Analysis/Profiler",
                "Profiler",
                ReadBool(args, "open", true),
                ReadBool(args, "focus", true),
                diagnostics);

            if (HasProperty(args, "selectedFrameIndex"))
            {
                var selectedFrameIndex = ReadInt(args, "selectedFrameIndex", -1);
                if (!TrySetReflectedMember(window, "selectedFrameIndex", selectedFrameIndex, out var error))
                {
                    throw new NotSupportedException($"Profiler selectedFrameIndex assignment is unsupported in this Unity version. {error}");
                }
            }

            var moduleIdentifier = ReadString(args, "moduleIdentifier") ?? ReadString(args, "selectedModuleIdentifier");
            var module = ReadString(args, "module");
            if (string.IsNullOrWhiteSpace(moduleIdentifier) && !string.IsNullOrWhiteSpace(module))
            {
                moduleIdentifier = ResolveProfilerModuleIdentifier(window, module!, diagnostics);
            }

            if (!string.IsNullOrWhiteSpace(moduleIdentifier)
                && !TrySelectProfilerModule(window, moduleIdentifier!, out var moduleError))
            {
                throw new NotSupportedException($"Profiler module selection is unsupported in this Unity version. {moduleError}");
            }

            if (ReadBool(args, "latest", false) || ReadBool(args, "stayOnLatestFrame", false))
            {
                if (!TryInvokeReflectedMethod(window, "SelectAndStayOnLatestFrame", Array.Empty<object?>(), out _, out var error))
                {
                    throw new NotSupportedException($"Profiler SelectAndStayOnLatestFrame is unsupported in this Unity version. {error}");
                }
            }

            window.Repaint();
            EditorApplication.QueuePlayerLoopUpdate();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();

            return new
            {
                success = true,
                window = EditorWindowBridgeService.CreateEditorWindowSummary(window, diagnostics, includeTabs: true),
                profiler = CreateProfilerWindowState(window, diagnostics),
                diagnostics = diagnostics.Distinct(StringComparer.Ordinal).ToArray()
            };
        }

        public object ControlFrameDebugger(JToken args)
        {
            var diagnostics = new List<string>();
            var window = ResolveKnownEditorWindow(
                new[]
                {
                    "UnityEditor.FrameDebuggerWindow",
                    "UnityEditorInternal.FrameDebuggerWindow"
                },
                "Window/Analysis/Frame Debugger",
                "Frame Debugger",
                ReadBool(args, "open", true),
                ReadBool(args, "focus", true),
                diagnostics);

            var enabledSpecified = HasProperty(args, "enabled");
            var eventIndex = ReadNullableInt(args, "eventIndex");
            var eventLimit = ReadNullableInt(args, "eventLimit");
            bool? requestedEnabled = null;
            if (enabledSpecified)
            {
                // ReadBool falls back to false for a non-boolean value (e.g. enabled:1), which would
                // silently *disable* the debugger while reporting success. Reject instead of guessing.
                if (!TryReadStrictBool(args, "enabled", out var requested))
                {
                    throw new ArgumentException("frame-debugger-control 'enabled' must be true or false.", nameof(args));
                }

                requestedEnabled = requested;
                SetFrameDebuggerEnabled(window, requested, diagnostics);
            }
            else if (eventIndex.HasValue || eventLimit.HasValue)
            {
                requestedEnabled = true;
                SetFrameDebuggerEnabled(window, true, diagnostics);
                diagnostics.Add("Frame Debugger enabled automatically because event selection was requested.");
            }
            else if (!HasProperty(args, "open") && !HasProperty(args, "focus"))
            {
                // Nothing to do: without enabled/eventIndex/eventLimit this only opened a window, yet
                // used to answer success:true — indistinguishable from an actual capture.
                throw new ArgumentException(
                    "frame-debugger-control had nothing to do: pass enabled (true/false), eventIndex, or eventLimit. "
                    + "Use open/focus alone only to surface the window.",
                    nameof(args));
            }

            var eventCount = GetFrameDebuggerEventCount(diagnostics);
            if ((eventIndex.HasValue || eventLimit.HasValue) && eventCount.HasValue && eventCount.Value <= 0)
            {
                throw new InvalidOperationException("Frame Debugger has no captured events. Enable it on a rendered frame before selecting an event.");
            }

            if ((eventIndex.HasValue || eventLimit.HasValue) && !IsFrameDebuggerWindowReadyForEventSelection(window))
            {
                window.Repaint();
                EditorApplication.QueuePlayerLoopUpdate();
                throw new InvalidOperationException("Frame Debugger window is enabled but its event tree is still initializing. Call frame-debugger-control again after the Frame Debugger window repaints.");
            }

            if (eventLimit.HasValue)
            {
                if (eventLimit.Value <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(args), "eventLimit must be at least 1 because Unity FrameDebuggerUtility.limit is one-based.");
                }

                if (eventCount.HasValue && eventLimit.Value > eventCount.Value)
                {
                    throw new ArgumentOutOfRangeException(nameof(args), $"eventLimit {eventLimit.Value} is outside available Frame Debugger event limits 1..{eventCount.Value}.");
                }

                if (!TryChangeFrameDebuggerEventLimit(window, eventLimit.Value, out var error))
                {
                    throw new NotSupportedException($"Frame Debugger event limit control is unsupported in this Unity version. {error}");
                }
            }

            if (eventIndex.HasValue)
            {
                if (eventIndex.Value < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(args), "eventIndex must be zero or greater.");
                }

                if (eventCount.HasValue && eventIndex.Value >= eventCount.Value)
                {
                    throw new ArgumentOutOfRangeException(nameof(args), $"eventIndex {eventIndex.Value} is outside available Frame Debugger events 0..{eventCount.Value - 1}.");
                }

                var frameEventLimit = eventIndex.Value + 1;
                if (!TrySelectFrameDebuggerEventIndex(window, frameEventLimit, out var error))
                {
                    throw new NotSupportedException($"Frame Debugger event selection is unsupported in this Unity version. {error}");
                }
            }

            window.Repaint();
            EditorApplication.QueuePlayerLoopUpdate();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();

            var frameDebuggerState = CreateFrameDebuggerState(window, diagnostics);

            // success must reflect what actually happened. It used to be a literal true, so a request that
            // changed nothing still answered success:true enabled:false eventCount:0.
            var success = true;
            if (requestedEnabled.HasValue)
            {
                var actualEnabled = frameDebuggerState.TryGetValue("enabled", out var enabledValue) ? enabledValue as bool? : null;
                if (actualEnabled.HasValue && actualEnabled.Value != requestedEnabled.Value)
                {
                    success = false;
                    diagnostics.Add(requestedEnabled.Value
                        ? "Frame Debugger did not turn on. Unity toggles it asynchronously and needs a renderable Game view — make sure the Game view is visible and not docked in the same tab group as the Frame Debugger, then call again."
                        : "Frame Debugger did not turn off.");
                }
                else if (!actualEnabled.HasValue)
                {
                    success = false;
                    diagnostics.Add("Could not read Frame Debugger enabled state, so the requested change is unverified.");
                }
                else if (requestedEnabled.Value)
                {
                    var eventCountValue = frameDebuggerState.TryGetValue("eventCount", out var countValue) ? countValue as int? : null;
                    if (!eventCountValue.HasValue || eventCountValue.Value <= 0)
                    {
                        // Enabled, but nothing captured yet: the count only fills once a frame renders
                        // with the debugger on. Say so rather than letting eventCount:0 look like a bug.
                        diagnostics.Add("Frame Debugger is on but has captured no events yet — events appear after the next rendered frame. Call frame-debugger-events-list once the Game view has repainted.");
                    }
                }
            }

            return new
            {
                success,
                window = EditorWindowBridgeService.CreateEditorWindowSummary(window, diagnostics, includeTabs: true),
                frameDebugger = frameDebuggerState,
                diagnostics = diagnostics.Distinct(StringComparer.Ordinal).ToArray()
            };
        }

        // Strict boolean read: only real booleans and "true"/"false" strings count. Prevents a wrong-typed
        // value from silently meaning false.
        private static bool TryReadStrictBool(JToken args, string name, out bool value)
        {
            value = false;
            var token = args?[name];
            if (token is null)
            {
                return false;
            }

            if (token.Type == JTokenType.Boolean)
            {
                value = token.Value<bool>();
                return true;
            }

            return token.Type == JTokenType.String
                && bool.TryParse(token.Value<string>(), out value);
        }

        public object ListFrameDebuggerEvents(JToken args)
        {
            var diagnostics = new List<string>();
            var window = PrepareFrameDebuggerForInspection(args, diagnostics, selectEventIndex: null);
            var state = CreateFrameDebuggerState(window, diagnostics);
            var eventCount = TryConvertToIntValue(state.TryGetValue("eventCount", out var countValue) ? countValue : null);
            var startIndex = Math.Max(0, ReadInt(args, "startIndex", 0));
            var maxResults = Math.Max(1, Math.Min(ReadInt(args, "maxResults", 30), 200));

            var events = new List<Dictionary<string, object?>>();
            if (eventCount.HasValue)
            {
                var endIndex = Math.Min(eventCount.Value, startIndex + maxResults);
                for (var index = startIndex; index < endIndex; index++)
                {
                    events.Add(CreateFrameDebuggerEventSummary(index, includeDetails: false, diagnostics));
                }
            }

            state = CreateFrameDebuggerState(window, diagnostics);
            return new
            {
                success = true,
                frameDebugger = state,
                startIndex,
                count = events.Count,
                totalEvents = eventCount,
                maxResults,
                truncated = eventCount.HasValue && startIndex + events.Count < eventCount.Value,
                events = events.ToArray(),
                diagnostics = diagnostics.Distinct(StringComparer.Ordinal).ToArray()
            };
        }

        public object GetFrameDebuggerEvent(JToken args)
        {
            var diagnostics = new List<string>();
            var eventIndex = ReadInt(args, "eventIndex", -1);
            if (eventIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(args), "eventIndex must be zero or greater.");
            }

            var select = ReadBool(args, "select", true);
            var window = PrepareFrameDebuggerForInspection(args, diagnostics, select ? eventIndex : (int?)null);
            var state = CreateFrameDebuggerState(window, diagnostics);
            var eventCount = TryConvertToIntValue(state.TryGetValue("eventCount", out var countValue) ? countValue : null);
            if (eventCount.HasValue && eventIndex >= eventCount.Value)
            {
                throw new ArgumentOutOfRangeException(nameof(args), $"eventIndex {eventIndex} is outside available Frame Debugger events 0..{eventCount.Value - 1}.");
            }

            return new
            {
                success = true,
                frameDebugger = state,
                frameEvent = CreateFrameDebuggerEventSummary(eventIndex, includeDetails: true, diagnostics),
                diagnostics = diagnostics.Distinct(StringComparer.Ordinal).ToArray()
            };
        }

        public object ListFrameDebuggerGroups(JToken args)
        {
            var diagnostics = new List<string>();
            var window = PrepareFrameDebuggerForInspection(args, diagnostics, selectEventIndex: null);
            var state = CreateFrameDebuggerState(window, diagnostics);
            var groups = CreateFrameDebuggerGroups(state, diagnostics);

            return new
            {
                success = true,
                frameDebugger = state,
                count = groups.Length,
                totalEvents = TryConvertToIntValue(state.TryGetValue("eventCount", out var countValue) ? countValue : null),
                groups = groups.Select(group => group.ToSummary()).ToArray(),
                diagnostics = diagnostics.Distinct(StringComparer.Ordinal).ToArray()
            };
        }

        public object ListFrameDebuggerGroupEvents(JToken args)
        {
            var diagnostics = new List<string>();
            var window = PrepareFrameDebuggerForInspection(args, diagnostics, selectEventIndex: null);
            var state = CreateFrameDebuggerState(window, diagnostics);
            var groups = CreateFrameDebuggerGroups(state, diagnostics);
            var group = ResolveFrameDebuggerGroup(groups, ReadInt(args, "groupIndex", -1));
            var startIndex = Math.Max(0, ReadInt(args, "startIndex", 0));
            var maxResults = Math.Max(1, Math.Min(ReadInt(args, "maxResults", 50), 200));
            var events = new List<Dictionary<string, object?>>();
            var endIndex = Math.Min(group.EventIndices.Count, startIndex + maxResults);
            for (var drawCallIndex = startIndex; drawCallIndex < endIndex; drawCallIndex++)
            {
                var frameEvent = CreateFrameDebuggerEventSummary(group.EventIndices[drawCallIndex], includeDetails: false, diagnostics);
                frameEvent["groupIndex"] = group.Index;
                frameEvent["drawCallIndex"] = drawCallIndex;
                events.Add(frameEvent);
            }

            return new
            {
                success = true,
                frameDebugger = state,
                group = group.ToSummary(),
                startIndex,
                count = events.Count,
                totalEvents = group.EventIndices.Count,
                maxResults,
                truncated = startIndex + events.Count < group.EventIndices.Count,
                events = events.ToArray(),
                diagnostics = diagnostics.Distinct(StringComparer.Ordinal).ToArray()
            };
        }

        public object GetFrameDebuggerDrawCall(JToken args)
        {
            var diagnostics = new List<string>();
            var groupIndex = ReadInt(args, "groupIndex", -1);
            var drawCallIndex = ReadInt(args, "drawCallIndex", -1);
            if (drawCallIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(args), "drawCallIndex must be zero or greater.");
            }

            var window = PrepareFrameDebuggerForInspection(args, diagnostics, selectEventIndex: null);
            var state = CreateFrameDebuggerState(window, diagnostics);
            var groups = CreateFrameDebuggerGroups(state, diagnostics);
            var group = ResolveFrameDebuggerGroup(groups, groupIndex);
            if (drawCallIndex >= group.EventIndices.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(args), $"drawCallIndex {drawCallIndex} is outside group {groupIndex} draw calls 0..{group.EventIndices.Count - 1}.");
            }

            var eventIndex = group.EventIndices[drawCallIndex];
            var select = ReadBool(args, "select", true);
            if (select)
            {
                window = PrepareFrameDebuggerForInspection(args, diagnostics, eventIndex);
                state = CreateFrameDebuggerState(window, diagnostics);
            }

            var frameEvent = CreateFrameDebuggerEventSummary(eventIndex, includeDetails: true, diagnostics);
            frameEvent["groupIndex"] = group.Index;
            frameEvent["drawCallIndex"] = drawCallIndex;
            return new
            {
                success = true,
                frameDebugger = state,
                group = group.ToSummary(),
                frameEvent,
                diagnostics = diagnostics.Distinct(StringComparer.Ordinal).ToArray()
            };
        }

        /// <summary>
        /// "Which draw call wrote this pixel?" — the right first question for a wrong-looking pixel, and
        /// previously answerable only by eyeballing draw calls one at a time. Binary-searches the frame
        /// debugger event limit for the first event whose output at (x,y) matches the finished frame, so
        /// it costs ~log2(eventCount) captures instead of a linear sweep.
        /// </summary>
        public object PickFrameDebuggerPixel(JToken args)
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
            var screenshotArgs = new JObject { ["maxDimension"] = maxDimension };
            var screenshots = new ScreenshotBridgeService();
            var tolerance = Math.Max(0, Math.Min(ReadInt(args, "tolerance", 2), 255)) / 255f;

            // Sample the finished frame first: it fixes both the target colour and the capture size the
            // caller's normalized coordinates resolve against.
            SelectFrameDebuggerEventForCapture(window, eventCount.Value - 1, diagnostics);
            var finalTexture = DecodeCapture(screenshots.CaptureGameView(screenshotArgs));
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
                var texture = DecodeCapture(screenshots.CaptureGameView(screenshotArgs));
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
                }

                captures++;
            }

            var writerIndex = low;
            var beforeColor = writerIndex > 0 ? SampleAtLimit(writerIndex - 1) : (Color?)null;
            if (writerIndex > 0)
            {
                captures++;
            }

            // Leave the frame debugger showing the culprit so the Unity window matches the answer.
            SelectFrameDebuggerEventForCapture(window, writerIndex, diagnostics);

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

        public ImageResult CaptureFrameDebuggerDrawCall(JToken args)
        {
            var diagnostics = new List<string>();
            var groupIndex = ReadInt(args, "groupIndex", -1);
            var drawCallIndex = ReadInt(args, "drawCallIndex", -1);
            if (drawCallIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(args), "drawCallIndex must be zero or greater.");
            }

            var window = PrepareFrameDebuggerForInspection(args, diagnostics, selectEventIndex: null);
            var state = CreateFrameDebuggerState(window, diagnostics);
            var groups = CreateFrameDebuggerGroups(state, diagnostics);
            var group = ResolveFrameDebuggerGroup(groups, groupIndex);
            if (drawCallIndex >= group.EventIndices.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(args), $"drawCallIndex {drawCallIndex} is outside group {groupIndex} draw calls 0..{group.EventIndices.Count - 1}.");
            }

            var eventIndex = group.EventIndices[drawCallIndex];
            var beforeEventIndex = eventIndex > 0 ? eventIndex - 1 : eventIndex;
            var maxDimension = Math.Max(128, Math.Min(ReadInt(args, "maxDimension", 960), 4096));
            var screenshotArgs = new JObject { ["maxDimension"] = maxDimension };
            var screenshots = new ScreenshotBridgeService();

            SelectFrameDebuggerEventForCapture(window, beforeEventIndex, diagnostics);
            var before = screenshots.CaptureGameView(screenshotArgs);
            SelectFrameDebuggerEventForCapture(window, eventIndex, diagnostics);
            var after = screenshots.CaptureGameView(screenshotArgs);
            var combined = StackFrameDebuggerScreenshots(before, after, out var width, out var height);

            var frameEvent = CreateFrameDebuggerEventSummary(eventIndex, includeDetails: true, diagnostics);
            frameEvent["groupIndex"] = group.Index;
            frameEvent["drawCallIndex"] = drawCallIndex;
            var metadata = new Dictionary<string, object?>
            {
                ["captureSource"] = "frame-debugger-drawcall-screenshot",
                ["layout"] = "vertical",
                ["top"] = eventIndex > 0 ? "before" : "current (no previous event)",
                ["bottom"] = "current",
                ["beforeEventIndex"] = beforeEventIndex,
                ["currentEventIndex"] = eventIndex,
                ["group"] = group.ToSummary(),
                ["frameEvent"] = frameEvent,
                ["pngWidth"] = width,
                ["pngHeight"] = height,
                ["maxDimension"] = maxDimension,
                ["diagnostics"] = diagnostics.Distinct(StringComparer.Ordinal).ToArray()
            };
            return new ImageResult("image/png", Convert.ToBase64String(combined), metadata);
        }

        private sealed class FrameDebuggerGroup
        {
            public int Index { get; set; }
            public string Name { get; set; } = string.Empty;
            public string Path { get; set; } = string.Empty;
            public List<int> EventIndices { get; } = new List<int>();

            public Dictionary<string, object?> ToSummary()
            {
                var firstEventIndex = EventIndices.Count > 0 ? EventIndices[0] : -1;
                var lastEventIndex = EventIndices.Count > 0 ? EventIndices[EventIndices.Count - 1] : -1;
                return new Dictionary<string, object?>
                {
                    ["index"] = Index,
                    ["name"] = Name,
                    ["path"] = Path,
                    ["eventCount"] = EventIndices.Count,
                    ["firstEventIndex"] = firstEventIndex,
                    ["lastEventIndex"] = lastEventIndex
                };
            }
        }

        private static FrameDebuggerGroup[] CreateFrameDebuggerGroups(Dictionary<string, object?> state, List<string> diagnostics)
        {
            var eventCount = TryConvertToIntValue(state.TryGetValue("eventCount", out var countValue) ? countValue : null);
            if (!eventCount.HasValue)
            {
                return Array.Empty<FrameDebuggerGroup>();
            }

            var groups = new List<FrameDebuggerGroup>();
            var groupsByPath = new Dictionary<string, FrameDebuggerGroup>(StringComparer.Ordinal);
            for (var eventIndex = 0; eventIndex < eventCount.Value; eventIndex++)
            {
                var frameEvent = CreateFrameDebuggerEventSummary(eventIndex, includeDetails: false, diagnostics);
                var eventName = frameEvent.TryGetValue("name", out var nameValue)
                    ? Convert.ToString(nameValue, CultureInfo.InvariantCulture) ?? string.Empty
                    : string.Empty;
                var eventType = frameEvent.TryGetValue("type", out var typeValue)
                    ? Convert.ToString(typeValue, CultureInfo.InvariantCulture) ?? string.Empty
                    : string.Empty;
                var groupPath = GetFrameDebuggerGroupPath(eventName, eventType);
                if (!groupsByPath.TryGetValue(groupPath, out var group))
                {
                    group = new FrameDebuggerGroup
                    {
                        Index = groups.Count,
                        Name = GetFrameDebuggerGroupName(groupPath),
                        Path = groupPath
                    };
                    groupsByPath.Add(groupPath, group);
                    groups.Add(group);
                }

                group.EventIndices.Add(eventIndex);
            }

            return groups.ToArray();
        }

        private static FrameDebuggerGroup ResolveFrameDebuggerGroup(FrameDebuggerGroup[] groups, int groupIndex)
        {
            if (groupIndex < 0 || groupIndex >= groups.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(groupIndex), groups.Length == 0
                    ? "No Frame Debugger groups are available."
                    : $"groupIndex {groupIndex} is outside available Frame Debugger groups 0..{groups.Length - 1}.");
            }

            return groups[groupIndex];
        }

        private static void SelectFrameDebuggerEventForCapture(EditorWindow window, int eventIndex, List<string> diagnostics)
        {
            if (!TrySelectFrameDebuggerEventIndex(window, eventIndex + 1, out var error))
            {
                throw new NotSupportedException($"Frame Debugger event selection is unsupported in this Unity version. {error}");
            }

            window.Repaint();
            EditorApplication.QueuePlayerLoopUpdate();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            Thread.Sleep(120);
        }

        private static byte[] StackFrameDebuggerScreenshots(ImageResult before, ImageResult after, out int width, out int height)
        {
            var beforeTexture = DecodePngTexture(before.Base64);
            var afterTexture = DecodePngTexture(after.Base64);
            try
            {
                width = Math.Max(beforeTexture.width, afterTexture.width);
                var separatorHeight = 4;
                height = beforeTexture.height + afterTexture.height + separatorHeight;
                var combined = new Texture2D(width, height, TextureFormat.RGBA32, false);
                try
                {
                    FillTexture(combined, new Color32(20, 20, 20, 255));
                    BlitTexture(beforeTexture, combined, 0, afterTexture.height + separatorHeight);
                    BlitTexture(afterTexture, combined, 0, 0);
                    combined.Apply();
                    return combined.EncodeToPNG();
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(combined);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(beforeTexture);
                UnityEngine.Object.DestroyImmediate(afterTexture);
            }
        }

        private static Texture2D DecodePngTexture(string base64)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(texture, Convert.FromBase64String(base64)))
            {
                UnityEngine.Object.DestroyImmediate(texture);
                throw new InvalidOperationException("Frame Debugger screenshot PNG could not be decoded.");
            }

            return texture;
        }

        private static void FillTexture(Texture2D texture, Color32 color)
        {
            var pixels = Enumerable.Repeat(color, texture.width * texture.height).ToArray();
            texture.SetPixels32(pixels);
        }

        private static void BlitTexture(Texture2D source, Texture2D destination, int offsetX, int offsetY)
        {
            var pixels = source.GetPixels32();
            destination.SetPixels32(offsetX, offsetY, source.width, source.height, pixels);
        }

        private static string GetFrameDebuggerGroupPath(string eventName, string eventType)
        {
            if (string.Equals(eventType, "ClearColor", StringComparison.OrdinalIgnoreCase))
            {
                return "ExecuteRenderGraph/Clear (color)";
            }

            var normalized = string.IsNullOrWhiteSpace(eventName) ? eventType : eventName;
            var executeMarker = "/ExecuteRenderGraph/";
            var executeIndex = normalized.IndexOf(executeMarker, StringComparison.Ordinal);
            var afterExecute = executeIndex >= 0
                ? normalized.Substring(executeIndex + executeMarker.Length)
                : normalized;

            var parts = afterExecute.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return string.IsNullOrWhiteSpace(eventType) ? "<unknown>" : eventType;
            }

            var groupName = parts[0];
            if (groupName.StartsWith("(RP ", StringComparison.Ordinal))
            {
                return "ExecuteRenderGraph/" + groupName;
            }

            return "ExecuteRenderGraph/" + groupName;
        }

        private static string GetFrameDebuggerGroupName(string groupPath)
        {
            var slashIndex = groupPath.LastIndexOf('/');
            return slashIndex >= 0 && slashIndex < groupPath.Length - 1
                ? groupPath.Substring(slashIndex + 1)
                : groupPath;
        }

        private static EditorWindow ResolveKnownEditorWindow(
            string[] typeNames,
            string menuPath,
            string titleHint,
            bool open,
            bool focus,
            List<string> diagnostics)
        {
            var type = TryResolveTypeByNames(typeNames);
            var window = FindOpenKnownEditorWindow(type, titleHint);
            if (window == null && open && type != null)
            {
                try
                {
                    window = EditorWindowBridgeService.GetEditorWindow(type, focus, null);
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"EditorWindow.GetWindow fallback needed for '{type.FullName}': {ex.GetBaseException().Message}");
                }
            }

            if (window == null && open)
            {
                var windowsBeforeMenu = EditorWindowBridgeService.GetOpenEditorWindows();
                var focusedInstanceIdBeforeMenu = EditorWindow.focusedWindow != null
                    ? GetLegacyInstanceId(EditorWindow.focusedWindow)
                    : 0;
                if (!EditorApplication.ExecuteMenuItem(menuPath))
                {
                    throw new InvalidOperationException($"Unity menu item could not be executed: '{menuPath}'.");
                }

                window = EditorWindowBridgeService.FindOpenedEditorWindowAfterMenu(
                        type?.FullName,
                        titleHint,
                        menuPath,
                        windowsBeforeMenu,
                        focusedInstanceIdBeforeMenu,
                        diagnostics)
                    ?? FindOpenKnownEditorWindow(type, titleHint);
            }

            if (window == null)
            {
                throw new InvalidOperationException($"No open EditorWindow matched '{titleHint}'. Set open=true or open it in Unity first.");
            }

            if (focus)
            {
                EditorWindowBridgeService.SelectAndFocusEditorWindow(window, diagnostics);
            }
            else
            {
                window.Repaint();
            }

            return window;
        }

        private static EditorWindow? FindOpenKnownEditorWindow(Type? type, string titleHint)
        {
            var normalizedTitle = EditorWindowBridgeService.NormalizeEditorWindowTitleHint(titleHint);
            return EditorWindowBridgeService.GetOpenEditorWindows()
                .Where(window => type == null || type.IsAssignableFrom(window.GetType()))
                .FirstOrDefault(window => string.Equals(
                    EditorWindowBridgeService.NormalizeEditorWindowTitleHint(EditorWindowBridgeService.GetEditorWindowTitle(window)),
                    normalizedTitle,
                    StringComparison.OrdinalIgnoreCase))
                ?? EditorWindowBridgeService.GetOpenEditorWindows()
                    .Where(window => type == null || type.IsAssignableFrom(window.GetType()))
                    .FirstOrDefault(window => EditorWindowBridgeService.GetEditorWindowTitle(window).IndexOf(titleHint, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        internal static Type? TryResolveTypeByNames(IEnumerable<string> typeNames)
        {
            foreach (var typeName in typeNames)
            {
                var direct = Type.GetType(typeName, throwOnError: false);
                if (direct != null)
                {
                    return direct;
                }

                var match = EditorWindowBridgeService.GetLoadableTypes().FirstOrDefault(type =>
                    string.Equals(type.FullName, typeName, StringComparison.Ordinal)
                    || string.Equals(type.AssemblyQualifiedName, typeName, StringComparison.Ordinal)
                    || string.Equals(type.Name, typeName, StringComparison.Ordinal));
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        private static Dictionary<string, object?> CreateProfilerWindowState(EditorWindow window, List<string> diagnostics)
        {
            var state = new Dictionary<string, object?>
            {
                ["selectedFrameIndex"] = ReadNullableIntMember(window, "selectedFrameIndex"),
                ["firstAvailableFrameIndex"] = ReadNullableIntMember(window, "firstAvailableFrameIndex"),
                ["lastAvailableFrameIndex"] = ReadNullableIntMember(window, "lastAvailableFrameIndex"),
                ["selectedModuleIdentifier"] = ReadStringMember(window, "selectedModuleIdentifier")
            };

            try
            {
                state["recordingEnabled"] = GetProfilerEnabled();
            }
            catch (Exception ex)
            {
                state["recordingEnabled"] = null;
                diagnostics.Add($"Profiler recording state unavailable: {ex.GetBaseException().Message}");
            }

            return state;
        }

        private static string ResolveProfilerModuleIdentifier(EditorWindow window, string module, List<string> diagnostics)
        {
            var normalized = module.Trim().ToUpperInvariant();
            var memberNames = normalized switch
            {
                "CPU" => new[] { "cpuModuleIdentifier", "CpuModuleIdentifier", "CPU_MODULE_IDENTIFIER" },
                "GPU" => new[] { "gpuModuleIdentifier", "GpuModuleIdentifier", "GPU_MODULE_IDENTIFIER" },
                _ => throw new ArgumentException("module must be 'CPU' or 'GPU'.", nameof(module))
            };

            foreach (var memberName in memberNames)
            {
                var value = ReadStaticStringMember(window.GetType(), memberName);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value!;
                }
            }

            diagnostics.Add($"Profiler {normalized} module identifier constant was unavailable; using display-name fallback.");
            return normalized == "CPU" ? "CPU Usage" : "GPU Usage";
        }

        private static bool TrySelectProfilerModule(EditorWindow window, string moduleIdentifier, out string? error)
        {
            error = null;
            if (TrySetReflectedMember(window, "selectedModuleIdentifier", moduleIdentifier, out error))
            {
                return true;
            }

            foreach (var methodName in new[] { "SetSelectedModuleIdentifier", "SelectModule", "SetSelectedModule", "SetActiveVisibleProfilerModule" })
            {
                if (TryInvokeReflectedMethod(window, methodName, new object?[] { moduleIdentifier }, out _, out error))
                {
                    return true;
                }
            }

            return false;
        }

        private static Dictionary<string, object?> CreateFrameDebuggerState(EditorWindow window, List<string> diagnostics)
        {
            var state = new Dictionary<string, object?>();
            try
            {
                state["enabled"] = GetFrameDebuggerEnabled();
            }
            catch (Exception ex)
            {
                state["enabled"] = null;
                diagnostics.Add($"FrameDebugger.enabled unavailable: {ex.GetBaseException().Message}");
            }

            state["eventCount"] = GetFrameDebuggerEventCount(diagnostics);
            var currentEventLimit = GetFrameDebuggerEventLimit(window, diagnostics);
            state["currentEventLimit"] = currentEventLimit;
            state["selectedEventIndex"] = GetFrameDebuggerSelectedEventIndex(window, currentEventLimit);
            return state;
        }

        private EditorWindow PrepareFrameDebuggerForInspection(JToken args, List<string> diagnostics, int? selectEventIndex)
        {
            var window = ResolveKnownEditorWindow(
                new[]
                {
                    "UnityEditor.FrameDebuggerWindow",
                    "UnityEditorInternal.FrameDebuggerWindow"
                },
                "Window/Analysis/Frame Debugger",
                "Frame Debugger",
                ReadBool(args, "open", true),
                ReadBool(args, "focus", false),
                diagnostics);

            if (!TryGetFrameDebuggerEnabled(out var enabled, out _)
                || !enabled)
            {
                SetFrameDebuggerEnabled(window, true, diagnostics);
                diagnostics.Add("Frame Debugger enabled automatically for event inspection.");
            }

            var eventCount = GetFrameDebuggerEventCount(diagnostics);
            if (eventCount.HasValue && eventCount.Value <= 0)
            {
                window.Repaint();
                EditorApplication.QueuePlayerLoopUpdate();
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            }

            eventCount = GetFrameDebuggerEventCount(diagnostics);
            if (eventCount.HasValue && eventCount.Value <= 0)
            {
                throw new InvalidOperationException("Frame Debugger has no captured events. Make sure a rendered Game/Scene view is available, then retry.");
            }

            if (selectEventIndex.HasValue)
            {
                if (eventCount.HasValue && selectEventIndex.Value >= eventCount.Value)
                {
                    throw new ArgumentOutOfRangeException(nameof(args), $"eventIndex {selectEventIndex.Value} is outside available Frame Debugger events 0..{eventCount.Value - 1}.");
                }

                if (!IsFrameDebuggerWindowReadyForEventSelection(window))
                {
                    window.Repaint();
                    EditorApplication.QueuePlayerLoopUpdate();
                    throw new InvalidOperationException("Frame Debugger window is enabled but its event tree is still initializing. Retry after the Frame Debugger window repaints.");
                }

                if (!TrySelectFrameDebuggerEventIndex(window, selectEventIndex.Value + 1, out var error))
                {
                    throw new NotSupportedException($"Frame Debugger event selection is unsupported in this Unity version. {error}");
                }
            }

            return window;
        }

        private static Dictionary<string, object?> CreateFrameDebuggerEventSummary(int eventIndex, bool includeDetails, List<string> diagnostics)
        {
            var summary = new Dictionary<string, object?>
            {
                ["index"] = eventIndex,
                ["eventLimit"] = eventIndex + 1
            };

            var utilityType = GetFrameDebuggerUtilityType();
            if (utilityType == null)
            {
                diagnostics.Add("FrameDebuggerUtility type unavailable; frame event data cannot be reported.");
                return summary;
            }

            var frameEvent = GetFrameDebuggerEvent(utilityType, eventIndex, diagnostics);
            if (frameEvent != null)
            {
                var eventType = GetReflectedInstanceValue(frameEvent, "m_Type");
                if (eventType != null)
                {
                    summary["type"] = Convert.ToString(eventType, CultureInfo.InvariantCulture);
                }

                var obj = GetReflectedInstanceValue(frameEvent, "m_Obj") as Object
                    ?? InvokeFrameDebuggerObjectGetter(utilityType, eventIndex) as Object;
                AddObjectSummary(summary, "object", obj);
            }

            var infoName = InvokeFrameDebuggerStringMethod(utilityType, "GetFrameEventInfoName", eventIndex);
            if (!string.IsNullOrWhiteSpace(infoName))
            {
                summary["name"] = infoName;
            }

            var eventData = includeDetails ? CreateFrameDebuggerEventData(utilityType, eventIndex, diagnostics) : null;
            if (eventData != null)
            {
                AddFrameDebuggerEventData(summary, eventData, includeDetails, diagnostics);
            }

            return summary;
        }

        private static object? GetFrameDebuggerEvent(Type utilityType, int eventIndex, List<string> diagnostics)
        {
            if (!TryInvokeReflectedMethod(utilityType, "GetFrameEvents", Array.Empty<object?>(), out var result, out var error)
                || !(result is Array events))
            {
                diagnostics.Add($"FrameDebuggerUtility.GetFrameEvents unavailable: {error}");
                return null;
            }

            return eventIndex >= 0 && eventIndex < events.Length ? events.GetValue(eventIndex) : null;
        }

        private static object? CreateFrameDebuggerEventData(Type utilityType, int eventIndex, List<string> diagnostics)
        {
            var dataType = TryResolveTypeByNames(new[]
            {
                "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData",
                "UnityEditorInternal.FrameDebuggerEventData",
                "UnityEditor.FrameDebuggerEventData"
            });
            if (dataType == null)
            {
                diagnostics.Add("FrameDebuggerEventData type unavailable.");
                return null;
            }

            var data = Activator.CreateInstance(dataType);
            if (data == null)
            {
                diagnostics.Add("FrameDebuggerEventData could not be constructed.");
                return null;
            }

            if (!TryInvokeReflectedMethod(utilityType, "GetFrameEventData", new object?[] { eventIndex, data }, out var ok, out var error)
                || (ok is bool boolOk && !boolOk))
            {
                diagnostics.Add($"FrameDebuggerUtility.GetFrameEventData unavailable for event {eventIndex}: {error}");
                return null;
            }

            return data;
        }

        private static void AddFrameDebuggerEventData(Dictionary<string, object?> summary, object eventData, bool includeDetails, List<string> diagnostics)
        {
            AddString(summary, "shader", ReadFirstString(eventData, "m_RealShaderName", "m_OriginalShaderName", "m_ComputeShaderName", "m_RayTracingShaderName"));
            AddString(summary, "pass", ReadFirstString(eventData, "m_PassName", "m_RayTracingShaderPassName"));
            AddString(summary, "passLightMode", ReadFirstString(eventData, "m_PassLightMode"));
            AddInt(summary, "drawCalls", ReadFirstInt(eventData, "m_DrawCallCount"));
            AddInt(summary, "vertices", ReadFirstInt(eventData, "m_VertexCount"));
            AddInt(summary, "indices", ReadFirstInt(eventData, "m_IndexCount"));
            AddInt(summary, "instances", ReadFirstInt(eventData, "m_InstanceCount"));
            AddObjectSummary(summary, "mesh", GetReflectedInstanceValue(eventData, "m_Mesh") as Object);
            AddInt(summary, "meshSubset", ReadFirstInt(eventData, "m_MeshSubset"));
            AddString(summary, "renderTarget", ReadFirstString(eventData, "m_RenderTargetName"));

            var batchBreakCause = ReadFirstInt(eventData, "m_BatchBreakCause");
            if (batchBreakCause.HasValue)
            {
                summary["batchBreakCause"] = batchBreakCause.Value;
                var batchBreakText = GetBatchBreakCauseText(batchBreakCause.Value, diagnostics);
                if (!string.IsNullOrWhiteSpace(batchBreakText))
                {
                    summary["batchBreakReason"] = batchBreakText;
                }
            }

            if (!includeDetails)
            {
                return;
            }

            AddInt(summary, "shaderInstanceId", ReadFirstInt(eventData, "m_ShaderInstanceID", "m_ComputeShaderInstanceID", "m_RayTracingShaderInstanceID"));
            AddInt(summary, "shaderPassIndex", ReadFirstInt(eventData, "m_ShaderPassIndex"));
            AddInt(summary, "subShaderIndex", ReadFirstInt(eventData, "m_SubShaderIndex"));
            AddInt(summary, "componentInstanceId", ReadFirstInt(eventData, "m_ComponentInstanceID"));
            AddString(summary, "computeKernel", ReadFirstString(eventData, "m_ComputeShaderKernelName"));
            AddInt(summary, "computeThreadGroupsX", ReadFirstInt(eventData, "m_ComputeShaderThreadGroupsX"));
            AddInt(summary, "computeThreadGroupsY", ReadFirstInt(eventData, "m_ComputeShaderThreadGroupsY"));
            AddInt(summary, "computeThreadGroupsZ", ReadFirstInt(eventData, "m_ComputeShaderThreadGroupsZ"));
            AddInt(summary, "renderTargetWidth", ReadFirstInt(eventData, "m_RenderTargetWidth"));
            AddInt(summary, "renderTargetHeight", ReadFirstInt(eventData, "m_RenderTargetHeight"));
            AddInt(summary, "renderTargetFormat", ReadFirstInt(eventData, "m_RenderTargetFormat"));
            AddInt(summary, "renderTargetCount", ReadFirstInt(eventData, "m_RenderTargetCount"));
            AddBool(summary, "renderTargetBackBuffer", ReadFirstBool(eventData, "m_RenderTargetIsBackBuffer"));
        }

        private static string? GetBatchBreakCauseText(int cause, List<string> diagnostics)
        {
            var utilityType = GetFrameDebuggerUtilityType();
            if (utilityType == null)
            {
                diagnostics.Add("FrameDebuggerUtility.GetBatchBreakCauseStrings unavailable: utility type missing.");
                return null;
            }

            if (!TryInvokeReflectedMethod(utilityType, "GetBatchBreakCauseStrings", Array.Empty<object?>(), out var result, out var error)
                || !(result is string[] reasons))
            {
                diagnostics.Add($"FrameDebuggerUtility.GetBatchBreakCauseStrings unavailable: {error}");
                return null;
            }

            return cause >= 0 && cause < reasons.Length ? reasons[cause] : null;
        }

        private static Type GetFrameDebuggerType()
        {
            return TryResolveTypeByNames(new[] { "UnityEngine.FrameDebugger", "UnityEditorInternal.FrameDebugger", "UnityEditor.FrameDebugger" })
                ?? throw new NotSupportedException("FrameDebugger API is unavailable in this Unity version.");
        }

        private static Type? GetFrameDebuggerUtilityType()
        {
            return TryResolveTypeByNames(new[]
            {
                "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility",
                "UnityEditorInternal.FrameDebuggerUtility",
                "UnityEditor.FrameDebuggerUtility"
            });
        }

        private static bool GetFrameDebuggerEnabled()
        {
            var type = GetFrameDebuggerType();
            var value = GetReflectedStaticValue(type, "enabled") ?? GetReflectedStaticValue(type, "Enabled");
            if (value == null)
            {
                if (TryInvokeReflectedMethod(type, "IsLocalEnabled", Array.Empty<object?>(), out var localEnabled, out _))
                {
                    return Convert.ToBoolean(localEnabled, CultureInfo.InvariantCulture);
                }

                throw new NotSupportedException("FrameDebugger.enabled/IsLocalEnabled is unavailable in this Unity version.");
            }

            return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }

        private static void SetFrameDebuggerEnabled(EditorWindow window, bool enabled, List<string> diagnostics)
        {
            var stateKnown = TryGetFrameDebuggerEnabled(out var currentEnabled, out var stateError);
            if (!stateKnown)
            {
                diagnostics.Add($"FrameDebugger.enabled state unavailable before set: {stateError}");
            }

            if (stateKnown && currentEnabled == enabled)
            {
                if (!enabled || !IsFrameDebuggerWindowMissingEnabledState(window))
                {
                    return;
                }

                diagnostics.Add("Frame Debugger was enabled without initialized window state; restarting through FrameDebuggerWindow.");
                if (TryInvokeReflectedMethod(window, "DisableFrameDebugger", Array.Empty<object?>(), out _, out _))
                {
                    currentEnabled = false;
                }
                else
                {
                    SetFrameDebuggerEnabledViaApi(false);
                    currentEnabled = false;
                }
            }

            var methodName = enabled ? "EnableFrameDebugger" : "DisableFrameDebugger";
            if (TryInvokeReflectedMethod(window, methodName, Array.Empty<object?>(), out _, out var windowError))
            {
                if (TryGetFrameDebuggerEnabled(out var afterEnabled, out _)
                    && afterEnabled != enabled)
                {
                    throw new InvalidOperationException(enabled
                        ? "Frame Debugger window did not enable. Make sure a Game view can be shown and is not docked in the same tab group as the Frame Debugger."
                        : "Frame Debugger window did not disable.");
                }

                return;
            }

            if (enabled)
            {
                throw new NotSupportedException($"Safe FrameDebuggerWindow.EnableFrameDebugger reflection is unavailable in this Unity version. {windowError}");
            }

            diagnostics.Add($"FrameDebuggerWindow.DisableFrameDebugger unavailable; falling back to FrameDebugger API. {windowError}");
            SetFrameDebuggerEnabledViaApi(false);
        }

        private static bool TryGetFrameDebuggerEnabled(out bool enabled, out string? error)
        {
            enabled = false;
            error = null;
            try
            {
                enabled = GetFrameDebuggerEnabled();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetBaseException().Message;
                return false;
            }
        }

        private static void SetFrameDebuggerEnabledViaApi(bool enabled)
        {
            var type = GetFrameDebuggerType();
            if (!TrySetReflectedMember(type, "enabled", enabled, out var error))
            {
                var utilityType = GetFrameDebuggerUtilityType();
                if (utilityType != null)
                {
                    var remotePlayerGuid = 0;
                    if (TryInvokeReflectedMethod(utilityType, "GetRemotePlayerGUID", Array.Empty<object?>(), out var remoteResult, out _)
                        && TryConvertToInt(remoteResult, out var reflectedRemotePlayerGuid))
                    {
                        remotePlayerGuid = reflectedRemotePlayerGuid;
                    }

                    if (TryInvokeReflectedMethod(utilityType, "SetEnabled", new object?[] { enabled, remotePlayerGuid }, out _, out error))
                    {
                        return;
                    }
                }

                throw new NotSupportedException($"FrameDebugger enabled assignment is unsupported in this Unity version. {error}");
            }
        }

        private static bool IsFrameDebuggerWindowMissingEnabledState(EditorWindow window)
        {
            return EditorWindowBridgeService.GetReflectedValue(window, "m_EventDetailsView") == null;
        }

        private static bool IsFrameDebuggerWindowReadyForEventSelection(EditorWindow window)
        {
            // Unity renamed the tree field: 6.x has m_Tree, older versions m_TreeView. Requiring only the
            // old name made this permanently false on Unity 6, so every eventIndex/eventLimit call failed
            // with "event tree is still initializing".
            return EditorWindowBridgeService.GetReflectedValue(window, "m_EventDetailsView") != null
                && (EditorWindowBridgeService.GetReflectedValue(window, "m_Tree") != null
                    || EditorWindowBridgeService.GetReflectedValue(window, "m_TreeView") != null);
        }

        private static int? GetFrameDebuggerEventCount(List<string> diagnostics)
        {
            var utilityType = GetFrameDebuggerUtilityType();
            if (utilityType == null)
            {
                diagnostics.Add("FrameDebuggerUtility type unavailable; event count cannot be reported.");
                return null;
            }

            foreach (var memberName in new[] { "count", "eventCount", "frameEventCount" })
            {
                if (TryReadStaticIntMember(utilityType, memberName, out var value))
                {
                    return value;
                }
            }

            foreach (var methodName in new[] { "GetFrameEventCount", "GetEventCount" })
            {
                if (TryInvokeReflectedMethod(utilityType, methodName, Array.Empty<object?>(), out var result, out _)
                    && TryConvertToInt(result, out var value))
                {
                    return value;
                }
            }

            if (TryInvokeReflectedMethod(utilityType, "GetFrameEvents", Array.Empty<object?>(), out var frameEvents, out _)
                && frameEvents is Array frameEventArray)
            {
                return frameEventArray.Length;
            }

            diagnostics.Add("FrameDebuggerUtility event count members unavailable.");
            return null;
        }

        private static int? GetFrameDebuggerEventLimit(EditorWindow window, List<string> diagnostics)
        {
            var utilityType = GetFrameDebuggerUtilityType();
            if (utilityType != null)
            {
                foreach (var memberName in new[] { "limit", "eventLimit", "currentEventLimit" })
                {
                    if (TryReadStaticIntMember(utilityType, memberName, out var value))
                    {
                        return value;
                    }
                }
            }

            foreach (var memberName in new[] { "m_FrameEventLimit", "m_CurrentFrameEventLimit", "m_FrameDebuggerEventLimit" })
            {
                var value = ReadNullableIntMember(window, memberName);
                if (value.HasValue)
                {
                    return value;
                }
            }

            diagnostics.Add("Frame Debugger current event limit members unavailable.");
            return null;
        }

        private static int? GetFrameDebuggerSelectedEventIndex(EditorWindow window, int? currentEventLimit)
        {
            foreach (var target in EnumerateFrameDebuggerTreeViewTargets(window))
            {
                foreach (var memberName in new[] { "selectedFrameEventIndex", "m_SelectedFrameEventIndex", "selectedEventIndex", "m_SelectedEventIndex" })
                {
                    var value = ReadNullableIntMember(target, memberName);
                    if (value.HasValue)
                    {
                        return value;
                    }
                }

                var treeController = EditorWindowBridgeService.GetReflectedValue(target, "m_TreeView") ?? target;
                if (TryInvokeReflectedMethod(treeController, "GetSelection", Array.Empty<object?>(), out var selection, out _)
                    && selection is IEnumerable selectedIds)
                {
                    foreach (var selectedId in selectedIds)
                    {
                        if (TryConvertToInt(selectedId, out var reflectedSelectedId) && reflectedSelectedId > 0)
                        {
                            return reflectedSelectedId - 1;
                        }
                    }
                }
            }

            return currentEventLimit.HasValue && currentEventLimit.Value > 0
                ? currentEventLimit.Value - 1
                : null;
        }

        private static bool TryChangeFrameDebuggerEventLimit(EditorWindow window, int eventLimit, out string? error)
        {
            if (TryInvokeReflectedMethod(window, "ChangeFrameEventLimit", new object?[] { eventLimit }, out _, out error))
            {
                TryInvokeReflectedMethod(window, "RepaintOnLimitChange", Array.Empty<object?>(), out _, out _);
                return true;
            }

            return false;
        }

        private static bool TrySelectFrameDebuggerEventIndex(EditorWindow window, int frameEventLimit, out string? error)
        {
            if (TryChangeFrameDebuggerEventLimit(window, frameEventLimit, out error))
            {
                return true;
            }

            foreach (var target in EnumerateFrameDebuggerTreeViewTargets(window))
            {
                if (TryInvokeReflectedMethod(target, "SelectFrameEventIndex", new object?[] { frameEventLimit }, out _, out error))
                {
                    TryInvokeReflectedMethod(window, "RepaintOnLimitChange", Array.Empty<object?>(), out _, out _);
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<object> EnumerateFrameDebuggerTreeViewTargets(EditorWindow window)
        {
            var targets = new List<object>();
            foreach (var memberName in new[] { "m_TreeView", "m_FrameDebuggerTreeView", "m_FrameEventsTreeView", "m_FrameEvents" })
            {
                var value = EditorWindowBridgeService.GetReflectedValue(window, memberName);
                if (value != null)
                {
                    targets.Add(value);
                }
            }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var field in window.GetType().GetFields(flags))
            {
                if (field.Name.IndexOf("TreeView", StringComparison.OrdinalIgnoreCase) < 0
                    && (field.FieldType.FullName?.IndexOf("FrameDebugger", StringComparison.OrdinalIgnoreCase) ?? -1) < 0)
                {
                    continue;
                }

                try
                {
                    var value = field.GetValue(window);
                    if (value != null)
                    {
                        targets.Add(value);
                    }
                }
                catch
                {
                    // Ignore version-specific reflected members that reject access.
                }
            }

            return targets.Where(target => target != null).Distinct().ToArray();
        }

        private static object? InvokeFrameDebuggerObjectGetter(Type utilityType, int eventIndex)
        {
            return TryInvokeReflectedMethod(utilityType, "GetFrameEventObject", new object?[] { eventIndex }, out var result, out _)
                ? result
                : null;
        }

        private static string? InvokeFrameDebuggerStringMethod(Type utilityType, string methodName, int eventIndex)
        {
            return TryInvokeReflectedMethod(utilityType, methodName, new object?[] { eventIndex }, out var result, out _)
                ? Convert.ToString(result, CultureInfo.InvariantCulture)
                : null;
        }

        private static void AddObjectSummary(Dictionary<string, object?> summary, string prefix, Object? obj)
        {
            if (obj == null)
            {
                return;
            }

            summary[prefix + "Name"] = obj.name;
            summary[prefix + "Type"] = obj.GetType().Name;
            summary[prefix + "InstanceId"] = GetLegacyInstanceId(obj);
        }

        private static void AddString(Dictionary<string, object?> summary, string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                summary[key] = value;
            }
        }

        private static void AddInt(Dictionary<string, object?> summary, string key, int? value)
        {
            if (value.HasValue && value.Value != 0)
            {
                summary[key] = value.Value;
            }
        }

        private static void AddBool(Dictionary<string, object?> summary, string key, bool? value)
        {
            if (value.HasValue)
            {
                summary[key] = value.Value;
            }
        }

        private static string? ReadFirstString(object target, params string[] names)
        {
            foreach (var name in names)
            {
                var value = GetReflectedInstanceValue(target, name);
                var text = value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            return null;
        }

        private static int? ReadFirstInt(object target, params string[] names)
        {
            foreach (var name in names)
            {
                if (TryConvertToInt(GetReflectedInstanceValue(target, name), out var value))
                {
                    return value;
                }
            }

            return null;
        }

        private static bool? ReadFirstBool(object target, params string[] names)
        {
            foreach (var name in names)
            {
                var value = GetReflectedInstanceValue(target, name);
                if (value != null)
                {
                    return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                }
            }

            return null;
        }

        private static int? TryConvertToIntValue(object? value)
        {
            return TryConvertToInt(value, out var intValue) ? intValue : (int?)null;
        }

        private static int? ReadNullableIntMember(object target, string name)
        {
            var value = target is Type type ? GetReflectedStaticValue(type, name) : EditorWindowBridgeService.GetReflectedValue(target, name);
            return TryConvertToInt(value, out var intValue) ? intValue : null;
        }

        private static string? ReadStringMember(object target, string name)
        {
            var value = target is Type type ? GetReflectedStaticValue(type, name) : EditorWindowBridgeService.GetReflectedValue(target, name);
            return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string? ReadStaticStringMember(Type type, string name)
        {
            var value = GetReflectedStaticValue(type, name);
            return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static object? GetReflectedStaticValue(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var property = type.GetProperties(flags).FirstOrDefault(candidate =>
                    candidate.GetIndexParameters().Length == 0
                    && string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
                if (property != null)
                {
                    return property.GetValue(null);
                }
            }
            catch
            {
                return null;
            }

            try
            {
                var field = type.GetFields(flags).FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
                return field?.GetValue(null);
            }
            catch
            {
                return null;
            }
        }

        private static object? GetReflectedInstanceValue(object target, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var type = target.GetType();
                var property = type.GetProperties(flags).FirstOrDefault(candidate =>
                    candidate.GetIndexParameters().Length == 0
                    && string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
                if (property != null)
                {
                    return property.GetValue(target);
                }

                var field = type.GetFields(flags).FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
                return field?.GetValue(target);
            }
            catch
            {
                return null;
            }
        }

        private static bool TryReadStaticIntMember(Type type, string name, out int value)
        {
            return TryConvertToInt(GetReflectedStaticValue(type, name), out value);
        }

        private static bool TrySetReflectedMember(object target, string name, object? value, out string? error)
        {
            error = null;
            var type = target as Type ?? target.GetType();
            var receiver = target is Type ? null : target;
            var flags = (receiver == null ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var property = type.GetProperties(flags).FirstOrDefault(candidate =>
                    candidate.GetIndexParameters().Length == 0
                    && string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
                var setter = property?.GetSetMethod(true);
                if (setter != null)
                {
                    setter.Invoke(receiver, new[] { CoerceReflectedValue(value, property!.PropertyType) });
                    return true;
                }

                var field = type.GetFields(flags).FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
                if (field != null)
                {
                    field.SetValue(receiver, CoerceReflectedValue(value, field.FieldType));
                    return true;
                }

                error = $"No writable member '{name}' found on {type.FullName}.";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.GetBaseException().Message;
                return false;
            }
        }

        private static bool TryInvokeReflectedMethod(object target, string methodName, object?[] arguments, out object? result, out string? error)
        {
            result = null;
            error = null;
            var type = target as Type ?? target.GetType();
            var receiver = target is Type ? null : target;
            var flags = (receiver == null ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.Public | BindingFlags.NonPublic;
            var methods = type.GetMethods(flags)
                .Where(method => string.Equals(method.Name, methodName, StringComparison.OrdinalIgnoreCase)
                    && method.GetParameters().Length == arguments.Length)
                .ToArray();

            foreach (var method in methods)
            {
                try
                {
                    var parameters = method.GetParameters();
                    var coerced = new object?[arguments.Length];
                    for (var i = 0; i < arguments.Length; i++)
                    {
                        coerced[i] = CoerceReflectedValue(arguments[i], parameters[i].ParameterType);
                    }

                    result = method.Invoke(receiver, coerced);
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.GetBaseException().Message;
                }
            }

            error ??= $"No method '{methodName}' with {arguments.Length} parameter(s) found on {type.FullName}.";
            return false;
        }

        private static object? CoerceReflectedValue(object? value, Type targetType)
        {
            if (value == null)
            {
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
            }

            var nullableTarget = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (nullableTarget.IsInstanceOfType(value))
            {
                return value;
            }

            if (nullableTarget.IsEnum)
            {
                return value is string text
                    ? Enum.Parse(nullableTarget, text, ignoreCase: true)
                    : Enum.ToObject(nullableTarget, value);
            }

            return Convert.ChangeType(value, nullableTarget, CultureInfo.InvariantCulture);
        }

        private static bool TryConvertToInt(object? value, out int intValue)
        {
            switch (value)
            {
                case int number:
                    intValue = number;
                    return true;
                case long number:
                    intValue = Convert.ToInt32(number, CultureInfo.InvariantCulture);
                    return true;
                case short number:
                    intValue = number;
                    return true;
                case byte number:
                    intValue = number;
                    return true;
                case null:
                    intValue = 0;
                    return false;
                default:
                    try
                    {
                        intValue = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                        return true;
                    }
                    catch
                    {
                        intValue = 0;
                        return false;
                    }
            }
        }

    }
}
