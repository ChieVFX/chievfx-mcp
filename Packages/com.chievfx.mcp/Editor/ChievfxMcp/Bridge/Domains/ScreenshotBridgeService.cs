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
    internal sealed class ScreenshotBridgeService : BridgeDomainServiceBase
    {
        public ImageResult CaptureGameView(JToken args)
        {
            var maxDimension = ReadGameViewMaxDimension(args);
            var gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            var renderTextureAvailable = false;
            if (gameViewType != null)
            {
                var gameView = EditorWindow.GetWindow(gameViewType);
                RequestGameViewRenderRefresh(gameView);
                var renderTextureField = gameViewType.GetField("m_RenderTexture", BindingFlags.Instance | BindingFlags.NonPublic);
                if (renderTextureField?.GetValue(gameView) is RenderTexture renderTexture && renderTexture.width > 0 && renderTexture.height > 0)
                {
                    renderTextureAvailable = true;
                    var dimensions = ResolveGameViewOutputDimensions(args, renderTexture.width, renderTexture.height, maxDimension);
                    var png = EncodeRenderTexture(renderTexture, dimensions.Width, dimensions.Height, SystemInfo.graphicsUVStartsAtTop);
                    if (IsRenderTextureProbablyBlank(renderTexture, dimensions.Width, dimensions.Height))
                    {
                        var fallback = CaptureGameViewCameraFallback(args, includeScreenSpaceOverlayCanvases: true, gameViewRenderTextureAvailable: true);
                        if (!IsPngProbablyBlank(fallback.Base64))
                        {
                            AddMetadataWarning(fallback, "GameView.m_RenderTexture was present but blank after repaint; screenshot-game-view used camera fallback.");
                            return fallback;
                        }
                    }

                    var metadata = new Dictionary<string, object?>
                    {
                        ["captureSource"] = "gameview.renderTexture",
                        ["pngWidth"] = dimensions.Width,
                        ["pngHeight"] = dimensions.Height,
                    };
                    AddInputSpaceMetadata(metadata, dimensions.Width, dimensions.Height);
                    AddShaderCompileMetadata(metadata);
                    return new ImageResult("image/png", Convert.ToBase64String(png), metadata);
                }
            }

            return CaptureGameViewCameraFallback(args, includeScreenSpaceOverlayCanvases: true, gameViewRenderTextureAvailable: renderTextureAvailable);
        }

        // Screenshots are the Game View render (top-left origin); ui-runtime-click/probe/drag map coords
        // against the runtime Screen (bottom-left origin). When the two differ in aspect the Game View is
        // letterboxed and screenshot pixels no longer map linearly to click positions — the single biggest
        // coordinate footgun. Report the input Screen size, and pixelMappingReliable:false ONLY when they
        // mismatch, so a caller knows to target by path instead. The how-to lives in the tool descriptor.
        private static void AddInputSpaceMetadata(Dictionary<string, object?> metadata, int captureWidth, int captureHeight)
        {
            var screenWidth = Mathf.Max(1, Screen.width);
            var screenHeight = Mathf.Max(1, Screen.height);
            metadata["screenWidth"] = screenWidth;
            metadata["screenHeight"] = screenHeight;

            var captureAspect = captureHeight > 0 ? captureWidth / (double)captureHeight : 0d;
            var screenAspect = screenWidth / (double)screenHeight;
            var aspectMatches = Math.Abs(captureAspect - screenAspect) <= 0.02 * screenAspect;
            if (!aspectMatches)
            {
                metadata["pixelMappingReliable"] = false;
            }
        }

        // A frame captured while shader variants are still compiling shows placeholder colors (the classic
        // cyan/magenta), which reads as a real rendering result and sends callers chasing a bug that is
        // just a mid-compile frame. Flag it on the capture itself.
        private static void AddShaderCompileMetadata(Dictionary<string, object?> metadata)
        {
            try
            {
                if (!UnityEditor.ShaderUtil.anythingCompiling)
                {
                    return;
                }
            }
            catch (Exception)
            {
                return;
            }

            metadata["shadersCompiling"] = true;
            metadata["shadersCompilingNote"] =
                "Shader variants were still compiling when this frame was captured; placeholder (cyan/magenta) colors may not be real. Recapture once shader-status reports compiling:false.";
        }

        public ImageResult CaptureCamera(JToken args)
        {
            return CaptureCamera(args, includeScreenSpaceOverlayCanvases: false, gameViewRenderTextureAvailable: false);
        }

        private static ImageResult CaptureCamera(JToken args, bool includeScreenSpaceOverlayCanvases, bool gameViewRenderTextureAvailable)
        {
            var width = ReadInt(args, "width", 1280);
            var height = ReadInt(args, "height", 720);
            ValidateScreenshotDimensions(width, height);
            return CaptureCameraAtResolution(args, width, height, includeScreenSpaceOverlayCanvases, gameViewRenderTextureAvailable);
        }

        private static ImageResult CaptureGameViewCameraFallback(JToken args, bool includeScreenSpaceOverlayCanvases, bool gameViewRenderTextureAvailable)
        {
            var maxDimension = ReadGameViewMaxDimension(args);
            var dimensions = ResolveGameViewCameraFallbackDimensions(args, maxDimension);
            var result = CaptureCameraAtResolution(args, dimensions.Width, dimensions.Height, includeScreenSpaceOverlayCanvases, gameViewRenderTextureAvailable);
            if (result.Metadata != null)
            {
                result.Metadata["maxDimension"] = maxDimension;
            }

            return result;
        }

        private static ImageResult CaptureCameraAtResolution(JToken args, int width, int height, bool includeScreenSpaceOverlayCanvases, bool gameViewRenderTextureAvailable)
        {
            var camera = ResolveCamera(args) ?? throw new InvalidOperationException("No camera found for screenshot capture.");
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            var renderTexture = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            var warnings = new List<string>();
            TemporaryOverlayCanvasCameraScope? overlayScope = null;

            try
            {
                if (includeScreenSpaceOverlayCanvases)
                {
                    overlayScope = TryPrepareScreenSpaceOverlayCanvasesForCamera(camera, warnings);
                    ForceUpdateCanvasesForCapture(warnings);
                }

                camera.targetTexture = renderTexture;
                camera.Render();
                RenderTexture.active = renderTexture;
                var png = EncodeActiveRenderTexture(width, height, false);
                var metadata = includeScreenSpaceOverlayCanvases
                    ? CreateGameViewCameraFallbackMetadata(
                        width,
                        height,
                        camera,
                        gameViewRenderTextureAvailable,
                        overlayScope?.CanvasCount ?? 0,
                        overlayScope?.CanvasCount > 0
                            ? "attempted-temporary-screen-space-camera"
                            : "none-detected",
                        warnings)
                    : CreateCameraCaptureMetadata(camera, width, height);
                return new ImageResult("image/png", Convert.ToBase64String(png), metadata);
            }
            finally
            {
                overlayScope?.Dispose();
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(renderTexture);
            }
        }

        private static TemporaryOverlayCanvasCameraScope? TryPrepareScreenSpaceOverlayCanvasesForCamera(Camera camera, List<string> warnings)
        {
            var canvasType = ProfilerBridgeService.TryResolveTypeByNames(new[] { "UnityEngine.Canvas", "UnityEngine.Canvas, UnityEngine.UIModule" });
            if (canvasType == null)
            {
                return null;
            }

            var renderModeProperty = canvasType.GetProperty("renderMode", BindingFlags.Instance | BindingFlags.Public);
            var worldCameraProperty = canvasType.GetProperty("worldCamera", BindingFlags.Instance | BindingFlags.Public);
            var planeDistanceProperty = canvasType.GetProperty("planeDistance", BindingFlags.Instance | BindingFlags.Public);
            if (renderModeProperty == null || worldCameraProperty == null || !renderModeProperty.CanWrite || !worldCameraProperty.CanWrite)
            {
                warnings.Add("GameView.m_RenderTexture was unavailable and Canvas renderMode/worldCamera could not be inspected; Screen Space Overlay UI may be missing. Workaround: temporarily set the Canvas to Screen Space Camera and assign the capture camera, or use screenshot-editor-window on the visible Game View.");
                return null;
            }

            object screenSpaceCameraValue;
            try
            {
                screenSpaceCameraValue = Enum.Parse(renderModeProperty.PropertyType, "ScreenSpaceCamera");
            }
            catch (Exception ex)
            {
                warnings.Add($"GameView.m_RenderTexture was unavailable and Screen Space Camera render mode could not be resolved: {ex.GetBaseException().Message}. Workaround: use screenshot-editor-window on the visible Game View.");
                return null;
            }

            var states = new List<TemporaryOverlayCanvasState>();
            var context = GameObjectBridgeService.GetGameObjectQueryContext();
            foreach (var unityObject in Resources.FindObjectsOfTypeAll(canvasType))
            {
                if (unityObject is not Component component || !component.gameObject.scene.IsValid())
                {
                    continue;
                }

                object? currentRenderMode;
                try
                {
                    currentRenderMode = renderModeProperty.GetValue(component);
                }
                catch (Exception ex)
                {
                    warnings.Add($"Could not inspect Canvas '{GameObjectBridgeService.GetHierarchyPath(component.gameObject, context)}' renderMode: {ex.GetBaseException().Message}.");
                    continue;
                }

                if (!string.Equals(currentRenderMode?.ToString(), "ScreenSpaceOverlay", StringComparison.Ordinal))
                {
                    continue;
                }

                var state = new TemporaryOverlayCanvasState(
                    component,
                    renderModeProperty,
                    worldCameraProperty,
                    planeDistanceProperty,
                    currentRenderMode,
                    worldCameraProperty.GetValue(component),
                    planeDistanceProperty?.GetValue(component));
                states.Add(state);

                try
                {
                    renderModeProperty.SetValue(component, screenSpaceCameraValue);
                    worldCameraProperty.SetValue(component, camera);
                    if (planeDistanceProperty?.CanWrite == true)
                    {
                        planeDistanceProperty.SetValue(component, Math.Max(camera.nearClipPlane + 0.01f, 1f));
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"Could not temporarily render Canvas '{GameObjectBridgeService.GetHierarchyPath(component.gameObject, context)}' through camera '{camera.name}': {ex.GetBaseException().Message}. Workaround: set that Canvas to Screen Space Camera manually or use screenshot-editor-window on the visible Game View.");
                }
            }

            if (states.Count > 0)
            {
                warnings.Add($"Screen Space Overlay Canvas detected. screenshot-game-view attempted a temporary Screen Space Camera capture for {states.Count} Canvas(es) and restored them afterward, but Unity may still omit overlay UI from manual camera renders in Edit Mode. If UI is missing, use the documented workaround.");
            }

            return states.Count == 0 ? null : new TemporaryOverlayCanvasCameraScope(states);
        }

        private static void ForceUpdateCanvasesForCapture(List<string> warnings)
        {
            var canvasType = ProfilerBridgeService.TryResolveTypeByNames(new[] { "UnityEngine.Canvas", "UnityEngine.Canvas, UnityEngine.UIModule" });
            var forceUpdateCanvases = canvasType?.GetMethod("ForceUpdateCanvases", BindingFlags.Static | BindingFlags.Public);
            if (forceUpdateCanvases == null)
            {
                return;
            }

            try
            {
                forceUpdateCanvases.Invoke(null, null);
            }
            catch (Exception ex)
            {
                warnings.Add($"Canvas.ForceUpdateCanvases failed before screenshot capture: {ex.GetBaseException().Message}.");
            }
        }

        private static void RequestGameViewRenderRefresh(EditorWindow gameView)
        {
            EditorWindowBridgeService.RequestEditorWindowScreenshotRepaint(gameView);
            var hostView = EditorWindowBridgeService.GetEditorWindowHostView(gameView);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                hostView?.GetType()
                    .GetMethod("RepaintImmediately", flags, null, Type.EmptyTypes, null)
                    ?.Invoke(hostView, null);
            }
            catch
            {
                // Best-effort only; camera fallback below handles stale/blank render textures.
            }
        }

        private static bool IsRenderTextureProbablyBlank(RenderTexture renderTexture, int width, int height)
        {
            var previousActive = RenderTexture.active;
            RenderTexture? sampleTexture = null;
            var sampleWidth = Math.Min(64, Math.Max(1, width));
            var sampleHeight = Math.Min(64, Math.Max(1, height));
            try
            {
                if (renderTexture.width != sampleWidth || renderTexture.height != sampleHeight)
                {
                    sampleTexture = RenderTexture.GetTemporary(sampleWidth, sampleHeight, 0, renderTexture.format);
                    Graphics.Blit(renderTexture, sampleTexture);
                    RenderTexture.active = sampleTexture;
                }
                else
                {
                    RenderTexture.active = renderTexture;
                }

                return IsActiveRenderTextureProbablyBlank(sampleWidth, sampleHeight);
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (sampleTexture != null)
                {
                    RenderTexture.ReleaseTemporary(sampleTexture);
                }
            }
        }

        private static bool IsActiveRenderTextureProbablyBlank(int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                return IsTextureProbablyBlank(texture);
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        private static bool IsPngProbablyBlank(string base64)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                return !texture.LoadImage(Convert.FromBase64String(base64)) || IsTextureProbablyBlank(texture);
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        private static bool IsTextureProbablyBlank(Texture2D texture)
        {
            var pixels = texture.GetPixels32();
            if (pixels.Length == 0)
            {
                return true;
            }

            var visiblePixels = 0;
            var litPixels = 0;
            foreach (var pixel in pixels)
            {
                if (pixel.a <= 8)
                {
                    continue;
                }

                visiblePixels++;
                if (Math.Max(pixel.r, Math.Max(pixel.g, pixel.b)) > 12)
                {
                    litPixels++;
                }
            }

            return visiblePixels == 0 || litPixels <= Math.Max(1, visiblePixels / 1000);
        }

        private static void AddMetadataWarning(ImageResult result, string warning)
        {
            if (result.Metadata == null)
            {
                return;
            }

            var warnings = new List<string>();
            if (result.Metadata.TryGetValue("warnings", out var existing) && existing is IEnumerable<string> existingWarnings)
            {
                warnings.AddRange(existingWarnings);
            }

            if (!warnings.Contains(warning, StringComparer.Ordinal))
            {
                warnings.Add(warning);
            }

            result.Metadata["warnings"] = warnings.ToArray();
        }

        private static Dictionary<string, object?> CreateGameViewCameraFallbackMetadata(
            int width,
            int height,
            Camera camera,
            bool gameViewRenderTextureAvailable,
            int screenSpaceOverlayCanvasCount,
            string screenSpaceOverlayHandling,
            IEnumerable<string> warnings)
        {
            var metadata = CreateCameraCaptureMetadata(camera, width, height);
            var gameViewTargetSize = TryGetMainGameViewTargetSize();
            if (gameViewTargetSize.HasValue)
            {
                metadata["gameViewWidth"] = (int)gameViewTargetSize.Value.x;
                metadata["gameViewHeight"] = (int)gameViewTargetSize.Value.y;
            }

            metadata["renderTextureAvailable"] = gameViewRenderTextureAvailable;
            metadata["screenSpaceOverlayCanvasCount"] = screenSpaceOverlayCanvasCount;
            metadata["screenSpaceOverlayHandling"] = screenSpaceOverlayHandling;
            AddInputSpaceMetadata(metadata, width, height);
            AddShaderCompileMetadata(metadata);
            var distinctWarnings = warnings.Distinct(StringComparer.Ordinal).ToArray();
            if (distinctWarnings.Length > 0)
            {
                metadata["warnings"] = distinctWarnings;
            }
            return metadata;
        }

        private static Dictionary<string, object?> CreateCameraCaptureMetadata(Camera camera, int width, int height)
        {
            var context = GameObjectBridgeService.GetGameObjectQueryContext();
            return new Dictionary<string, object?>
            {
                ["captureSource"] = "camera.render",
                ["cameraName"] = camera.name,
                ["cameraPath"] = GameObjectBridgeService.GetHierarchyPath(camera.gameObject, context),
                ["cameraInstanceId"] = GetLegacyInstanceId(camera.gameObject),
                ["cameraComponentInstanceId"] = GetLegacyInstanceId(camera),
                ["pngWidth"] = width,
                ["pngHeight"] = height,
            };
        }

        private static Camera? ResolveCamera(JToken args)
        {
            var cameraPath = ReadString(args, "cameraPath") ?? ReadString(args, "path");
            var cameraInstanceId = ReadNullableInt(args, "cameraInstanceId") ?? ReadNullableInt(args, "instanceId");
            var cameraName = ReadString(args, "cameraName");
            var context = GameObjectBridgeService.GetGameObjectQueryContext();

            Camera? camera = null;
            if (cameraInstanceId.HasValue)
            {
                camera = ResolveCameraByInstanceId(context, cameraInstanceId.Value);
                if (camera == null)
                {
                    throw new InvalidOperationException($"No Camera or Camera GameObject with instanceId {cameraInstanceId.Value} was found in current {context.Source}.");
                }
            }

            if (!string.IsNullOrWhiteSpace(cameraPath))
            {
                if (camera != null)
                {
                    ValidateCameraPathMatchesInstance(context, camera, cameraPath!);
                }
                else
                {
                    camera = ResolveCameraByPath(context, cameraPath!);
                }
            }

            return camera ?? FindCameraByNameOrDefault(cameraName);
        }

        private static Camera ResolveCameraByPath(GameObjectQueryContext context, string cameraPath)
        {
            var gameObject = GameObjectBridgeService.ResolveGameObjectByPath(context, cameraPath);
            return gameObject.GetComponent<Camera>()
                ?? throw new InvalidOperationException($"GameObject '{GameObjectBridgeService.GetHierarchyPath(gameObject, context)}' has no Camera component.");
        }

        private static void ValidateCameraPathMatchesInstance(GameObjectQueryContext context, Camera camera, string cameraPath)
        {
            var normalizedInput = NormalizeHierarchyPath(cameraPath);
            var resolvedPath = GameObjectBridgeService.GetHierarchyPath(camera.gameObject, context);
            if (string.Equals(resolvedPath, normalizedInput, StringComparison.Ordinal)
                || string.Equals(RemoveDuplicateIndexes(resolvedPath), normalizedInput, StringComparison.Ordinal))
            {
                return;
            }

            throw new InvalidOperationException(
                $"cameraPath '{cameraPath}' does not match cameraInstanceId {GetLegacyInstanceId(camera.gameObject)} "
                + $"('{resolvedPath}').");
        }

        private static string NormalizeHierarchyPath(string path)
        {
            return path.Trim().Trim('/');
        }

        private static string RemoveDuplicateIndexes(string path)
        {
            return Regex.Replace(path, @"(^|/)([^/]+)\[\d+\](?=/|$)", "$1$2");
        }

        private static Camera? ResolveCameraByInstanceId(GameObjectQueryContext context, int cameraInstanceId)
        {
            var unityObject = UnityObjectIdentity.LegacyInstanceIdToObject(cameraInstanceId);
            if (unityObject is Camera camera && camera.gameObject.scene.IsValid())
            {
                return camera;
            }

            if (unityObject is GameObject gameObject && gameObject.scene.IsValid())
            {
                return gameObject.GetComponent<Camera>();
            }

            var cameraGameObject = GameObjectBridgeService.EnumerateContextGameObjects(context)
                .FirstOrDefault(candidate => GetLegacyInstanceId(candidate) == cameraInstanceId);
            return cameraGameObject != null ? cameraGameObject.GetComponent<Camera>() : null;
        }

        private static Camera? FindCameraByNameOrDefault(string? cameraName)
        {
            var cameras = Resources.FindObjectsOfTypeAll<Camera>()
                .Where(camera => camera != null && camera.gameObject.scene.IsValid())
                .ToArray();

            if (!string.IsNullOrWhiteSpace(cameraName))
            {
                var namedCamera = cameras.FirstOrDefault(camera => string.Equals(camera.name, cameraName, StringComparison.Ordinal));
                if (namedCamera != null)
                {
                    return namedCamera;
                }
            }

            if (Camera.main != null)
            {
                return Camera.main;
            }

            return cameras.FirstOrDefault(camera => camera.isActiveAndEnabled) ?? cameras.FirstOrDefault();
        }

        internal static byte[] EncodeRenderTexture(RenderTexture renderTexture, bool flipVertical)
        {
            return EncodeRenderTexture(renderTexture, renderTexture.width, renderTexture.height, flipVertical);
        }

        internal static byte[] EncodeRenderTexture(RenderTexture renderTexture, int width, int height, bool flipVertical)
        {
            ValidateScreenshotDimensions(width, height);
            var previousActive = RenderTexture.active;
            RenderTexture? scaledTexture = null;
            try
            {
                if (width != renderTexture.width || height != renderTexture.height)
                {
                    scaledTexture = RenderTexture.GetTemporary(width, height, 0, renderTexture.format);
                    Graphics.Blit(renderTexture, scaledTexture);
                    RenderTexture.active = scaledTexture;
                }
                else
                {
                    RenderTexture.active = renderTexture;
                }

                return EncodeActiveRenderTexture(width, height, flipVertical);
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (scaledTexture != null)
                {
                    RenderTexture.ReleaseTemporary(scaledTexture);
                }
            }
        }

        private static Vector2? TryGetMainGameViewTargetSize()
        {
            try
            {
                var gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                var method = gameViewType?.GetMethod("GetMainGameViewTargetSize", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                return method?.Invoke(null, null) is Vector2 size && size.x >= 1f && size.y >= 1f ? size : null;
            }
            catch
            {
                return null;
            }
        }

        private static int ReadGameViewMaxDimension(JToken args)
        {
            var value = ReadNullableInt(args, "maxDimension")
                ?? ReadNullableInt(args, "maxSideResolution")
                ?? ReadNullableInt(args, "max_side_resolution")
                ?? DefaultGameViewScreenshotMaxDimension;
            return ClampInt(value, 1, MaxScreenshotDimension);
        }

        private static (int Width, int Height) ResolveGameViewOutputDimensions(JToken args, int sourceWidth, int sourceHeight, int maxDimension)
        {
            var requestedWidth = ReadNullableInt(args, "width");
            var requestedHeight = ReadNullableInt(args, "height");
            if (requestedWidth.HasValue && requestedHeight.HasValue)
            {
                var width = requestedWidth.Value;
                var height = requestedHeight.Value;
                ValidateScreenshotDimensions(width, height);
                return (width, height);
            }

            var safeSourceWidth = Math.Max(1, sourceWidth);
            var safeSourceHeight = Math.Max(1, sourceHeight);
            var longestSide = Math.Max(safeSourceWidth, safeSourceHeight);
            if (longestSide <= maxDimension)
            {
                return (safeSourceWidth, safeSourceHeight);
            }

            var scale = maxDimension / (double)longestSide;
            return (
                Math.Max(1, (int)Math.Round(safeSourceWidth * scale)),
                Math.Max(1, (int)Math.Round(safeSourceHeight * scale)));
        }

        private static (int Width, int Height) ResolveGameViewCameraFallbackDimensions(JToken args, int maxDimension)
        {
            var requestedWidth = ReadNullableInt(args, "width");
            var requestedHeight = ReadNullableInt(args, "height");
            if (requestedWidth.HasValue && requestedHeight.HasValue)
            {
                var width = requestedWidth.Value;
                var height = requestedHeight.Value;
                ValidateScreenshotDimensions(width, height);
                return (width, height);
            }

            return (maxDimension, Math.Max(1, (int)Math.Round(maxDimension * 9d / 16d)));
        }

        private static byte[] EncodeActiveRenderTexture(int width, int height, bool flipVertical)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                if (flipVertical)
                {
                    FlipTextureVertically(texture);
                }

                return texture.EncodeToPNG();
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        private static void FlipTextureVertically(Texture2D texture)
        {
            var pixels = texture.GetPixels32();
            var width = texture.width;
            var height = texture.height;
            var row = new Color32[width];
            for (var y = 0; y < height / 2; y++)
            {
                var opposite = height - y - 1;
                Array.Copy(pixels, y * width, row, 0, width);
                Array.Copy(pixels, opposite * width, pixels, y * width, width);
                Array.Copy(row, 0, pixels, opposite * width, width);
            }

            texture.SetPixels32(pixels);
            texture.Apply();
        }

        internal static void ValidateScreenshotDimensions(int width, int height)
        {
            if (width <= 0 || height <= 0 || width > MaxScreenshotDimension || height > MaxScreenshotDimension)
            {
                throw new ArgumentOutOfRangeException(nameof(width), $"Screenshot dimensions must be between 1 and {MaxScreenshotDimension}.");
            }
        }

    }
}
