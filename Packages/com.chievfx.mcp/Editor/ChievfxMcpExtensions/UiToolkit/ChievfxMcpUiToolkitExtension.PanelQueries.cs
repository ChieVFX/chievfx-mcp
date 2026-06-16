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
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitRows;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitShared;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitSchemas;

namespace Chievfx.Mcp.Extensions.UiToolkit
{
    internal static class UiToolkitPanelQueries
    {
        internal static bool IsRuntimePlayModeActive()
        {
            return EditorApplication.isPlaying;
        }

        internal static Component[] FindRuntimeDocuments(UiToolkitDependencyStatus status)
        {
            if (status.UIDocumentType == null)
            {
                return Array.Empty<Component>();
            }

            return Resources.FindObjectsOfTypeAll(status.UIDocumentType)
                .OfType<Component>()
                .Where(component => component != null
                    && component.gameObject != null
                    && component.gameObject.scene.IsValid()
                    && component.gameObject.scene.isLoaded)
                .OrderBy(component => component.gameObject.scene.path, StringComparer.Ordinal)
                .ThenBy(component => GetTransformPath(component.transform), StringComparer.Ordinal)
                .ToArray();
        }

        internal static PanelGroup[] FindRuntimePanelGroups(UiToolkitDependencyStatus status)
        {
            return FindRuntimeDocuments(status)
                .Select(PanelGroup.FromDocument)
                .Where(group => group.Panel != null)
                .GroupBy(group => RuntimeHelpers.GetHashCode(group.Panel!), group => group)
                .Select(group => PanelGroup.FromPanel(group.First().Panel!, group.SelectMany(item => item.Documents).ToArray()))
                .OrderByDescending(GetPanelSortingOrder)
                .ThenBy(GetPanelTargetDisplay)
                .ThenBy(group => CreatePanelRef(group.Panel), StringComparer.Ordinal)
                .ToArray();
        }

        internal static int GetPanelSortingOrder(PanelGroup group)
        {
            var settings = group.Documents.Select(GetPanelSettings).FirstOrDefault(setting => setting != null);
            var settingsOrder = settings == null ? 0 : ReadIntMember(settings, "sortingOrder", 0);
            return group.Documents.Select(ReadDocumentSortingOrder).Concat(new[] { settingsOrder }).Max();
        }

        internal static int GetPanelTargetDisplay(PanelGroup group)
        {
            var settings = group.Documents.Select(GetPanelSettings).FirstOrDefault(setting => setting != null);
            return settings == null ? 0 : ReadIntMember(settings, "targetDisplay", 0);
        }

        internal static object? GetRootVisualElement(Component document)
        {
            return GetMemberValue(document, "rootVisualElement");
        }

        internal static object? GetPanelSettings(Component document)
        {
            return GetMemberValue(document, "panelSettings");
        }

        internal static object? GetPanel(object visualElement)
        {
            return GetMemberValue(visualElement, "panel");
        }

        internal static Vector2? ConvertScreenToPanel(UiToolkitDependencyStatus status, PanelGroup group, RuntimeScreenPosition position, List<string> warnings)
        {
            if (position.NormalizedInputSupplied)
            {
                var root = group.Documents.Select(GetRootVisualElement).FirstOrDefault(element => element != null);
                if (root != null)
                {
                    var bounds = ReadRectMember(root, "worldBound");
                    if (bounds.HasValue && bounds.Value.width > 0f && bounds.Value.height > 0f)
                    {
                        return new Vector2(
                            bounds.Value.x + position.NormalizedPosition.x * bounds.Value.width,
                            bounds.Value.yMax - position.NormalizedPosition.y * bounds.Value.height);
                    }
                }
            }

            var panel = group.Panel;
            if (panel == null || status.RuntimePanelUtilsType == null)
            {
                warnings.Add("RuntimePanelUtils.ScreenToPanel could not run because panel/runtime helper type is unavailable.");
                return null;
            }

            var screenForUiToolkit = new Vector2(position.ScreenPosition.x, position.ScreenSize.y - position.ScreenPosition.y);
            var method = status.RuntimePanelUtilsType
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(candidate => string.Equals(candidate.Name, "ScreenToPanel", StringComparison.Ordinal)
                    && candidate.GetParameters().Length == 2);
            if (method == null)
            {
                warnings.Add("RuntimePanelUtils.ScreenToPanel method was not found.");
                return null;
            }

            try
            {
                return (Vector2)method.Invoke(null, new[] { panel, (object)screenForUiToolkit })!;
            }
            catch (Exception ex)
            {
                warnings.Add("RuntimePanelUtils.ScreenToPanel failed: " + RootMessage(ex));
                return null;
            }
        }

