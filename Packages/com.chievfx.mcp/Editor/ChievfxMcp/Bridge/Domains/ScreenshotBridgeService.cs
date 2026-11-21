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
        // The crop the last screenshot-game-view *tool call* applied. ui-runtime-click/drag/probe consult it
        // only to warn when a space:"screenshot" position arrives without the matching left/top/right/bottom
        // — never to silently change where a click lands, since several agents may share one editor.
        internal static ScreenshotRegion? LastGameViewToolRegion { get; private set; }

        // Tool entry point. The PNG's longest side is always DefaultGameViewScreenshotMaxDimension: callers
        // used to pick a resolution, which made pngWidth/pngHeight vary per call for no gain and still left
        // small HUD text unreadable. Zoom now comes from left/top/right/bottom instead — a sub-region is
        // cropped and scaled up to the same fixed size, so the agent sees it magnified and can compare
        // one isolated area between captures.
        public object CaptureGameView(JToken args)
        {
            if (args is JObject storyboardArgs && storyboardArgs.Value<bool?>("storyboard") == true)
            {
                return CaptureStoryboard();
            }

            var region = ScreenshotRegion.Read(args);
            var result = CaptureGameView(region, FitLongestSideExactly(DefaultGameViewScreenshotMaxDimension));
            LastGameViewToolRegion = region;
            return result;
        }

        // Internal callers (frame-debugger pixel picking and draw-call diffs) sample pixels off the whole
        // frame at a cap of their choosing; they never crop and never upscale.
        internal ImageResult CaptureGameView(int maxDimension)
        {
            return CaptureGameView(ScreenshotRegion.Full, FitWithinLongestSide(ClampInt(maxDimension, 1, MaxScreenshotDimension)));
        }

        private static ImageResult CaptureGameView(ScreenshotRegion region, Func<int, int, (int Width, int Height)> resolveOutput)
        {
            var gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            var renderTextureAvailable = false;
            if (gameViewType != null)
            {
                var gameView = ResolveGameViewWithoutFocus(gameViewType);
                RequestGameViewRenderRefresh(gameView);
                var renderTextureField = gameViewType.GetField("m_RenderTexture", BindingFlags.Instance | BindingFlags.NonPublic);
                if (renderTextureField?.GetValue(gameView) is RenderTexture renderTexture && renderTexture.width > 0 && renderTexture.height > 0)
                {
                    renderTextureAvailable = true;
                    var sourceRect = region.ToPixelRect(renderTexture.width, renderTexture.height);
                    var dimensions = resolveOutput(sourceRect.width, sourceRect.height);
                    var png = EncodeRenderTexture(renderTexture, region, dimensions.Width, dimensions.Height, SystemInfo.graphicsUVStartsAtTop);
                    if (IsRenderTextureProbablyBlank(renderTexture, renderTexture.width, renderTexture.height))
                    {
                        var fallback = CaptureGameViewCameraFallback(region, resolveOutput, includeScreenSpaceOverlayCanvases: true, gameViewRenderTextureAvailable: true);
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
                    AddRegionMetadata(metadata, region, renderTexture.width, renderTexture.height);
                    AddInputSpaceMetadata(metadata, renderTexture.width, renderTexture.height);
                    AddShaderCompileMetadata(metadata);
                    return new ImageResult("image/png", Convert.ToBase64String(png), metadata);
                }
            }

            return CaptureGameViewCameraFallback(region, resolveOutput, includeScreenSpaceOverlayCanvases: true, gameViewRenderTextureAvailable: renderTextureAvailable);
        }

        // storyboard:true lays out the frame recorder's buffer (the last minute of Play) as one image; the first call starts
        // the recorder, since there is nothing to show before it ran. Extensions change the interval through the recorder.
        private static object CaptureStoryboard()
        {
            if (EditorApplication.isPlaying == false)
            {
                throw new InvalidOperationException("storyboard records Play Mode only: enter Play Mode, start recording with storyboard:true, act, then call it again.");
            }

            var wasRunning = ChievfxMcpFrameRecorder.IsRunning;
            ChievfxMcpFrameRecorder.EnsureRunning();

            var interval = Mathf.RoundToInt(ChievfxMcpFrameRecorder.IntervalSeconds * 1000f);
            var window = Mathf.RoundToInt(ChievfxMcpFrameRecorder.WindowSeconds);
            if (ChievfxMcpFrameRecorder.TryBuildSheet(out var jpg, out var legend, out var frames, out var coveredSeconds) == false)
            {
                return new Dictionary<string, object?>
                {
                    ["summary"] = (wasRunning ? "Frame recording is on, no frames yet" : "Frame recording started")
                                  + ": a frame every " + interval + " ms, the last " + window + " s kept. Act, then call screenshot-game-view storyboard:true.",
                    ["everyMs"] = interval,
                    ["windowSeconds"] = window
                };
            }

            var sheetPath = ChievfxMcpFrameRecorder.WriteSheet(jpg, legend, "storyboard");
            var metadata = new Dictionary<string, object?>
            {
                ["summary"] = "Storyboard of the last " + Math.Round(coveredSeconds, 1).ToString(CultureInfo.InvariantCulture) + " s: " + frames
                              + " frames, left to right, top to bottom. Legend: seconds before now, state, marks.",
                ["captureSource"] = "frameRecorder",
                ["frames"] = frames,
                ["everyMs"] = interval,
                ["windowSeconds"] = window,
                ["sheetPath"] = sheetPath,
                // The first line of the legend file explains the layout; the summary above says it already.
                ["legend"] = legend.TrimEnd('\n').Split('\n').Skip(1).ToArray()
            };
            return new ImageResult("image/jpeg", Convert.ToBase64String(jpg), metadata);
        }

        // EditorWindow.GetWindow focuses the window and with it the whole editor, which pulls Unity over the
        // user's windows on every screenshot (several editors on one machine made that constant). An open
        // Game view is used as it is; a hidden docked one only becomes the selected tab so it renders.
        // GetWindow stays for the case with no Game view at all.
        internal static EditorWindow ResolveGameViewWithoutFocus(Type gameViewType)
        {
            var gameView = FindOpenGameView(gameViewType);
            if (gameView == null)
            {
                return EditorWindow.GetWindow(gameViewType);
            }

            EditorWindowBridgeService.PrepareEditorWindowForScreenshot(gameView, selectDockedTab: true, new List<string>());
            return gameView;
        }

        // The main play-mode view is the one the game renders into; with several Game views open the
        // first one found may be a stale secondary display.
        internal static EditorWindow? FindOpenGameView(Type gameViewType)
        {
            const BindingFlags staticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var playModeViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.PlayModeView");
                var mainView = playModeViewType?.GetMethod("GetMainPlayModeView", staticFlags, null, Type.EmptyTypes, null)?.Invoke(null, null) as EditorWindow;
                if (mainView != null && gameViewType.IsInstanceOfType(mainView) && EditorWindowBridgeService.GetEditorWindowHostView(mainView) != null)
                {
                    return mainView;
                }
            }
            catch
            {
                // Internal API probe only; the plain search below still finds an open Game view.
            }

            foreach (var window in Resources.FindObjectsOfTypeAll(gameViewType))
            {
                if (window is EditorWindow editorWindow && editorWindow != null && EditorWindowBridgeService.GetEditorWindowHostView(editorWindow) != null)
                {
                    return editorWindow;
                }
            }

            return null;
        }

        // Output sizing for the tool path: the longest side of the (cropped) source lands exactly on
        // `longestSide`, scaling up or down, so every PNG has the same footprint and a crop is magnified.
        private static Func<int, int, (int Width, int Height)> FitLongestSideExactly(int longestSide)
        {
            return (sourceWidth, sourceHeight) =>
            {
                var safeWidth = Math.Max(1, sourceWidth);
                var safeHeight = Math.Max(1, sourceHeight);
                var scale = longestSide / (double)Math.Max(safeWidth, safeHeight);
                return (
                    ClampInt((int)Math.Round(safeWidth * scale), 1, MaxScreenshotDimension),
                    ClampInt((int)Math.Round(safeHeight * scale), 1, MaxScreenshotDimension));
            };
        }

        // Output sizing for internal callers: downscale only when the source exceeds the cap.
        private static Func<int, int, (int Width, int Height)> FitWithinLongestSide(int maxDimension)
        {
            return (sourceWidth, sourceHeight) =>
            {
                var safeWidth = Math.Max(1, sourceWidth);
                var safeHeight = Math.Max(1, sourceHeight);
                var longestSide = Math.Max(safeWidth, safeHeight);
                if (longestSide <= maxDimension)
                {
                    return (safeWidth, safeHeight);
                }

                var scale = maxDimension / (double)longestSide;
                return (
                    Math.Max(1, (int)Math.Round(safeWidth * scale)),
                    Math.Max(1, (int)Math.Round(safeHeight * scale)));
            };
        }

        // Only a cropped capture carries region metadata; the full-view case stays as lean as before.
        // The note repeats the click recipe because the crop is exactly what makes a bare 0..1 PNG
        // position map to the wrong spot.
        private static void AddRegionMetadata(Dictionary<string, object?> metadata, ScreenshotRegion region, int sourceWidth, int sourceHeight)
        {
            if (region.IsFull)
            {
                return;
            }

            var sourceRect = region.ToPixelRect(sourceWidth, sourceHeight);
            metadata["region"] = region.ToMetadata();
            metadata["regionSourcePixels"] = new Dictionary<string, object?>
            {
                ["x"] = sourceRect.x,
                ["y"] = sourceRect.y,
                ["width"] = sourceRect.width,
                ["height"] = sourceRect.height,
            };
            metadata["regionNote"] =
                "The PNG shows only this region of the view (0..1, top-left origin). To click something in it, pass the same left/top/right/bottom together with space:\"screenshot\" to ui-runtime-click/drag/probe.";
        }

        // Screenshots are the Game View render (top-left origin); ui-runtime-click/probe/drag map coords
        // against the runtime Screen (bottom-left origin). When the two differ in aspect the Game View is
        // letterboxed and screenshot pixels no longer map linearly to click positions — the single biggest
        // coordinate footgun. Report the input Screen size, and pixelMappingReliable:false ONLY when they
        // mismatch, so a caller knows to target by path instead. The how-to lives in the tool descriptor.
        // The size has to come from ChievfxMcpRuntimeScreenSize, not Screen.*: on a fixed-resolution Game
        // View, Screen.* read from the editor is the window size, which both mis-scales these numbers and
        // reports a bogus aspect mismatch on a capture that maps perfectly.
        // captureWidth/captureHeight are the FULL source frame, not the cropped PNG: a crop changes the
        // aspect by design and must not be mistaken for letterboxing.
        private static void AddInputSpaceMetadata(Dictionary<string, object?> metadata, int captureWidth, int captureHeight)
        {
            var screenSize = ChievfxMcpRuntimeScreenSize.Resolve();
            var screenWidth = Mathf.Max(1, Mathf.RoundToInt(screenSize.x));
            var screenHeight = Mathf.Max(1, Mathf.RoundToInt(screenSize.y));
            metadata["screenWidth"] = screenWidth;
            metadata["screenHeight"] = screenHeight;
            var screenSizeSource = ChievfxMcpRuntimeScreenSize.DescribeResolvedSource(screenSize);
            if (screenSizeSource != null)
            {
                metadata["screenSizeSource"] = screenSizeSource;
            }

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
            var width = ReadInt(args, "width", 1280);
            var height = ReadInt(args, "height", 720);
            ValidateScreenshotDimensions(width, height);
            return CaptureCameraAtResolution(
                args,
                width,
                height,
                ScreenshotRegion.Full,
                (width, height),
                includeScreenSpaceOverlayCanvases: false,
                gameViewRenderTextureAvailable: false);
        }

        // Camera fallback renders keep the render target within this many pixels per side: it exists to
        // approximate the Game View, not to produce print-resolution frames.
        private const int MaxCameraFallbackRenderDimension = 4096;

        // Without the Game View render texture the camera renders the whole frame itself. Render at the
        // resolved runtime screen size so the frame has the game's real aspect — the previous fixed 16:9
        // render flagged every portrait game as letterboxed — then crop and scale exactly like the main
        // path, so region semantics do not depend on which capture source happened to be available.
        private static ImageResult CaptureGameViewCameraFallback(
            ScreenshotRegion region,
            Func<int, int, (int Width, int Height)> resolveOutput,
            bool includeScreenSpaceOverlayCanvases,
            bool gameViewRenderTextureAvailable)
        {
            var screenSize = ChievfxMcpRuntimeScreenSize.Resolve();
            var renderWidth = ClampInt(Mathf.RoundToInt(screenSize.x), 1, MaxCameraFallbackRenderDimension);
            var renderHeight = ClampInt(Mathf.RoundToInt(screenSize.y), 1, MaxCameraFallbackRenderDimension);
            var sourceRect = region.ToPixelRect(renderWidth, renderHeight);
            var output = resolveOutput(sourceRect.width, sourceRect.height);
            return CaptureCameraAtResolution(
                new JObject(),
                renderWidth,
                renderHeight,
                region,
                output,
                includeScreenSpaceOverlayCanvases,
                gameViewRenderTextureAvailable);
        }

        private static ImageResult CaptureCameraAtResolution(
            JToken args,
            int width,
            int height,
            ScreenshotRegion region,
            (int Width, int Height) output,
            bool includeScreenSpaceOverlayCanvases,
            bool gameViewRenderTextureAvailable)
        {
            var warnings = new List<string>();
            var camera = ResolveCamera(args, warnings) ?? throw new InvalidOperationException("No camera found for screenshot capture.");
            if (!camera.isActiveAndEnabled)
            {
                // A disabled camera renders whatever its settings show, often a single color, and nothing says so in the picture.
                warnings.Add($"Camera '{GameObjectBridgeService.GetHierarchyPath(camera.gameObject, GameObjectBridgeService.GetGameObjectQueryContext(null))}' is not active and enabled: "
                             + "nothing renders with it now, so the shot may be empty or one color. Active cameras: gameobject-find componentType:Camera; pass cameraInstanceId.");
            }

            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            var renderTexture = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
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
                // A camera render into a RenderTexture already reads back upright, hence no vertical flip.
                var png = EncodeRenderTexture(renderTexture, region, output.Width, output.Height, flipVertical: false);
                var metadata = includeScreenSpaceOverlayCanvases
                    ? CreateGameViewCameraFallbackMetadata(
                        output.Width,
                        output.Height,
                        width,
                        height,
                        region,
                        camera,
                        gameViewRenderTextureAvailable,
                        overlayScope?.CanvasCount ?? 0,
                        overlayScope?.CanvasCount > 0
                            ? "attempted-temporary-screen-space-camera"
                            : "none-detected",
                        warnings)
                    : CreateCameraCaptureMetadata(camera, output.Width, output.Height, warnings);
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
            int pngWidth,
            int pngHeight,
            int renderWidth,
            int renderHeight,
            ScreenshotRegion region,
            Camera camera,
            bool gameViewRenderTextureAvailable,
            int screenSpaceOverlayCanvasCount,
            string screenSpaceOverlayHandling,
            IEnumerable<string> warnings)
        {
            var metadata = CreateCameraCaptureMetadata(camera, pngWidth, pngHeight);
            var gameViewTargetSize = TryGetMainGameViewTargetSize();
            if (gameViewTargetSize.HasValue)
            {
                metadata["gameViewWidth"] = (int)gameViewTargetSize.Value.x;
                metadata["gameViewHeight"] = (int)gameViewTargetSize.Value.y;
            }

            metadata["renderTextureAvailable"] = gameViewRenderTextureAvailable;
            metadata["screenSpaceOverlayCanvasCount"] = screenSpaceOverlayCanvasCount;
            metadata["screenSpaceOverlayHandling"] = screenSpaceOverlayHandling;
            AddRegionMetadata(metadata, region, renderWidth, renderHeight);
            AddInputSpaceMetadata(metadata, renderWidth, renderHeight);
            AddShaderCompileMetadata(metadata);
            var distinctWarnings = warnings.Distinct(StringComparer.Ordinal).ToArray();
            if (distinctWarnings.Length > 0)
            {
                metadata["warnings"] = distinctWarnings;
            }
            return metadata;
        }

        private static Dictionary<string, object?> CreateCameraCaptureMetadata(Camera camera, int width, int height, IReadOnlyCollection<string>? warnings = null)
        {
            var context = GameObjectBridgeService.GetGameObjectQueryContext(null);
            var metadata = new Dictionary<string, object?>
            {
                ["captureSource"] = "camera.render",
                ["cameraName"] = camera.name,
                ["cameraPath"] = GameObjectBridgeService.GetHierarchyPath(camera.gameObject, context),
                ["cameraInstanceId"] = GetLegacyInstanceId(camera.gameObject),
                ["cameraComponentInstanceId"] = GetLegacyInstanceId(camera),
                ["pngWidth"] = width,
                ["pngHeight"] = height,
            };
            if (warnings != null && warnings.Count > 0)
            {
                metadata["warnings"] = warnings.Distinct(StringComparer.Ordinal).ToArray();
            }

            return metadata;
        }

        // Paths resolve over every loaded scene (plus DontDestroyOnLoad in Play Mode), as in gameobject-* tools: a game keeps
        // its camera in an additive scene, and the active scene alone did not find it.
        private static Camera? ResolveCamera(JToken args, List<string> warnings)
        {
            var cameraPath = ReadString(args, "cameraPath") ?? ReadString(args, "path");
            var cameraInstanceId = ReadNullableInt(args, "cameraInstanceId") ?? ReadNullableInt(args, "instanceId");
            var cameraName = ReadString(args, "cameraName");
            var context = GameObjectBridgeService.GetGameObjectQueryContext(null);

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

            return camera ?? FindCameraByNameOrDefault(cameraName, context, warnings);
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

        // Of several cameras with the name the active and enabled one is taken: a common name like "Camera" also belongs to
        // disabled helper cameras (UI mock-ups, render-to-texture), and the first one found was often one of those.
        internal static Camera? FindCameraByNameOrDefault(string? cameraName, GameObjectQueryContext context, List<string> warnings)
        {
            var cameras = Resources.FindObjectsOfTypeAll<Camera>()
                .Where(camera => camera != null && camera.gameObject.scene.IsValid())
                .ToArray();

            if (!string.IsNullOrWhiteSpace(cameraName))
            {
                var namedCameras = cameras.Where(camera => string.Equals(camera.name, cameraName, StringComparison.Ordinal)).ToArray();
                if (namedCameras.Length > 0)
                {
                    var chosen = namedCameras.FirstOrDefault(camera => camera.isActiveAndEnabled) ?? namedCameras[0];
                    if (namedCameras.Length > 1)
                    {
                        var others = namedCameras.Where(camera => camera != chosen).Take(MaxCameraCandidates)
                            .Select(camera => GameObjectBridgeService.GetHierarchyPath(camera.gameObject, context) + (camera.isActiveAndEnabled ? "" : " (inactive)"));
                        warnings.Add($"{namedCameras.Length} cameras are named '{cameraName}'; took '{GameObjectBridgeService.GetHierarchyPath(chosen.gameObject, context)}'"
                                     + (chosen.isActiveAndEnabled ? " (active)" : " (none of them is active)") + ". Others: " + string.Join(", ", others)
                                     + ". Pass cameraInstanceId or cameraPath to pick another.");
                    }

                    return chosen;
                }

                warnings.Add($"No camera is named '{cameraName}': took the default camera instead.");
            }

            if (Camera.main != null)
            {
                return Camera.main;
            }

            return cameras.FirstOrDefault(camera => camera.isActiveAndEnabled) ?? cameras.FirstOrDefault();
        }

        private const int MaxCameraCandidates = 4;

        internal static byte[] EncodeRenderTexture(RenderTexture renderTexture, bool flipVertical)
        {
            return EncodeRenderTexture(renderTexture, renderTexture.width, renderTexture.height, flipVertical);
        }

        internal static byte[] EncodeRenderTexture(RenderTexture renderTexture, int width, int height, bool flipVertical)
        {
            return EncodeRenderTexture(renderTexture, ScreenshotRegion.Full, width, height, flipVertical);
        }

        // Crops `region` out of the source and scales it to width x height in one blit. Blit samples
        // source uv = dest uv * scale + offset, in the texture's STORED orientation. The region is given
        // top-left (as read off a PNG); when the source is stored top-first (flipVertical, the Game View
        // texture on UV-starts-at-top graphics APIs) stored v already runs top-down and `top` is the
        // offset, otherwise stored v runs bottom-up and the offset is measured from the bottom edge. Get
        // this wrong and a crop of the HUD returns the ground — while a full-view capture looks fine.
        internal static byte[] EncodeRenderTexture(RenderTexture renderTexture, ScreenshotRegion region, int width, int height, bool flipVertical)
        {
            ValidateScreenshotDimensions(width, height);
            var previousActive = RenderTexture.active;
            RenderTexture? scaledTexture = null;
            try
            {
                if (!region.IsFull || width != renderTexture.width || height != renderTexture.height)
                {
                    scaledTexture = RenderTexture.GetTemporary(width, height, 0, renderTexture.format);
                    var scale = new Vector2(region.Width, region.Height);
                    var offset = new Vector2(region.Left, flipVertical ? region.Top : 1f - region.Bottom);
                    Graphics.Blit(renderTexture, scaledTexture, scale, offset);
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

        // UnityEditor.GameView.GetMainGameViewTargetSize is gone in current Unity versions (renamed to
        // PlayModeView.GetMainPlayModeViewTargetSize), which silently dropped gameViewWidth/Height from this
        // metadata; the shared resolver probes every spelling.
        private static Vector2? TryGetMainGameViewTargetSize()
        {
            return ChievfxMcpRuntimeScreenSize.TryGetGameViewTargetSize();
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
