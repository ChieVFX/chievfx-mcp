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
        public object CaptureFrameDebuggerDrawCall(JToken args)
        {
            var captureStep = ReadString(args, "captureStep");
            if (string.Equals(captureStep, "start", StringComparison.OrdinalIgnoreCase))
            {
                return StartDrawCallCapture(args);
            }

            if (string.Equals(captureStep, "next", StringComparison.OrdinalIgnoreCase))
            {
                return ContinueDrawCallCapture(args);
            }

            return CaptureFrameDebuggerDrawCallInOneCall(args);
        }

        private object StartDrawCallCapture(JToken args)
        {
            _drawCallCapture = null;
            var diagnostics = new List<string>();
            var groupIndex = ReadInt(args, "groupIndex", -1);
            var drawCallIndex = ReadInt(args, "drawCallIndex", -1);
            if (drawCallIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(args), "drawCallIndex must be zero or greater.");
            }

            RequireFrameDebuggerGroupArgument(args);
            var window = PrepareFrameDebuggerForInspection(args, diagnostics, selectEventIndex: null);
            var state = CreateFrameDebuggerState(window, diagnostics);
            var groups = CreateFrameDebuggerGroups(state, diagnostics);
            var group = ResolveFrameDebuggerGroup(groups, groupIndex, ReadString(args, "groupName"));
            NoteRenamedGroup(group, ReadString(args, "groupName"), diagnostics);
            if (drawCallIndex >= group.EventIndices.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(args), DescribeDrawCallOutsideGroup(group, drawCallIndex, state));
            }

            var eventIndex = group.EventIndices[drawCallIndex];
            var capture = new DrawCallCapture
            {
                EventIndex = eventIndex,
                BeforeEventIndex = eventIndex > 0 ? eventIndex - 1 : eventIndex,
                DrawCallIndex = drawCallIndex,
                MaxDimension = Math.Max(128, Math.Min(ReadInt(args, "maxDimension", 960), 4096)),
                EventCount = state.TryGetValue("eventCount", out var countValue) ? countValue as int? : null,
                Group = group,
            };
            capture.Diagnostics.AddRange(diagnostics);
            SelectDrawCallCaptureEvent(window, capture.BeforeEventIndex);
            _drawCallCapture = capture;
            return new { success = true, pending = true, eventIndex };
        }

        private object ContinueDrawCallCapture(JToken args)
        {
            var capture = _drawCallCapture ?? throw new InvalidOperationException("No frame-debugger-drawcall-screenshot capture is running: call it with groupName or groupIndex and drawCallIndex.");
            var diagnostics = new List<string>();
            var window = PrepareFrameDebuggerForInspection(args, diagnostics, selectEventIndex: null);
            var eventCount = GetFrameDebuggerEventCount(diagnostics);
            if (eventCount.HasValue && capture.EventCount.HasValue && eventCount.Value != capture.EventCount.Value)
            {
                capture.Diagnostics.Add($"The captured frame changed while taking the picture ({capture.EventCount.Value} -> {eventCount.Value} events): it may show another draw call. Check the event with frame-debugger-event-get, or exit Play Mode.");
                capture.EventCount = eventCount;
            }

            var screenshots = new ScreenshotBridgeService();
            if (capture.Before == null)
            {
                capture.Before = screenshots.CaptureGameView(capture.MaxDimension);
                SelectDrawCallCaptureEvent(window, capture.EventIndex);
                return new { success = true, pending = true, eventIndex = capture.EventIndex };
            }

            _drawCallCapture = null;
            var after = screenshots.CaptureGameView(capture.MaxDimension);
            if (capture.EventIndex > 0 && capture.Before.Base64 == after.Base64)
            {
                capture.Diagnostics.Add("The two halves are the same picture: the draw call changed no pixel of the camera target (drawn off screen, culled by depth, or into another target), or the Game view did not re-render; call again to make sure.");
            }

            return BuildDrawCallScreenshot(capture.Before, after, capture.EventIndex, capture.BeforeEventIndex, capture.Group, capture.DrawCallIndex, capture.MaxDimension, capture.Diagnostics);
        }

        private static void SelectDrawCallCaptureEvent(EditorWindow window, int eventIndex)
        {
            if (!TrySelectFrameDebuggerEventIndex(window, eventIndex + 1, out var error))
            {
                _drawCallCapture = null;
                throw new NotSupportedException($"Frame Debugger event selection is unsupported in this Unity version. {error}");
            }

            window.Repaint();
            EditorApplication.QueuePlayerLoopUpdate();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }

        // groupName without its "(RP n:m)" prefix found a group of another name: the picture is from that one.
        private static void NoteRenamedGroup(FrameDebuggerGroup group, string? groupName, List<string> diagnostics)
        {
            if (string.IsNullOrWhiteSpace(groupName) || string.Equals(group.Name, groupName!.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var requested = groupName!.Trim();
            // A name matched without its "(RP n:m)" prefix is a match: the reply opened with "No group named" and read as a miss.
            diagnostics.Add(string.Equals(StripRenderPassPrefix(group.Name), StripRenderPassPrefix(requested), StringComparison.OrdinalIgnoreCase)
                ? $"groupName '{requested}' matched '{group.Name}' (group {group.Index}, {group.EventIndices.Count} draw calls): the Render Graph prefix '(RP n:m)' is ignored, it renumbers passes from frame to frame."
                : $"No group named '{requested}' in this captured frame: took '{group.Name}' (group {group.Index}, {group.EventIndices.Count} draw calls), whose name contains it.");
        }

        private ImageResult CaptureFrameDebuggerDrawCallInOneCall(JToken args)
        {
            var diagnostics = new List<string>();
            var groupIndex = ReadInt(args, "groupIndex", -1);
            var drawCallIndex = ReadInt(args, "drawCallIndex", -1);
            if (drawCallIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(args), "drawCallIndex must be zero or greater.");
            }

            RequireFrameDebuggerGroupArgument(args);
            var window = PrepareFrameDebuggerForInspection(args, diagnostics, selectEventIndex: null);
            var state = CreateFrameDebuggerState(window, diagnostics);
            var groups = CreateFrameDebuggerGroups(state, diagnostics);
            var group = ResolveFrameDebuggerGroup(groups, groupIndex, ReadString(args, "groupName"));
            NoteRenamedGroup(group, ReadString(args, "groupName"), diagnostics);
            if (drawCallIndex >= group.EventIndices.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(args), DescribeDrawCallOutsideGroup(group, drawCallIndex, state));
            }

            var eventIndex = group.EventIndices[drawCallIndex];
            var beforeEventIndex = eventIndex > 0 ? eventIndex - 1 : eventIndex;
            var maxDimension = Math.Max(128, Math.Min(ReadInt(args, "maxDimension", 960), 4096));
            var screenshots = new ScreenshotBridgeService();

            SelectFrameDebuggerEventForCapture(window, beforeEventIndex, diagnostics);
            var before = screenshots.CaptureGameView(maxDimension);
            SelectFrameDebuggerEventForCapture(window, eventIndex, diagnostics);
            var after = screenshots.CaptureGameView(maxDimension);
            return BuildDrawCallScreenshot(before, after, eventIndex, beforeEventIndex, group, drawCallIndex, maxDimension, diagnostics);
        }

        private ImageResult BuildDrawCallScreenshot(ImageResult before, ImageResult after, int eventIndex, int beforeEventIndex, FrameDebuggerGroup group, int drawCallIndex, int maxDimension, List<string> diagnostics)
        {
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

        // In Play Mode the game can render a different frame on some repaints (a second camera's group in front, an extra
        // group at the end), so a group number from groups-list can point to another group: a name stays put.
        private static FrameDebuggerGroup ResolveFrameDebuggerGroup(FrameDebuggerGroup[] groups, int groupIndex, string? groupName)
        {
            if (!string.IsNullOrWhiteSpace(groupName))
            {
                // Render Graph numbers its passes per frame ("(RP 1:0) DrawOpaqueObjects" becomes "(RP 2:0) ..." when a pass
                // comes and goes), so a name copied from an earlier reply is compared without that prefix; of several, the biggest.
                var bareName = StripRenderPassPrefix(groupName!);
                var named = groups.FirstOrDefault(group => string.Equals(group.Name, groupName, StringComparison.OrdinalIgnoreCase))
                    ?? groups.Where(group => string.Equals(StripRenderPassPrefix(group.Name), bareName, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(group => group.EventIndices.Count).FirstOrDefault()
                    ?? groups.Where(group => StripRenderPassPrefix(group.Name).IndexOf(bareName, StringComparison.OrdinalIgnoreCase) >= 0)
                        .OrderByDescending(group => group.EventIndices.Count).FirstOrDefault();
                return named ?? throw new ArgumentException(
                    $"No Frame Debugger group named '{groupName}' in the captured frame ({groups.Length} groups): "
                    + string.Join(", ", groups.Take(12).Select(group => "'" + group.Name + "'")) + (groups.Length > 12 ? ", ..." : string.Empty));
            }

            return ResolveFrameDebuggerGroup(groups, groupIndex);
        }

        // The schemas require neither argument: one of the two names the group. Checked before the Frame Debugger is turned
        // on, so a call without both does not enable it (and pause Play) only to ask for a repeat that fails again.
        private static void RequireFrameDebuggerGroupArgument(JToken args)
        {
            if (string.IsNullOrWhiteSpace(ReadString(args, "groupName")) && ReadInt(args, "groupIndex", -1) == -1)
            {
                throw new ArgumentException("Pass groupName (a group name from frame-debugger-groups-list) or groupIndex.");
            }
        }

        private static readonly System.Text.RegularExpressions.Regex RenderPassPrefix = new(@"^\(RP \d+:\d+\)\s*", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        private static string StripRenderPassPrefix(string name)
        {
            return RenderPassPrefix.Replace(name.Trim(), string.Empty);
        }

        private static string DescribeDrawCallOutsideGroup(FrameDebuggerGroup group, int drawCallIndex, Dictionary<string, object?> state)
        {
            var eventCount = state.TryGetValue("eventCount", out var count) ? count : null;
            var text = $"drawCallIndex {drawCallIndex} is outside group {group.Index} '{group.Name}' draw calls 0..{group.EventIndices.Count - 1} (the captured frame has {eventCount} events).";
            return EditorApplication.isPlaying
                ? text + " In Play Mode repaints can capture a different frame with other groups: pass groupName instead of groupIndex, or list groups again."
                : text;
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

        // The Frame Debugger re-renders the Game view for a newly selected event only on a later editor tick, so both
        // captures of one call were the same stale picture (in Play Mode the finished frame with its UI) while the reply
        // named the draw call. The server runs it in steps, as pick-pixel: start selects the event before the draw call,
        // the first next captures it and selects the draw call, the second next captures that and answers.
        private sealed class DrawCallCapture
        {
            public int EventIndex { get; set; }
            public int BeforeEventIndex { get; set; }
            public int DrawCallIndex { get; set; }
            public int MaxDimension { get; set; }
            public int? EventCount { get; set; }
            public FrameDebuggerGroup Group { get; set; } = null!;
            public List<string> Diagnostics { get; } = new();
            public ImageResult? Before { get; set; }
        }

        private static DrawCallCapture? _drawCallCapture;

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
    }
}
