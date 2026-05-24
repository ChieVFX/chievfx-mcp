#nullable enable
using static Chievfx.Mcp.Editor.McpLimits;
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
    internal sealed partial class EditorWindowBridgeService : BridgeDomainServiceBase
    {
        public object List(JToken args)
        {
            var maxResults = ClampInt(ReadInt(args, "maxResults", DefaultEditorWindowMaxResults), 1, HardEditorWindowMaxResults);
            var titleContains = ReadString(args, "titleContains");
            var typeName = ReadString(args, "typeName");
            var diagnostics = new List<string>();
            var typeFilter = string.IsNullOrWhiteSpace(typeName) ? null : ResolveEditorWindowType(typeName!);
            var titleComparison = StringComparison.OrdinalIgnoreCase;
            var allWindows = GetOpenEditorWindows()
                .Where(window => typeFilter == null || typeFilter.IsAssignableFrom(window.GetType()))
                .Where(window => string.IsNullOrWhiteSpace(titleContains)
                    || GetEditorWindowTitle(window).IndexOf(titleContains!, titleComparison) >= 0)
                .OrderBy(window => GetEditorWindowHostSortKey(window))
                .ThenBy(window => GetEditorWindowTabIndex(window))
                .ThenBy(window => GetEditorWindowTitle(window), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var selected = allWindows.Take(maxResults).ToArray();
            return new
            {
                count = selected.Length,
                matched = allWindows.Length,
                truncated = allWindows.Length > selected.Length,
                focusedInstanceId = GetLegacyInstanceId(EditorWindow.focusedWindow),
                mouseOverInstanceId = GetLegacyInstanceId(EditorWindow.mouseOverWindow),
                windows = selected.Select(CreateEditorWindowListSummary).ToArray(),
                diagnostics = diagnostics.Distinct(StringComparer.Ordinal).ToArray()
            };
        }

        public object Open(JToken args)
        {
            var typeName = ReadString(args, "typeName");
            var menuPath = ReadString(args, "menuPath");
            var focus = ReadBool(args, "focus", true);
            var title = ReadString(args, "title");
            var diagnostics = new List<string>();

            if (string.IsNullOrWhiteSpace(typeName) && string.IsNullOrWhiteSpace(menuPath))
            {
                throw new ArgumentException("editor-window-open requires 'typeName' and/or 'menuPath'.");
            }

            EditorWindow? window = null;
            string openedBy;

            if (!string.IsNullOrWhiteSpace(typeName))
            {
                try
                {
                    var type = ResolveEditorWindowType(typeName!);
                    window = GetEditorWindow(type, focus, title);
                    if (focus)
                    {
                        SelectAndFocusEditorWindow(window, diagnostics);
                    }
                    openedBy = "typeName";
                    return CreateEditorWindowActionResult(openedBy, window, diagnostics);
                }
                catch (Exception ex) when (!string.IsNullOrWhiteSpace(menuPath))
                {
                    diagnostics.Add($"typeName fallback: {ex.GetBaseException().Message}");
                }
            }

            if (string.IsNullOrWhiteSpace(menuPath))
            {
                throw new InvalidOperationException($"EditorWindow type could not be resolved: '{typeName}'.");
            }

            var windowsBeforeMenu = GetOpenEditorWindows();
            var focusedInstanceIdBeforeMenu = EditorWindow.focusedWindow != null
                ? GetLegacyInstanceId(EditorWindow.focusedWindow)
                : 0;

            if (!EditorApplication.ExecuteMenuItem(menuPath!))
            {
                throw new InvalidOperationException($"Unity menu item could not be executed: '{menuPath}'.");
            }

            openedBy = "menuPath";
            window = FindOpenedEditorWindowAfterMenu(
                typeName,
                title,
                menuPath,
                windowsBeforeMenu,
                focusedInstanceIdBeforeMenu,
                diagnostics);
            if (window == null)
            {
                var message = string.IsNullOrWhiteSpace(typeName) && string.IsNullOrWhiteSpace(title)
                    ? "Menu item executed, but Unity did not report a focused or newly opened EditorWindow."
                    : "Menu item executed, but no EditorWindow matched the requested type/title.";
                throw new InvalidOperationException($"{message} menuPath='{menuPath}'.");
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                window.titleContent = new GUIContent(title);
            }

            if (focus)
            {
                SelectAndFocusEditorWindow(window, diagnostics);
            }
            else
            {
                window.Repaint();
            }

            return CreateEditorWindowActionResult(openedBy, window, diagnostics);
        }

        public object Focus(JToken args)
        {
            var diagnostics = new List<string>();
            var window = ResolveEditorWindowTarget(args);
            SelectAndFocusEditorWindow(window, diagnostics);
            return CreateEditorWindowActionResult("focus", window, diagnostics);
        }

        internal static EditorWindowScreenshotSettings ReadEditorWindowScreenshotSettings(JToken args)
        {
            var captureAreaText = ReadString(args, "captureArea") ?? "view";
            if (!Enum.TryParse<EditorWindowCaptureArea>(captureAreaText, true, out var captureArea))
            {
                throw new ArgumentException("captureArea must be one of 'view', 'content', or 'window'.", nameof(args));
            }

            var hasDelayFrames = HasProperty(args, "delayFrames");
            var hasDelayMs = HasProperty(args, "delayMs");
            var delayFrames = hasDelayFrames
                ? ClampInt(ReadInt(args, "delayFrames", 0), 0, HardEditorWindowScreenshotDelayFrames)
                : hasDelayMs ? 0 : DefaultEditorWindowScreenshotDelayFrames;
            var delayMs = hasDelayMs
                ? ClampInt(ReadInt(args, "delayMs", 0), 0, HardEditorWindowScreenshotDelayMs)
                : hasDelayFrames ? 0 : DefaultEditorWindowScreenshotDelayMs;
            var maxDimension = HasProperty(args, "maxDimension")
                ? ClampInt(ReadInt(args, "maxDimension", MaxScreenshotDimension), 1, MaxScreenshotDimension)
                : MaxScreenshotDimension;

            return new EditorWindowScreenshotSettings
            {
                Target = ReadEditorWindowScreenshotTarget(args),
                OpenIfMissing = ReadBool(args, "openIfMissing", false),
                SelectDockedTab = ReadBool(args, "selectDockedTab", true),
                CaptureArea = captureArea,
                CaptureAreaText = captureArea.ToString().ToLowerInvariant(),
                DelayFrames = delayFrames,
                DelayMs = delayMs,
                DelayFramesExplicit = hasDelayFrames,
                DelayMsExplicit = hasDelayMs,
                WaitStrategy = hasDelayFrames || hasDelayMs ? "explicit-delay" : "default-conservative-delay",
                MaxDimension = maxDimension
            };
        }

        private static EditorWindowTargetSpec ReadEditorWindowScreenshotTarget(JToken args)
        {
            var target = ReadProperty(args, "target");
            if (target is JToken targetToken && targetToken.Type == JTokenType.String)
            {
                var targetText = targetToken.Value<string>();
                if (string.Equals(targetText, "focused", StringComparison.OrdinalIgnoreCase))
                {
                    return new EditorWindowTargetSpec { Focused = true, Source = "focused" };
                }

                if (string.Equals(targetText, "mouseOver", StringComparison.OrdinalIgnoreCase))
                {
                    return new EditorWindowTargetSpec { MouseOver = true, Source = "mouseOver" };
                }

                throw new ArgumentException("String target must be 'focused' or 'mouseOver'.", nameof(args));
            }

            var isObjectTarget = target is JObject;
            var targetObject = isObjectTarget ? target! : args;
            var spec = new EditorWindowTargetSpec
            {
                Source = isObjectTarget ? "object" : "focused",
                InstanceId = ReadNullableInt(targetObject, "instanceId"),
                TypeName = ReadString(targetObject, "typeName"),
                TitleContains = ReadString(targetObject, "titleContains"),
                MenuPath = ReadString(targetObject, "menuPath"),
                Focused = ReadBool(targetObject, "focused", false),
                MouseOver = ReadBool(targetObject, "mouseOver", false)
            };

            if (spec.InstanceId.HasValue
                || !string.IsNullOrWhiteSpace(spec.TypeName)
                || !string.IsNullOrWhiteSpace(spec.TitleContains)
                || !string.IsNullOrWhiteSpace(spec.MenuPath)
                || spec.Focused
                || spec.MouseOver)
            {
                return spec;
            }

            spec.Focused = true;
            return spec;
        }

        internal static EditorWindow ResolveEditorWindowScreenshotTarget(
            EditorWindowTargetSpec target,
            bool openIfMissing,
            List<string> diagnostics)
        {
            try
            {
                var existing = FindExistingEditorWindowTarget(target);
                if (existing != null)
                {
                    return existing;
                }
            }
            catch (Exception ex) when (openIfMissing && !string.IsNullOrWhiteSpace(target.MenuPath))
            {
                diagnostics.Add($"Existing EditorWindow lookup fallback: {ex.GetBaseException().Message}");
            }

            if (!openIfMissing)
            {
                throw new InvalidOperationException("No open EditorWindow matched the screenshot target. Set openIfMissing=true with typeName and/or menuPath to open one.");
            }

            if (!string.IsNullOrWhiteSpace(target.TypeName))
            {
                try
                {
                    return GetEditorWindow(ResolveEditorWindowType(target.TypeName!), false, null);
                }
                catch (Exception ex) when (!string.IsNullOrWhiteSpace(target.MenuPath))
                {
                    diagnostics.Add($"typeName open fallback: {ex.GetBaseException().Message}");
                }
            }

            if (string.IsNullOrWhiteSpace(target.MenuPath))
            {
                throw new InvalidOperationException("openIfMissing requires target.typeName and/or target.menuPath.");
            }

            var windowsBeforeMenu = GetOpenEditorWindows();
            var focusedInstanceIdBeforeMenu = EditorWindow.focusedWindow != null
                ? GetLegacyInstanceId(EditorWindow.focusedWindow)
                : 0;
            if (!EditorApplication.ExecuteMenuItem(target.MenuPath!))
            {
                throw new InvalidOperationException($"Unity menu item could not be executed: '{target.MenuPath}'.");
            }

            return FindOpenedEditorWindowAfterMenu(
                    target.TypeName,
                    target.TitleContains,
                    target.MenuPath,
                    windowsBeforeMenu,
                    focusedInstanceIdBeforeMenu,
                    diagnostics)
                ?? throw new InvalidOperationException($"Menu item executed, but no EditorWindow matched screenshot target '{target.MenuPath}'.");
        }

        private static EditorWindow? FindExistingEditorWindowTarget(EditorWindowTargetSpec target)
        {
            if (target.InstanceId.HasValue)
            {
                return GetOpenEditorWindows().FirstOrDefault(window => GetLegacyInstanceId(window) == target.InstanceId.Value);
            }

            if (target.Focused)
            {
                return EditorWindow.focusedWindow;
            }

            if (target.MouseOver)
            {
                return EditorWindow.mouseOverWindow;
            }

            Type? typeFilter = null;
            if (!string.IsNullOrWhiteSpace(target.TypeName))
            {
                typeFilter = ResolveEditorWindowType(target.TypeName!);
            }

            var matches = GetOpenEditorWindows()
                .Where(window => typeFilter == null || typeFilter.IsAssignableFrom(window.GetType()))
                .Where(window => string.IsNullOrWhiteSpace(target.TitleContains)
                    || GetEditorWindowTitle(window).IndexOf(target.TitleContains!, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToArray();

            if (matches.Length == 0)
            {
                return null;
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
                .Take(10)
                .Select(window => $"{GetEditorWindowTitle(window)} ({window.GetType().FullName}, instanceId:{GetLegacyInstanceId(window)})");
            throw new InvalidOperationException($"Ambiguous EditorWindow screenshot target. Matches: {string.Join("; ", descriptions)}");
        }

        internal static bool PrepareEditorWindowForScreenshot(EditorWindow window, bool selectDockedTab, List<string> diagnostics)
        {
            var selectedDockedTab = false;
            if (selectDockedTab)
            {
                selectedDockedTab = SelectDockedEditorWindowTab(window, diagnostics);
            }

            RequestEditorWindowScreenshotRepaint(window);
            return selectedDockedTab;
        }

        internal static void RequestEditorWindowScreenshotRepaint(EditorWindow? window)
        {
            if (window == null)
            {
                return;
            }

            window.Repaint();
            SceneView.RepaintAll();
            EditorApplication.QueuePlayerLoopUpdate();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }

        internal static string FormatEditorWindowScreenshotWait(int delayFrames, int delayMs)
        {
            if (delayFrames > 0 && delayMs > 0)
            {
                return $"{delayFrames} editor update(s) and {delayMs} ms";
            }

            if (delayFrames > 0)
            {
                return $"{delayFrames} editor update(s)";
            }

            if (delayMs > 0)
            {
                return $"{delayMs} ms";
            }

            return "no delay";
        }

}
}
