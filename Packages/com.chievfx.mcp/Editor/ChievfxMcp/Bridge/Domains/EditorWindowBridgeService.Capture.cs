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
        internal static object RunEditorWindowScreenshotForResponse(PendingEditorWindowScreenshotRequest pending)
        {
            var image = CaptureEditorWindowScreenshot(pending);
            return new
            {
                ok = true,
                contentType = "image",
                mimeType = image.MimeType,
                base64 = image.Base64,
                metadata = image.Metadata
            };
        }

        private static ImageResult CaptureEditorWindowScreenshot(PendingEditorWindowScreenshotRequest pending)
        {
            var window = pending.Window;
            if (window == null)
            {
                throw new InvalidOperationException("Target EditorWindow was closed before screenshot capture.");
            }

            RequestEditorWindowScreenshotRepaint(window);

            var warnings = new List<string>(pending.Warnings);
            if (!pending.SelectedDockedTab && IsDockArea(GetEditorWindowHostView(window)))
            {
                warnings.Add("Inactive hidden docked tab pixels cannot be captured; selectDockedTab=true is required for docked tabs.");
            }

            if (Application.isBatchMode)
            {
                warnings.Add("Unity is running in batch mode; EditorWindow pixels may be unavailable.");
            }

            var pixelsPerPoint = EditorGUIUtility.pixelsPerPoint;
            if (!Mathf.Approximately(pixelsPerPoint, 1f))
            {
                warnings.Add($"EditorGUIUtility.pixelsPerPoint is {pixelsPerPoint.ToString("0.###", CultureInfo.InvariantCulture)}; capture rects are reported in Unity GUI points.");
            }

            var effectiveCaptureArea = pending.CaptureArea;
            var selectedTab = IsEditorWindowSelected(window);
            if (!selectedTab && IsDockArea(GetEditorWindowHostView(window)))
            {
                warnings.Add("Target is not the selected docked tab; captured pixels may belong to the currently selected tab in the same DockArea.");
            }

            var capture = CaptureEditorWindowToPng(window, pending.CaptureArea, pending.MaxDimension, warnings, pending.Diagnostics, out effectiveCaptureArea);
            var waitedMs = Math.Max(0d, (EditorApplication.timeSinceStartup - pending.StartedEditorTime) * 1000d);
            var waitedEditorUpdates = Math.Max(0, RuntimeState.EditorUpdateTick - pending.StartedEditorUpdateTick);
            var metadata = new Dictionary<string, object?>
            {
                ["targetType"] = window.GetType().FullName ?? window.GetType().Name,
                ["title"] = GetEditorWindowTitle(window),
                ["instanceId"] = GetLegacyInstanceId(window),
                ["captureBackend"] = capture.Backend,
                ["captureRect"] = RectToDto(capture.CaptureRect),
                ["capturePixelRect"] = RectToDto(capture.CapturePixelRect),
                ["captureArea"] = effectiveCaptureArea.ToString().ToLowerInvariant(),
                ["requestedCaptureArea"] = pending.RequestedCaptureArea,
                ["selectedTab"] = selectedTab,
                ["selectedDockedTab"] = pending.SelectedDockedTab,
                ["focused"] = ReferenceEquals(EditorWindow.focusedWindow, window),
                ["docked"] = IsDockArea(GetEditorWindowHostView(window)),
                ["waitStrategy"] = pending.WaitStrategy,
                ["waitDescription"] = FormatEditorWindowScreenshotWait(pending.EffectiveDelayFrames, pending.EffectiveDelayMs),
                ["effectiveDelayFrames"] = pending.EffectiveDelayFrames,
                ["effectiveDelayMs"] = pending.EffectiveDelayMs,
                ["delayFramesExplicit"] = pending.DelayFramesExplicit,
                ["delayMsExplicit"] = pending.DelayMsExplicit,
                ["waitedEditorUpdates"] = waitedEditorUpdates,
                ["waitedMs"] = Math.Round(waitedMs, 1),
                ["pixelsPerPoint"] = pixelsPerPoint,
                ["pngWidth"] = Mathf.RoundToInt(capture.CapturePixelRect.width),
                ["pngHeight"] = Mathf.RoundToInt(capture.CapturePixelRect.height),
                ["warnings"] = warnings.Distinct(StringComparer.Ordinal).ToArray(),
                ["diagnostics"] = pending.Diagnostics.Distinct(StringComparer.Ordinal).ToArray()
            };

            return new ImageResult("image/png", Convert.ToBase64String(capture.Png), metadata);
        }

        private static EditorWindowCaptureResult CaptureEditorWindowToPng(
            EditorWindow window,
            EditorWindowCaptureArea requestedCaptureArea,
            int maxDimension,
            List<string> warnings,
            List<string> diagnostics,
            out EditorWindowCaptureArea effectiveCaptureArea)
        {
            try
            {
                return CaptureEditorWindowGuiViewToPng(
                    window,
                    requestedCaptureArea,
                    maxDimension,
                    warnings,
                    diagnostics,
                    out effectiveCaptureArea);
            }
            catch (Exception ex)
            {
                warnings.Add($"GUIView.GrabPixels capture failed: {ex.GetBaseException().Message}; falling back to desktop pixel capture.");
            }

            return CaptureEditorWindowScreenPixelsToPng(window, requestedCaptureArea, maxDimension, warnings, out effectiveCaptureArea);
        }

        private static EditorWindowCaptureResult CaptureEditorWindowGuiViewToPng(
            EditorWindow window,
            EditorWindowCaptureArea requestedCaptureArea,
            int maxDimension,
            List<string> warnings,
            List<string> diagnostics,
            out EditorWindowCaptureArea effectiveCaptureArea)
        {
            var hostView = GetEditorWindowHostView(window)
                ?? throw new InvalidOperationException("Target EditorWindow host view is unavailable.");
            var grabPixelsMethod = GetGuiViewGrabPixelsMethod(hostView)
                ?? throw new NotSupportedException("UnityEditor.GUIView.GrabPixels(RenderTexture, Rect) is unavailable.");

            RequestEditorWindowScreenshotRepaint(window);
            RepaintGuiViewImmediately(hostView, diagnostics);

            var captureRect = ResolveEditorWindowGuiViewCaptureRect(window, requestedCaptureArea, warnings, out effectiveCaptureArea);
            var pixelRect = ScaleRect(captureRect, EditorGUIUtility.pixelsPerPoint);
            var grabPixelRect = ToGuiViewGrabPixelRect(window, captureRect, EditorGUIUtility.pixelsPerPoint);
            ValidateEditorWindowScreenshotDimensions(pixelRect, maxDimension);

            var width = Mathf.RoundToInt(pixelRect.width);
            var height = Mathf.RoundToInt(pixelRect.height);
            var previousActive = RenderTexture.active;
            var renderTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            try
            {
                grabPixelsMethod.Invoke(hostView, new object[] { renderTexture, grabPixelRect });
                var png = ScreenshotBridgeService.EncodeRenderTexture(renderTexture, SystemInfo.graphicsUVStartsAtTop);
                return new EditorWindowCaptureResult("guiView.grabPixels", png, captureRect, pixelRect);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
            finally
            {
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(renderTexture);
            }
        }

        private static MethodInfo? GetGuiViewGrabPixelsMethod(object hostView)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var parameterTypes = new[] { typeof(RenderTexture), typeof(Rect) };
            return hostView.GetType().GetMethod("GrabPixels", flags, null, parameterTypes, null)
                ?? typeof(EditorWindow).Assembly
                    .GetType("UnityEditor.GUIView")
                    ?.GetMethod("GrabPixels", flags, null, parameterTypes, null);
        }

        private static void RepaintGuiViewImmediately(object hostView, List<string> diagnostics)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                hostView.GetType()
                    .GetMethod("RepaintImmediately", flags, null, Type.EmptyTypes, null)
                    ?.Invoke(hostView, null);
            }
            catch (Exception ex)
            {
                diagnostics.Add($"GUIView.RepaintImmediately failed: {ex.GetBaseException().Message}");
            }
        }

        private static Rect ResolveEditorWindowGuiViewCaptureRect(
            EditorWindow window,
            EditorWindowCaptureArea captureArea,
            List<string> warnings,
            out EditorWindowCaptureArea effectiveCaptureArea)
        {
            var hostView = GetEditorWindowHostView(window);
            effectiveCaptureArea = captureArea;
            if (captureArea == EditorWindowCaptureArea.Window)
            {
                warnings.Add("GUIView capture cannot include native OS window chrome; falling back to host view capture.");
                effectiveCaptureArea = EditorWindowCaptureArea.View;
            }

            if (!TryGetRect(hostView, "screenPosition", out var hostRect) || hostRect.width <= 0 || hostRect.height <= 0)
            {
                warnings.Add("HostView/DockArea screenPosition was unavailable; falling back to EditorWindow size.");
                hostRect = new Rect(0, 0, window.position.width, window.position.height);
            }

            if (effectiveCaptureArea == EditorWindowCaptureArea.Content)
            {
                return ResolveEditorWindowContentRectInHost(window, hostRect);
            }

            return new Rect(0, 0, hostRect.width, hostRect.height);
        }

        private static Rect ToGuiViewGrabPixelRect(EditorWindow window, Rect captureRect, float scale)
        {
            var hostView = GetEditorWindowHostView(window);
            var hostHeight = TryGetRect(hostView, "screenPosition", out var hostRect) && hostRect.height > 0
                ? hostRect.height
                : captureRect.height;
            var topLeftPixelRect = ScaleRect(captureRect, scale);
            var hostPixelHeight = Mathf.CeilToInt(hostHeight * scale);
            var bottomLeftY = Math.Max(0, hostPixelHeight - Mathf.RoundToInt(topLeftPixelRect.yMax));
            return new Rect(
                topLeftPixelRect.x,
                bottomLeftY,
                topLeftPixelRect.width,
                topLeftPixelRect.height);
        }

        private static Rect ResolveEditorWindowContentRectInHost(EditorWindow window, Rect hostRect)
        {
            var windowRect = window.position;
            var localX = windowRect.x - hostRect.x;
            var localY = windowRect.y - hostRect.y;

            if (IsDockArea(GetEditorWindowHostView(window)))
            {
                var widthGap = hostRect.width - windowRect.width;
                var heightGap = hostRect.height - windowRect.height;
                if (Mathf.Abs(localX) < 0.5f && widthGap > 0.5f)
                {
                    localX = widthGap * 0.5f;
                }

                if (Mathf.Abs(localY) < 0.5f && heightGap > 0.5f)
                {
                    localY = heightGap;
                }
            }

            localX = Mathf.Clamp(localX, 0, Math.Max(0, hostRect.width - 1));
            localY = Mathf.Clamp(localY, 0, Math.Max(0, hostRect.height - 1));
            var width = Mathf.Clamp(windowRect.width, 1, Math.Max(1, hostRect.width - localX));
            var height = Mathf.Clamp(windowRect.height, 1, Math.Max(1, hostRect.height - localY));
            return new Rect(localX, localY, width, height);
        }

        private static EditorWindowCaptureResult CaptureEditorWindowScreenPixelsToPng(
            EditorWindow window,
            EditorWindowCaptureArea requestedCaptureArea,
            int maxDimension,
            List<string> warnings,
            out EditorWindowCaptureArea effectiveCaptureArea)
        {
            warnings.Add("ReadScreenPixel captures visible desktop pixels only; Unity must be frontmost and the target rect must be unobscured.");
            var captureRect = ResolveEditorWindowCaptureRect(window, requestedCaptureArea, warnings, out effectiveCaptureArea);
            var pixelRect = ScaleRect(captureRect, EditorGUIUtility.pixelsPerPoint);
            ValidateEditorWindowScreenshotDimensions(pixelRect, maxDimension);
            if (pixelRect.x < 0 || pixelRect.y < 0)
            {
                warnings.Add("Capture rect starts outside the primary desktop origin; offscreen or secondary-display capture is best effort.");
            }

            return new EditorWindowCaptureResult(
                "desktop.readScreenPixel",
                CaptureScreenRectToPng(pixelRect),
                captureRect,
                pixelRect);
        }

        private static Rect ResolveEditorWindowCaptureRect(
            EditorWindow window,
            EditorWindowCaptureArea captureArea,
            List<string> warnings,
            out EditorWindowCaptureArea effectiveCaptureArea)
        {
            var hostView = GetEditorWindowHostView(window);
            effectiveCaptureArea = captureArea;
            if (captureArea == EditorWindowCaptureArea.Content)
            {
                return window.position;
            }

            if (captureArea == EditorWindowCaptureArea.Window)
            {
                if (hostView != null && !IsDockArea(hostView) && TryGetContainerWindowRect(hostView, out var containerRect))
                {
                    return containerRect;
                }

                warnings.Add("captureArea=window is only safe for floating/container windows; falling back to host view capture.");
                effectiveCaptureArea = EditorWindowCaptureArea.View;
            }

            if (TryGetRect(hostView, "screenPosition", out var hostRect) && hostRect.width > 0 && hostRect.height > 0)
            {
                return hostRect;
            }

            warnings.Add("HostView/DockArea screenPosition was unavailable; falling back to EditorWindow content rect.");
            effectiveCaptureArea = EditorWindowCaptureArea.Content;
            return window.position;
        }

        private static Rect ScaleRect(Rect rect, float scale)
        {
            var xMin = Mathf.FloorToInt(rect.xMin * scale);
            var yMin = Mathf.FloorToInt(rect.yMin * scale);
            var xMax = Mathf.CeilToInt(rect.xMax * scale);
            var yMax = Mathf.CeilToInt(rect.yMax * scale);
            return new Rect(xMin, yMin, Math.Max(0, xMax - xMin), Math.Max(0, yMax - yMin));
        }

        private static void ValidateEditorWindowScreenshotDimensions(Rect pixelRect, int maxDimension)
        {
            var width = Mathf.RoundToInt(pixelRect.width);
            var height = Mathf.RoundToInt(pixelRect.height);
            ScreenshotBridgeService.ValidateScreenshotDimensions(width, height);
            if (width > maxDimension || height > maxDimension)
            {
                throw new ArgumentOutOfRangeException(nameof(maxDimension), $"EditorWindow screenshot dimensions {width}x{height} exceed maxDimension {maxDimension}.");
            }
        }

        private static byte[] CaptureScreenRectToPng(Rect pixelRect)
        {
            var x = Mathf.RoundToInt(pixelRect.x);
            var y = Mathf.RoundToInt(pixelRect.y);
            var width = Mathf.RoundToInt(pixelRect.width);
            var height = Mathf.RoundToInt(pixelRect.height);
            var colors = UnityEditorInternal.InternalEditorUtility.ReadScreenPixel(new Vector2(x, y), width, height);
            if (colors == null || colors.Length != width * height)
            {
                throw new InvalidOperationException($"ReadScreenPixel returned {colors?.Length ?? 0} pixels for requested rect {width}x{height}.");
            }

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                texture.SetPixels(colors);
                texture.Apply();
                return texture.EncodeToPNG();
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        private static bool IsEditorWindowSelected(EditorWindow window)
        {
            var hostView = GetEditorWindowHostView(window);
            var panes = GetDockAreaPanes(hostView).ToArray();
            if (panes.Length == 0)
            {
                return true;
            }

            var selectedWindow = GetDockAreaSelectedWindow(hostView) ?? GetHostActualView(hostView);
            return ReferenceEquals(selectedWindow, window);
        }

        internal static void RestoreEditorWindowScreenshotSelection(PendingEditorWindowScreenshotRequest pending)
        {
            if (pending.PreviousDockArea != null
                && pending.PreviousSelectedDockedWindow != null
                && !ReferenceEquals(pending.PreviousSelectedDockedWindow, pending.Window))
            {
                var panes = GetDockAreaPanes(pending.PreviousDockArea).ToArray();
                var tabIndex = Array.IndexOf(panes, pending.PreviousSelectedDockedWindow);
                if (tabIndex >= 0 && TrySetDockAreaSelected(pending.PreviousDockArea, pending.PreviousSelectedDockedWindow, tabIndex, pending.Diagnostics))
                {
                    RequestEditorWindowScreenshotRepaint(pending.PreviousSelectedDockedWindow);
                }
            }

        }

    }
}