        internal static object[] PickAll(UiToolkitDependencyStatus status, object? panel, Vector2 panelPosition, List<string> warnings)
        {
            if (panel == null || status.IPanelType == null || status.VisualElementType == null)
            {
                return Array.Empty<object>();
            }

            var listType = typeof(List<>).MakeGenericType(status.VisualElementType);
            var twoArg = status.IPanelType.GetMethod(
                "PickAll",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(Vector2), listType },
                null);
            try
            {
                if (twoArg != null)
                {
                    var list = (IList)Activator.CreateInstance(listType)!;
                    twoArg.Invoke(panel, new[] { (object)panelPosition, list });
                    return list.Cast<object>().ToArray();
                }

                var oneArg = status.IPanelType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method => string.Equals(method.Name, "PickAll", StringComparison.Ordinal)
                        && method.GetParameters().Length == 1
                        && method.GetParameters()[0].ParameterType == typeof(Vector2));
                if (oneArg != null)
                {
                    return ((IEnumerable?)oneArg.Invoke(panel, new[] { (object)panelPosition }))?.Cast<object>().ToArray()
                        ?? Array.Empty<object>();
                }
            }
            catch (Exception ex)
            {
                warnings.Add("IPanel.PickAll failed: " + RootMessage(ex));
                return Array.Empty<object>();
            }

