#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Chievfx.Mcp.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using static Chievfx.Mcp.Extensions.Ugui.ChievfxMcpUguiExtension;
using static Chievfx.Mcp.Extensions.Ugui.UguiDesignTools;
using static Chievfx.Mcp.Extensions.Ugui.UguiElementHelpers;
using static Chievfx.Mcp.Extensions.Ugui.UguiLayoutHelpers;
using static Chievfx.Mcp.Extensions.Ugui.UguiResourcesAndRows;
using static Chievfx.Mcp.Extensions.Ugui.UguiRuntimeControlHelpers;
using static Chievfx.Mcp.Extensions.Ugui.UguiRuntimeTools;
using static Chievfx.Mcp.Extensions.Ugui.UguiSchemas;
using static Chievfx.Mcp.Extensions.Ugui.UguiSharedHelpers;

namespace Chievfx.Mcp.Extensions.Ugui
{
    internal static class UguiRuntimeHelpers
    {
        internal static bool EnsureRuntimeReadAllowed(List<string> warnings)
        {
            if (IsRuntimePlayModeActive())
            {
                return true;
            }

            warnings.Add("Runtime uGUI reads are gated to Play Mode by default; enter Play Mode before reading runtime UI state.");
            return false;
        }

        internal static bool IsRuntimePlayModeActive()
        {
            if (runtimeReadAllowedOverrideForTests.HasValue)
            {
                return runtimeReadAllowedOverrideForTests.Value;
            }

            return EditorApplication.isPlaying || Application.isPlaying;
        }

        // Only the conditions that break every runtime interaction. Per-canvas facts (closed screens, HUD
        // canvases without a raycaster) are normal in a real game: a shipping UI produced 70+ of them on every
        // call, including successful ones. They are reported by AddCanvasMissWarnings, and only when nothing
        // was hit, scoped to the canvases under that point.
        internal static void AddRuntimeWarnings(UguiDependencyStatus status, List<string> warnings)
        {
            if (GetCurrentEventSystem(status) == null)
            {
                warnings.Add("No EventSystem.current found; uGUI selection and raycast routing may be unavailable.");
            }

            var canvases = FindRuntimeCanvases(status);
            if (status.GraphicRaycasterType == null
                || !canvases.Any(canvas => canvas.gameObject.activeInHierarchy
                    && IsEnabledComponent(canvas)
                    && canvas.gameObject.GetComponents(status.GraphicRaycasterType).OfType<Component>().Any(IsEnabledComponent)))
            {
                warnings.Add("No GraphicRaycaster found on runtime canvases; screen probes will not hit uGUI Graphics.");
            }
        }

        // Why an EventSystem raycast at this point can come back empty: a canvas covering it is inactive, has no
        // GraphicRaycaster of its own (graphics register with their nearest Canvas, so a nested canvas needs
        // one too), or is world-space with no camera to raycast from.
        internal static void AddCanvasMissWarnings(UguiDependencyStatus status, List<string> warnings, Vector2 screenPosition)
        {
            foreach (var canvas in FindRuntimeCanvases(status))
            {
                var renderMode = Convert.ToString(GetPropertyValue(canvas, "renderMode"), CultureInfo.InvariantCulture) ?? string.Empty;
                var isWorldSpace = string.Equals(renderMode, "WorldSpace", StringComparison.Ordinal);
                var eventCamera = ResolveCanvasEventCamera(canvas, renderMode);
                if (isWorldSpace && eventCamera == null)
                {
                    if (canvas.gameObject.activeInHierarchy && IsEnabledComponent(canvas))
                    {
                        warnings.Add($"Canvas '{GetTransformPath(canvas.transform)}' uses WorldSpace without worldCamera/event camera.");
                    }

                    continue;
                }

                if (canvas.transform is not RectTransform canvasRect
                    || !RectTransformUtility.RectangleContainsScreenPoint(canvasRect, screenPosition, eventCamera))
                {
                    continue;
                }

                if (!canvas.gameObject.activeInHierarchy || !IsEnabledComponent(canvas))
                {
                    warnings.Add($"Inactive canvas '{GetTransformPath(canvas.transform)}' will not receive runtime raycasts.");
                    continue;
                }

                if (!HasComponent(canvas.gameObject, status.GraphicRaycasterType))
                {
                    warnings.Add($"Canvas '{GetTransformPath(canvas.transform)}' has no GraphicRaycaster.");
                }
            }
        }

        internal static Camera? ResolveCanvasEventCamera(Component canvas, string renderMode)
        {
            if (string.Equals(renderMode, "ScreenSpaceOverlay", StringComparison.Ordinal))
            {
                return null;
            }

            var worldCamera = GetPropertyValue(canvas, "worldCamera") as Camera;
            if (worldCamera != null)
            {
                return worldCamera;
            }

            // A Screen Space - Camera canvas without a camera renders and raycasts as an overlay; a world-space
            // one falls back to Camera.main, as GraphicRaycaster.eventCamera does.
            return string.Equals(renderMode, "WorldSpace", StringComparison.Ordinal) ? Camera.main : null;
        }

