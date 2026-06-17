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
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitSchemas;

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
            var request = args is JObject obj ? (JObject)obj.DeepClone() : new JObject();
            request["action"] = "pointerClick";
            var result = InteractRuntime(request, status);
            var resolved = result.TryGetValue("target", out var target) && target != null;
            result["resolved"] = resolved;
            result["framework"] = "uitoolkit";
            return result;
        }

        internal static Dictionary<string, object?> InteractRuntime(JToken args, UiToolkitDependencyStatus status)
        {
            var warnings = new List<string>();
            var action = ReadString(args, "action") ?? "pointerClick";
            var dryRun = ReadBool(args, "dryRun", true);
            if (args["dryRun"] == null)
            {
                warnings.Add("dryRun was not specified; defaulted to true. No UI Toolkit event or value mutation was performed.");
            }

            var result = CreateEnvelope("tool://uitoolkit-runtime-interact", status);
            result["action"] = action;
            result["dryRun"] = dryRun;
            result["playMode"] = IsRuntimePlayModeActive();
            result["runtimeAvailable"] = EnsureRuntimeReadAllowed(warnings);
            result["allowStateMutation"] = ReadBool(args, "allowStateMutation", false);
            result["mutationRisk"] = "Real dispatch can invoke user callbacks and mutate game state; use dryRun:true to inspect first.";
            result["focusedElementBefore"] = CreateFocusedElementRow(status);
            result["dispatchedEvents"] = Array.Empty<string>();

            var resolution = ResolveRuntimeInteractionTarget(args, status, warnings);
            result["input"] = resolution.Position == null ? null : CreateScreenPositionRow(resolution.Position.Value);
            result["panelPosition"] = resolution.PanelPosition.HasValue ? CreateVector2Row(resolution.PanelPosition.Value) : null;
            result["resolvedBy"] = resolution.ResolvedBy;
            result["stack"] = resolution.Stack;
            result["target"] = resolution.Target == null ? null : CreateVisualElementRow(resolution.Target, status, resolution.Group ?? PanelGroup.FromElement(resolution.Target), includeTextAndValue: true);
            result["targetStateBefore"] = resolution.Target == null ? null : CreateVisualElementStateRow(resolution.Target, status);
            result["plan"] = CreateRuntimeInteractionPlan(action, resolution.Target, args);

            if (resolution.Target == null)
            {
                warnings.Add("No runtime UI Toolkit target resolved for interaction.");
            }

            if (!dryRun)
            {
                EnsureRuntimeMutationAllowed(args);
                if (resolution.Target == null)
                {
                    throw new InvalidOperationException("Runtime UI Toolkit interaction requires a resolved target.");
                }

                result["dispatchedEvents"] = ApplyRuntimeInteraction(action, resolution.Target, resolution.PanelPosition, args, warnings);
            }

            result["focusedElementAfter"] = CreateFocusedElementRow(status);
            result["targetStateAfter"] = resolution.Target == null ? null : CreateVisualElementStateRow(resolution.Target, status);
            result["warnings"] = warnings.Distinct().ToArray();
            return result;
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
                        if (!ChievfxMcpRuntimeUiControlFind.MatchesWildcards(elementName, elementPath, wildcards))
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
