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
            // The acted-on object among same-named siblings keeps its [k], as ui-control-find printed it.
            row["path"] = GetIndexedTransformPath(target.transform);
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
