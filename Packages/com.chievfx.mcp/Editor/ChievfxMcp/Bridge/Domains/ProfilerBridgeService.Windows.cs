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
            // Opening the window applies its own Record toggle to the profiler: a stopped recording started again and kept
            // growing the capture after profiler-stop-recording.
            var wasRecording = GetProfilerEnabled();
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
            // The window may also keep its Record button pressed and switch recording on later, after this reply (its first
            // draw while the editor is in the background); a pressed button also hides the frame data in the window.
            if (GetProfilerEnabled() != wasRecording || IsWindowSetToRecord(window) is bool windowRecords && windowRecords != wasRecording)
            {
                SetProfilerEnabled(wasRecording);
                diagnostics.Add(GetProfilerEnabled() == wasRecording
                    ? $"Opening the Profiler window switched recording {(wasRecording ? "off" : "on")} (its Record button); it is {(wasRecording ? "on" : "off")} again as before."
                    : $"Opening the Profiler window switched recording {(wasRecording ? "off" : "on")} (its Record button) and it stayed so: use profiler-{(wasRecording ? "start" : "stop")}-recording.");
            }

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

            // The rest of the call (open, latest, frame) still applies: module selection is missing in some Unity versions.
            if (!string.IsNullOrWhiteSpace(moduleIdentifier)
                && !TrySelectProfilerModule(window, moduleIdentifier!, out var moduleError))
            {
                diagnostics.Add($"Module not switched: profiler module selection is unsupported in this Unity version ({moduleError}); the selected module is in profiler.selectedModuleIdentifier.");
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

        private static bool? IsWindowSetToRecord(EditorWindow window)
        {
            if (TryInvokeReflectedMethod(window, "IsSetToRecord", Array.Empty<object?>(), out var result, out _) && result is bool isSet)
            {
                return isSet;
            }

            return window.GetType().GetField("m_Recording", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window) as bool?;
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
            RequireFrameDebuggerGroupArgument(args);
            var window = PrepareFrameDebuggerForInspection(args, diagnostics, selectEventIndex: null);
            var state = CreateFrameDebuggerState(window, diagnostics);
            var groups = CreateFrameDebuggerGroups(state, diagnostics);
            // groupName as in drawcall-get, which in Play Mode advises it over groupIndex: here it was dropped and the reply
            // said "groupIndex -1 is outside available Frame Debugger groups".
            var group = ResolveFrameDebuggerGroup(groups, ReadInt(args, "groupIndex", -1), ReadString(args, "groupName"));
            NoteRenamedGroup(group, ReadString(args, "groupName"), diagnostics);
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
    }
}
