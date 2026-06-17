#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Chievfx.Mcp.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using static Chievfx.Mcp.Extensions.UiToolkit.ChievfxMcpUiToolkitExtension;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitResources;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitInteractions;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitPanelQueries;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitRows;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitShared;
namespace Chievfx.Mcp.Extensions.UiToolkit
{
    internal static class UiToolkitRuntimeTools
    {
        internal static Dictionary<string, object?> ProbeRuntimeScreenPosition(JToken args, UiToolkitDependencyStatus status)
        {
            ChievfxMcpRuntimeUiProbeCompact.EnsurePlayModeForProbe(IsRuntimePlayModeActive());

            var warnings = new List<string>();
            var maxRows = Mathf.Clamp(ReadInt(args, "maxRows", DefaultMaxRows), 1, 1024);
            var position = ReadScreenPosition(args, warnings);
            var probe = ChievfxMcpRuntimeUiProbeCompact.CreateProbeBlock(
                position.ScreenSize,
                position.ScreenPosition,
                position.NormalizedPosition);

            if (IsOutsideScreen(position.ScreenPosition, position.ScreenSize))
            {
                warnings.Add("Coordinate is outside current screen/game-view bounds.");
            }

            var stackRows = new List<Dictionary<string, object?>>();
            var truncated = false;
            foreach (var panelGroup in FindRuntimePanelGroups(status))
            {
                var panelPosition = ConvertScreenToPanel(status, panelGroup, position, warnings);
                if (!panelPosition.HasValue)
                {
                    continue;
                }

                var hits = MergePickAllWithBoundsHits(PickAll(status, panelGroup.Panel, panelPosition.Value, warnings), panelGroup, status, panelPosition.Value);
                foreach (var hit in hits)
                {
                    if (stackRows.Count >= maxRows)
                    {
                        truncated = true;
                        break;
                    }

                    stackRows.Add(CreateCompactProbeStackRow(hit, status, panelGroup, stackRows.Count, stackRows.Count));
                }

                if (truncated)
                {
                    break;
                }
            }

            return ChievfxMcpRuntimeUiProbeCompact.CreateProbeResult(
                probe,
                runtimeAvailable: true,
                maxRows,
                truncated,
                warnings,
                uitoolkit: ChievfxMcpRuntimeUiProbeCompact.CreateUiToolkitSection(
                    available: true,
                    probed: true,
                    position.ScreenSize,
                    position.ScreenPosition,
                    stackRows.ToArray(),
                    truncated: truncated));
        }

        internal static Dictionary<string, object?> RuntimeClickAtPosition(JToken args, UiToolkitDependencyStatus status)
        {
            var warnings = new List<string>();
            var handler = ChievfxMcpRuntimeUiAdapterRegistry.ReadRuntimeClickHandler(args);
            var action = handler == "submit" ? "navigationSubmit" : "pointerClick";
            var result = CreateEnvelope("tool://ui-runtime-click#uitoolkit", status);
            result["handler"] = handler;
            result["playMode"] = IsRuntimePlayModeActive();
            result["runtimeAvailable"] = EnsureRuntimeReadAllowed(warnings);
            result["focusedElementBefore"] = CreateFocusedElementRow(status);
            result["dispatchedEvents"] = Array.Empty<string>();

            var resolution = ResolveRuntimeInteractionTarget(args, status, warnings);
            result["input"] = resolution.Position == null ? null : CreateScreenPositionRow(resolution.Position.Value);
            result["panelPosition"] = resolution.PanelPosition.HasValue ? CreateVector2Row(resolution.PanelPosition.Value) : null;
            result["resolvedBy"] = resolution.ResolvedBy;
            result["stack"] = resolution.Stack;
            result["target"] = resolution.Target == null ? null : CreateVisualElementRow(resolution.Target, status, resolution.Group ?? PanelGroup.FromElement(resolution.Target), includeTextAndValue: true);
            result["targetStateBefore"] = resolution.Target == null ? null : CreateVisualElementStateRow(resolution.Target, status);
            result["intendedHandler"] = resolution.Target == null
                ? null
                : new Dictionary<string, object?> { ["handler"] = handler, ["events"] = PlannedEvents(action) };

            if (resolution.Target == null)
            {
                warnings.Add("No runtime UI Toolkit target resolved for click.");
            }
            else if (!IsRuntimePlayModeActive())
            {
                throw new InvalidOperationException("Runtime UI Toolkit click requires Play Mode. Enter Play Mode before firing interactions.");
            }
            else
            {
                result["dispatchedEvents"] = ApplyRuntimeInteraction(action, resolution.Target, resolution.PanelPosition, args, warnings);
            }

            result["focusedElementAfter"] = CreateFocusedElementRow(status);
            result["targetStateAfter"] = resolution.Target == null ? null : CreateVisualElementStateRow(resolution.Target, status);
            result["warnings"] = warnings.Distinct().ToArray();
            var resolved = result.TryGetValue("target", out var target) && target != null;
            result["resolved"] = resolved;
            result["framework"] = "uitoolkit";
            return result;
        }