            warnings.Add("IPanel.PickAll method was not found.");
            return Array.Empty<object>();
        }

        internal static object[] MergePickAllWithBoundsHits(object[] pickAllHits, PanelGroup group, UiToolkitDependencyStatus status, Vector2 panelPosition)
        {
            var merged = new List<object>();
            var seen = new HashSet<int>();
            foreach (var hit in FindBoundsHits(group, status, panelPosition).Concat(pickAllHits))
            {
                var id = RuntimeHelpers.GetHashCode(hit);
                if (seen.Add(id))
                {
                    merged.Add(hit);
                }
            }

            return merged.ToArray();
        }

        internal static IEnumerable<object> FindBoundsHits(PanelGroup group, UiToolkitDependencyStatus status, Vector2 panelPosition)
        {
            var roots = group.Documents
                .Select(GetRootVisualElement)
                .Where(root => root != null)
                .Cast<object>();
            foreach (var root in roots)
            {
                foreach (var item in EnumerateVisibleTree(root, status, DefaultMaxRows * 4)
                    .Where(item => IsProbeHitElement(item.Element, panelPosition))
                    .OrderByDescending(item => item.Depth)
                    .ThenBy(item => GetProbeHitArea(item.Element)))
                {
                    yield return item.Element;
                }
            }
        }

        internal static bool IsProbeHitElement(object visualElement, Vector2 panelPosition)
        {
            var bounds = ReadRectMember(visualElement, "worldBound");
            if (!bounds.HasValue || !bounds.Value.Contains(panelPosition))
            {
                return false;
            }

            return string.Equals(ReadMemberString(visualElement, "pickingMode"), "Position", StringComparison.OrdinalIgnoreCase)
                || ReadBoolMember(visualElement, "focusable", false);
        }

        internal static float GetProbeHitArea(object visualElement)
        {
            var bounds = ReadRectMember(visualElement, "worldBound");
            return bounds.HasValue ? bounds.Value.width * bounds.Value.height : float.MaxValue;
        }

        internal static IEnumerable<TreeItem> EnumerateVisibleTree(object root, UiToolkitDependencyStatus status, int maxRows)
        {
            return EnumerateVisibleTree(root, status, maxRows, out _);
        }

        internal static IEnumerable<TreeItem> EnumerateVisibleTree(object root, UiToolkitDependencyStatus status, int maxRows, out bool truncated)
        {
            var rows = new List<TreeItem>();
            var stack = new Stack<TreeItem>();
            stack.Push(new TreeItem(root, 0));
            truncated = false;
            while (stack.Count > 0)
            {
                var item = stack.Pop();
                if (!IsVisibleVisualElement(item.Element, status))
                {
                    continue;
                }

                if (rows.Count >= maxRows)
                {
                    truncated = true;
                    break;
                }

                rows.Add(item);
                var children = GetChildren(item.Element).Reverse().ToArray();
                foreach (var child in children)
                {
                    stack.Push(new TreeItem(child, item.Depth + 1));
                }
            }

            return rows;
        }

        internal static int CountVisibleElements(object root, UiToolkitDependencyStatus status, int maxRows)
        {
            var count = 0;
            var stack = new Stack<object>();
            stack.Push(root);
            while (stack.Count > 0 && count < maxRows)
            {
                var element = stack.Pop();
                if (!IsVisibleVisualElement(element, status))
                {
                    continue;
                }

                count++;
                foreach (var child in GetChildren(element))
                {
                    stack.Push(child);
                }
            }

            return count;
        }

        internal static IEnumerable<object> GetChildren(object visualElement)
        {
            var method = visualElement.GetType().GetMethod("Children", BindingFlags.Public | BindingFlags.Instance);
            return ((IEnumerable?)method?.Invoke(visualElement, null))?.Cast<object>() ?? Enumerable.Empty<object>();
        }

        internal static bool IsVisibleVisualElement(object visualElement, UiToolkitDependencyStatus status)
        {
            return ReadBoolMember(visualElement, "visible", true)
                && !string.Equals(ReadResolvedStyleMember(visualElement, "display"), "None", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(ReadResolvedStyleMember(visualElement, "visibility"), "Hidden", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsInteractableVisualElement(object visualElement, UiToolkitDependencyStatus status)
        {
            if (!IsVisibleVisualElement(visualElement, status))
            {
                return false;
            }

            if (!ReadBoolMember(visualElement, "enabledInHierarchy", true))
            {
                return false;
            }

            var pickingMode = ReadMemberString(visualElement, "pickingMode");
            var focusable = ReadBoolMember(visualElement, "focusable", false);
            var typeName = visualElement.GetType().Name;
            return focusable
                || string.Equals(pickingMode, "Position", StringComparison.OrdinalIgnoreCase)
                || typeName.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0
                || typeName.IndexOf("Toggle", StringComparison.OrdinalIgnoreCase) >= 0
                || typeName.IndexOf("Slider", StringComparison.OrdinalIgnoreCase) >= 0
                || typeName.IndexOf("Field", StringComparison.OrdinalIgnoreCase) >= 0
                || typeName.IndexOf("Scroller", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool TryGetUiToolkitScreenZone(
            UiToolkitDependencyStatus status,
            object? panel,
            object visualElement,
            Vector2 screenSize,
            out Dictionary<string, object?> zone)
        {
            zone = new Dictionary<string, object?>();
            var bounds = ReadRectMember(visualElement, "worldBound");
            if (!bounds.HasValue || bounds.Value.width <= 0.5f || bounds.Value.height <= 0.5f)
            {
                return false;
            }

            if (!ConvertPanelBoundsToScreenZone(status, panel, bounds.Value, screenSize, out var xMin, out var yMin, out var xMax, out var yMax))
            {
                return false;
            }

            if (!ChievfxMcpRuntimeUiControlFind.IsZonePartiallyOnScreen(xMin, yMin, xMax, yMax, screenSize))
            {
                return false;
            }

            zone = ChievfxMcpRuntimeUiControlFind.CreateZoneRow(xMin, yMin, xMax, yMax);
            return true;
        }

        internal static bool ConvertPanelBoundsToScreenZone(
            UiToolkitDependencyStatus status,
            object? panel,
            Rect panelBounds,
            Vector2 screenSize,
            out float xMin,
            out float yMin,
            out float xMax,
            out float yMax)
        {
            xMin = yMin = xMax = yMax = 0f;
            if (panel != null && status.RuntimePanelUtilsType != null)
            {
                var method = status.RuntimePanelUtilsType
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .FirstOrDefault(candidate => string.Equals(candidate.Name, "PanelToScreen", StringComparison.Ordinal)
                        && candidate.GetParameters().Length == 2);
                if (method != null)
                {
                    var panelPoints = new[]
                    {
                        new Vector2(panelBounds.xMin, panelBounds.yMin),
                        new Vector2(panelBounds.xMax, panelBounds.yMin),
                        new Vector2(panelBounds.xMax, panelBounds.yMax),
                        new Vector2(panelBounds.xMin, panelBounds.yMax),
                    };
                    var screenPoints = panelPoints
                        .Select(point =>
                        {
                            var topLeftScreen = (Vector2)method.Invoke(null, new[] { panel, (object)point })!;
                            return new Vector2(topLeftScreen.x, screenSize.y - topLeftScreen.y);
                        })
                        .ToArray();
                    xMin = screenPoints.Min(point => point.x);
                    xMax = screenPoints.Max(point => point.x);
                    yMin = screenPoints.Min(point => point.y);
                    yMax = screenPoints.Max(point => point.y);
                    return xMax > xMin && yMax > yMin;
                }
            }

            xMin = panelBounds.xMin;
            xMax = panelBounds.xMax;
            yMin = screenSize.y - panelBounds.yMax;
            yMax = screenSize.y - panelBounds.yMin;
            return xMax > xMin && yMax > yMin;
        }
    }
}
