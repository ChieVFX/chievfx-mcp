#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;
using PackageManagerClient = UnityEditor.PackageManager.Client;
using PackageManagerPackageInfo = UnityEditor.PackageManager.PackageInfo;


namespace Chievfx.Mcp.Editor
{
    internal sealed partial class EditorWindowBridgeService
    {
        private static EditorWindow ResolveEditorWindowTarget(JToken args)
        {
            var instanceId = ReadNullableInt(args, "instanceId");
            var typeName = ReadString(args, "typeName");
            var titleContains = ReadString(args, "titleContains");
            var focused = ReadBool(args, "focused", false);
            var mouseOver = ReadBool(args, "mouseOver", false);

            if (instanceId.HasValue)
            {
                return GetOpenEditorWindows().FirstOrDefault(window => GetLegacyInstanceId(window) == instanceId.Value)
                    ?? throw new InvalidOperationException(
                        $"No open EditorWindow found with instanceId {instanceId.Value}. Re-read editor-window-list: ids change when a window is reopened.");
            }

            // No targeting argument at all. Without this guard every open window "matches" and the caller
            // gets an Ambiguous error listing them, which misreads as "the tool is broken" — and hides the
            // real cause (arguments never arrived, e.g. dropped by a client's tool-call wrapper).
            if (!focused && !mouseOver && string.IsNullOrWhiteSpace(typeName) && string.IsNullOrWhiteSpace(titleContains))
            {
                throw new ArgumentException(
                    "editor-window-focus needs a target: instanceId (from editor-window-list), typeName, titleContains, focused, or mouseOver. "
                    + "Received no targeting argument — if you did pass one, your client dropped it; retry with instanceId.");
            }

            if (focused)
            {
                return EditorWindow.focusedWindow
                    ?? throw new InvalidOperationException("No focused EditorWindow is currently available.");
            }

            if (mouseOver)
            {
                return EditorWindow.mouseOverWindow
                    ?? throw new InvalidOperationException("No mouse-over EditorWindow is currently available.");
            }

            Type? typeFilter = null;
            if (!string.IsNullOrWhiteSpace(typeName))
            {
                typeFilter = ResolveEditorWindowType(typeName!);
            }

            var matches = GetOpenEditorWindows()
                .Where(window => typeFilter == null || typeFilter.IsAssignableFrom(window.GetType()))
                .Where(window => string.IsNullOrWhiteSpace(titleContains)
                    || GetEditorWindowTitle(window).IndexOf(titleContains!, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToArray();

            if (matches.Length == 0)
            {
                throw new InvalidOperationException("No open EditorWindow matched the requested target.");
            }

            if (matches.Length == 1)
            {
                return matches[0];
            }

            var focusedMatch = matches.FirstOrDefault(window => ReferenceEquals(EditorWindow.focusedWindow, window));
            if (focusedMatch != null)
            {
                return focusedMatch;
            }

            var descriptions = matches
                .Take(5)
                .Select(window => $"{GetEditorWindowTitle(window)} (instanceId:{GetLegacyInstanceId(window)})");
            var overflow = matches.Length > 5 ? $" (+{matches.Length - 5} more)" : string.Empty;
            throw new InvalidOperationException(
                $"{matches.Length} EditorWindows matched. Pass one instanceId: {string.Join("; ", descriptions)}{overflow}");
        }

        private static object CreateEditorWindowActionResult(string action, EditorWindow window, List<string> diagnostics)
        {
            return new
            {
                action,
                success = true,
                window = CreateEditorWindowSummary(window, diagnostics, includeTabs: true),
                diagnostics = diagnostics.Distinct(StringComparer.Ordinal).ToArray()
            };
        }

        internal static void SelectAndFocusEditorWindow(EditorWindow window, List<string> diagnostics)
        {
            SelectDockedEditorWindowTab(window, diagnostics);
            window.Focus();
            EditorWindow.FocusWindowIfItsOpen(window.GetType());
            window.Repaint();
            EditorApplication.QueuePlayerLoopUpdate();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }

        private static bool SelectDockedEditorWindowTab(EditorWindow window, List<string> diagnostics)
        {
            var hostView = GetEditorWindowHostView(window);
            if (!IsDockArea(hostView))
            {
                return false;
            }

            var panes = GetDockAreaPanes(hostView).ToArray();
            var tabIndex = Array.IndexOf(panes, window);
            if (tabIndex < 0)
            {
                diagnostics.Add($"DockArea panes did not include target window instanceId {GetLegacyInstanceId(window)}.");
                return false;
            }

            if (ReferenceEquals(GetDockAreaSelectedWindow(hostView), window))
            {
                return true;
            }

            if (TrySetDockAreaSelected(hostView!, window, tabIndex, diagnostics))
            {
                return true;
            }

            diagnostics.Add($"Could not select docked tab '{GetEditorWindowTitle(window)}'; Unity DockArea reflection members were unavailable.");
            return false;
        }

        internal static EditorWindow? FindOpenedEditorWindowAfterMenu(
            string? typeName,
            string? requestedTitle,
            string? menuPath,
            EditorWindow[] windowsBeforeMenu,
            int focusedInstanceIdBeforeMenu,
            List<string> diagnostics)
        {
            Type? typeFilter = null;
            if (!string.IsNullOrWhiteSpace(typeName))
            {
                try
                {
                    typeFilter = ResolveEditorWindowType(typeName!);
                }
                catch
                {
                    typeFilter = null;
                }
            }

            var beforeInstanceIds = new HashSet<int>(windowsBeforeMenu.Select(GetLegacyInstanceId));
            var candidates = GetOpenEditorWindows()
                .Where(window => typeFilter == null || typeFilter.IsAssignableFrom(window.GetType()))
                .ToArray();
            var titleHints = GetEditorWindowTitleHints(requestedTitle, menuPath).ToArray();

            foreach (var titleHint in titleHints)
            {
                var titleMatches = candidates
                    .Where(window => EditorWindowTitleEquals(window, titleHint))
                    .ToArray();
                var exactTitleMatch = ChooseEditorWindowAfterMenu(
                    titleMatches,
                    beforeInstanceIds,
                    focusedInstanceIdBeforeMenu,
                    allowExistingFallback: true);
                if (exactTitleMatch != null)
                {
                    return exactTitleMatch;
                }
            }

            foreach (var titleHint in titleHints)
            {
                var titleMatches = candidates
                    .Where(window => EditorWindowTitleContains(window, titleHint))
                    .ToArray();
                var titleMatch = ChooseEditorWindowAfterMenu(
                    titleMatches,
                    beforeInstanceIds,
                    focusedInstanceIdBeforeMenu,
                    allowExistingFallback: true);
                if (titleMatch != null)
                {
                    return titleMatch;
                }
            }

            if (titleHints.Length > 0)
            {
                diagnostics.Add($"Menu item executed, but no open EditorWindow title matched hints: {string.Join(", ", titleHints)}.");
            }

            if (typeFilter != null)
            {
                var typeMatch = ChooseEditorWindowAfterMenu(
                    candidates,
                    beforeInstanceIds,
                    focusedInstanceIdBeforeMenu,
                    allowExistingFallback: true);
                if (typeMatch != null)
                {
                    return typeMatch;
                }
            }

            return ChooseEditorWindowAfterMenu(
                candidates,
                beforeInstanceIds,
                focusedInstanceIdBeforeMenu,
                allowExistingFallback: false);
        }

        private static EditorWindow? ChooseEditorWindowAfterMenu(
            EditorWindow[] candidates,
            HashSet<int> beforeInstanceIds,
            int focusedInstanceIdBeforeMenu,
            bool allowExistingFallback)
        {
            if (candidates.Length == 0)
            {
                return null;
            }

            var focusedWindow = EditorWindow.focusedWindow;
            var focusedChanged = focusedWindow != null && GetLegacyInstanceId(focusedWindow) != focusedInstanceIdBeforeMenu;
            if (focusedChanged && candidates.Any(window => ReferenceEquals(window, focusedWindow)))
            {
                return focusedWindow;
            }

            var newCandidates = candidates
                .Where(window => !beforeInstanceIds.Contains(GetLegacyInstanceId(window)))
                .ToArray();
            if (newCandidates.Length > 0)
            {
                return newCandidates.FirstOrDefault(window => ReferenceEquals(window, focusedWindow))
                    ?? newCandidates.OrderBy(GetEditorWindowTitle, StringComparer.OrdinalIgnoreCase).First();
            }

            if (!allowExistingFallback)
            {
                return null;
            }

            return candidates.FirstOrDefault(window => ReferenceEquals(window, focusedWindow))
                ?? candidates.OrderBy(GetEditorWindowTitle, StringComparer.OrdinalIgnoreCase).First();
        }

        private static IEnumerable<string> GetEditorWindowTitleHints(string? requestedTitle, string? menuPath)
        {
            if (!string.IsNullOrWhiteSpace(requestedTitle))
            {
                var normalizedRequestedTitle = NormalizeEditorWindowTitleHint(requestedTitle!);
                if (!string.IsNullOrWhiteSpace(normalizedRequestedTitle))
                {
                    yield return normalizedRequestedTitle;
                }
            }

            var menuTitle = GetEditorWindowMenuLeafTitle(menuPath);
            if (!string.IsNullOrWhiteSpace(menuTitle))
            {
                var normalizedMenuTitle = NormalizeEditorWindowTitleHint(menuTitle!);
                if (string.IsNullOrWhiteSpace(requestedTitle)
                    || !string.Equals(NormalizeEditorWindowTitleHint(requestedTitle!), normalizedMenuTitle, StringComparison.OrdinalIgnoreCase))
                {
                    yield return normalizedMenuTitle;
                }
            }
        }

        private static string? GetEditorWindowMenuLeafTitle(string? menuPath)
        {
            if (string.IsNullOrWhiteSpace(menuPath))
            {
                return null;
            }

            return menuPath!
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(segment => segment.Trim())
                .LastOrDefault(segment => !string.IsNullOrWhiteSpace(segment));
        }

        private static bool EditorWindowTitleEquals(EditorWindow window, string titleHint)
        {
            return string.Equals(
                NormalizeEditorWindowTitleHint(GetEditorWindowTitle(window)),
                NormalizeEditorWindowTitleHint(titleHint),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool EditorWindowTitleContains(EditorWindow window, string titleHint)
        {
            var windowTitle = NormalizeEditorWindowTitleHint(GetEditorWindowTitle(window));
            var normalizedHint = NormalizeEditorWindowTitleHint(titleHint);
            return windowTitle.IndexOf(normalizedHint, StringComparison.OrdinalIgnoreCase) >= 0
                || normalizedHint.IndexOf(windowTitle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static string NormalizeEditorWindowTitleHint(string title)
        {
            var normalized = Regex.Replace(title.Replace("&", string.Empty), @"\s+", " ").Trim();
            normalized = normalized.Replace("...", string.Empty).Replace("…", string.Empty).Trim();
            return normalized;
        }

        internal static EditorWindow GetEditorWindow(Type type, bool focus, string? title)
        {
            var window = InvokeGetWindow(type, focus, title);
            if (!string.IsNullOrWhiteSpace(title))
            {
                window.titleContent = new GUIContent(title);
            }

            if (focus)
            {
                window.Focus();
            }
            else
            {
                window.Repaint();
            }

            return window;
        }

        private static EditorWindow InvokeGetWindow(Type type, bool focus, string? title)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public;
            var methods = typeof(EditorWindow).GetMethods(flags)
                .Where(method => method.Name == "GetWindow")
                .ToArray();
            var titleArgument = title ?? string.Empty;
            var method = methods.FirstOrDefault(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length == 4
                    && parameters[0].ParameterType == typeof(Type)
                    && parameters[1].ParameterType == typeof(bool)
                    && parameters[2].ParameterType == typeof(string)
                    && parameters[3].ParameterType == typeof(bool);
            });
            if (method?.Invoke(null, new object[] { type, false, titleArgument, focus }) is EditorWindow fourArgWindow)
            {
                return fourArgWindow;
            }

            method = methods.FirstOrDefault(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length == 3
                    && parameters[0].ParameterType == typeof(Type)
                    && parameters[1].ParameterType == typeof(bool)
                    && parameters[2].ParameterType == typeof(string);
            });
            if (method?.Invoke(null, new object[] { type, false, titleArgument }) is EditorWindow threeArgWindow)
            {
                return threeArgWindow;
            }

            method = methods.FirstOrDefault(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == typeof(Type);
            });
            if (method?.Invoke(null, new object[] { type }) is EditorWindow oneArgWindow)
            {
                return oneArgWindow;
            }

            throw new NotSupportedException("EditorWindow.GetWindow(Type) overloads are unavailable in this Unity version.");
        }

        private static Type ResolveEditorWindowType(string typeName)
        {
            var trimmed = typeName.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                throw new ArgumentException("EditorWindow typeName cannot be empty.", nameof(typeName));
            }

            var direct = Type.GetType(trimmed, throwOnError: false);
            if (direct != null)
            {
                if (!typeof(EditorWindow).IsAssignableFrom(direct))
                {
                    throw new InvalidOperationException($"Type '{direct.FullName}' is not an EditorWindow.");
                }

                return direct;
            }

            var matches = GetLoadableTypes()
                .Where(type => typeof(EditorWindow).IsAssignableFrom(type))
                .Where(type =>
                    string.Equals(type.AssemblyQualifiedName, trimmed, StringComparison.Ordinal)
                    || string.Equals(type.FullName, trimmed, StringComparison.Ordinal)
                    || string.Equals(type.Name, trimmed, StringComparison.Ordinal)
                    || string.Equals(type.AssemblyQualifiedName, trimmed, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(type.FullName, trimmed, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(type.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .ToArray();

            if (matches.Length == 0)
            {
                throw new InvalidOperationException($"No EditorWindow type found for typeName '{typeName}'.");
            }

            if (matches.Length > 1)
            {
                var descriptions = matches
                    .Take(10)
                    .Select(type => type.FullName ?? type.Name);
                throw new InvalidOperationException($"Ambiguous EditorWindow typeName '{typeName}'. Matches: {string.Join("; ", descriptions)}");
            }

            return matches[0];
        }

        internal static IEnumerable<Type> GetLoadableTypes()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(type => type != null).Cast<Type>().ToArray();
                }

                foreach (var type in types)
                {
                    yield return type;
                }
            }
        }

        internal static EditorWindow[] GetOpenEditorWindows()
        {
            return Resources.FindObjectsOfTypeAll<EditorWindow>()
                .Where(window => window != null)
                .ToArray();
        }

        internal static Dictionary<string, object?> CreateEditorWindowSummary(EditorWindow window, List<string> diagnostics, bool includeTabs)
        {
            var type = window.GetType();
            var hostView = GetEditorWindowHostView(window);
            var panes = GetDockAreaPanes(hostView).ToArray();
            var selectedWindow = GetDockAreaSelectedWindow(hostView) ?? GetHostActualView(hostView);
            var tabIndex = panes.Length == 0 ? -1 : Array.IndexOf(panes, window);
            var selectedTabIndex = selectedWindow == null || panes.Length == 0 ? -1 : Array.IndexOf(panes, selectedWindow);
            var summary = new Dictionary<string, object?>
            {
                ["instanceId"] = GetLegacyInstanceId(window),
                ["title"] = GetEditorWindowTitle(window),
                ["typeName"] = type.Name,
                ["fullTypeName"] = type.FullName ?? type.Name,
                ["assemblyQualifiedTypeName"] = type.AssemblyQualifiedName,
                ["focused"] = ReferenceEquals(EditorWindow.focusedWindow, window),
                ["mouseOver"] = ReferenceEquals(EditorWindow.mouseOverWindow, window),
                ["selected"] = ReferenceEquals(selectedWindow, window) || panes.Length <= 1,
                ["docked"] = IsDockArea(hostView),
                ["floating"] = hostView != null && !IsDockArea(hostView),
                ["contentRect"] = RectToDto(window.position),
                ["hostViewTypeName"] = hostView?.GetType().FullName,
                ["hostViewInstanceId"] = GetReflectedObjectId(hostView),
                ["hostViewScreenRect"] = TryGetRect(hostView, "screenPosition", out var hostRect) ? RectToDto(hostRect) : null,
                ["containerWindowRect"] = TryGetContainerWindowRect(hostView, out var containerRect) ? RectToDto(containerRect) : null,
                ["tabIndex"] = tabIndex,
                ["selectedTabIndex"] = selectedTabIndex,
                ["tabCount"] = panes.Length == 0 ? 1 : panes.Length
            };

            if (includeTabs)
            {
                summary["tabs"] = panes.Length == 0
                    ? Array.Empty<object>()
                    : panes.Select((pane, index) => CreateEditorWindowTabSummary(pane, index, ReferenceEquals(pane, selectedWindow))).ToArray();
            }

            if (hostView != null && summary["hostViewScreenRect"] == null)
            {
                diagnostics.Add($"HostView '{hostView.GetType().FullName}' did not expose screenPosition.");
            }

            return summary;
        }

        private static Dictionary<string, object?> CreateEditorWindowListSummary(EditorWindow window)
        {
            var type = window.GetType();
            var hostView = GetEditorWindowHostView(window);
            var panes = GetDockAreaPanes(hostView).ToArray();
            var selectedWindow = GetDockAreaSelectedWindow(hostView) ?? GetHostActualView(hostView);
            var tabIndex = panes.Length == 0 ? -1 : Array.IndexOf(panes, window);
            var selectedTabIndex = selectedWindow == null || panes.Length == 0 ? -1 : Array.IndexOf(panes, selectedWindow);

            return new Dictionary<string, object?>
            {
                ["instanceId"] = GetLegacyInstanceId(window),
                ["title"] = GetEditorWindowTitle(window),
                ["typeName"] = type.Name,
                ["fullTypeName"] = type.FullName ?? type.Name,
                ["focused"] = ReferenceEquals(EditorWindow.focusedWindow, window),
                ["mouseOver"] = ReferenceEquals(EditorWindow.mouseOverWindow, window),
                ["selected"] = ReferenceEquals(selectedWindow, window) || panes.Length <= 1,
                ["docked"] = IsDockArea(hostView),
                ["floating"] = hostView != null && !IsDockArea(hostView),
                ["hostViewInstanceId"] = GetReflectedObjectId(hostView),
                ["tabIndex"] = tabIndex,
                ["selectedTabIndex"] = selectedTabIndex,
                ["tabCount"] = panes.Length == 0 ? 1 : panes.Length
            };
        }

        private static Dictionary<string, object?> CreateEditorWindowTabSummary(EditorWindow window, int index, bool selected)
        {
            var type = window.GetType();
            return new Dictionary<string, object?>
            {
                ["index"] = index,
                ["instanceId"] = GetLegacyInstanceId(window),
                ["title"] = GetEditorWindowTitle(window),
                ["typeName"] = type.Name,
                ["fullTypeName"] = type.FullName ?? type.Name,
                ["focused"] = ReferenceEquals(EditorWindow.focusedWindow, window),
                ["selected"] = selected
            };
        }

        private static Dictionary<string, object?> CreateDockAreaSummary(object dockArea, List<string> diagnostics)
        {
            var panes = GetDockAreaPanes(dockArea).ToArray();
            var selectedWindow = GetDockAreaSelectedWindow(dockArea) ?? GetHostActualView(dockArea);
            var selectedTabIndex = selectedWindow == null ? -1 : Array.IndexOf(panes, selectedWindow);
            var summary = new Dictionary<string, object?>
            {
                ["instanceId"] = GetReflectedObjectId(dockArea),
                ["typeName"] = dockArea.GetType().FullName,
                ["screenRect"] = TryGetRect(dockArea, "screenPosition", out var screenRect) ? RectToDto(screenRect) : null,
                ["containerWindowRect"] = TryGetContainerWindowRect(dockArea, out var containerRect) ? RectToDto(containerRect) : null,
                ["selectedTabIndex"] = selectedTabIndex,
                ["tabCount"] = panes.Length,
                ["tabs"] = panes.Select((pane, index) => CreateEditorWindowTabSummary(pane, index, ReferenceEquals(pane, selectedWindow))).ToArray()
            };

            if (summary["screenRect"] == null)
            {
                diagnostics.Add($"DockArea '{dockArea.GetType().FullName}' did not expose screenPosition.");
            }

            return summary;
        }

        private static IEnumerable<object> GetEditorWindowDockAreas(EditorWindow[] windows)
        {
            var dockAreas = new List<object>();
            foreach (var window in windows)
            {
                var hostView = GetEditorWindowHostView(window);
                if (!IsDockArea(hostView) || hostView == null)
                {
                    continue;
                }

                if (dockAreas.Any(existing => ReferenceEquals(existing, hostView)))
                {
                    continue;
                }

                dockAreas.Add(hostView);
            }

            return dockAreas;
        }

        private static string GetEditorWindowHostSortKey(EditorWindow window)
        {
            var hostView = GetEditorWindowHostView(window);
            return GetReflectedObjectId(hostView).ToString(CultureInfo.InvariantCulture);
        }

        private static int GetEditorWindowTabIndex(EditorWindow window)
        {
            var panes = GetDockAreaPanes(GetEditorWindowHostView(window)).ToArray();
            return panes.Length == 0 ? 0 : Array.IndexOf(panes, window);
        }

        internal static object? GetEditorWindowHostView(EditorWindow window)
        {
            return typeof(EditorWindow).GetField("m_Parent", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(window);
        }

        internal static bool IsDockArea(object? hostView)
        {
            return string.Equals(hostView?.GetType().FullName, "UnityEditor.DockArea", StringComparison.Ordinal);
        }

        private static IEnumerable<EditorWindow> GetDockAreaPanes(object? hostView)
        {
            if (!IsDockArea(hostView))
            {
                return Array.Empty<EditorWindow>();
            }

            if (GetReflectedValue(hostView!, "m_Panes") is IEnumerable panes)
            {
                return panes.OfType<EditorWindow>().Where(window => window != null).ToArray();
            }

            return Array.Empty<EditorWindow>();
        }

        internal static EditorWindow? GetDockAreaSelectedWindow(object? hostView)
        {
            if (!IsDockArea(hostView))
            {
                return null;
            }

            var selected = GetReflectedValue(hostView!, "selected");
            if (selected is EditorWindow selectedWindow)
            {
                return selectedWindow;
            }

            if (selected is int selectedIndex)
            {
                return GetDockAreaPanes(hostView).ElementAtOrDefault(selectedIndex);
            }

            var selectedField = GetReflectedValue(hostView!, "m_Selected");
            return selectedField is int fieldIndex
                ? GetDockAreaPanes(hostView).ElementAtOrDefault(fieldIndex)
                : null;
        }

        private static EditorWindow? GetHostActualView(object? hostView)
        {
            return hostView == null ? null : GetReflectedValue(hostView, "actualView") as EditorWindow;
        }

        private static bool TrySetDockAreaSelected(object dockArea, EditorWindow window, int tabIndex, List<string> diagnostics)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = dockArea.GetType();
            var property = type.GetProperty("selected", flags);
            if (property != null && property.CanWrite)
            {
                try
                {
                    if (property.PropertyType.IsAssignableFrom(window.GetType()))
                    {
                        property.SetValue(dockArea, window);
                        return true;
                    }

                    if (property.PropertyType == typeof(int))
                    {
                        property.SetValue(dockArea, tabIndex);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"DockArea.selected set failed: {ex.GetBaseException().Message}");
                }
            }

            var field = type.GetField("m_Selected", flags) ?? type.GetField("selected", flags);
            if (field != null)
            {
                try
                {
                    if (field.FieldType == typeof(int))
                    {
                        field.SetValue(dockArea, tabIndex);
                        return true;
                    }

                    if (field.FieldType.IsAssignableFrom(window.GetType()))
                    {
                        field.SetValue(dockArea, window);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"DockArea selected field set failed: {ex.GetBaseException().Message}");
                }
            }

            return false;
        }

        internal static object? GetReflectedValue(object target, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = target.GetType();
            try
            {
                var property = type.GetProperty(name, flags);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    return property.GetValue(target);
                }
            }
            catch
            {
                return null;
            }

            try
            {
                return type.GetField(name, flags)?.GetValue(target);
            }
            catch
            {
                return null;
            }
        }

        private static bool TryGetRect(object? target, string name, out Rect rect)
        {
            rect = default;
            if (target == null)
            {
                return false;
            }

            var value = GetReflectedValue(target, name);
            if (value is Rect reflectedRect)
            {
                rect = reflectedRect;
                return true;
            }

            return false;
        }

        private static bool TryGetContainerWindowRect(object? hostView, out Rect rect)
        {
            rect = default;
            if (hostView == null)
            {
                return false;
            }

            var containerWindow = GetReflectedValue(hostView, "window") ?? GetReflectedValue(hostView, "m_Window");
            return containerWindow != null && TryGetRect(containerWindow, "position", out rect);
        }

        private static int GetReflectedObjectId(object? value)
        {
            if (value == null)
            {
                return 0;
            }

            return value is Object unityObject ? GetLegacyInstanceId(unityObject) : value.GetHashCode();
        }

        private static object RectToDto(Rect rect)
        {
            return new
            {
                x = rect.x,
                y = rect.y,
                width = rect.width,
                height = rect.height
            };
        }

        internal static string GetEditorWindowTitle(EditorWindow window)
        {
            var title = window.titleContent?.text;
            return string.IsNullOrWhiteSpace(title)
                ? window.GetType().Name
                : title!;
        }

    }
}
