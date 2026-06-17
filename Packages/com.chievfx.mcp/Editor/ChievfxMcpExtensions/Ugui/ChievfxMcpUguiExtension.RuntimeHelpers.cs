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

            foreach (var canvas in canvases)
            {
                if (!canvas.gameObject.activeInHierarchy || !IsEnabledComponent(canvas))
                {
                    warnings.Add($"Inactive canvas '{GetTransformPath(canvas.transform)}' will not receive runtime raycasts.");
                }

                if (!HasComponent(canvas.gameObject, status.GraphicRaycasterType))
                {
                    warnings.Add($"Canvas '{GetTransformPath(canvas.transform)}' has no GraphicRaycaster.");
                }

                var renderMode = Convert.ToString(GetPropertyValue(canvas, "renderMode"), CultureInfo.InvariantCulture) ?? string.Empty;
                var hasCamera = GetPropertyValue(canvas, "worldCamera") != null;
                if ((string.Equals(renderMode, "ScreenSpaceCamera", StringComparison.Ordinal)
                        || string.Equals(renderMode, "WorldSpace", StringComparison.Ordinal))
                    && !hasCamera)
                {
                    warnings.Add($"Canvas '{GetTransformPath(canvas.transform)}' uses {renderMode} without worldCamera/event camera.");
                }
            }
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

        internal static void EnsureRuntimeMutationAllowed(JToken args, List<string> warnings)
        {
            if (!IsRuntimePlayModeActive())
            {
                throw new InvalidOperationException("Runtime uGUI mutations are gated to Play Mode. Enter Play Mode before firing interactions or changing control values.");
            }

            if (!ReadBool(args, "allowStateMutation", false))
            {
                throw new InvalidOperationException("Runtime uGUI mutation requires explicit allowStateMutation:true.");
            }
        }

        internal static PointerEventData CreatePointerEventData(EventSystem eventSystem, Vector2 position)
        {
            return new PointerEventData(eventSystem)
            {
                button = PointerEventData.InputButton.Left,
                clickCount = 1,
                eligibleForClick = true,
                pressPosition = position,
                position = position,
                delta = Vector2.zero,
            };
        }

        internal static GameObject? ResolveRuntimeInteractionTarget(
            JToken args,
            UguiDependencyStatus status,
            EventSystem? eventSystem,
            Vector2 screenPosition,
            List<string> warnings,
            out Dictionary<string, object?>[] stack)
        {
            var explicitTarget = ResolveGameObject(args, "targetPath", "instanceId");
            if (explicitTarget != null)
            {
                stack = Array.Empty<Dictionary<string, object?>>();
                return explicitTarget;
            }

            if (eventSystem == null)
            {
                stack = Array.Empty<Dictionary<string, object?>>();
                return null;
            }

            if (IsOutsideScreen(screenPosition, new Vector2(Math.Max(1, Screen.width), Math.Max(1, Screen.height))))
            {
                warnings.Add("Coordinate is outside current screen/game-view bounds.");
            }

            status.CanvasType?.GetMethod("ForceUpdateCanvases", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
            var raycastResults = RaycastAll(status, eventSystem, screenPosition, warnings);
            if (raycastResults.Length > 0)
            {
                stack = raycastResults.Select((raycastResult, index) => CreateRuntimeStackRow(raycastResult, index, status)).ToArray();
                return ResolveClickableTargetFromRaycastHits(raycastResults, status);
            }

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
            return PromoteToClickableControlTarget(
                rectHits
                    .Select(rect => rect.gameObject)
                    .FirstOrDefault(target => IsRuntimeInteractionCandidate(target, status)),
                status);
        }

        internal static GameObject? ResolveClickableTargetFromRaycastHits(object[] raycastResults, UguiDependencyStatus status)
        {
            foreach (var raycastResult in raycastResults)
            {
                if (GetMemberValue(raycastResult, "gameObject") is GameObject target
                    && IsRuntimeInteractionCandidate(target, status))
                {
                    return PromoteToClickableControlTarget(target, status);
                }
            }

            return null;
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

            return ExecuteEvents.GetEventHandler<IPointerClickHandler>(target) != null;
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

        internal static IEnumerable<RectTransform> FindRuntimeRectHits(Vector2 screenPosition, UguiDependencyStatus status)
        {
            return FindRuntimeCanvases(status)
                .Where(canvas => canvas.gameObject.activeInHierarchy && IsEnabledComponent(canvas))
                .SelectMany(canvas => canvas.GetComponentsInChildren<RectTransform>(false))
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

            method.Invoke(eventSystem, new object[] { pointerEventData!, results });
            if (results.Count == 0)
            {
                foreach (var raycastResult in GraphicRaycastAll(status, pointerEventData!, listType))
                {
                    results.Add(raycastResult);
                }
            }

            return results.Cast<object>().ToArray();
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
            var hits = FindRuntimeCanvases(status)
                .Where(canvas => canvas.gameObject.activeInHierarchy && IsEnabledComponent(canvas))
                .SelectMany(canvas => canvas.GetComponentsInChildren<RectTransform>(false))
                .Where(rect => rect.gameObject != null
                    && rect.gameObject.activeInHierarchy
                    && IsRuntimeVisibleUiElement(rect.gameObject, status)
                    && IsRuntimeProbeHitElement(rect.gameObject, status)
                    && RectTransformUtility.RectangleContainsScreenPoint(rect, screenPosition, null))
                .OrderByDescending(rect => GetCanvasSortingOrder(FindParentCanvas(rect.gameObject, status)))
                .ThenByDescending(GetTransformSiblingSortKey, StringComparer.Ordinal)
                .Take(256)
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
            var row = new Dictionary<string, object?>
            {
                ["i"] = index,
                ["path"] = GetTransformPath(target.transform),
                ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(target),
                ["type"] = ResolveCompactProbeHitType(target, GetMemberValue(raycastResult, "module") as Component, controls, status),
                ["enabled"] = target.GetComponents<Component>().Where(component => component is Behaviour).Cast<Behaviour>().All(component => component.enabled),
                ["interactable"] = GetFirstPropertyValue(target, "interactable", status.SelectableType, status.ButtonType, status.ToggleType, status.SliderType, status.ScrollbarType, status.DropdownType, status.TmpDropdownType, status.InputFieldType),
                ["raycastTarget"] = GetFirstPropertyValue(target, "raycastTarget", status.GraphicType, status.ImageType, status.TmpTextType),
            };
            if (controls.Length > 0)
            {
                row["controls"] = controls;
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
            row["controlComponents"] = GetControlComponents(target, status).Select(component => component.GetType().Name).ToArray();
            row["enabled"] = target.GetComponents<Component>().Where(component => component is Behaviour).Cast<Behaviour>().All(component => component.enabled);
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
            row["canvas"] = CreateCanvasReferenceRow(canvas.gameObject, status);
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

            return GetFirstPropertyValue(target, "raycastTarget", status.GraphicType, status.ImageType, status.TmpTextType) is true;
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
                .GroupBy(component => component.GetInstanceID())
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
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects()
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