        internal static Dictionary<string, object?> RuntimeDragAtPosition(JToken args, UiToolkitDependencyStatus status)
        {
            var warnings = new List<string>();
            var geometry = ChievfxMcpRuntimeUiAdapterRegistry.ReadRuntimeDragGeometry(args, warnings);
            var panelDelta = new Vector2(geometry.ScreenDelta.x, -geometry.ScreenDelta.y);
            var dragArgs = args is JObject obj ? (JObject)obj.DeepClone() : new JObject();
            dragArgs["x"] = geometry.Start.ScreenPosition.x;
            dragArgs["y"] = geometry.Start.ScreenPosition.y;
            dragArgs["isNormalized"] = false;
            dragArgs["delta"] = new JObject { ["x"] = panelDelta.x, ["y"] = panelDelta.y };
            if (args["steps"] != null)
            {
                dragArgs["steps"] = args["steps"];
            }

            var result = CreateEnvelope("tool://ui-runtime-drag#uitoolkit", status);
            result["playMode"] = IsRuntimePlayModeActive();
            result["runtimeAvailable"] = EnsureRuntimeReadAllowed(warnings);
            result["focusedElementBefore"] = CreateFocusedElementRow(status);
            result["dispatchedEvents"] = Array.Empty<string>();
            result["startCoordinateConvention"] = CreateScreenPositionRow(
                new RuntimeScreenPosition(geometry.Start.ScreenPosition, geometry.Start.ScreenSize, geometry.Start.NormalizedPosition, false));
            result["endCoordinateConvention"] = CreateScreenPositionRow(
                new RuntimeScreenPosition(geometry.End.ScreenPosition, geometry.End.ScreenSize, geometry.End.NormalizedPosition, false));
            result["screenDelta"] = CreateVector2Row(geometry.ScreenDelta);

            var resolution = ResolveRuntimeInteractionTarget(dragArgs, status, warnings);
            result["input"] = resolution.Position == null ? null : CreateScreenPositionRow(resolution.Position.Value);
            result["panelPosition"] = resolution.PanelPosition.HasValue ? CreateVector2Row(resolution.PanelPosition.Value) : null;
            result["resolvedBy"] = resolution.ResolvedBy;
            result["stack"] = resolution.Stack;
            result["target"] = resolution.Target == null ? null : CreateVisualElementRow(resolution.Target, status, resolution.Group ?? PanelGroup.FromElement(resolution.Target), includeTextAndValue: true);
            result["targetStateBefore"] = resolution.Target == null ? null : CreateVisualElementStateRow(resolution.Target, status);
            result["intendedHandler"] = resolution.Target == null
                ? null
                : new Dictionary<string, object?> { ["handler"] = "pointerDrag", ["events"] = PlannedEvents("pointerDrag") };

            if (resolution.Target == null)
            {
                warnings.Add("No runtime UI Toolkit target resolved for drag.");
            }
            else if (!IsRuntimePlayModeActive())
            {
                throw new InvalidOperationException("Runtime UI Toolkit drag requires Play Mode. Enter Play Mode before firing interactions.");
            }
            else
            {
                result["dispatchedEvents"] = ApplyRuntimeInteraction("pointerDrag", resolution.Target, resolution.PanelPosition, dragArgs, warnings);
            }

            result["focusedElementAfter"] = CreateFocusedElementRow(status);
            result["targetStateAfter"] = resolution.Target == null ? null : CreateVisualElementStateRow(resolution.Target, status);
            result["warnings"] = warnings.Distinct().ToArray();
            var resolved = result.TryGetValue("target", out var target) && target != null;
            result["resolved"] = resolved;
            result["framework"] = "uitoolkit";
            return result;
        }

