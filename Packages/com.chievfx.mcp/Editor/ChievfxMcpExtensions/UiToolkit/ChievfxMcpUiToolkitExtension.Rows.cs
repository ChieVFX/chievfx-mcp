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
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitRuntimeTools;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitResources;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitInteractions;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitPanelQueries;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitShared;
namespace Chievfx.Mcp.Extensions.UiToolkit
{
    internal static class UiToolkitRows
    {
        internal static Dictionary<string, object?> CreatePanelRow(PanelGroup group, UiToolkitDependencyStatus status)
        {
            var root = group.Documents.Select(GetRootVisualElement).FirstOrDefault(element => element != null);
            return new Dictionary<string, object?>
            {
                ["framework"] = "uitoolkit",
                ["panelRef"] = CreatePanelRef(group.Panel),
                ["panelType"] = group.Panel?.GetType().FullName,
                ["panelSettings"] = group.Documents.Select(GetPanelSettings).Where(setting => setting != null).Select(CreatePanelSettingsRow).FirstOrDefault(),
                ["documentRefs"] = group.Documents.Select(CreateDocumentRef).ToArray(),
                ["documents"] = group.Documents.Select(document => CreateDocumentRow(document, status)).ToArray(),
                ["root"] = root == null ? null : CreateVisualElementRow(root, status, group, includeTextAndValue: false),
            };
        }

        internal static Dictionary<string, object?> CreateDocumentRow(Component document, UiToolkitDependencyStatus status)
        {
            var root = GetRootVisualElement(document);
            return new Dictionary<string, object?>
            {
                ["framework"] = "uitoolkit",
                ["documentRef"] = CreateDocumentRef(document),
                ["path"] = GetTransformPath(document.transform),
                ["name"] = document.gameObject.name,
                ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(document.gameObject),
                ["documentInstanceId"] = UnityObjectIdentity.GetLegacyInstanceId(document),
                ["scene"] = CreateSceneRow(document.gameObject.scene),
                ["activeInHierarchy"] = document.gameObject.activeInHierarchy,
                ["enabled"] = document is not Behaviour behaviour || behaviour.enabled,
                ["sortingOrder"] = ReadDocumentSortingOrder(document),
                ["panelRef"] = root == null ? null : CreatePanelRef(GetPanel(root)),
                ["panelSettings"] = CreatePanelSettingsRow(GetPanelSettings(document)),
                ["root"] = root == null ? null : CreateVisualElementRow(root, status, PanelGroup.FromDocument(document), includeTextAndValue: false),
            };
        }

        internal static Dictionary<string, object?> CreateVisualElementRow(object visualElement, UiToolkitDependencyStatus status, PanelGroup group, bool includeTextAndValue)
        {
            var row = new Dictionary<string, object?>
            {
                ["framework"] = "uitoolkit",
                ["panelRef"] = CreatePanelRef(group.Panel ?? GetPanel(visualElement)),
                ["documentRefs"] = group.Documents.Select(CreateDocumentRef).ToArray(),
                ["visualElementRef"] = CreateVisualElementRef(visualElement),
                ["path"] = GetVisualElementPath(visualElement),
                ["type"] = visualElement.GetType().FullName,
                ["typeName"] = visualElement.GetType().Name,
                ["name"] = ReadMemberString(visualElement, "name"),
                ["classes"] = ReadClasses(visualElement),
                ["visible"] = ReadBoolMember(visualElement, "visible", true),
                ["enabledInHierarchy"] = ReadBoolMember(visualElement, "enabledInHierarchy", true),
                ["enabledSelf"] = ReadBoolMember(visualElement, "enabledSelf", true),
                ["pickingMode"] = ReadMemberString(visualElement, "pickingMode"),
                ["focusable"] = ReadBoolMember(visualElement, "focusable", false),
                ["tabIndex"] = ReadNullableIntMember(visualElement, "tabIndex"),
                ["worldBound"] = CreateRectRow(ReadRectMember(visualElement, "worldBound")),
                ["layout"] = CreateRectRow(ReadRectMember(visualElement, "layout")),
                ["display"] = ReadResolvedStyleMember(visualElement, "display"),
                ["visibility"] = ReadResolvedStyleMember(visualElement, "visibility"),
            };

            if (includeTextAndValue)
            {
                row["text"] = ReadSimpleMemberValue(visualElement, "text");
                row["value"] = ReadSimpleMemberValue(visualElement, "value");
                row["tooltip"] = ReadSimpleMemberValue(visualElement, "tooltip");
            }

            return row;
        }

