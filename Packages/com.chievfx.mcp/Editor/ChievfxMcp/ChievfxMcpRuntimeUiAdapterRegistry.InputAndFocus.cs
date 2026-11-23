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
    internal static partial class ChievfxMcpRuntimeUiAdapterRegistry
    {
        private static object? TypeText(string uri, JToken args)
        {
            var request = args is JObject obj ? obj : new JObject();
            ChievfxMcpRuntimeUiInteractionInput.EnsureTargetOrScreenPosition(request, "ui-runtime-type-text");
            var adapterRequest = CreateAdapterInteractionRequest(request);
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

                var forced = ((IChievfxMcpRuntimeUiTextInputAdapter)selected.Adapter).TypeIntoFocusedTextField(adapterRequest.DeepClone(), requireTarget: true);
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
                    result = ((IChievfxMcpRuntimeUiTextInputAdapter)registered.Adapter).TypeIntoFocusedTextField(adapterRequest.DeepClone(), requireTarget: false);
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

            return CreateUnresolvedInteractionResult(
                uri,
                request,
                attempts,
                "No uGUI or UI Toolkit text field resolved from the supplied screen position. Provide path/instanceId/name/visualElementRef or x/y over a focusable text field, or set framework explicitly.");
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
            var adapterRequest = CreateAdapterInteractionRequest(request);
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

                var forced = ((IChievfxMcpRuntimeUiSetControlValueAdapter)selected.Adapter).SetControlValue(adapterRequest.DeepClone(), requireTarget: true);
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
                    result = ((IChievfxMcpRuntimeUiSetControlValueAdapter)registered.Adapter).SetControlValue(adapterRequest.DeepClone(), requireTarget: false);
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

            return CreateUnresolvedInteractionResult(
                "tool://" + SetControlValueToolName,
                request,
                attempts,
                "No uGUI or UI Toolkit settable control resolved from the supplied screen position. Provide path/instanceId or x/y over a Slider, Toggle, Dropdown, or other writable control, or set framework explicitly.");
        }

        // "Nothing at that path" and "the object is there but has no control this tool can drive" need different
        // fixes, and both used to collapse into one generic "no runtime UI control found". When an adapter found
        // the object, its own explanation (which controls it looked for, which components are there) is the
        // answer; only when no adapter found anything is the path itself wrong.
        private static Dictionary<string, object?> CreateUnresolvedInteractionResult(
            string uri,
            JObject request,
            List<Dictionary<string, object?>> attempts,
            string positionMissMessage)
        {
            var explicitTarget = ChievfxMcpRuntimeUiInteractionInput.HasExplicitTargetInput(request);
            string[] warnings;
            if (!explicitTarget)
            {
                warnings = new[] { positionMissMessage };
            }
            else
            {
                var foundTargetWarnings = attempts
                    .Select(attempt => attempt.TryGetValue("detail", out var detail) && TryReadDictionary(detail, out var row) ? row : null)
                    .Where(row => row != null && row.TryGetValue("target", out var target) && target != null)
                    .SelectMany(row => ChievfxMcpRuntimeUiProbeCompact.ReadStringArray(row!.TryGetValue("warnings", out var rowWarnings) ? rowWarnings : null))
                    .Distinct()
                    .ToArray();
                warnings = foundTargetWarnings.Length > 0
                    ? foundTargetWarnings
                    : new[] { ChievfxMcpRuntimeUiInteractionInput.FormatCrossFrameworkTargetNotFoundMessage(request) };
            }

            return new Dictionary<string, object?>
            {
                ["uri"] = uri,
                ["playMode"] = EditorApplication.isPlaying || Application.isPlaying,
                ["resolved"] = false,
                ["framework"] = null,
                ["warnings"] = warnings,
                ["attempts"] = explicitTarget
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
            var adapterRequest = CreateAdapterInteractionRequest(request);
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

                var forced = ((IChievfxMcpRuntimeUiFocusAdapter)selected.Adapter).Focus(adapterRequest.DeepClone(), requireTarget: true);
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
                    result = ((IChievfxMcpRuntimeUiFocusAdapter)registered.Adapter).Focus(adapterRequest.DeepClone(), requireTarget: false);
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
    }
}