        internal static Dictionary<string, object?> RuntimeSetControlValueAt(JToken args, UiToolkitDependencyStatus status, bool requireTarget)
        {
            var warnings = new List<string>();
            var valueToken = args["value"];
            if (valueToken == null || valueToken.Type == JTokenType.Null)
            {
                throw new ArgumentException("ui-runtime-set-control-value requires 'value'.");
            }

            var result = CreateEnvelope("tool://ui-runtime-set-control-value#uitoolkit", status);
            result["framework"] = "uitoolkit";
            result["playMode"] = IsRuntimePlayModeActive();
            result["runtimeAvailable"] = EnsureRuntimeReadAllowed(warnings);
            result["focusedElementBefore"] = CreateFocusedElementRow(status);

            var resolution = ResolveRuntimeInteractionTarget(args, status, warnings);
            result["stack"] = resolution.Stack;
            result["resolvedBy"] = resolution.ResolvedBy;
            result["input"] = resolution.Position == null ? null : CreateScreenPositionRow(resolution.Position.Value);

            var element = resolution.Target as VisualElement;
            var valueProperty = element?.GetType().GetProperty("value", BindingFlags.Public | BindingFlags.Instance);
            var hasWritableValue = element != null && valueProperty != null && valueProperty.CanWrite;
            var isTextField = hasWritableValue
                && (Nullable.GetUnderlyingType(valueProperty!.PropertyType) ?? valueProperty.PropertyType) == typeof(string);
            var resolved = hasWritableValue && !isTextField;
            result["resolved"] = resolved;
            result["target"] = element == null ? null : CreateVisualElementRow(element, status, resolution.Group ?? PanelGroup.FromElement(element), includeTextAndValue: true);
            result["targetStateBefore"] = element == null ? null : CreateVisualElementStateRow(element, status);
            result["intendedHandler"] = element == null || valueProperty == null
                ? null
                : new Dictionary<string, object?>
                {
                    ["controlType"] = element.GetType().Name,
                    ["operation"] = "setValue",
                    ["valueType"] = valueProperty.PropertyType.Name,
                };

            if (!resolved)
            {
                if (requireTarget)
                {
                    throw new ArgumentException(element == null
                        ? "ui-runtime-set-control-value could not resolve a UI Toolkit target from path or screen position."
                        : isTextField
                            ? $"Target '{GetVisualElementPath(element)}' is a text field; use ui-runtime-type-text for string entry."
                            : $"Target '{GetVisualElementPath(element)}' has no writable value property.");
                }

                warnings.Add("No runtime UI Toolkit settable control resolved.");
                result["warnings"] = warnings.Distinct().ToArray();
                return result;
            }

            if (!IsRuntimePlayModeActive())
            {
                throw new InvalidOperationException("Runtime UI Toolkit set-control-value requires Play Mode. Enter Play Mode before mutating controls.");
            }

            ApplyRuntimeControlValue(element!, new JObject { ["value"] = valueToken, ["invokeCallbacks"] = true }, warnings);
            result["focusedElementAfter"] = CreateFocusedElementRow(status);
            result["targetStateAfter"] = CreateVisualElementStateRow(element!, status);
            result["warnings"] = warnings.Distinct().ToArray();
            return result;
        }