        internal static Dictionary<string, object?> CreateCompactProbeStackRow(
            object visualElement,
            UiToolkitDependencyStatus status,
            PanelGroup group,
            int index,
            int hitOrder)
        {
            var sortingOrder = group.Documents.Select(ReadDocumentSortingOrder).DefaultIfEmpty(0).Max();
            return new Dictionary<string, object?>
            {
                ["i"] = index,
                ["path"] = GetVisualElementPath(visualElement),
                ["type"] = visualElement.GetType().Name,
                ["text"] = ReadSimpleMemberValue(visualElement, "text"),
                ["value"] = ReadSimpleMemberValue(visualElement, "value"),
                ["focusable"] = ReadBoolMember(visualElement, "focusable", false),
                ["enabled"] = ReadBoolMember(visualElement, "enabledInHierarchy", true),
                ["pickingMode"] = ReadMemberString(visualElement, "pickingMode"),
                ["sortingOrder"] = sortingOrder,
                ["bound"] = CreateRectRow(ReadRectMember(visualElement, "worldBound")),
            };
        }

        internal static string[] CreateRuntimeProbeHierarchyLines(IEnumerable<object> hitElements, bool includeAllComponents, bool includeUssClasses)
        {
            var included = new HashSet<int>();
            var byId = new Dictionary<int, object>();
            foreach (var hit in hitElements)
            {
                for (var current = hit; current != null; current = GetMemberValue(current, "parent"))
                {
                    var id = RuntimeHelpers.GetHashCode(current);
                    included.Add(id);
                    byId[id] = current;
                }
            }

            var roots = byId.Values
                .Where(element =>
                {
                    var parent = GetMemberValue(element, "parent");
                    return parent == null || !included.Contains(RuntimeHelpers.GetHashCode(parent));
                })
                .OrderBy(element => GetVisualElementPath(element), StringComparer.Ordinal)
                .ToArray();

            var lines = new List<string>();
            foreach (var root in roots)
            {
                AppendProbeHierarchyLines(root, included, includeAllComponents, includeUssClasses, 0, lines);
            }

            return lines.ToArray();
        }

        internal static void AppendProbeHierarchyLines(object visualElement, HashSet<int> included, bool includeAllComponents, bool includeUssClasses, int depth, List<string> lines)
        {
            var prefix = depth == 0 ? string.Empty : new string('-', depth);
            var labels = CreateProbeElementLabels(visualElement, includeAllComponents);
            var ussClasses = includeUssClasses ? CreateProbeUssClassLabels(visualElement) : Array.Empty<string>();
            var line = prefix + GetProbeElementName(visualElement);
            if (labels.Length > 0)
            {
                line += " [" + string.Join(", ", labels) + "]";
            }

            if (ussClasses.Length > 0)
            {
                line += " <" + string.Join(" ", ussClasses) + ">";
            }

            lines.Add(line);

            foreach (var child in GetChildren(visualElement))
            {
                if (included.Contains(RuntimeHelpers.GetHashCode(child)))
                {
                    AppendProbeHierarchyLines(child, included, includeAllComponents, includeUssClasses, depth + 1, lines);
                }
            }
        }

