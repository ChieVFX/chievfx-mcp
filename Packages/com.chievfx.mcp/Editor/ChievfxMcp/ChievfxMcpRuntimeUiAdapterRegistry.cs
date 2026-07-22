#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Chievfx.Mcp.Editor
{
    internal interface IChievfxMcpRuntimeUiAdapter
    {
        string FrameworkId { get; }

        string FrameworkName { get; }

        int Priority { get; }

        bool Available { get; }

        object? Status { get; }

        IEnumerable<string> Resources { get; }

        object? ProbeScreenPosition(JToken request);
    }

    /// <summary>
    /// Optional capability implemented by runtime UI adapters that can focus a runtime text
    /// field and type text into it. Enables the shared cross-framework "ui-runtime-type-text" tool.
    /// </summary>
    internal interface IChievfxMcpRuntimeUiTextInputAdapter
    {
        /// <summary>
        /// Focuses the resolved runtime text field (when requested) and types into it.
        /// Must not mutate state when the request is a dry run or when no text field resolves.
        /// When <paramref name="requireTarget"/> is true the adapter throws if it cannot resolve a
        /// text field; otherwise it returns a result whose "resolved" flag is false.
        /// </summary>
        object? TypeIntoFocusedTextField(JToken request, bool requireTarget);
    }

    /// <summary>
    /// Optional capability for listing enabled, on-screen clickable controls in a framework.
    /// </summary>
    internal interface IChievfxMcpRuntimeUiControlFindAdapter
    {
        object? FindControls(JToken request);
    }

    /// <summary>
    /// Optional capability for clicking runtime UI at a screen position or explicit target.
    /// Enables the shared cross-framework "ui-runtime-click" tool.
    /// </summary>
    internal interface IChievfxMcpRuntimeUiClickAdapter
    {
        object? ClickAtPosition(JToken request);
    }

    /// <summary>
    /// Optional capability for dragging runtime UI at a screen position or explicit target.
    /// Enables the shared cross-framework "ui-runtime-drag" tool.
    /// </summary>
    internal interface IChievfxMcpRuntimeUiDragAdapter
    {
        object? DragAtPosition(JToken request);
    }

    /// <summary>
    /// Optional capability for setting runtime control values (slider, toggle, dropdown, etc.).
    /// Enables the shared cross-framework "ui-runtime-set-control-value" tool.
    /// </summary>
    internal interface IChievfxMcpRuntimeUiSetControlValueAdapter
    {
        object? SetControlValue(JToken request, bool requireTarget);
    }

    /// <summary>
    /// Optional capability for focusing or clearing focus on runtime UI controls.
    /// Enables shared "ui-runtime-focus" and "ui-runtime-clear-focus" tools.
    /// </summary>
    internal interface IChievfxMcpRuntimeUiFocusAdapter
    {
        object? Focus(JToken request, bool requireTarget);

        object? ClearFocus(JToken request);
    }

    internal static class ChievfxMcpRuntimeUiAdapterRegistry
    {
        private const string ExtensionId = "chievfx.runtime-ui";
        private const string CommonCategory = "ui-runtime-common";
        private const string EssentialsCategory = "essentials";
        private const string UriPrefix = "chievfx://extensions/chievfx.runtime-ui/";
        private const string StatusUri = UriPrefix + "status";
        private const string ProbeToolName = "ui-runtime-probe";
        private const string TypeTextToolName = "ui-runtime-type-text";
        internal const string ClickToolName = "ui-runtime-click";
        internal const string DragToolName = "ui-runtime-drag";
        internal const string SetControlValueToolName = "ui-runtime-set-control-value";
        internal const string FocusToolName = "ui-runtime-focus";
        internal const string ClearFocusToolName = "ui-runtime-clear-focus";
        private const int AdapterProbeMaxRows = 1024;

        private static readonly Regex FrameworkIdPattern = new(@"^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.Compiled);
        private static readonly object SyncRoot = new();
        private static readonly Dictionary<string, RegisteredAdapter> Adapters = new(StringComparer.Ordinal);
        private static int nextRegistrationOrder;

        static ChievfxMcpRuntimeUiAdapterRegistry()
        {
            ChievfxMcpExtensionRegistry.RegisterExtension(CreateDescriptor());
        }

        public static void EnsureRegistered()
        {
        }

        public static void Register(IChievfxMcpRuntimeUiAdapter adapter)
        {
            if (adapter == null)
            {
                throw new ArgumentNullException(nameof(adapter));
            }

            if (string.IsNullOrWhiteSpace(adapter.FrameworkId) || !FrameworkIdPattern.IsMatch(adapter.FrameworkId))
            {
                throw new InvalidOperationException($"Runtime UI adapter framework id '{adapter.FrameworkId}' is invalid.");
            }

            lock (SyncRoot)
            {
                Adapters[adapter.FrameworkId] = new RegisteredAdapter(adapter, nextRegistrationOrder++);
            }
        }

        public static bool Unregister(string frameworkId)
        {
            lock (SyncRoot)
            {
                return Adapters.Remove(frameworkId);
            }
        }

        private static ChievfxMcpExtensionDescriptor CreateDescriptor()
        {
            var descriptor = new ChievfxMcpExtensionDescriptor
            {
                Id = ExtensionId,
                DisplayName = "ChievFX MCP Runtime UI",
                Version = "0.1.0",
                Description = "Shared runtime UI adapter registry and merged screen-position probe.",
                ToolRunner = RunTool,
                ResourceReader = ReadResource,
            };
            descriptor.Resources.Add(new ChievfxMcpResourceDescriptor
            {
                Id = "runtime-ui-status",
                Uri = StatusUri,
                Name = "Runtime UI adapter status",
                Description = "Compact runtime UI capability summary, current hierarchy counts, and Play Mode drill-down hints.",
                MimeType = "application/json",
                Category = EssentialsCategory,
            });
            descriptor.Tools.Add(new ChievfxMcpToolDescriptor
            {
                Name = ProbeToolName,
                Description = "Probe Play Mode runtime UI hit stack at a screen position. Bottom-left origin is (0,0); top-right is screen size in pixels or (1,1) when isNormalized is true. Returns up to 10 hits per page; pass page to fetch more. Requires Play Mode.",
                Category = CommonCategory,
                InputSchema = RuntimeProbeSchema(),
            });
            descriptor.Tools.Add(new ChievfxMcpToolDescriptor
            {
                Name = TypeTextToolName,
                Description = "Focus a Play Mode text field and type text into it like a player. Works for uGUI InputField/TMP_InputField and UI Toolkit TextField; auto-detects the framework or use framework to force one. Requires Play Mode.",
                Category = CommonCategory,
                InputSchema = TypeTextSchema(),
            });
            return descriptor;
        }

        private static object? RunTool(string toolName, JToken args)
        {
            return toolName switch
            {
                ProbeToolName => ProbeScreenPosition("tool://" + ProbeToolName, args),
                TypeTextToolName => TypeText("tool://" + TypeTextToolName, args),
                _ => throw new InvalidOperationException($"Unknown runtime UI registry tool '{toolName}'."),
            };
        }

        private static object? TypeText(string uri, JToken args)
        {
            var request = args is JObject obj ? obj : new JObject();
            ChievfxMcpRuntimeUiInteractionInput.EnsureTargetOrScreenPosition(request, "ui-runtime-type-text");
            var framework = (request["framework"]?.Value<string>() ?? string.Empty).Trim().ToLowerInvariant();
            var candidates = SnapshotAdapters()
                .Where(registered => registered.Adapter is IChievfxMcpRuntimeUiTextInputAdapter)
                .ToArray();
            if (candidates.Length == 0)
            {
                throw new InvalidOperationException("No runtime UI adapter supports text input.");
            }

            if (!string.IsNullOrEmpty(framework) && !string.Equals(framework, "auto", StringComparison.Ordinal))
            {
                var selected = candidates.FirstOrDefault(registered => string.Equals(registered.Adapter.FrameworkId, framework, StringComparison.Ordinal));
                if (selected == null)
                {
                    throw new ArgumentException($"No text-input adapter for framework '{framework}'. Available: {string.Join(", ", candidates.Select(registered => registered.Adapter.FrameworkId))}.");
                }

                if (!selected.Adapter.Available)
                {
                    throw new InvalidOperationException($"Text-input adapter '{framework}' is registered but unavailable.");
                }

                var forced = ((IChievfxMcpRuntimeUiTextInputAdapter)selected.Adapter).TypeIntoFocusedTextField(request.DeepClone(), requireTarget: true);
                return WrapTypeTextResult(uri, selected.Adapter, forced);
            }

            var attempts = new List<Dictionary<string, object?>>();
            foreach (var registered in candidates)
            {
                if (!registered.Adapter.Available)
                {
                    attempts.Add(new Dictionary<string, object?> { ["framework"] = registered.Adapter.FrameworkId, ["available"] = false });
                    continue;
                }

                object? result;
                try
                {
                    result = ((IChievfxMcpRuntimeUiTextInputAdapter)registered.Adapter).TypeIntoFocusedTextField(request.DeepClone(), requireTarget: false);
                }
                catch (Exception ex)
                {
                    attempts.Add(new Dictionary<string, object?> { ["framework"] = registered.Adapter.FrameworkId, ["error"] = RootMessage(ex) });
                    continue;
                }

                if (ReadResolvedFlag(result))
                {
                    return WrapTypeTextResult(uri, registered.Adapter, result);
                }

                attempts.Add(new Dictionary<string, object?> { ["framework"] = registered.Adapter.FrameworkId, ["resolved"] = false, ["detail"] = result });
            }

            return new Dictionary<string, object?>
            {
                ["uri"] = uri,
                ["resolved"] = false,
                ["framework"] = null,
                ["warnings"] = new[]
                {
                    ChievfxMcpRuntimeUiInteractionInput.HasExplicitTargetInput(request)
                        ? ChievfxMcpRuntimeUiInteractionInput.FormatCrossFrameworkTargetNotFoundMessage(request)
                        : "No uGUI or UI Toolkit text field resolved from the supplied screen position. Provide path/instanceId/name/visualElementRef or x/y over a focusable text field, or set framework explicitly.",
                },
                ["attempts"] = ChievfxMcpRuntimeUiInteractionInput.HasExplicitTargetInput(request)
                    ? Array.Empty<Dictionary<string, object?>>()
                    : attempts.ToArray(),
            };
        }

        private static Dictionary<string, object?> WrapTypeTextResult(string uri, IChievfxMcpRuntimeUiAdapter adapter, object? result)
        {
            if (!TryReadDictionary(result, out var row))
            {
                row = new Dictionary<string, object?> { ["result"] = result };
            }

            row["uri"] = uri;
            row["framework"] = adapter.FrameworkId;
            return row;
        }

        internal static object? RuntimeSetControlValue(JToken args)
        {
            var request = args is JObject obj ? obj : new JObject();
            ChievfxMcpRuntimeUiInteractionInput.EnsureTargetOrScreenPosition(request, "ui-runtime-set-control-value");
            var framework = (request["framework"]?.Value<string>() ?? string.Empty).Trim().ToLowerInvariant();
            var candidates = SnapshotAdapters()
                .Where(registered => registered.Adapter is IChievfxMcpRuntimeUiSetControlValueAdapter)
                .ToArray();
            if (candidates.Length == 0)
            {
                throw new InvalidOperationException("No runtime UI adapter supports set-control-value.");
            }

            if (!string.IsNullOrEmpty(framework) && !string.Equals(framework, "auto", StringComparison.Ordinal))
            {
                var selected = candidates.FirstOrDefault(registered => string.Equals(registered.Adapter.FrameworkId, framework, StringComparison.Ordinal));
                if (selected == null)
                {
                    throw new ArgumentException($"No set-control-value adapter for framework '{framework}'. Available: {string.Join(", ", candidates.Select(registered => registered.Adapter.FrameworkId))}.");
                }

                if (!selected.Adapter.Available)
                {
                    throw new InvalidOperationException($"Set-control-value adapter '{framework}' is registered but unavailable.");
                }

                var forced = ((IChievfxMcpRuntimeUiSetControlValueAdapter)selected.Adapter).SetControlValue(request.DeepClone(), requireTarget: true);
                return WrapSetControlValueResult(forced, selected.Adapter);
            }

            var attempts = new List<Dictionary<string, object?>>();
            foreach (var registered in candidates)
            {
                if (!registered.Adapter.Available)
                {
                    attempts.Add(new Dictionary<string, object?> { ["framework"] = registered.Adapter.FrameworkId, ["available"] = false });
                    continue;
                }

                object? result;
                try
                {
                    result = ((IChievfxMcpRuntimeUiSetControlValueAdapter)registered.Adapter).SetControlValue(request.DeepClone(), requireTarget: false);
                }
                catch (ArgumentException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    attempts.Add(new Dictionary<string, object?> { ["framework"] = registered.Adapter.FrameworkId, ["error"] = RootMessage(ex) });
                    continue;
                }

                if (ReadResolvedFlag(result))
                {
                    return WrapSetControlValueResult(result, registered.Adapter);
                }

                attempts.Add(new Dictionary<string, object?> { ["framework"] = registered.Adapter.FrameworkId, ["resolved"] = false, ["detail"] = result });
            }

            return new Dictionary<string, object?>
            {
                ["uri"] = "tool://" + SetControlValueToolName,
                ["resolved"] = false,
                ["framework"] = null,
                ["warnings"] = new[]
                {
                    ChievfxMcpRuntimeUiInteractionInput.HasExplicitTargetInput(request)
                        ? ChievfxMcpRuntimeUiInteractionInput.FormatCrossFrameworkTargetNotFoundMessage(request)
                        : "No uGUI or UI Toolkit settable control resolved from the supplied screen position. Provide path/instanceId or x/y over a Slider, Toggle, Dropdown, or other writable control, or set framework explicitly.",
                },
                ["attempts"] = ChievfxMcpRuntimeUiInteractionInput.HasExplicitTargetInput(request)
                    ? Array.Empty<Dictionary<string, object?>>()
                    : attempts.ToArray(),
            };
        }

        private static Dictionary<string, object?> WrapSetControlValueResult(object? result, IChievfxMcpRuntimeUiAdapter adapter)
        {
            if (!TryReadDictionary(result, out var row))
            {
                row = new Dictionary<string, object?> { ["result"] = result };
            }

            row["uri"] = "tool://" + SetControlValueToolName;
            row["framework"] = adapter.FrameworkId;
            if (!row.ContainsKey("resolved"))
            {
                row["resolved"] = true;
            }

            return row;
        }

        internal static object? RuntimeFocus(JToken args)
        {
            ChievfxMcpRuntimeUiProbeCompact.EnsurePlayModeForProbe(EditorApplication.isPlaying || Application.isPlaying);

            var request = args is JObject obj ? obj : new JObject();
            ChievfxMcpRuntimeUiInteractionInput.EnsureTargetOrScreenPosition(request, "ui-runtime-focus");
            var framework = (request["framework"]?.Value<string>() ?? string.Empty).Trim().ToLowerInvariant();
            var candidates = SnapshotAdapters()
                .Where(registered => registered.Adapter is IChievfxMcpRuntimeUiFocusAdapter)
                .ToArray();
            if (candidates.Length == 0)
            {
                throw new InvalidOperationException("No runtime UI adapter supports focus.");
            }

            if (!string.IsNullOrEmpty(framework) && !string.Equals(framework, "auto", StringComparison.Ordinal))
            {
                var selected = candidates.FirstOrDefault(registered => string.Equals(registered.Adapter.FrameworkId, framework, StringComparison.Ordinal));
                if (selected == null)
                {
                    throw new ArgumentException($"No focus adapter for framework '{framework}'. Available: {string.Join(", ", candidates.Select(registered => registered.Adapter.FrameworkId))}.");
                }

                if (!selected.Adapter.Available)
                {
                    throw new InvalidOperationException($"Focus adapter '{framework}' is registered but unavailable.");
                }

                var forced = ((IChievfxMcpRuntimeUiFocusAdapter)selected.Adapter).Focus(request.DeepClone(), requireTarget: true);
                return WrapFocusResult(forced, selected.Adapter);
            }

            var attempts = new List<Dictionary<string, object?>>();
            foreach (var registered in candidates)
            {
                if (!registered.Adapter.Available)
                {
                    attempts.Add(new Dictionary<string, object?> { ["framework"] = registered.Adapter.FrameworkId, ["available"] = false });
                    continue;
                }

                object? result;
                try
                {
                    result = ((IChievfxMcpRuntimeUiFocusAdapter)registered.Adapter).Focus(request.DeepClone(), requireTarget: false);
                }
                catch (Exception ex)
                {
                    attempts.Add(new Dictionary<string, object?> { ["framework"] = registered.Adapter.FrameworkId, ["error"] = RootMessage(ex) });
                    continue;
                }

                if (ReadResolvedFlag(result))
                {
                    return WrapFocusResult(result, registered.Adapter);
                }

                attempts.Add(new Dictionary<string, object?> { ["framework"] = registered.Adapter.FrameworkId, ["resolved"] = false, ["detail"] = result });
            }

            return new Dictionary<string, object?>
            {
                ["uri"] = "tool://" + FocusToolName,
                ["resolved"] = false,
                ["framework"] = null,
                ["playMode"] = EditorApplication.isPlaying || Application.isPlaying,
                ["warnings"] = new[]
                {
                    ChievfxMcpRuntimeUiInteractionInput.HasExplicitTargetInput(request)
                        ? ChievfxMcpRuntimeUiInteractionInput.FormatCrossFrameworkTargetNotFoundMessage(request)
                        : "No uGUI or UI Toolkit focus target resolved from the supplied screen position. Provide path/instanceId or x/y over a focusable control, or set framework explicitly.",
                },
                ["attempts"] = ChievfxMcpRuntimeUiInteractionInput.HasExplicitTargetInput(request)
                    ? Array.Empty<Dictionary<string, object?>>()
                    : attempts.ToArray(),
            };
        }

        internal static object? RuntimeClearFocus(JToken args)
        {
            ChievfxMcpRuntimeUiProbeCompact.EnsurePlayModeForProbe(EditorApplication.isPlaying || Application.isPlaying);

            var request = args is JObject obj ? obj : new JObject();
            var framework = (request["framework"]?.Value<string>() ?? string.Empty).Trim().ToLowerInvariant();
            var warnings = new List<string>();
            var clearAdapters = SnapshotAdapters()
                .Where(registered => registered.Adapter is IChievfxMcpRuntimeUiFocusAdapter)
                .Where(registered => ChievfxMcpRuntimeUiControlFind.MatchesFrameworkFilter(framework, registered.Adapter.FrameworkId))
                .ToArray();

            if (!ChievfxMcpRuntimeUiControlFind.IncludesAllFrameworks(framework) && clearAdapters.Length == 0)
            {
                throw new ArgumentException(
                    $"No clear-focus adapter for framework '{framework}'. Available: {string.Join(", ", SnapshotAdapters().Where(registered => registered.Adapter is IChievfxMcpRuntimeUiFocusAdapter).Select(registered => registered.Adapter.FrameworkId))}.");
            }

            var sections = new List<Dictionary<string, object?>>();
            var anyCleared = false;

            foreach (var registered in clearAdapters)
            {
                var adapter = registered.Adapter;
                if (!adapter.Available)
                {
                    warnings.Add($"Framework '{adapter.FrameworkId}' is unavailable.");
                    sections.Add(CreateClearFocusSection(adapter.FrameworkId, available: false, cleared: false, detail: null));
                    continue;
                }

                Dictionary<string, object?>? detail;
                try
                {
                    detail = ReadFocusDetail(((IChievfxMcpRuntimeUiFocusAdapter)adapter).ClearFocus(request));
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"ui-runtime-clear-focus failed for framework '{adapter.FrameworkId}': {RootMessage(ex)}", ex);
                }

                var cleared = ReadClearedFlag(detail);
                if (cleared)
                {
                    anyCleared = true;
                }

                sections.Add(CreateClearFocusSection(adapter.FrameworkId, available: true, cleared: cleared, detail: detail));
            }

            return new Dictionary<string, object?>
            {
                ["uri"] = "tool://" + ClearFocusToolName,
                ["playMode"] = EditorApplication.isPlaying || Application.isPlaying,
                ["frameworkFilter"] = string.IsNullOrEmpty(framework) ? "all" : framework,
                ["anyCleared"] = anyCleared,
                ["frameworks"] = sections.ToArray(),
                ["warnings"] = warnings.Where(warning => !string.IsNullOrWhiteSpace(warning)).Distinct().ToArray(),
            };
        }

        private static Dictionary<string, object?> WrapFocusResult(object? result, IChievfxMcpRuntimeUiAdapter adapter)
        {
            if (!TryReadDictionary(result, out var row))
            {
                row = new Dictionary<string, object?> { ["result"] = result };
            }

            row["uri"] = "tool://" + FocusToolName;
            row["framework"] = adapter.FrameworkId;
            if (!row.ContainsKey("resolved"))
            {
                row["resolved"] = true;
            }

            return row;
        }

        private static Dictionary<string, object?>? ReadFocusDetail(object? result)
        {
            return TryReadDictionary(result, out var detail) ? detail : null;
        }

        private static bool ReadClearedFlag(object? result)
        {
            return TryReadDictionary(result, out var row)
                && row.TryGetValue("cleared", out var cleared)
                && cleared is bool clearedValue
                && clearedValue;
        }

        private static Dictionary<string, object?> CreateClearFocusSection(
            string frameworkId,
            bool available,
            bool cleared,
            Dictionary<string, object?>? detail)
        {
            var section = new Dictionary<string, object?>
            {
                ["framework"] = frameworkId,
                ["available"] = available,
                ["cleared"] = cleared,
            };

            if (detail == null)
            {
                return section;
            }

            if (detail.TryGetValue("focusBefore", out var focusBefore))
            {
                section["focusBefore"] = focusBefore;
            }

            if (detail.TryGetValue("focusAfter", out var focusAfter))
            {
                section["focusAfter"] = focusAfter;
            }

            if (detail.TryGetValue("selectedBefore", out var selectedBefore))
            {
                section["selectedBefore"] = selectedBefore;
            }

            if (detail.TryGetValue("selectedAfter", out var selectedAfter))
            {
                section["selectedAfter"] = selectedAfter;
            }

            if (detail.TryGetValue("events", out var events))
            {
                section["events"] = events;
            }

            return section;
        }

        private static bool ReadResolvedFlag(object? result)
        {
            return TryReadDictionary(result, out var row)
                && row.TryGetValue("resolved", out var resolved)
                && resolved is bool resolvedValue
                && resolvedValue;
        }

        private static bool ReadHandlerInvokedFlag(object? result)
        {
            return TryReadDictionary(result, out var row)
                && row.TryGetValue("handlerInvoked", out var invoked)
                && invoked is bool invokedValue
                && invokedValue;
        }

        internal static string ReadRuntimeClickHandler(JToken args)
        {
            var raw = ReadString(args, "handler") ?? ReadString(args, "sequence") ?? "pointerClick";
            if (string.Equals(raw, "pointerClick", StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw, "pointer", StringComparison.OrdinalIgnoreCase))
            {
                return "pointerClick";
            }

            if (string.Equals(raw, "submit", StringComparison.OrdinalIgnoreCase))
            {
                return "submit";
            }

            throw new ArgumentException($"Unknown click handler '{raw}'. Use pointerClick or submit.");
        }

        internal readonly struct RuntimeDragScreenGeometry
        {
            public RuntimeDragScreenGeometry(RuntimeScreenPosition start, RuntimeScreenPosition end, Vector2 screenDelta)
            {
                Start = start;
                End = end;
                ScreenDelta = screenDelta;
            }

            public RuntimeScreenPosition Start { get; }

            public RuntimeScreenPosition End { get; }

            public Vector2 ScreenDelta { get; }
        }

        internal static RuntimeDragScreenGeometry ReadRuntimeDragGeometry(JToken args, List<string> warnings)
        {
            var isNormalized = ReadBool(args, "isNormalized", false);
            var screenshotSpace = IsScreenshotSpace(args);
            var screenSize = new Vector2(Mathf.Max(1, Screen.width), Mathf.Max(1, Screen.height));
            RuntimeScreenPosition start;
            if (TryReadDragScreenPoint(args, "x", "y", "startScreenPosition", "startNormalized", isNormalized, screenshotSpace, screenSize, out var startScreen))
            {
                start = ToRuntimeScreenPosition(startScreen, screenSize);
            }
            else
            {
                throw new ArgumentException(
                    "ui-runtime-drag requires x/y start coordinates (or legacy startScreenPosition/startNormalized).");
            }

            RuntimeScreenPosition end;
            if (TryReadDragScreenPoint(args, "toX", "toY", "endScreenPosition", "endNormalized", isNormalized, screenshotSpace, screenSize, out var endScreen))
            {
                end = ToRuntimeScreenPosition(endScreen, screenSize);
            }
            else if (TryReadDragScreenDelta(args, isNormalized, screenSize, out var screenDelta))
            {
                endScreen = startScreen + screenDelta;
                end = ToRuntimeScreenPosition(endScreen, screenSize);
            }
            else
            {
                throw new ArgumentException("ui-runtime-drag requires toX/toY or deltaX/deltaY (or legacy endScreenPosition/endNormalized or delta:{x,y}).");
            }

            var delta = end.ScreenPosition - start.ScreenPosition;
            return new RuntimeDragScreenGeometry(start, end, delta);
        }

        internal static object? RuntimeDrag(JToken args)
        {
            ChievfxMcpRuntimeUiProbeCompact.EnsurePlayModeForProbe(EditorApplication.isPlaying || Application.isPlaying);

            var request = args is JObject obj ? obj : new JObject();
            var framework = (request["framework"]?.Value<string>() ?? string.Empty).Trim().ToLowerInvariant();
            ChievfxMcpRuntimeUiInteractionInput.EnsureTargetOrScreenPosition(request, "ui-runtime-drag");
            var explicitTargetMode = ChievfxMcpRuntimeUiInteractionInput.HasExplicitTargetInput(request);
            var warnings = new List<string>();
            var geometry = ReadRuntimeDragGeometry(request, warnings);
            var dragAdapters = SnapshotAdapters()
                .Where(registered => registered.Adapter is IChievfxMcpRuntimeUiDragAdapter)
                .Where(registered => ChievfxMcpRuntimeUiControlFind.MatchesFrameworkFilter(framework, registered.Adapter.FrameworkId))
                .ToArray();

            if (!ChievfxMcpRuntimeUiControlFind.IncludesAllFrameworks(framework) && dragAdapters.Length == 0)
            {
                throw new ArgumentException(
                    $"No drag adapter for framework '{framework}'. Available: {string.Join(", ", SnapshotAdapters().Where(registered => registered.Adapter is IChievfxMcpRuntimeUiDragAdapter).Select(registered => registered.Adapter.FrameworkId))}.");
            }

            var sections = new List<Dictionary<string, object?>>();
            var anyResolved = false;
            var anyDragged = false;

            foreach (var registered in dragAdapters)
            {
                var adapter = registered.Adapter;
                if (!adapter.Available)
                {
                    warnings.Add($"Framework '{adapter.FrameworkId}' is unavailable.");
                    sections.Add(CreateDragSection(adapter.FrameworkId, available: false, resolved: false, dragged: false, detail: null));
                    continue;
                }

                Dictionary<string, object?>? dragDetail;
                try
                {
                    dragDetail = ReadDragDetail(((IChievfxMcpRuntimeUiDragAdapter)adapter).DragAtPosition(request));
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"ui-runtime-drag failed for framework '{adapter.FrameworkId}': {RootMessage(ex)}", ex);
                }

                var resolved = ReadResolvedFlag(dragDetail);
                var dragged = resolved;
                if (resolved)
                {
                    anyResolved = true;
                    anyDragged = true;
                }

                sections.Add(CreateDragSection(adapter.FrameworkId, available: true, resolved: resolved, dragged: dragged, detail: dragDetail));
            }

            if (explicitTargetMode)
            {
                if (!anyResolved)
                {
                    throw new ArgumentException(ChievfxMcpRuntimeUiInteractionInput.FormatCrossFrameworkTargetNotFoundMessage(request));
                }

                sections = sections.Where(section => section.TryGetValue("resolved", out var resolvedFlag) && resolvedFlag is true).ToList();
            }
            else if (!ChievfxMcpRuntimeUiControlFind.IncludesAllFrameworks(framework) && !anyResolved)
            {
                throw new InvalidOperationException(
                    $"ui-runtime-drag could not resolve a {framework} target at the supplied start position or explicit target.");
            }
            else if (!anyResolved)
            {
                warnings.Add("No uGUI or UI Toolkit target resolved at the drag start position. Use ui-control-find or ui-runtime-probe to inspect draggable controls and coordinates.");
            }

            return new Dictionary<string, object?>
            {
                ["uri"] = "tool://" + DragToolName,
                ["playMode"] = EditorApplication.isPlaying || Application.isPlaying,
                ["frameworkFilter"] = string.IsNullOrEmpty(framework) ? "all" : framework,
                ["startCoordinateConvention"] = CreateCoordinateConvention(geometry.Start, request),
                ["endCoordinateConvention"] = CreateCoordinateConvention(geometry.End, request),
                ["screenDelta"] = new Dictionary<string, object?>
                {
                    ["x"] = RoundCoordinate(geometry.ScreenDelta.x),
                    ["y"] = RoundCoordinate(geometry.ScreenDelta.y),
                },
                ["anyResolved"] = anyResolved,
                ["anyDragged"] = anyDragged,
                ["frameworks"] = sections.ToArray(),
                ["warnings"] = warnings.Where(warning => !string.IsNullOrWhiteSpace(warning)).Distinct().ToArray(),
            };
        }

        private static bool TryReadDragScreenPoint(
            JToken args,
            string xKey,
            string yKey,
            string legacyScreenKey,
            string legacyNormalizedKey,
            bool isNormalized,
            bool screenshotSpace,
            Vector2 screenSize,
            out Vector2 screenPoint)
        {
            if (screenshotSpace)
            {
                if (args[xKey] != null && args[yKey] != null)
                {
                    // Normalized 0..1 from the screenshot PNG (top-left origin); flip Y to bottom-left Screen space.
                    var nx = Mathf.Clamp01(ReadFloat(args, xKey, 0f));
                    var ny = Mathf.Clamp01(ReadFloat(args, yKey, 0f));
                    screenPoint = new Vector2(nx * screenSize.x, (1f - ny) * screenSize.y);
                    return true;
                }

                screenPoint = default;
                return false;
            }

            if (args[xKey] != null || args[yKey] != null)
            {
                var x = ReadFloat(args, xKey, 0f);
                var y = ReadFloat(args, yKey, 0f);
                screenPoint = isNormalized
                    ? new Vector2(x * screenSize.x, y * screenSize.y)
                    : new Vector2(x, y);
                return true;
            }

            if (TryReadVector2(args[legacyScreenKey], out screenPoint))
            {
                return true;
            }

            if (TryReadVector2(args[legacyNormalizedKey], out var normalized))
            {
                screenPoint = new Vector2(normalized.x * screenSize.x, normalized.y * screenSize.y);
                return true;
            }

            screenPoint = default;
            return false;
        }

        private static bool TryReadDragScreenDelta(JToken args, bool isNormalized, Vector2 screenSize, out Vector2 screenDelta)
        {
            if (args["deltaX"] != null || args["deltaY"] != null)
            {
                var deltaX = ReadFloat(args, "deltaX", 0f);
                var deltaY = ReadFloat(args, "deltaY", 0f);
                screenDelta = isNormalized
                    ? new Vector2(deltaX * screenSize.x, deltaY * screenSize.y)
                    : new Vector2(deltaX, deltaY);
                return true;
            }

            if (TryReadVector2(args["delta"], out screenDelta))
            {
                return true;
            }

            screenDelta = default;
            return false;
        }

        private static RuntimeScreenPosition ToRuntimeScreenPosition(Vector2 screenPoint, Vector2 screenSize)
        {
            return new RuntimeScreenPosition(
                screenPoint,
                screenSize,
                new Vector2(screenPoint.x / screenSize.x, screenPoint.y / screenSize.y));
        }

        private static Dictionary<string, object?>? ReadDragDetail(object? result)
        {
            return TryReadDictionary(result, out var detail) ? detail : null;
        }

        private static Dictionary<string, object?> CreateDragSection(
            string frameworkId,
            bool available,
            bool resolved,
            bool dragged,
            Dictionary<string, object?>? detail)
        {
            var section = new Dictionary<string, object?>
            {
                ["framework"] = frameworkId,
                ["available"] = available,
                ["resolved"] = resolved,
                ["dragged"] = dragged,
            };

            if (detail == null)
            {
                return section;
            }

            if (detail.TryGetValue("target", out var target))
            {
                section["target"] = target;
            }

            if (detail.TryGetValue("intendedHandler", out var handler))
            {
                section["handler"] = handler;
            }

            if (detail.TryGetValue("dispatchedEvents", out var events))
            {
                section["events"] = events;
            }

            if (detail.TryGetValue("selectedObjectAfter", out var selectedAfter))
            {
                section["selectedAfter"] = selectedAfter;
            }

            if (detail.TryGetValue("focusedElementAfter", out var focusedAfter))
            {
                section["focusedAfter"] = focusedAfter;
            }

            if (detail.TryGetValue("targetStateAfter", out var targetStateAfter))
            {
                section["targetStateAfter"] = targetStateAfter;
            }

            return section;
        }

        internal static object? RuntimeClick(JToken args)
        {
            ChievfxMcpRuntimeUiProbeCompact.EnsurePlayModeForProbe(EditorApplication.isPlaying || Application.isPlaying);

            var request = args is JObject obj ? obj : new JObject();
            var framework = (request["framework"]?.Value<string>() ?? string.Empty).Trim().ToLowerInvariant();
            ChievfxMcpRuntimeUiInteractionInput.EnsureTargetOrScreenPosition(request, "ui-runtime-click");
            var explicitTargetMode = ChievfxMcpRuntimeUiInteractionInput.HasExplicitTargetInput(request);
            var warnings = new List<string>();
            RuntimeScreenPosition? position = null;
            if (ChievfxMcpRuntimeUiInteractionInput.HasScreenPositionInput(request))
            {
                position = ReadScreenPosition(request, warnings);
            }

            var clickAdapters = SnapshotAdapters()
                .Where(registered => registered.Adapter is IChievfxMcpRuntimeUiClickAdapter)
                .Where(registered => ChievfxMcpRuntimeUiControlFind.MatchesFrameworkFilter(framework, registered.Adapter.FrameworkId))
                .ToArray();

            if (!ChievfxMcpRuntimeUiControlFind.IncludesAllFrameworks(framework) && clickAdapters.Length == 0)
            {
                throw new ArgumentException(
                    $"No click adapter for framework '{framework}'. Available: {string.Join(", ", SnapshotAdapters().Where(registered => registered.Adapter is IChievfxMcpRuntimeUiClickAdapter).Select(registered => registered.Adapter.FrameworkId))}.");
            }

            var sections = new List<Dictionary<string, object?>>();
            var anyResolved = false;
            var anyClicked = false;

            foreach (var registered in clickAdapters)
            {
                var adapter = registered.Adapter;
                if (!adapter.Available)
                {
                    warnings.Add($"Framework '{adapter.FrameworkId}' is unavailable.");
                    sections.Add(CreateClickSection(adapter.FrameworkId, available: false, resolved: false, clicked: false, detail: null));
                    continue;
                }

                Dictionary<string, object?>? clickDetail;
                try
                {
                    clickDetail = ReadClickDetail(((IChievfxMcpRuntimeUiClickAdapter)adapter).ClickAtPosition(request));
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"ui-runtime-click failed for framework '{adapter.FrameworkId}': {RootMessage(ex)}", ex);
                }

                var resolved = ReadResolvedFlag(clickDetail);
                // clicked now means "a recognized handler actually responded" (handlerInvoked),
                // not merely "a raycast target was resolved". A resolved-but-not-clicked result
                // means the click landed on a non-interactive element with no click handler.
                var clicked = ReadHandlerInvokedFlag(clickDetail);
                if (resolved)
                {
                    anyResolved = true;
                }

                if (clicked)
                {
                    anyClicked = true;
                }

                sections.Add(CreateClickSection(adapter.FrameworkId, available: true, resolved: resolved, clicked: clicked, detail: clickDetail));
            }

            if (explicitTargetMode)
            {
                if (!anyResolved)
                {
                    throw new ArgumentException(ChievfxMcpRuntimeUiInteractionInput.FormatCrossFrameworkTargetNotFoundMessage(request));
                }

                sections = sections.Where(section => section.TryGetValue("resolved", out var resolvedFlag) && resolvedFlag is true).ToList();
            }
            else if (!ChievfxMcpRuntimeUiControlFind.IncludesAllFrameworks(framework) && !anyResolved)
            {
                throw new InvalidOperationException(
                    $"ui-runtime-click could not resolve a {framework} target at the supplied position or explicit target.");
            }
            else if (!anyResolved)
            {
                warnings.Add("No uGUI or UI Toolkit target resolved at the supplied position. Use ui-control-find or ui-runtime-probe to inspect clickable controls and coordinates.");
            }

            var result = new Dictionary<string, object?>
            {
                ["uri"] = "tool://" + ClickToolName,
                ["playMode"] = EditorApplication.isPlaying || Application.isPlaying,
                ["frameworkFilter"] = string.IsNullOrEmpty(framework) ? "all" : framework,
                ["anyResolved"] = anyResolved,
                ["anyClicked"] = anyClicked,
                ["frameworks"] = sections.ToArray(),
                ["warnings"] = warnings.Where(warning => !string.IsNullOrWhiteSpace(warning)).Distinct().ToArray(),
            };

            if (position.HasValue)
            {
                result["coordinateConvention"] = CreateCoordinateConvention(position.Value, request);
            }

            return result;
        }

        private static Dictionary<string, object?> CreateClickSection(
            string frameworkId,
            bool available,
            bool resolved,
            bool clicked,
            Dictionary<string, object?>? detail)
        {
            var section = new Dictionary<string, object?>
            {
                ["framework"] = frameworkId,
                ["available"] = available,
                ["resolved"] = resolved,
                ["clicked"] = clicked,
            };

            if (detail == null)
            {
                return section;
            }

            if (detail.TryGetValue("target", out var target))
            {
                section["target"] = target;
            }

            if (detail.TryGetValue("intendedHandler", out var handler))
            {
                section["handler"] = handler;
            }

            // The element that actually caught the click (may be an ancestor of the raycast target,
            // e.g. clicking a label inside a Button reports the Button). Null when nothing responded.
            if (detail.TryGetValue("handlerObject", out var handlerObject))
            {
                section["handlerObject"] = handlerObject;
            }

            if (detail.TryGetValue("dispatchedEvents", out var events))
            {
                section["events"] = events;
            }

            if (detail.TryGetValue("selectedObjectAfter", out var selectedAfter))
            {
                section["selectedAfter"] = selectedAfter;
            }

            if (detail.TryGetValue("focusedElementAfter", out var focusedAfter))
            {
                section["focusedAfter"] = focusedAfter;
            }

            if (detail.TryGetValue("targetStateAfter", out var targetStateAfter))
            {
                section["targetStateAfter"] = targetStateAfter;
            }

            return section;
        }

        private static Dictionary<string, object?>? ReadClickDetail(object? result)
        {
            return TryReadDictionary(result, out var detail) ? detail : null;
        }

        private static Dictionary<string, object?> CreateCoordinateConvention(RuntimeScreenPosition position, JToken request)
        {
            return new Dictionary<string, object?>
            {
                ["origin"] = "bottom-left",
                ["unit"] = "screen-pixels",
                ["xAxis"] = "right",
                ["yAxis"] = "up",
                ["screenSize"] = new Dictionary<string, object?>
                {
                    ["width"] = (int)position.ScreenSize.x,
                    ["height"] = (int)position.ScreenSize.y,
                },
                ["screenPosition"] = new Dictionary<string, object?>
                {
                    ["x"] = RoundCoordinate(position.ScreenPosition.x),
                    ["y"] = RoundCoordinate(position.ScreenPosition.y),
                },
                ["normalizedPosition"] = new Dictionary<string, object?>
                {
                    ["x"] = RoundNormalized(position.NormalizedPosition.x),
                    ["y"] = RoundNormalized(position.NormalizedPosition.y),
                },
                ["normalizedInputSupplied"] = request["normalized"] != null,
            };
        }

        private static float RoundCoordinate(float value)
        {
            return (float)Math.Round(value, 1, MidpointRounding.AwayFromZero);
        }

        private static float RoundNormalized(float value)
        {
            return (float)Math.Round(Mathf.Clamp01(value), 4, MidpointRounding.AwayFromZero);
        }

        internal static object? ControlFind(JToken args)
        {
            var request = args is JObject obj ? obj : new JObject();
            var framework = (request["framework"]?.Value<string>() ?? string.Empty).Trim().ToLowerInvariant();
            var warnings = new List<string>();
            MapControlFindFilterAliases(request, warnings);
            var wildcards = ChievfxMcpRuntimeUiControlFind.ParseWildcards(request, "wildcards");
            var controlTypeFilter = ChievfxMcpRuntimeUiControlFind.NormalizeControlTypeFilter(request["controlType"]?.Value<string>());
            var pageSize = ChievfxMcpRuntimeUiControlFind.DefaultPageSize;
            var controls = new List<Dictionary<string, object?>>();
            var totalMatches = 0;
            var sections = new List<Dictionary<string, object?>>();

            foreach (var registered in SnapshotAdapters())
            {
                if (!ChievfxMcpRuntimeUiControlFind.MatchesFrameworkFilter(framework, registered.Adapter.FrameworkId))
                {
                    continue;
                }

                if (registered.Adapter is not IChievfxMcpRuntimeUiControlFindAdapter finder)
                {
                    continue;
                }

                if (!registered.Adapter.Available)
                {
                    warnings.Add($"Framework '{registered.Adapter.FrameworkId}' is unavailable.");
                    sections.Add(new Dictionary<string, object?>
                    {
                        ["framework"] = registered.Adapter.FrameworkId,
                        ["available"] = false,
                        ["totalMatches"] = 0,
                        ["controls"] = Array.Empty<Dictionary<string, object?>>(),
                    });
                    continue;
                }

                if (!TryReadDictionary(finder.FindControls(request.DeepClone()), out var section))
                {
                    continue;
                }

                sections.Add(section);
                totalMatches += section.TryGetValue("totalMatches", out var total) && total is int totalValue ? totalValue : 0;
                if (section.TryGetValue("warnings", out var sectionWarnings) && sectionWarnings is IEnumerable enumerable)
                {
                    foreach (var warning in enumerable)
                    {
                        if (warning != null)
                        {
                            warnings.Add(Convert.ToString(warning, CultureInfo.InvariantCulture) ?? string.Empty);
                        }
                    }
                }

                if (section.TryGetValue("controls", out var sectionControls) && sectionControls is IEnumerable controlRows)
                {
                    foreach (var controlRow in controlRows)
                    {
                        if (TryReadDictionary(controlRow, out var control))
                        {
                            controls.Add(control);
                        }
                    }
                }
            }

            if (!ChievfxMcpRuntimeUiControlFind.IncludesAllFrameworks(framework)
                && sections.Count == 0)
            {
                throw new ArgumentException($"No control-find adapter for framework '{framework}'. Available: {string.Join(", ", SnapshotAdapters().Where(registered => registered.Adapter is IChievfxMcpRuntimeUiControlFindAdapter).Select(registered => registered.Adapter.FrameworkId))}.");
            }

            var totalPages = Math.Max(1, (int)Math.Ceiling(totalMatches / (double)pageSize));
            var page = Math.Max(1, ReadInt(request, "page", 1));
            if (page > totalPages)
            {
                page = totalPages;
            }

            var normalizeCoords = ReadBool(request, "normalizeCoords", false);
            var screenSize = new Vector2(Mathf.Max(1, Screen.width), Mathf.Max(1, Screen.height));
            var selected = controls
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToArray();
            if (wildcards.Length > 0 && totalMatches == 0)
            {
                var containsHint = wildcards.All(pattern => pattern.IndexOfAny(new[] { '*', '?' }) < 0)
                    ? $" Patterns without * or ? must match the full control name or path exactly; try '*{wildcards[0]}*' for a contains search."
                    : string.Empty;
                warnings.Add($"No controls matched wildcards [{string.Join(", ", wildcards)}].{containsHint}");
            }

            var payload = new Dictionary<string, object?>
            {
                ["page"] = page,
                ["totalPages"] = totalPages,
                ["total"] = totalMatches,
                ["matched"] = wildcards.Length == 0 || totalMatches > 0,
                ["wildcards"] = wildcards.Length == 0 ? null : wildcards,
                ["controlTypeFilter"] = controlTypeFilter,
                ["normalizeCoords"] = normalizeCoords,
                ["screenSize"] = new Dictionary<string, object?>
                {
                    ["width"] = (int)screenSize.x,
                    ["height"] = (int)screenSize.y,
                },
                ["frameworkFilter"] = string.IsNullOrEmpty(framework) ? null : framework,
                ["controls"] = selected,
                ["frameworks"] = sections.ToArray(),
                ["warnings"] = warnings.Where(warning => !string.IsNullOrWhiteSpace(warning)).Distinct().ToArray(),
            };
            var outputFormat = request["outputFormat"]?.Value<string>();
            if (string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase))
            {
                return payload;
            }

            return payload;
        }

        /// <summary>
        /// The only supported filter argument is 'wildcards'; a misnamed filter (query, name, ...)
        /// would silently apply no filter and return every control as if it matched. Map the common
        /// aliases onto wildcards (contains semantics) and say so, instead of lying.
        /// </summary>
        private static void MapControlFindFilterAliases(JObject request, List<string> warnings)
        {
            if (request["wildcards"] != null && request["wildcards"]!.Type != JTokenType.Null)
            {
                return;
            }

            foreach (var alias in new[] { "query", "name", "nameContains", "namePattern", "search", "pattern", "text" })
            {
                if (request[alias]?.Type != JTokenType.String)
                {
                    continue;
                }

                var value = request[alias]!.Value<string>()?.Trim();
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                var pattern = value!.IndexOfAny(new[] { '*', '?' }) >= 0 ? value! : "*" + value + "*";
                request["wildcards"] = pattern;
                warnings.Add($"'{alias}' is not a ui-control-find argument; interpreted it as wildcards='{pattern}'.");
                return;
            }
        }

        private static object? ReadResource(string uri)
        {
            if (string.Equals(uri, StatusUri, StringComparison.Ordinal))
            {
                return ReadStatus(uri);
            }

            return null;
        }

        private static Dictionary<string, object?> ReadStatus(string uri)
        {
            var adapters = SnapshotAdapters();
            var canvasType = FindLoadedType("UnityEngine.Canvas");
            var eventSystemType = FindLoadedType("UnityEngine.EventSystems.EventSystem");
            var tmpTextType = FindLoadedType("TMPro.TextMeshProUGUI");
            var uiDocumentType = FindLoadedType("UnityEngine.UIElements.UIDocument");
            var capabilities = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var registered in adapters)
            {
                if (!TryReadDictionary(registered.Adapter.Status, out var status))
                {
                    capabilities[registered.Adapter.FrameworkId] = registered.Adapter.Available
                        ? new Dictionary<string, object?> { ["loaded"] = true }
                        : new Dictionary<string, object?> { ["loaded"] = false };
                    continue;
                }

                if (string.Equals(registered.Adapter.FrameworkId, "ugui", StringComparison.Ordinal))
                {
                    capabilities["ugui"] = ChievfxMcpUiStatusHelpers.ExtractCompactCapability(status);
                    capabilities["textMeshPro"] = Equals(ReadObject(status, "textMeshProConfigured"), true)
                        ? new Dictionary<string, object?> { ["loaded"] = true }
                        : new Dictionary<string, object?> { ["loaded"] = false };
                }
                else if (string.Equals(registered.Adapter.FrameworkId, "uitoolkit", StringComparison.Ordinal))
                {
                    capabilities["uitoolkit"] = ChievfxMcpUiStatusHelpers.ExtractCompactCapability(status);
                }
                else
                {
                    capabilities[registered.Adapter.FrameworkId] = registered.Adapter.Available
                        ? new Dictionary<string, object?> { ["loaded"] = true }
                        : new Dictionary<string, object?> { ["loaded"] = false };
                }
            }

            return new Dictionary<string, object?>
            {
                ["context"] = ChievfxMcpUiStatusHelpers.DescribeEditorContext(),
                ["capabilities"] = capabilities,
                ["currentHierarchy"] = ChievfxMcpUiStatusHelpers.DescribeCrossFrameworkHierarchy(
                    canvasType,
                    eventSystemType,
                    tmpTextType,
                    uiDocumentType),
                ["adapters"] = adapters.Select(CreateCompactAdapterStatusRow).ToArray(),
            };
        }

        private static Dictionary<string, object?> CreateCompactAdapterStatusRow(RegisteredAdapter registered)
        {
            var adapter = registered.Adapter;
            return new Dictionary<string, object?>
            {
                ["frameworkId"] = adapter.FrameworkId,
                ["frameworkName"] = adapter.FrameworkName,
                ["priority"] = adapter.Priority,
            };
        }

        private static Type? FindLoadedType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName, throwOnError: false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static Dictionary<string, object?> ProbeScreenPosition(string uri, JToken args)
        {
            ChievfxMcpRuntimeUiProbeCompact.EnsurePlayModeForProbe(EditorApplication.isPlaying || Application.isPlaying);

            var request = args is JObject obj ? obj : new JObject();
            var pageSize = ChievfxMcpRuntimeUiControlFind.DefaultPageSize;
            var warnings = new List<string>();
            var position = ReadProbeScreenPosition(request, warnings);
            var probe = ChievfxMcpRuntimeUiProbeCompact.CreateProbeBlock(
                position.ScreenSize,
                position.ScreenPosition,
                position.NormalizedPosition);
            var adapterRequest = CreateAdapterProbeRequest(request, position);

            Dictionary<string, object?>? uguiSection = null;
            Dictionary<string, object?>? uitoolkitSection = null;
            var anyProbed = false;
            var truncated = false;
            var uguiTotalHits = 0;
            var uitoolkitTotalHits = 0;

            foreach (var registered in SnapshotAdapters())
            {
                var adapter = registered.Adapter;
                if (string.Equals(adapter.FrameworkId, "ugui", StringComparison.Ordinal))
                {
                    uguiSection = ProbeAdapterSection(adapter, adapterRequest, "ugui", position, ref anyProbed, ref truncated);
                    uguiTotalHits = ReadSectionTotalHits(uguiSection);
                    continue;
                }

                if (string.Equals(adapter.FrameworkId, "uitoolkit", StringComparison.Ordinal))
                {
                    uitoolkitSection = ProbeAdapterSection(adapter, adapterRequest, "uitoolkit", position, ref anyProbed, ref truncated);
                    uitoolkitTotalHits = ReadSectionTotalHits(uitoolkitSection);
                }
            }

            var totalHits = uguiTotalHits + uitoolkitTotalHits;
            var totalPages = Math.Max(
                1,
                Math.Max(
                    (int)Math.Ceiling(uguiTotalHits / (double)pageSize),
                    (int)Math.Ceiling(uitoolkitTotalHits / (double)pageSize)));
            var page = Math.Max(1, ReadInt(request, "page", 1));
            if (page > totalPages)
            {
                page = totalPages;
            }

            if (uguiSection != null)
            {
                ChievfxMcpRuntimeUiProbeCompact.PaginateProbeSection(uguiSection, page, pageSize);
            }

            if (uitoolkitSection != null)
            {
                ChievfxMcpRuntimeUiProbeCompact.PaginateProbeSection(uitoolkitSection, page, pageSize);
            }

            return ChievfxMcpRuntimeUiProbeCompact.CreateMergedProbeResult(
                probe,
                runtimeAvailable: anyProbed,
                page,
                totalPages,
                totalHits,
                truncated,
                warnings,
                uguiSection,
                uitoolkitSection);
        }

        private static JObject CreateAdapterProbeRequest(JObject request, RuntimeScreenPosition position)
        {
            var adapterRequest = (JObject)request.DeepClone();
            adapterRequest.Remove("x");
            adapterRequest.Remove("y");
            adapterRequest.Remove("isNormalized");
            adapterRequest.Remove("page");
            adapterRequest.Remove("normalized");
            adapterRequest.Remove("maxRows");
            adapterRequest["screenPosition"] = new JObject
            {
                ["x"] = position.ScreenPosition.x,
                ["y"] = position.ScreenPosition.y,
            };
            adapterRequest["maxRows"] = AdapterProbeMaxRows;
            return adapterRequest;
        }

        private static int ReadSectionTotalHits(Dictionary<string, object?> section)
        {
            if (section.TryGetValue("hits", out var hitsValue))
            {
                return ChievfxMcpRuntimeUiProbeCompact.ReadHits(new Dictionary<string, object?> { ["hits"] = hitsValue }, string.Empty).Length;
            }

            return 0;
        }

        private static Dictionary<string, object?> ProbeAdapterSection(
            IChievfxMcpRuntimeUiAdapter adapter,
            JToken request,
            string frameworkId,
            RuntimeScreenPosition position,
            ref bool anyProbed,
            ref bool truncated)
        {
            if (!adapter.Available)
            {
                return frameworkId switch
                {
                    "ugui" => ChievfxMcpRuntimeUiProbeCompact.CreateUguiSection(
                        available: false,
                        probed: false,
                        Array.Empty<Dictionary<string, object?>>(),
                        new[] { "Adapter is registered but unavailable." }),
                    "uitoolkit" => ChievfxMcpRuntimeUiProbeCompact.CreateUiToolkitSection(
                        available: false,
                        probed: false,
                        position.ScreenSize,
                        position.ScreenPosition,
                        Array.Empty<Dictionary<string, object?>>(),
                        new[] { "Adapter is registered but unavailable." }),
                    _ => throw new InvalidOperationException($"Unknown runtime UI adapter '{frameworkId}'."),
                };
            }

            try
            {
                var adapterProbe = adapter.ProbeScreenPosition(request.DeepClone());
                anyProbed = true;
                var sectionTruncated = ChievfxMcpRuntimeUiProbeCompact.ReadTruncated(adapterProbe, frameworkId);
                truncated |= sectionTruncated;
                var hits = ChievfxMcpRuntimeUiProbeCompact.ReadHits(adapterProbe, frameworkId);
                var sectionWarnings = ChievfxMcpRuntimeUiProbeCompact.ReadSectionWarnings(adapterProbe, frameworkId);
                return frameworkId switch
                {
                    "ugui" => ChievfxMcpRuntimeUiProbeCompact.CreateUguiSection(
                        available: true,
                        probed: true,
                        hits,
                        sectionWarnings,
                        sectionTruncated),
                    "uitoolkit" => ChievfxMcpRuntimeUiProbeCompact.CreateUiToolkitSection(
                        available: true,
                        probed: true,
                        position.ScreenSize,
                        position.ScreenPosition,
                        hits,
                        sectionWarnings,
                        sectionTruncated),
                    _ => throw new InvalidOperationException($"Unknown runtime UI adapter '{frameworkId}'."),
                };
            }
            catch (Exception ex)
            {
                return frameworkId switch
                {
                    "ugui" => ChievfxMcpRuntimeUiProbeCompact.CreateUguiSection(
                        available: true,
                        probed: false,
                        Array.Empty<Dictionary<string, object?>>(),
                        new[] { "Adapter probe failed: " + RootMessage(ex) }),
                    "uitoolkit" => ChievfxMcpRuntimeUiProbeCompact.CreateUiToolkitSection(
                        available: true,
                        probed: false,
                        position.ScreenSize,
                        position.ScreenPosition,
                        Array.Empty<Dictionary<string, object?>>(),
                        new[] { "Adapter probe failed: " + RootMessage(ex) }),
                    _ => throw new InvalidOperationException($"Unknown runtime UI adapter '{frameworkId}'."),
                };
            }
        }

        private static RegisteredAdapter[] SnapshotAdapters()
        {
            lock (SyncRoot)
            {
                return Adapters.Values
                    .OrderByDescending(item => item.Adapter.Priority)
                    .ThenBy(item => item.RegistrationOrder)
                    .ThenBy(item => item.Adapter.FrameworkId, StringComparer.Ordinal)
                    .ToArray();
            }
        }

        private static object? ReadObject(Dictionary<string, object?> source, string key)
        {
            return source.TryGetValue(key, out var value) ? value : null;
        }

        private static bool TryReadDictionary(object? value, out Dictionary<string, object?> dictionary)
        {
            if (value is Dictionary<string, object?> typed)
            {
                dictionary = typed;
                return true;
            }

            if (value is JObject obj)
            {
                dictionary = obj.Properties().ToDictionary(property => property.Name, property => (object?)property.Value);
                return true;
            }

            if (value is IDictionary untyped)
            {
                dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in untyped)
                {
                    if (entry.Key is string key)
                    {
                        dictionary[key] = entry.Value;
                    }
                }

                return true;
            }

            dictionary = new Dictionary<string, object?>();
            return false;
        }

        private static IEnumerable<object?> ReadEnumerable(object value)
        {
            if (value is JArray array)
            {
                return array.Cast<object?>();
            }

            return value is IEnumerable enumerable && value is not string
                ? enumerable.Cast<object?>()
                : Enumerable.Empty<object?>();
        }

        private static bool TryConvertInt(object? value, out int intValue)
        {
            switch (value)
            {
                case int typed:
                    intValue = typed;
                    return true;
                case long typed:
                    intValue = (int)typed;
                    return true;
                case float typed:
                    intValue = Mathf.RoundToInt(typed);
                    return true;
                case double typed:
                    intValue = (int)Math.Round(typed);
                    return true;
                case JValue { Value: int typed }:
                    intValue = typed;
                    return true;
                case JValue { Value: long typed }:
                    intValue = (int)typed;
                    return true;
                case JValue { Value: double typed }:
                    intValue = (int)Math.Round(typed);
                    return true;
                case JValue { Value: string typed } when int.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                    intValue = parsed;
                    return true;
                default:
                    intValue = 0;
                    return false;
            }
        }

        private static RuntimeScreenPosition ReadProbeScreenPosition(JToken args, List<string> warnings)
        {
            var screenSize = new Vector2(Mathf.Max(1, Screen.width), Mathf.Max(1, Screen.height));
            if (IsScreenshotSpace(args))
            {
                return ReadScreenshotSpacePosition(args, screenSize);
            }

            var isNormalized = ReadBool(args, "isNormalized", false);
            float x;
            float y;

            if (args["x"] != null || args["y"] != null)
            {
                x = ReadFloat(args, "x", 0f);
                y = ReadFloat(args, "y", 0f);
            }
            else if (TryReadVector2(args["screenPosition"], out var screenPosition))
            {
                x = screenPosition.x;
                y = screenPosition.y;
            }
            else if (TryReadVector2(args["normalized"], out var legacyNormalized))
            {
                isNormalized = true;
                x = legacyNormalized.x;
                y = legacyNormalized.y;
            }
            else
            {
                throw new ArgumentException("ui-runtime-probe requires x and y coordinates.");
            }

            if (isNormalized)
            {
                var normalized = new Vector2(x, y);
                return new RuntimeScreenPosition(
                    new Vector2(normalized.x * screenSize.x, normalized.y * screenSize.y),
                    screenSize,
                    normalized);
            }

            return RuntimeScreenPosition.FromScreenPosition(new Vector2(x, y));
        }

        // Screenshot space: x/y are normalized 0..1 against the screenshot-game-view PNG (TOP-LEFT origin).
        // Flip Y to reach the bottom-left Screen space the runtime uses. Only valid when that screenshot
        // did not report pixelMappingReliable:false (aspects match); the caller is trusted on that.
        private static bool IsScreenshotSpace(JToken args) =>
            string.Equals((args["space"]?.Value<string>() ?? string.Empty).Trim(), "screenshot", StringComparison.OrdinalIgnoreCase);

        private static RuntimeScreenPosition ReadScreenshotSpacePosition(JToken args, Vector2 screenSize)
        {
            if (args["x"] == null || args["y"] == null)
            {
                throw new ArgumentException("space:\"screenshot\" requires normalized x and y (px/pngWidth, py/pngHeight) read off the screenshot PNG.");
            }

            var x = ReadFloat(args, "x", 0f);
            var y = ReadFloat(args, "y", 0f);
            if (x < -0.001f || x > 1.001f || y < -0.001f || y > 1.001f)
            {
                throw new ArgumentException("space:\"screenshot\" x/y must be normalized 0..1 — divide the pixel you read by pngWidth/pngHeight.");
            }

            var normalized = new Vector2(Mathf.Clamp01(x), 1f - Mathf.Clamp01(y));
            return new RuntimeScreenPosition(new Vector2(normalized.x * screenSize.x, normalized.y * screenSize.y), screenSize, normalized);
        }

        private static RuntimeScreenPosition ReadScreenPosition(JToken args, List<string> warnings)
        {
            var screenSize = new Vector2(Mathf.Max(1, Screen.width), Mathf.Max(1, Screen.height));
            if (IsScreenshotSpace(args))
            {
                return ReadScreenshotSpacePosition(args, screenSize);
            }

            if (TryReadVector2(args["normalized"], out var normalized))
            {
                return new RuntimeScreenPosition(new Vector2(normalized.x * screenSize.x, normalized.y * screenSize.y), screenSize, normalized);
            }

            if (TryReadVector2(args["screenPosition"], out var screenPosition))
            {
                return RuntimeScreenPosition.FromScreenPosition(screenPosition);
            }

            if (args["x"] != null || args["y"] != null)
            {
                var isNormalized = ReadBool(args, "isNormalized", false);
                var x = ReadFloat(args, "x", 0f);
                var y = ReadFloat(args, "y", 0f);
                if (isNormalized)
                {
                    var normalizedPosition = new Vector2(x, y);
                    return new RuntimeScreenPosition(
                        new Vector2(normalizedPosition.x * screenSize.x, normalizedPosition.y * screenSize.y),
                        screenSize,
                        normalizedPosition);
                }

                return RuntimeScreenPosition.FromScreenPosition(new Vector2(x, y));
            }

            throw new ArgumentException(
                "Runtime UI interaction requires x/y screen coordinates when path, instanceId, visualElementRef, or name is not supplied.");
        }

        private static JObject RuntimeProbeSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["required"] = new JArray("x", "y"),
                ["properties"] = new JObject
                {
                    ["x"] = NumberSchema("Screen X. Bottom-left origin is 0; top-right is screen width in pixels, or 1 when isNormalized is true."),
                    ["y"] = NumberSchema("Screen Y. Bottom-left origin is 0; top-right is screen height in pixels, or 1 when isNormalized is true."),
                    ["isNormalized"] = new JObject
                    {
                        ["type"] = "boolean",
                        ["default"] = false,
                        ["description"] = "When true, x/y are normalized 0..1 from bottom-left (0,0) to top-right (1,1). When false (default), x/y are pixels.",
                    },
                    ["space"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray("screen", "screenshot"),
                        ["default"] = "screen",
                        ["description"] = "screen (default): x/y in Screen space (see isNormalized). screenshot: x/y are 0..1 from the screenshot-game-view PNG (top-left origin), auto Y-flipped; use only if that screenshot did not report pixelMappingReliable:false.",
                    },
                    ["page"] = new JObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 1,
                        ["default"] = 1,
                        ["description"] = "1-based page index. Each page returns up to 10 hits per framework section.",
                    },
                },
                ["additionalProperties"] = true,
            };
        }

        private static JObject TypeTextSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["framework"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray("auto", "ugui", "uitoolkit"),
                        ["description"] = "Target framework. auto (default) resolves the field across registered frameworks; ugui/uitoolkit force one.",
                    },
                    ["x"] = NumberSchema("Screen X. Bottom-left origin is 0; top-right is screen width in pixels, or 1 when isNormalized is true."),
                    ["y"] = NumberSchema("Screen Y. Bottom-left origin is 0; top-right is screen height in pixels, or 1 when isNormalized is true."),
                    ["isNormalized"] = new JObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "When true, x and y are normalized bottom-left coordinates where 1 is screen width/height.",
                    },
                    ["path"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "uGUI GameObject transform path, UI Toolkit VisualElement path, or visualElementRef (ve:...) from runtime reads/probes.",
                    },
                    ["instanceId"] = new JObject { ["type"] = "integer", ["description"] = "uGUI target GameObject instance id." },
                    ["text"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Text to type into the field. Use append:true with an empty string to focus without replacing existing text.",
                    },
                    ["append"] = new JObject { ["type"] = "boolean", ["description"] = "Append to the current text instead of replacing it. Defaults false." },
                    ["submit"] = new JObject { ["type"] = "boolean", ["description"] = "After typing, submit/end edit (uGUI onEndEdit, UI Toolkit NavigationSubmit + blur). Defaults false." },
                },
                ["required"] = new JArray("text"),
                ["additionalProperties"] = true,
            };
        }

        private static JObject Vector2Schema(string description)
        {
            return new JObject
            {
                ["type"] = "object",
                ["description"] = description,
                ["properties"] = new JObject
                {
                    ["x"] = NumberSchema("X coordinate."),
                    ["y"] = NumberSchema("Y coordinate."),
                },
                ["required"] = new JArray("x", "y"),
                ["additionalProperties"] = false,
            };
        }

        private static JObject NumberSchema(string description)
        {
            return new JObject
            {
                ["type"] = "number",
                ["description"] = description,
            };
        }

        private static bool TryReadVector2(JToken? token, out Vector2 value)
        {
            if (token is JObject obj && obj["x"] != null && obj["y"] != null)
            {
                value = new Vector2(ReadFloat(obj, "x", 0f), ReadFloat(obj, "y", 0f));
                return true;
            }

            value = default;
            return false;
        }

        private static int ReadInt(JToken token, string key, int defaultValue)
        {
            return token[key]?.Value<int?>() ?? defaultValue;
        }

        private static bool ReadBool(JToken token, string key, bool defaultValue)
        {
            return token[key]?.Value<bool?>() ?? defaultValue;
        }

        private static float ReadFloat(JToken token, string key, float defaultValue)
        {
            return token[key]?.Value<float?>() ?? defaultValue;
        }

        private static string? ReadString(JToken token, string key)
        {
            var value = token[key]?.Value<string>();
            return string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
        }

        private static string RootMessage(Exception ex)
        {
            return ex is System.Reflection.TargetInvocationException { InnerException: { } inner }
                ? RootMessage(inner)
                : ex.Message;
        }

        private sealed class RegisteredAdapter
        {
            public RegisteredAdapter(IChievfxMcpRuntimeUiAdapter adapter, int registrationOrder)
            {
                Adapter = adapter;
                RegistrationOrder = registrationOrder;
            }

            public IChievfxMcpRuntimeUiAdapter Adapter { get; }

            public int RegistrationOrder { get; }
        }

        internal readonly struct RuntimeScreenPosition
        {
            public RuntimeScreenPosition(Vector2 screenPosition, Vector2 screenSize, Vector2 normalizedPosition)
            {
                ScreenPosition = screenPosition;
                ScreenSize = screenSize;
                NormalizedPosition = normalizedPosition;
            }

            public Vector2 ScreenPosition { get; }

            public Vector2 ScreenSize { get; }

            public Vector2 NormalizedPosition { get; }

            public static RuntimeScreenPosition FromScreenPosition(Vector2 screenPosition)
            {
                var screenSize = new Vector2(Mathf.Max(1, Screen.width), Mathf.Max(1, Screen.height));
                return new RuntimeScreenPosition(screenPosition, screenSize, new Vector2(screenPosition.x / screenSize.x, screenPosition.y / screenSize.y));
            }
        }
    }
}