        internal static Dictionary<string, object?> RuntimeFocusAt(JToken args, UiToolkitDependencyStatus status, bool requireTarget)
        {
            var warnings = new List<string>();
            var result = CreateEnvelope("tool://ui-runtime-focus#uitoolkit", status);
            result["framework"] = "uitoolkit";
            result["playMode"] = IsRuntimePlayModeActive();
            result["runtimeAvailable"] = EnsureRuntimeReadAllowed(warnings);
            result["focusedElementBefore"] = CreateFocusedElementRow(status);

            var resolution = ResolveRuntimeInteractionTarget(args, status, warnings);
            result["stack"] = resolution.Stack;
            result["resolvedBy"] = resolution.ResolvedBy;
            result["input"] = resolution.Position == null ? null : CreateScreenPositionRow(resolution.Position.Value);

            var element = resolution.Target as VisualElement;
            var focusable = element != null && ReadBoolMember(element, "focusable", false);
            result["resolved"] = focusable;
            result["target"] = element == null ? null : CreateVisualElementRow(element, status, resolution.Group ?? PanelGroup.FromElement(element), includeTextAndValue: true);
            result["targetStateBefore"] = element == null ? null : CreateVisualElementStateRow(element, status);
            result["intendedHandler"] = element == null
                ? null
                : new Dictionary<string, object?> { ["operation"] = "VisualElement.Focus", ["events"] = new[] { "VisualElement.Focus" } };

            if (!focusable)
            {
                if (requireTarget)
                {
                    throw new ArgumentException(element == null
                        ? "ui-runtime-focus could not resolve a UI Toolkit target from path or screen position."
                        : $"Target '{GetVisualElementPath(element)}' is not focusable.");
                }

                warnings.Add("No runtime UI Toolkit focus target resolved.");
                result["warnings"] = warnings.Distinct().ToArray();
                return result;
            }

            if (!IsRuntimePlayModeActive())
            {
                throw new InvalidOperationException("Runtime UI Toolkit focus requires Play Mode. Enter Play Mode before focusing controls.");
            }

            element!.Focus();
            result["dispatchedEvents"] = new[] { "VisualElement.Focus" };
            result["focusedElementAfter"] = CreateFocusedElementRow(status);
            result["targetStateAfter"] = CreateVisualElementStateRow(element, status);
            result["focused"] = true;
            result["warnings"] = warnings.Distinct().ToArray();
            return result;
        }

        internal static Dictionary<string, object?> RuntimeClearFocus(UiToolkitDependencyStatus status)
        {
            var warnings = new List<string>();
            var result = CreateEnvelope("tool://ui-runtime-clear-focus#uitoolkit", status);
            result["framework"] = "uitoolkit";
            result["playMode"] = IsRuntimePlayModeActive();
            result["runtimeAvailable"] = EnsureRuntimeReadAllowed(warnings);
            result["focusBefore"] = CreateFocusedElementRow(status);

            if (!IsRuntimePlayModeActive())
            {
                throw new InvalidOperationException("Runtime UI Toolkit clear-focus requires Play Mode. Enter Play Mode before clearing focus.");
            }

            var cleared = ClearRuntimeFocus(status);
            result["focusAfter"] = CreateFocusedElementRow(status);
            result["cleared"] = cleared;
            result["events"] = cleared ? new[] { "VisualElement.Blur" } : Array.Empty<string>();
            result["warnings"] = warnings.Distinct().ToArray();
            return result;
        }