        internal static string GetProbeElementName(object visualElement)
        {
            var name = ReadMemberString(visualElement, "name");
            return string.IsNullOrWhiteSpace(name) ? visualElement.GetType().Name : "#" + name;
        }

        internal static string[] CreateProbeElementLabels(object visualElement, bool includeAllComponents)
        {
            var labels = new List<string> { visualElement.GetType().Name };
            return labels.Distinct(StringComparer.Ordinal).ToArray();
        }

        internal static string[] CreateProbeUssClassLabels(object visualElement)
        {
            return ReadClasses(visualElement)
                .Select(className => "." + className)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        internal static Dictionary<string, object?> CreatePanelOrderingRow(PanelGroup group, object visualElement, int hitOrder)
        {
            var settings = group.Documents.Select(GetPanelSettings).FirstOrDefault(setting => setting != null);
            return new Dictionary<string, object?>
            {
                ["sortingOrder"] = GetPanelSortingOrder(group),
                ["targetDisplay"] = settings == null ? 0 : ReadIntMember(settings, "targetDisplay", 0),
                ["documentDepth"] = GetVisualElementDepth(visualElement),
                ["hitOrder"] = hitOrder,
                ["panelRef"] = CreatePanelRef(group.Panel),
            };
        }

        internal static int GetVisualElementDepth(object visualElement)
        {
            var depth = 0;
            for (var current = GetMemberValue(visualElement, "parent"); current != null; current = GetMemberValue(current, "parent"))
            {
                depth++;
            }

            return depth;
        }

        internal static Dictionary<string, object?>? CreatePanelSettingsRow(object? panelSettings)
        {
            if (panelSettings == null)
            {
                return null;
            }

            var unityObject = panelSettings as UnityEngine.Object;
            return new Dictionary<string, object?>
            {
                ["name"] = unityObject == null ? panelSettings.ToString() : unityObject.name,
                ["instanceId"] = unityObject == null ? null : UnityObjectIdentity.GetLegacyInstanceId(unityObject),
                ["sortingOrder"] = ReadNullableIntMember(panelSettings, "sortingOrder"),
                ["targetDisplay"] = ReadNullableIntMember(panelSettings, "targetDisplay"),
                ["targetTexture"] = CreateObjectRef(GetMemberValue(panelSettings, "targetTexture") as UnityEngine.Object),
                ["scaleMode"] = ReadMemberString(panelSettings, "scaleMode"),
                ["referenceResolution"] = CreateVector2Row(ReadVector2Member(panelSettings, "referenceResolution")),
            };
        }

        internal static Dictionary<string, object?> CreateSceneRow(Scene scene)
        {
            return new Dictionary<string, object?>
            {
                ["name"] = scene.name,
                ["path"] = scene.path,
                ["handle"] = scene.handle,
            };
        }

        internal static Dictionary<string, object?>? CreateObjectRef(UnityEngine.Object? obj)
        {
            if (obj == null)
            {
                return null;
            }

            return new Dictionary<string, object?>
            {
                ["name"] = obj.name,
                ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(obj),
                ["type"] = obj.GetType().FullName,
            };
        }

        internal static string CreatePanelRef(object? panel)
        {
            return panel == null ? string.Empty : "panel:" + RuntimeHelpers.GetHashCode(panel).ToString(CultureInfo.InvariantCulture);
        }

        internal static string CreateDocumentRef(Component document)
        {
            return "uidocument:" + UnityObjectIdentity.GetEntityIdText(document);
        }

        internal static string CreateVisualElementRef(object visualElement)
        {
            return "ve:" + RuntimeHelpers.GetHashCode(visualElement).ToString(CultureInfo.InvariantCulture);
        }

        internal static string GetVisualElementPath(object visualElement)
        {
            var segments = new Stack<string>();
            var current = visualElement;
            while (current != null)
            {
                var name = ReadMemberString(current, "name");
                var type = current.GetType().Name;
                var parent = GetMemberValue(current, "parent");
                var index = parent == null ? 0 : GetChildren(parent).TakeWhile(child => !ReferenceEquals(child, current)).Count();
                segments.Push(string.IsNullOrEmpty(name) ? $"{type}[{index}]" : $"{type}#{name}[{index}]");
                current = parent;
            }

            return string.Join("/", segments);
        }

        internal static string GetTransformPath(Transform transform)
        {
            var stack = new Stack<string>();
            var current = transform;
            while (current != null)
            {
                stack.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", stack);
        }

        internal static RuntimeScreenPosition ReadScreenPosition(JToken args, List<string> warnings)
        {
            var screenSize = new Vector2(Mathf.Max(1, Screen.width), Mathf.Max(1, Screen.height));
            var isNormalized = ReadBool(args, "isNormalized", false);

            if (TryReadVector2(args["normalized"], out var legacyNormalized))
            {
                return new RuntimeScreenPosition(new Vector2(legacyNormalized.x * screenSize.x, legacyNormalized.y * screenSize.y), screenSize, legacyNormalized, normalizedInputSupplied: true);
            }

            if (TryReadVector2(args["screenPosition"], out var screenPosition))
            {
                return RuntimeScreenPosition.FromScreenPosition(screenPosition);
            }

            if (args["x"] != null || args["y"] != null)
            {
                var x = ReadFloat(args, "x", 0f);
                var y = ReadFloat(args, "y", 0f);
                if (isNormalized)
                {
                    var normalizedPosition = new Vector2(x, y);
                    return new RuntimeScreenPosition(
                        new Vector2(normalizedPosition.x * screenSize.x, normalizedPosition.y * screenSize.y),
                        screenSize,
                        normalizedPosition,
                        normalizedInputSupplied: true);
                }

                return RuntimeScreenPosition.FromScreenPosition(new Vector2(x, y));
            }

            throw new ArgumentException(
                "Runtime UI Toolkit interaction requires x/y screen coordinates when path, visualElementRef, or name is not supplied.");
        }

        internal static void AddCoordinateInfo(Dictionary<string, object?> result, RuntimeScreenPosition position)
        {
            result["input"] = CreateScreenPositionRow(position);
            result["coordinateConvention"] = new Dictionary<string, object?>
            {
                ["origin"] = "bottom-left",
                ["screenSize"] = CreateVector2Row(position.ScreenSize),
                ["screenPosition"] = CreateVector2Row(position.ScreenPosition),
                ["normalizedPosition"] = CreateVector2Row(position.NormalizedPosition),
                ["uiToolkitScreenPosition"] = CreateVector2Row(new Vector2(position.ScreenPosition.x, position.ScreenSize.y - position.ScreenPosition.y)),
                ["uiToolkitYInverted"] = true,
            };
        }

        internal static Dictionary<string, object?> CreateScreenPositionRow(RuntimeScreenPosition position)
        {
            return new Dictionary<string, object?>
            {
                ["screenPosition"] = CreateVector2Row(position.ScreenPosition),
                ["normalizedPosition"] = CreateVector2Row(position.NormalizedPosition),
                ["origin"] = "bottom-left",
            };
        }

        internal static Dictionary<string, object?> CreateVector2Row(Vector2 value)
        {
            return new Dictionary<string, object?>
            {
                ["x"] = value.x,
                ["y"] = value.y,
            };
        }

        internal static Dictionary<string, object?>? CreateRectRow(Rect? rect)
        {
            if (!rect.HasValue)
            {
                return null;
            }

            return new Dictionary<string, object?>
            {
                ["x"] = rect.Value.x,
                ["y"] = rect.Value.y,
                ["width"] = rect.Value.width,
                ["height"] = rect.Value.height,
            };
        }

        internal static bool IsOutsideScreen(Vector2 position, Vector2 screenSize)
        {
            return position.x < 0f || position.y < 0f || position.x > screenSize.x || position.y > screenSize.y;
        }
    }
}