        internal static Component? GetCurrentEventSystem(UguiDependencyStatus status)
        {
            if (status.EventSystemType == null)
            {
                return null;
            }

            var current = status.EventSystemType.GetProperty("current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as Component;
            if (current != null && current.gameObject.activeInHierarchy && IsEnabledComponent(current))
            {
                return current;
            }

            return FindObjectsOfType(status.EventSystemType)
                .OfType<Component>()
                .FirstOrDefault(component => component.gameObject.activeInHierarchy && IsEnabledComponent(component));
        }

        internal static object? CreateSelectedObjectRow(UguiDependencyStatus status)
        {
            var current = GetCurrentEventSystem(status);
            var selected = current == null ? null : GetPropertyValue(current, "currentSelectedGameObject") as GameObject;
            return selected == null ? null : CreateRuntimeElementRow(selected, status);
        }

        internal static Dictionary<string, object?> CreateRuntimeInteractionEnvelope(string uri, UguiDependencyStatus status, bool dryRun, JToken args, List<string> warnings)
        {
            var result = CreateEnvelope(uri, status);
            result["dryRun"] = dryRun;
            result["playMode"] = IsRuntimePlayModeActive();
            result["runtimeAvailable"] = EnsureRuntimeReadAllowed(warnings);
            result["allowStateMutation"] = ReadBool(args, "allowStateMutation", false);
            result["selectedObjectBefore"] = CreateSelectedObjectRow(status);
            return result;
        }

        internal static EventSystem? RequireRuntimeEventSystem(UguiDependencyStatus status, List<string> warnings, bool dryRun)
        {
            if (!EnsureRuntimeReadAllowed(warnings))
            {
                return null;
            }

            AddRuntimeWarnings(status, warnings);
            var eventSystem = GetCurrentEventSystem(status) as EventSystem;
            if (eventSystem == null && !dryRun)
            {
                throw new InvalidOperationException("Runtime uGUI interaction requires an active EventSystem.current.");
            }

            return eventSystem;
        }

        // Pooled UI makes a hierarchy path ambiguous: a shop list can hold 177 objects all named
        // "Root/List/CarItem(Clone)", nearly all of them deactivated in the pool. Transform.Find hands back
        // whichever it walks into first, so targeting by path used to land on a disabled pooled entry and
        // report "no handler" as if the control itself were broken. Prefer the live one and say what was
        // ambiguous. The scan only runs when the cheap lookup came back empty or inactive, so the common
        // unambiguous case stays a single Transform.Find.
        internal static GameObject? ResolveRuntimeTargetFromArgs(JToken args, string pathKey, string instanceIdKey, List<string> warnings)
        {
            var byInstanceId = ResolveGameObject(ReadInt(args, instanceIdKey, 0));
            if (byInstanceId != null)
            {
                return byInstanceId;
            }

            var path = ReadString(args, pathKey);
            if (HasIndexedSegment(path))
            {
                var indexed = ResolveIndexedTransformPath(path!);
                if (indexed != null)
                {
                    return indexed;
                }
            }

            var firstMatch = ResolveGameObject(path);
            if (firstMatch == null)
            {
                return null;
            }

            var matches = FindGameObjectsByRuntimePath(path!);
            if (firstMatch.activeInHierarchy)
            {
                // Same-named siblings (pooled list rows) share one plain path: say which one was taken and how to pick another.
                var activeCount = matches.Count(candidate => candidate.activeInHierarchy);
                if (activeCount > 1)
                {
                    warnings.Add($"Path '{path}' matches {activeCount} active objects (same-named siblings); used '{GetIndexedTransformPath(firstMatch.transform)}', "
                        + $"instanceId {UnityObjectIdentity.GetLegacyInstanceId(firstMatch)}. Pass instanceId, or a path with [k] (1-based among same-named siblings) as ui-control-find prints it.");
                }

                return firstMatch;
            }

            var activeMatch = matches.FirstOrDefault(candidate => candidate.activeInHierarchy);
            if (activeMatch == null)
            {
                if (matches.Length > 1)
                {
                    warnings.Add($"Path '{path}' matched {matches.Length} objects and none are active in the hierarchy; using instanceId {UnityObjectIdentity.GetLegacyInstanceId(firstMatch)}. Pass instanceId to pick one.");
                }
                else
                {
                    warnings.Add($"Path '{path}' resolved to an object that is inactive in the hierarchy; runtime interactions will not reach it.");
                }

                return firstMatch;
            }

            if (matches.Length > 1)
            {
                warnings.Add($"Path '{path}' matched {matches.Length} objects ({matches.Count(candidate => candidate.activeInHierarchy)} active, typically a pooled list); using the active one, instanceId {UnityObjectIdentity.GetLegacyInstanceId(activeMatch)}. Pass instanceId to pick a specific object.");
            }

            return activeMatch;
        }

        internal static GameObject[] FindGameObjectsByRuntimePath(string path)
        {
            var normalized = path.Trim().Trim('/');
            if (normalized.Length == 0)
            {
                return Array.Empty<GameObject>();
            }

            // Callers pass either the full path the tools report or one relative to a scene root, so accept a
            // suffix on a '/' boundary. Filtering on the leaf name first keeps this off the hot path cost.
            var leafName = normalized.Substring(normalized.LastIndexOf('/') + 1);
            return Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(candidate => candidate.scene.IsValid()
                    && string.Equals(candidate.name, leafName, StringComparison.Ordinal))
                .Where(candidate =>
                {
                    var fullPath = GetTransformPath(candidate.transform);
                    return string.Equals(fullPath, normalized, StringComparison.Ordinal)
                        || fullPath.EndsWith("/" + normalized, StringComparison.Ordinal);
                })
                .ToArray();
        }

        internal enum RuntimeHitPurpose
        {
            Click,
            Drag,
            // ui-runtime-click by position: like Click, but a hit with no handler is still the target, so the reply says
            // the point was hit and nothing handled it (resolved:true, clicked:false) instead of "nothing resolved".
            Tap,
        }

        internal static GameObject? ResolveRuntimeInteractionTarget(
            JToken args,
            UguiDependencyStatus status,
            EventSystem? eventSystem,
            Vector2 screenPosition,
            List<string> warnings,
            out Dictionary<string, object?>[] stack,
            RuntimeHitPurpose purpose = RuntimeHitPurpose.Click)
        {
            var explicitTarget = ResolveRuntimeTargetFromArgs(args, "path", "instanceId", warnings)
                ?? ResolveRuntimeTargetFromArgs(args, "targetPath", "instanceId", warnings);
            if (explicitTarget != null)
            {
                stack = Array.Empty<Dictionary<string, object?>>();
                return explicitTarget;
            }

            if (ChievfxMcpRuntimeUiInteractionInput.HasExplicitTargetInput(args))
            {
                warnings.Add(ChievfxMcpRuntimeUiInteractionInput.FormatTargetNotFoundMessage(args, "uGUI"));
                stack = Array.Empty<Dictionary<string, object?>>();
                return null;
            }

            if (eventSystem == null)
            {
                stack = Array.Empty<Dictionary<string, object?>>();
                return null;
            }

            if (IsOutsideScreen(screenPosition, ResolveRuntimeUiScreenSize(status)))
            {
                warnings.Add("Coordinate is outside current screen/game-view bounds.");
            }

            status.CanvasType?.GetMethod("ForceUpdateCanvases", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
            var raycastResults = RaycastAll(status, eventSystem, screenPosition, warnings);
            if (raycastResults.Length > 0)
            {
                stack = raycastResults.Select((raycastResult, index) => CreateRuntimeStackRow(raycastResult, index, status)).ToArray();
                var hits = raycastResults
                    .Select(raycastResult => GetMemberValue(raycastResult, "gameObject") as GameObject)
                    .Where(hit => hit != null)
                    .Cast<GameObject>()
                    .ToArray();
                return ResolveTargetFromHits(hits, status, purpose, warnings);
            }

            // Nothing a real pointer could hit is here. Say why, then fall back to what merely overlaps the point,
            // and say that too: a target picked this way is one the player cannot reach by tapping.
            AddCanvasMissWarnings(status, warnings, screenPosition);
            var rectHits = FindRuntimeRectHits(screenPosition, status).ToArray();
            stack = rectHits.Select((rect, index) =>
            {
                var row = CreateRuntimeElementRow(rect.gameObject, status);
                row["stackIndex"] = index;
                row["raycastResult"] = new Dictionary<string, object?>
                {
                    ["source"] = "RectTransformUtility.RectangleContainsScreenPoint",
                    ["screenPosition"] = Vector2Row(screenPosition),
                };
                row["clickableHandlerTarget"] = CreateClickableHandlerTargetRow(rect.gameObject, status);
                return row;
            }).ToArray();
            // Elements a player can neither see nor tap (a hidden debug console under a transparent CanvasGroup) are left
            // out: picking one pressed a button nobody could see and reported it clicked.
            var visibleOverlaps = rectHits.Select(rect => rect.gameObject).Where(hit => !UguiRuntimeReachability.IsHiddenFromPlayer(hit)).ToArray();
            if (visibleOverlaps.Length == 0 && rectHits.Length > 0)
            {
                warnings.Add($"The EventSystem raycast hit nothing at this point, and the {rectHits.Length} element(s) overlapping it are hidden from the player (inactive, transparent or not blocking raycasts): nothing was picked.");
                return null;
            }

            var overlapTarget = ResolveTargetFromHits(visibleOverlaps, status, purpose, warnings);
            if (overlapTarget != null)
            {
                warnings.Add($"The EventSystem raycast hit nothing at this point; '{GetTransformPath(overlapTarget.transform)}' was picked because its RectTransform overlaps it, so a real tap would not reach it.");
            }

            return overlapTarget;
        }

        // Click: the first hit that reacts to a press or click, promoted to its control (a label inside a
        // Button resolves to the Button). Drag: the first hit with a drag handler above it, or else the top hit,
        // so "hit but nothing drags here" is reported rather than "nothing hit". Raycast plumbing objects
        // (UI Toolkit's PanelRaycaster) are skipped; a real blocker above the chosen target is called out.
        private static GameObject? ResolveTargetFromHits(GameObject[] hits, UguiDependencyStatus status, RuntimeHitPurpose purpose, List<string> warnings)
        {
            var chosenIndex = Array.FindIndex(hits, hit => purpose == RuntimeHitPurpose.Drag
                ? hit.activeInHierarchy && ExecuteEvents.GetEventHandler<IDragHandler>(hit) != null
                : IsRuntimeInteractionCandidate(hit, status));
            if (chosenIndex < 0)
            {
                return purpose == RuntimeHitPurpose.Drag || purpose == RuntimeHitPurpose.Tap
                    ? hits.FirstOrDefault(hit => hit.activeInHierarchy && !IsRaycastInfrastructureTarget(hit, status))
                    : null;
            }

            var chosen = purpose == RuntimeHitPurpose.Drag ? hits[chosenIndex] : PromoteToClickableControlTarget(hits[chosenIndex], status)!;
            var blocker = hits
                .Take(chosenIndex)
                .FirstOrDefault(hit => !IsRaycastInfrastructureTarget(hit, status) && !hit.transform.IsChildOf(chosen.transform));
            if (blocker != null)
            {
                warnings.Add($"'{GetTransformPath(blocker.transform)}' is above '{GetTransformPath(chosen.transform)}' at this point and would catch a real tap; the tool went past it because it does not handle this interaction.");
            }

            return chosen;
        }

        internal static GameObject? PromoteToClickableControlTarget(GameObject? hit, UguiDependencyStatus status)
        {
            if (hit == null)
            {
                return null;
            }
            if (GetClickableControlComponents(hit, status).Any(control => IsEnabledComponent(control)))
            {
                return hit;
            }

            for (var current = hit.transform.parent; current != null; current = current.parent)
            {
                var candidate = current.gameObject;
                if (GetClickableControlComponents(candidate, status).Any(control => IsEnabledComponent(control)))
                {
                    return candidate;
                }
            }

            return ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit) ?? hit;
        }

        internal static bool IsRuntimeInteractionCandidate(GameObject target, UguiDependencyStatus status)
        {
            if (!target.activeInHierarchy)
            {
                return false;
            }

            if (GetClickableControlComponents(target, status).Any(control => IsEnabledComponent(control)))
            {
                return true;
            }

            if (!IsRuntimeProbeHitElement(target, status) || IsRaycastInfrastructureTarget(target, status))
            {
                return false;
            }

            // A press handler takes the tap too: the input module sends PointerDown up the hierarchy whether or
            // not anything handles PointerClick.
            return ExecuteEvents.GetEventHandler<IPointerClickHandler>(target) != null
                || ExecuteEvents.GetEventHandler<IPointerDownHandler>(target) != null;
        }

        internal static bool IsRaycastInfrastructureTarget(GameObject target, UguiDependencyStatus status)
        {
            if (GetClickableControlComponents(target, status).Any())
            {
                return false;
            }

            var components = target.GetComponents<Component>()
                .Where(component => component != null && component is not Transform)
                .ToArray();
            if (components.Length == 0)
            {
                return true;
            }

            return components.All(IsRaycastInfrastructureComponent);
        }

        internal static bool IsRaycastInfrastructureComponent(Component component)
        {
            return component switch
            {
                CanvasRenderer => true,
                _ => IsRaycastInfrastructureComponentName(component.GetType().Name),
            };
        }

        internal static bool IsRaycastInfrastructureComponentName(string typeName)
        {
            return string.Equals(typeName, "PanelRaycaster", StringComparison.Ordinal)
                || string.Equals(typeName, "PanelEventHandler", StringComparison.Ordinal)
                || string.Equals(typeName, "GraphicRaycaster", StringComparison.Ordinal);
        }

        // A nested Canvas is listed both on its own and inside its parent canvas's children, so without Distinct
        // every element under it appeared twice in the hit stack.
        internal static IEnumerable<RectTransform> FindRuntimeRectHits(Vector2 screenPosition, UguiDependencyStatus status)
        {
            return FindRuntimeCanvases(status)
                .Where(canvas => canvas.gameObject.activeInHierarchy && IsEnabledComponent(canvas))
                .SelectMany(canvas => canvas.GetComponentsInChildren<RectTransform>(false))
                .Distinct()
                .Where(rect => rect.gameObject != null
                    && rect.gameObject.activeInHierarchy
                    && IsRuntimeVisibleUiElement(rect.gameObject, status)
                    && IsRuntimeProbeHitElement(rect.gameObject, status)
                    && RectTransformUtility.RectangleContainsScreenPoint(rect, screenPosition, null))
                .OrderByDescending(rect => GetCanvasSortingOrder(FindParentCanvas(rect.gameObject, status)))
                .ThenByDescending(GetTransformSiblingSortKey, StringComparer.Ordinal)
                .Take(256);
        }

        internal static Component[] FindRuntimeCanvases(UguiDependencyStatus status)
        {
            return FindCanvases(status, includeInactive: true);
        }

        internal static object[] RaycastAll(UguiDependencyStatus status, Component eventSystem, Vector2 screenPosition, List<string> warnings)
        {
            if (status.PointerEventDataType == null || status.RaycastResultType == null || status.EventSystemType == null)
            {
                warnings.Add("PointerEventData or RaycastResult type unavailable; cannot run EventSystem.RaycastAll.");
                return Array.Empty<object>();
            }

            var pointerEventDataConstructor = status.PointerEventDataType.GetConstructor(new[] { status.EventSystemType });
            if (pointerEventDataConstructor == null)
            {
                warnings.Add("PointerEventData(EventSystem) constructor unavailable; cannot run EventSystem.RaycastAll.");
                return Array.Empty<object>();
            }

            var pointerEventData = pointerEventDataConstructor.Invoke(new object[] { eventSystem });
            SetProperty(pointerEventData!, "position", screenPosition);
            var listType = typeof(List<>).MakeGenericType(status.RaycastResultType);
            var results = (IList)Activator.CreateInstance(listType)!;
            var method = status.EventSystemType?.GetMethod("RaycastAll", new[] { status.PointerEventDataType, listType });
            if (method == null)
            {
                warnings.Add("EventSystem.RaycastAll(PointerEventData,List<RaycastResult>) was unavailable.");
                return Array.Empty<object>();
            }

            if (eventSystem is EventSystem stockEventSystem && pointerEventData is PointerEventData pointer && results is List<RaycastResult> typedResults)
            {
                UguiTargetSizeRaycast.RaycastAll(stockEventSystem, pointer, typedResults);
            }
            else
            {
                method.Invoke(eventSystem, new object[] { pointerEventData!, results });
            }

            if (results.Count == 0)
            {
                foreach (var raycastResult in GraphicRaycastAll(status, pointerEventData!, listType))
                {
                    results.Add(raycastResult);
                }
            }

            // Two raycasters on one canvas each report the same Graphic; one row per hit object is the stack.
            var seen = new HashSet<GameObject>();
            return results.Cast<object>()
                .Where(raycastResult => GetMemberValue(raycastResult, "gameObject") is not GameObject hit || seen.Add(hit))
                .ToArray();
        }

        internal static object[] GraphicRaycastAll(UguiDependencyStatus status, object pointerEventData, Type listType)
        {
            if (status.GraphicRaycasterType == null || status.PointerEventDataType == null)
            {
                return Array.Empty<object>();
            }

            var raycastMethod = status.GraphicRaycasterType.GetMethod("Raycast", new[] { status.PointerEventDataType, listType });
            if (raycastMethod == null)
            {
                return Array.Empty<object>();
            }

            var collected = (IList)Activator.CreateInstance(listType)!;
            foreach (var canvas in FindRuntimeCanvases(status).Where(canvas => canvas.gameObject.activeInHierarchy && IsEnabledComponent(canvas)))
            {
                var raycasters = canvas.gameObject
                    .GetComponents(status.GraphicRaycasterType)
                    .OfType<Component>()
                    .Where(IsEnabledComponent);
                foreach (var raycaster in raycasters)
                {
                    var canvasResults = (IList)Activator.CreateInstance(listType)!;
                    raycastMethod.Invoke(raycaster, new[] { pointerEventData, canvasResults });
                    foreach (var result in canvasResults)
                    {
                        collected.Add(result);
                    }
                }
            }

            return collected.Cast<object>().ToArray();
        }

        internal static Dictionary<string, object?>[] CreateRuntimeRectHitStack(Vector2 screenPosition, UguiDependencyStatus status)
        {
            var hits = FindRuntimeRectHits(screenPosition, status)
                .Select((rect, index) =>
                {
                    var row = CreateRuntimeElementRow(rect.gameObject, status);
                    row["stackIndex"] = index;
                    row["raycastResult"] = new Dictionary<string, object?>
                    {
                        ["source"] = "RectTransformUtility.RectangleContainsScreenPoint",
                        ["screenPosition"] = Vector2Row(screenPosition),
                    };
                    row["clickableHandlerTarget"] = CreateClickableHandlerTargetRow(rect.gameObject, status);
                    return row;
                })
                .ToArray();
            return hits;
        }

        internal static int GetCanvasSortingOrder(Component? canvas)
        {
            if (canvas == null)
            {
                return 0;
            }

            return GetPropertyValue(canvas, "sortingOrder") is int sortingOrder ? sortingOrder : 0;
        }

        internal static string GetTransformSiblingSortKey(RectTransform rect)
        {
            var indices = new Stack<int>();
            for (var current = rect.transform; current != null; current = current.parent)
            {
                indices.Push(current.GetSiblingIndex());
            }

            return string.Join("/", indices.Select(index => index.ToString("D5", CultureInfo.InvariantCulture)));
        }

        internal static Dictionary<string, object?> CreateRuntimeStackRow(object raycastResult, int index, UguiDependencyStatus status)
        {
            var target = GetMemberValue(raycastResult, "gameObject") as GameObject;
            var module = GetMemberValue(raycastResult, "module") as Component;
            var row = target == null
                ? new Dictionary<string, object?>()
                : CreateRuntimeElementRow(target, status);
            row["stackIndex"] = index;
            row["canvas"] = target == null ? null : CreateCanvasReferenceRow(target, status);
            row["raycastResult"] = CreateRaycastResultRow(raycastResult, module);
            row["clickableHandlerTarget"] = target == null ? null : CreateClickableHandlerTargetRow(target, status);
            return row;
        }

        internal static Dictionary<string, object?> CreateCompactProbeStackRow(object raycastResult, int index, UguiDependencyStatus status)
        {
            var target = GetMemberValue(raycastResult, "gameObject") as GameObject;
            if (target == null)
            {
                return new Dictionary<string, object?> { ["i"] = index };
            }

            var controls = GetControlComponents(target, status).Select(component => component.GetType().Name).ToArray();
            var canvas = FindParentCanvas(target, status);
            var sorting = CreateSortingRow(canvas);
            var interactionEnabled = ResolveInteractionEnabled(target, status, out var disabledComponents);
            var row = new Dictionary<string, object?>
            {
                ["i"] = index,
                ["path"] = GetTransformPath(target.transform),
                ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(target),
                ["type"] = ResolveCompactProbeHitType(target, GetMemberValue(raycastResult, "module") as Component, controls, status),
                ["enabled"] = interactionEnabled,
                ["interactable"] = GetFirstPropertyValue(target, "interactable", status.SelectableType, status.ButtonType, status.ToggleType, status.SliderType, status.ScrollbarType, status.DropdownType, status.TmpDropdownType, status.InputFieldType),
                ["raycastTarget"] = GetFirstPropertyValue(target, "raycastTarget", status.GraphicType, status.ImageType, status.TmpTextType),
            };
            if (disabledComponents.Length > 0)
            {
                row["disabledComponents"] = disabledComponents;
            }

            if (controls.Length > 0)
            {
                row["controls"] = controls;
            }

            var handlers = GetEventHandlerTypeNames(target, controls);
            if (handlers.Length > 0)
            {
                row["handlers"] = handlers;
            }

            if (sorting != null && sorting.TryGetValue("sortingOrder", out var sortingOrder))
            {
                row["sortingOrder"] = sortingOrder;
            }

            var clickTarget = ExecuteEvents.GetEventHandler<IPointerClickHandler>(target) ?? target;
            if (clickTarget != target)
            {
                row["handlerPath"] = GetTransformPath(clickTarget.transform);
            }

            return row;
        }

        internal static Dictionary<string, object?> CompactRectHitStackRow(Dictionary<string, object?> row)
        {
            if (row.TryGetValue("clickableHandlerTarget", out var handler)
                && handler is Dictionary<string, object?> handlerRow
                && handlerRow.TryGetValue("path", out var handlerPath))
            {
                row["handlerPath"] = handlerPath;
            }

            if (row.TryGetValue("sorting", out var sorting)
                && sorting is Dictionary<string, object?> sortingRow
                && sortingRow.TryGetValue("sortingOrder", out var sortingOrder))
            {
                row["sortingOrder"] = sortingOrder;
            }

            if (row.TryGetValue("controlComponents", out var controlComponents)
                && controlComponents is IEnumerable controlNames)
            {
                foreach (var name in controlNames)
                {
                    if (name == null)
                    {
                        continue;
                    }

                    row["type"] = Convert.ToString(name, CultureInfo.InvariantCulture);
                    break;
                }
            }

            return ChievfxMcpRuntimeUiProbeCompact.CompactUguiHit(row);
        }

        private static string? moduleTypeName(Component? module)
        {
            return module == null ? null : module.GetType().Name;
        }

        private static string ResolveCompactProbeHitType(
            GameObject target,
            Component? raycastModule,
            string[] controls,
            UguiDependencyStatus status)
        {
            if (controls.Length > 0)
            {
                return controls[0];
            }

            if (status.TmpTextType != null && target.GetComponent(status.TmpTextType) != null)
            {
                return status.TmpTextType.Name;
            }

            if (status.ImageType != null && target.GetComponent(status.ImageType) is Component image)
            {
                return image.GetType().Name;
            }

            if (status.GraphicType != null && target.GetComponent(status.GraphicType) is Component graphic)
            {
                return graphic.GetType().Name;
            }

            var moduleName = moduleTypeName(raycastModule);
            if (!string.IsNullOrWhiteSpace(moduleName)
                && !string.Equals(moduleName, "GraphicRaycaster", StringComparison.Ordinal))
            {
                return moduleName!;
            }

            return "Graphic";
        }

        internal static Dictionary<string, object?> CreateRuntimeElementRow(GameObject target, UguiDependencyStatus status)
        {
            var row = CreateGameObjectRow(target);
            row["type"] = "GameObject";
            var controlComponents = GetControlComponents(target, status).Select(component => component.GetType().Name).ToArray();
            row["controlComponents"] = controlComponents;
            row["enabled"] = ResolveInteractionEnabled(target, status, out var disabledComponents);
            if (disabledComponents.Length > 0)
            {
                row["disabledComponents"] = disabledComponents;
            }

            var handlers = GetEventHandlerTypeNames(target, controlComponents);
            if (handlers.Length > 0)
            {
                row["handlers"] = handlers;
            }

            row["interactable"] = GetFirstPropertyValue(target, "interactable", status.SelectableType, status.ButtonType, status.ToggleType, status.SliderType, status.ScrollbarType, status.DropdownType, status.TmpDropdownType, status.InputFieldType);
            row["raycastTarget"] = GetFirstPropertyValue(target, "raycastTarget", status.GraphicType, status.ImageType, status.TmpTextType);
            row["canvas"] = CreateCanvasReferenceRow(target, status);
            row["sorting"] = CreateSortingRow(FindParentCanvas(target, status));
            return row;
        }

        internal static Dictionary<string, object?> CreateRuntimeCanvasRow(Component canvas, UguiDependencyStatus status)
        {
            var row = CreateGameObjectRow(canvas.gameObject);
            row["enabled"] = IsEnabledComponent(canvas);
            // The canvas reference row of a Canvas is the canvas itself, so it only repeated path, render mode
            // and sorting. A nested canvas names its parent canvas instead.
            var parentCanvas = canvas.transform.parent == null ? null : FindParentCanvas(canvas.transform.parent.gameObject, status);
            if (parentCanvas != null)
            {
                row["parentCanvas"] = GetTransformPath(parentCanvas.transform);
            }

            row["sorting"] = CreateSortingRow(canvas);
            row["renderMode"] = Convert.ToString(GetPropertyValue(canvas, "renderMode"), CultureInfo.InvariantCulture);
            row["worldCamera"] = CreateObjectReferenceRow(GetPropertyValue(canvas, "worldCamera") as UnityEngine.Object);
            row["graphicRaycaster"] = CreateComponentReferenceRow(canvas.gameObject.GetComponent(status.GraphicRaycasterType));
            return row;
        }

        internal static bool IsRuntimeVisibleUiElement(GameObject target, UguiDependencyStatus status)
        {
            return target.activeInHierarchy
                && (GetControlComponents(target, status).Any()
                    || HasComponent(target, status.GraphicType)
                    || HasComponent(target, status.ImageType)
                    || HasComponent(target, status.TmpTextType));
        }

        internal static bool IsRuntimeProbeHitElement(GameObject target, UguiDependencyStatus status)
        {
            if (GetControlComponents(target, status).Any())
            {
                return true;
            }

            // A disabled Graphic keeps raycastTarget set but is unregistered from raycasting, so it cannot be hit.
            return status.GraphicType != null
                && target.GetComponents(status.GraphicType)
                    .OfType<Behaviour>()
                    .Any(graphic => graphic.enabled && GetPropertyValue(graphic, "raycastTarget") is true);
        }

        // "enabled" used to mean "every Behaviour on this GameObject is enabled", so a single unrelated
        // disabled script (an Animator, a game-side controller) reported a perfectly clickable button as
        // "not enabled" — a false negative that sends callers chasing the wrong cause. Scope it to the
        // components that actually decide whether this hit can react: the raycast Graphic and the event
        // handlers. When it is false, name them, because "not enabled" alone says nothing actionable.
        internal static bool ResolveInteractionEnabled(GameObject target, UguiDependencyStatus status, out string[] disabledComponents)
        {
            var relevant = target.GetComponents<Component>()
                .OfType<Behaviour>()
                .Where(behaviour => behaviour is IEventSystemHandler
                    || IsComponentOfType(behaviour, status.GraphicType)
                    || IsComponentOfType(behaviour, status.ImageType)
                    || IsComponentOfType(behaviour, status.TmpTextType))
                .ToArray();

            disabledComponents = relevant
                .Where(behaviour => !behaviour.enabled)
                .Select(behaviour => behaviour.GetType().Name)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return disabledComponents.Length == 0;
        }

        // The handler types are the difference between "this is a Button whose onClick I can fire" and "this
        // is an EventTrigger with PointerDown/PointerUp entries that a synthetic click cannot satisfy" —
        // exactly what is needed to diagnose a click that resolves but does nothing. Control components are
        // already reported separately, so skip those names here.
        internal static string[] GetEventHandlerTypeNames(GameObject target, IEnumerable<string> reportedControls)
        {
            var known = new HashSet<string>(reportedControls, StringComparer.Ordinal);
            return target.GetComponents<Component>()
                .OfType<IEventSystemHandler>()
                .Select(handler => handler.GetType().Name)
                .Where(name => !known.Contains(name))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        private static bool IsComponentOfType(Component component, Type? type)
        {
            return type != null && type.IsInstanceOfType(component);
        }

        internal static Component[] GetControlComponents(GameObject target, UguiDependencyStatus status)
        {
            var types = new[]
            {
                status.ButtonType,
                status.ToggleType,
                status.SliderType,
                status.ScrollbarType,
                status.ScrollRectType,
                status.DropdownType,
                status.TmpDropdownType,
                status.InputFieldType,
            }.Where(type => type != null).Cast<Type>();
            return types.SelectMany(type => target.GetComponents(type).OfType<Component>()).ToArray();
        }

        internal static Component[] GetClickableControlComponents(GameObject target, UguiDependencyStatus status)
        {
            var controls = GetControlComponents(target, status).ToList();
            var tmpInputFieldType = FindType("TMPro.TMP_InputField");
            if (tmpInputFieldType != null)
            {
                controls.AddRange(target.GetComponents(tmpInputFieldType).OfType<Component>());
            }

            return controls
                .GroupBy(component => UnityObjectIdentity.GetLegacyInstanceId(component))
                .Select(group => group.First())
                .ToArray();
        }

        internal static bool IsEnabledClickableControl(GameObject target, Component control)
        {
            if (!target.activeInHierarchy || !IsEnabledComponent(control))
            {
                return false;
            }

            var interactable = GetPropertyValue(control, "interactable");
            return interactable is not bool interactableValue || interactableValue;
        }

        internal static bool TryGetUguiScreenZone(
            GameObject target,
            UguiDependencyStatus status,
            Vector2 screenSize,
            out Dictionary<string, object?> zone)
        {
            zone = new Dictionary<string, object?>();
            var screenRect = CreateScreenRectRow(target.GetComponent<RectTransform>(), status, normalizedCoords: false);
            if (screenRect == null
                || screenRect["rect"] is not Dictionary<string, object?> rect
                || rect["xMin"] is not float xMin
                || rect["yMin"] is not float yMin
                || rect["xMax"] is not float xMax
                || rect["yMax"] is not float yMax)
            {
                return false;
            }

            if (!ChievfxMcpRuntimeUiControlFind.IsZonePartiallyOnScreen(xMin, yMin, xMax, yMax, screenSize))
            {
                return false;
            }

            zone = ChievfxMcpRuntimeUiControlFind.CreateZoneRow(xMin, yMin, xMax, yMax, screenSize);
            return true;
        }

        internal static object? GetFirstPropertyValue(GameObject target, string propertyName, params Type?[] componentTypes)
        {
            foreach (var type in componentTypes.Where(type => type != null).Cast<Type>())
            {
                var component = target.GetComponent(type);
                if (component == null)
                {
                    continue;
                }

                var value = GetPropertyValue(component, propertyName);
                if (value != null)
                {
                    return value;
                }
            }

            return null;
        }

        internal static Component? FindParentCanvas(GameObject target, UguiDependencyStatus status)
        {
            if (status.CanvasType == null)
            {
                return null;
            }

            for (var current = target.transform; current != null; current = current.parent)
            {
                var canvas = current.GetComponent(status.CanvasType) as Component;
                if (canvas != null)
                {
                    return canvas;
                }
            }

            return null;
        }

        internal static Dictionary<string, object?>? CreateCanvasReferenceRow(GameObject target, UguiDependencyStatus status)
        {
            var canvas = FindParentCanvas(target, status);
            if (canvas == null)
            {
                return null;
            }

            return new Dictionary<string, object?>
            {
                ["path"] = GetTransformPath(canvas.transform),
                ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(canvas.gameObject),
                ["renderMode"] = Convert.ToString(GetPropertyValue(canvas, "renderMode"), CultureInfo.InvariantCulture),
                ["sorting"] = CreateSortingRow(canvas),
            };
        }

        internal static Dictionary<string, object?>? CreateSortingRow(Component? canvas)
        {
            if (canvas == null)
            {
                return null;
            }

            var sortingLayerId = GetPropertyValue(canvas, "sortingLayerID");
            return new Dictionary<string, object?>
            {
                ["overrideSorting"] = GetPropertyValue(canvas, "overrideSorting"),
                ["sortingLayerId"] = sortingLayerId,
                ["sortingLayerName"] = sortingLayerId is int layerId ? SortingLayer.IDToName(layerId) : string.Empty,
                ["sortingOrder"] = GetPropertyValue(canvas, "sortingOrder"),
                ["targetDisplay"] = GetPropertyValue(canvas, "targetDisplay"),
            };
        }

        internal static Dictionary<string, object?> CreateRaycastResultRow(object raycastResult, Component? module)
        {
            return new Dictionary<string, object?>
            {
                ["module"] = CreateComponentReferenceRow(module),
                ["distance"] = GetMemberValue(raycastResult, "distance"),
                ["index"] = GetMemberValue(raycastResult, "index"),
                ["depth"] = GetMemberValue(raycastResult, "depth"),
                ["sortingLayer"] = GetMemberValue(raycastResult, "sortingLayer"),
                ["sortingOrder"] = GetMemberValue(raycastResult, "sortingOrder"),
                ["displayIndex"] = GetMemberValue(raycastResult, "displayIndex"),
                ["worldPosition"] = Vector3Row(GetMemberValue(raycastResult, "worldPosition")),
                ["worldNormal"] = Vector3Row(GetMemberValue(raycastResult, "worldNormal")),
                ["screenPosition"] = Vector2Row(GetMemberValue(raycastResult, "screenPosition")),
            };
        }

        internal static Dictionary<string, object?>? CreateClickableHandlerTargetRow(GameObject target, UguiDependencyStatus status)
        {
            if (status.PointerClickHandlerType == null)
            {
                return null;
            }

            for (var current = target.transform; current != null; current = current.parent)
            {
                var handler = current.GetComponents<Component>().FirstOrDefault(component => component != null && status.PointerClickHandlerType.IsAssignableFrom(component.GetType()));
                if (handler != null)
                {
                    return new Dictionary<string, object?>
                    {
                        ["path"] = GetTransformPath(current),
                        ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(current.gameObject),
                        ["component"] = handler.GetType().Name,
                    };
                }
            }

            return null;
        }

        internal static string[] CreateRuntimeProbeHierarchyLines(IEnumerable<Dictionary<string, object?>> stack, bool includeAllComponents, UguiDependencyStatus status)
        {
            var included = new HashSet<Transform>();
            foreach (var row in stack)
            {
                AddProbeHierarchyPath(row.TryGetValue("path", out var path) ? path as string : null, included);
                if (row.TryGetValue("clickableHandlerTarget", out var handlerValue)
                    && handlerValue is Dictionary<string, object?> handler
                    && handler.TryGetValue("path", out var handlerPath))
                {
                    AddProbeHierarchyPath(handlerPath as string, included);
                }
            }

            var lines = new List<string>();
            foreach (var root in GetPathResolutionRoots()
                .Select(root => root.transform)
                .Where(included.Contains)
                .OrderBy(transform => transform.GetSiblingIndex()))
            {
                AppendProbeHierarchyLines(root, included, includeAllComponents, status, 0, lines);
            }

            return lines.ToArray();
        }

        internal static void AddProbeHierarchyPath(string? path, HashSet<Transform> included)
        {
            var target = ResolveGameObject(path);
            if (target == null)
            {
                return;
            }

            for (var current = target.transform; current != null; current = current.parent)
            {
                included.Add(current);
            }
        }

        internal static void AppendProbeHierarchyLines(Transform transform, HashSet<Transform> included, bool includeAllComponents, UguiDependencyStatus status, int depth, List<string> lines)
        {
            var prefix = depth == 0 ? string.Empty : new string('-', depth);
            var labels = CreateProbeComponentLabels(transform.gameObject, includeAllComponents, status);
            lines.Add(labels.Length == 0
                ? prefix + transform.name
                : prefix + transform.name + " [" + string.Join(", ", labels) + "]");

            foreach (Transform child in transform)
            {
                if (included.Contains(child))
                {
                    AppendProbeHierarchyLines(child, included, includeAllComponents, status, depth + 1, lines);
                }
            }
        }

        internal static string[] CreateProbeComponentLabels(GameObject target, bool includeAllComponents, UguiDependencyStatus status)
        {
            var components = target.GetComponents<Component>().Where(component => component != null).ToArray();
            return components
                .Where(component => includeAllComponents || IsProbeRelevantComponent(component, status))
                .Select(component => FormatProbeComponentName(component.GetType()))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        internal static bool IsProbeRelevantComponent(Component component, UguiDependencyStatus status)
        {
            var type = component.GetType();
            if (type == status.CanvasType
                || type == status.CanvasScalerType
                || type == status.GraphicRaycasterType
                || type == status.ImageType
                || type == status.ButtonType
                || type == status.SliderType
                || type == status.ToggleType
                || type == status.ScrollbarType
                || type == status.ScrollRectType
                || type == status.DropdownType
                || type == status.TmpDropdownType
                || type == status.InputFieldType
                || type == status.TmpTextType)
            {
                return true;
            }

            var ns = type.Namespace ?? string.Empty;
            return ns.StartsWith("UnityEngine.UI", StringComparison.Ordinal)
                || ns.StartsWith("TMPro", StringComparison.Ordinal);
        }

        internal static string FormatProbeComponentName(Type type)
        {
            return type.Name == "TextMeshProUGUI" ? "TextMeshProText" : type.Name;
        }

        internal static Dictionary<string, object?>? CreateObjectReferenceRow(UnityEngine.Object? obj)
        {
            return obj == null
                ? null
                : new Dictionary<string, object?>
                {
                    ["name"] = obj.name,
                    ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(obj),
                    ["type"] = obj.GetType().Name,
                };
        }

        internal static Dictionary<string, object?>? CreateComponentReferenceRow(Component? component)
        {
            if (component == null)
            {
                return null;
            }

            return new Dictionary<string, object?>
            {
                ["path"] = GetTransformPath(component.transform),
                ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(component.gameObject),
                ["type"] = component.GetType().Name,
                ["enabled"] = IsEnabledComponent(component),
            };
        }
    }
}