        internal static bool ClearRuntimeFocus(UiToolkitDependencyStatus status)
        {
            var cleared = false;
            foreach (var group in FindRuntimePanelGroups(status))
            {
                var focusController = group.Panel == null ? null : GetMemberValue(group.Panel, "focusController");
                if (focusController == null)
                {
                    continue;
                }

                if (GetMemberValue(focusController, "focusedElement") is VisualElement focused)
                {
                    focused.Blur();
                    cleared = true;
                }
            }

            return cleared;
        }

        internal static Dictionary<string, object?> ControlFind(JToken args, UiToolkitDependencyStatus status)
        {
            var warnings = new List<string>();
            var wildcards = ChievfxMcpRuntimeUiControlFind.ParseWildcards(args, "wildcards");
            var controlTypeFilter = ChievfxMcpRuntimeUiControlFind.NormalizeControlTypeFilter(ReadString(args, "controlType"));
            var playMode = IsRuntimePlayModeActive();
            if (!playMode && FindRuntimeDocuments(status).Length == 0)
            {
                warnings.Add("Runtime UI Toolkit reads are gated to Play Mode; enter Play Mode before reading runtime UI state.");
            }
            else if (!playMode)
            {
                warnings.Add("UI Toolkit outside Play Mode uses editor panel layout; enter Play Mode for runtime-accurate UI state.");
            }

            var screenSize = new Vector2(Mathf.Max(1, Screen.width), Mathf.Max(1, Screen.height));
            var matches = new List<(object element, PanelGroup group, string controlType)>();

            foreach (var group in FindRuntimePanelGroups(status))
            {
                foreach (var document in group.Documents)
                {
                    var root = GetRootVisualElement(document);
                    if (root == null)
                    {
                        continue;
                    }

                    foreach (var item in EnumerateVisibleTree(root, status, DefaultMaxRows * 4))
                    {
                        if (!IsInteractableVisualElement(item.Element, status))
                        {
                            continue;
                        }

                        var elementName = ReadMemberString(item.Element, "name");
                        var elementPath = GetVisualElementPath(item.Element);
                        if (!ChievfxMcpRuntimeUiControlFind.MatchesWildcards(elementName ?? string.Empty, elementPath, wildcards))
                        {
                            continue;
                        }

                        var controlType = ChievfxMcpRuntimeUiControlFind.NormalizeControlType(item.Element.GetType());
                        if (!string.IsNullOrWhiteSpace(controlTypeFilter)
                            && !string.Equals(controlType, controlTypeFilter, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (!TryGetUiToolkitScreenZone(status, group.Panel, item.Element, screenSize, out _))
                        {
                            continue;
                        }

                        matches.Add((item.Element, group, controlType));
                    }
                }
            }

            if (!playMode && matches.Count == 0 && FindRuntimeDocuments(status).Length > 0)
            {
                warnings.Add("No on-screen UI Toolkit controls matched; enter Play Mode if the UI is runtime-only.");
            }

            var rows = matches
                .Select(entry =>
                {
                    TryGetUiToolkitScreenZone(status, entry.group.Panel, entry.element, screenSize, out var zone);
                    return new Dictionary<string, object?>
                    {
                        ["framework"] = "uitoolkit",
                        ["path"] = GetVisualElementPath(entry.element),
                        ["visualElementRef"] = CreateVisualElementRef(entry.element),
                        ["controlType"] = entry.controlType,
                        ["zone"] = zone,
                    };
                })
                .ToArray();

            return new Dictionary<string, object?>
            {
                ["framework"] = "uitoolkit",
                ["available"] = status.Available,
                ["playMode"] = playMode,
                ["runtimeAvailable"] = playMode,
                ["totalMatches"] = matches.Count,
                ["wildcards"] = wildcards.Length == 0 ? null : wildcards,
                ["controlTypeFilter"] = controlTypeFilter,
                ["controls"] = rows,
                ["warnings"] = warnings.ToArray(),
            };
        }
    }
}
