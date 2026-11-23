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

        internal static RuntimeDragScreenGeometry ReadRuntimeDragGeometry(JToken args, List<string> warnings)
        {
            var isNormalized = ReadBool(args, "isNormalized", false);
            var screenshotRegion = IsScreenshotSpace(args) ? ReadScreenshotSpaceRegion(args, warnings) : (ScreenshotRegion?)null;
            var screenSize = ChievfxMcpRuntimeScreenSize.Resolve();
            RuntimeScreenPosition start;
            if (TryReadDragScreenPoint(args, "x", "y", "startScreenPosition", "startNormalized", isNormalized, screenshotRegion, screenSize, out var startScreen))
            {
                start = ToRuntimeScreenPosition(startScreen, screenSize);
            }
            else
            {
                throw new ArgumentException(
                    "ui-runtime-drag requires x/y start coordinates (or legacy startScreenPosition/startNormalized); path/instanceId only "
                    + "picks the target among the hits at that point. Take the start from the element's zone centre in ui-control-find or a screenshot.");
            }

            RuntimeScreenPosition end;
            if (TryReadDragScreenPoint(args, "toX", "toY", "endScreenPosition", "endNormalized", isNormalized, screenshotRegion, screenSize, out var endScreen))
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
            var adapterRequest = CreateAdapterDragRequest(request, geometry);
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
                    dragDetail = ReadDragDetail(((IChievfxMcpRuntimeUiDragAdapter)adapter).DragAtPosition(adapterRequest.DeepClone()));
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"ui-runtime-drag failed for framework '{adapter.FrameworkId}': {RootMessage(ex)}", ex);
                }

                var resolved = ReadResolvedFlag(dragDetail);
                // An adapter that reports handlerInvoked separates "hit something" from "something dragged";
                // one that does not keeps the older meaning, where a resolved target counts as dragged.
                var dragged = resolved
                    && (dragDetail == null || !dragDetail.ContainsKey("handlerInvoked") || ReadHandlerInvokedFlag(dragDetail));
                if (resolved)
                {
                    anyResolved = true;
                }

                if (dragged)
                {
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

            MergeAdapterWarnings(sections, anyResolved, warnings);

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
            ScreenshotRegion? screenshotRegion,
            Vector2 screenSize,
            out Vector2 screenPoint)
        {
            if (screenshotRegion.HasValue)
            {
                if (args[xKey] != null && args[yKey] != null)
                {
                    // Normalized 0..1 from the screenshot PNG (top-left origin), possibly a cropped one:
                    // map back onto the full view first, then flip Y to bottom-left Screen space.
                    var full = screenshotRegion.Value.ToFullViewNormalized(ReadFloat(args, xKey, 0f), ReadFloat(args, yKey, 0f));
                    screenPoint = new Vector2(full.x * screenSize.x, (1f - full.y) * screenSize.y);
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

            if (detail.TryGetValue("warnings", out var detailWarnings))
            {
                section["warnings"] = detailWarnings;
            }

            if (detail.TryGetValue("intendedHandler", out var handler))
            {
                section["handler"] = handler;
            }

            if (detail.TryGetValue("handlerObject", out var handlerObject))
            {
                section["handlerObject"] = handlerObject;
            }

            if (detail.TryGetValue("handlerInvoked", out var handlerInvoked))
            {
                section["handlerInvoked"] = handlerInvoked;
            }

            if (detail.TryGetValue("dispatchedEvents", out var events))
            {
                section["events"] = events;
            }

            if (detail.TryGetValue("pointerEvents", out var pointerEvents))
            {
                section["pointerEvents"] = pointerEvents;
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

            var adapterRequest = CreateAdapterInteractionRequest(request, position);
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
                    clickDetail = ReadClickDetail(((IChievfxMcpRuntimeUiClickAdapter)adapter).ClickAtPosition(adapterRequest.DeepClone()));
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

            MergeAdapterWarnings(sections, anyResolved, warnings);

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

        // An adapter's warnings are the explanation for what just happened — "click dispatched but no
        // recognized handler responded", "that path matched 177 objects, the active one was used". Folding
        // the adapter result into a framework section used to drop them, leaving a bare clicked:false with
        // nothing to act on. Surface the resolving framework's warnings; when nothing resolved, surface all
        // of them, because then they are the whole answer.
        private static void MergeAdapterWarnings(
            IEnumerable<Dictionary<string, object?>> sections,
            bool anyResolved,
            List<string> warnings)
        {
            foreach (var section in sections)
            {
                var sectionResolved = section.TryGetValue("resolved", out var resolved) && resolved is true;
                if (anyResolved && !sectionResolved)
                {
                    continue;
                }

                if (section.TryGetValue("warnings", out var sectionWarnings))
                {
                    warnings.AddRange(ChievfxMcpRuntimeUiProbeCompact.ReadStringArray(sectionWarnings));
                }
            }
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

            if (detail.TryGetValue("warnings", out var detailWarnings))
            {
                section["warnings"] = detailWarnings;
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

            if (detail.TryGetValue("pointerEvents", out var pointerEvents))
            {
                section["pointerEvents"] = pointerEvents;
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
                ["screenSizeSource"] = ChievfxMcpRuntimeScreenSize.DescribeResolvedSource(position.ScreenSize),
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
    }
}
