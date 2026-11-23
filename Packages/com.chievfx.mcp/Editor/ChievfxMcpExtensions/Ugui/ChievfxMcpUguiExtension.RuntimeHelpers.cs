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
    internal static partial class UguiRuntimeHelpers
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
    }
}
