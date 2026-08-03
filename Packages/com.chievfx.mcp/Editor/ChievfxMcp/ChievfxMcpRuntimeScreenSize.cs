#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Chievfx.Mcp.Editor
{
    // Screen.width/height read from the editor bridge report the Game View *window* size, not the size the
    // running game renders and raycasts against: Unity resolves Screen.* against the current GUIView when it
    // is asked from editor code. Lock the Game View to a fixed resolution (a 2340x1080 target inside a
    // 1414x1036 window) and every normalized<->pixel conversion is off by that ratio — ~20% on x — so a click
    // aimed at the right edge of the HUD lands a fifth of the screen to the left, silently, on whatever is
    // there. Canvas.pixelRect, Camera.pixelWidth, the Game View render texture, and the EventSystem all agree
    // on the target size, so resolve that and keep Screen.* as the last fallback.
    internal static class ChievfxMcpRuntimeScreenSize
    {
        internal const string GameViewSource = "gameView.targetSize";
        internal const string CanvasSource = "canvas.pixelRect";
        internal const string ScreenSource = "screen";

        private static MethodInfo? cachedTargetSizeMethod;
        private static bool targetSizeMethodResolved;

        internal static Vector2 UnityScreenSize => Sanitize(new Vector2(Screen.width, Screen.height));

        internal static Vector2 Resolve()
        {
            return Resolve(out _);
        }

        internal static Vector2 Resolve(out string source)
        {
            return Resolve(canvasPixelSizes: null, out source);
        }

        // Callers that already enumerate runtime canvases (the uGUI extension) pass them in so the fallback
        // does not pay for a second Resources.FindObjectsOfTypeAll sweep.
        internal static Vector2 Resolve(Func<IEnumerable<Vector2>>? canvasPixelSizes, out string source)
        {
            return Resolve(
                TryGetGameViewTargetSize(),
                UnityScreenSize,
                canvasPixelSizes ?? EnumerateRuntimeCanvasPixelSizes,
                out source);
        }

        // Pure selection so the priority order is unit-testable without a Game View.
        internal static Vector2 Resolve(
            Vector2? gameViewTargetSize,
            Vector2 screenSize,
            Func<IEnumerable<Vector2>>? canvasPixelSizes,
            out string source)
        {
            if (IsUsable(gameViewTargetSize))
            {
                source = GameViewSource;
                return Sanitize(gameViewTargetSize!.Value);
            }

            var canvasSize = LargestUsableSize(canvasPixelSizes);
            if (IsUsable(canvasSize))
            {
                source = CanvasSource;
                return Sanitize(canvasSize!.Value);
            }

            source = ScreenSource;
            return Sanitize(screenSize);
        }

        // Non-null only when Unity's Screen.* disagrees with the resolved size — the fixed-resolution Game
        // View case that used to mis-aim every normalized coordinate. Tools surface it so the divisor they
        // used is visible in the output instead of implied.
        internal static string? DescribeResolvedSource(Vector2 resolvedSize)
        {
            var screenSize = UnityScreenSize;
            if (Mathf.Approximately(resolvedSize.x, screenSize.x) && Mathf.Approximately(resolvedSize.y, screenSize.y))
            {
                return null;
            }

            Resolve(out var source);
            return source;
        }

        // The size the Game View (or Device Simulator) renders at, which is what the player loop sees as
        // Screen.width/height. Every Unity version exposes it, but under a different name.
        internal static Vector2? TryGetGameViewTargetSize()
        {
            var method = ResolveTargetSizeMethod();
            if (method == null)
            {
                return null;
            }

            try
            {
                return method.Invoke(null, null) is Vector2 size && IsUsable(size) ? size : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static MethodInfo? ResolveTargetSizeMethod()
        {
            if (targetSizeMethodResolved)
            {
                return cachedTargetSizeMethod;
            }

            targetSizeMethodResolved = true;
            var editorAssembly = typeof(EditorWindow).Assembly;
            var candidates = new[]
            {
                ("UnityEditor.PlayModeView", "GetMainPlayModeViewTargetSize"),
                ("UnityEditor.GameView", "GetMainGameViewTargetSize"),
                ("UnityEditor.Handles", "GetMainGameViewSize"),
            };

            foreach (var (typeName, methodName) in candidates)
            {
                try
                {
                    var type = editorAssembly.GetType(typeName);
                    var method = type?.GetMethod(
                        methodName,
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                        null,
                        Type.EmptyTypes,
                        null);
                    if (method != null && method.ReturnType == typeof(Vector2))
                    {
                        cachedTargetSizeMethod = method;
                        return cachedTargetSizeMethod;
                    }
                }
                catch (Exception)
                {
                    // Keep probing the remaining candidates; the canvas/Screen fallbacks cover total failure.
                }
            }

            return cachedTargetSizeMethod;
        }

        // Canvas is reached by reflection because com.unity.ugui can be absent from a project.
        private static IEnumerable<Vector2> EnumerateRuntimeCanvasPixelSizes()
        {
            var canvasType = ProfilerBridgeService.TryResolveTypeByNames(
                new[] { "UnityEngine.Canvas", "UnityEngine.Canvas, UnityEngine.UIModule" });
            if (canvasType == null)
            {
                yield break;
            }

            var pixelRectProperty = canvasType.GetProperty("pixelRect", BindingFlags.Instance | BindingFlags.Public);
            var isRootCanvasProperty = canvasType.GetProperty("isRootCanvas", BindingFlags.Instance | BindingFlags.Public);
            if (pixelRectProperty == null)
            {
                yield break;
            }

            foreach (var unityObject in Resources.FindObjectsOfTypeAll(canvasType))
            {
                if (unityObject is not Component component
                    || !component.gameObject.scene.IsValid()
                    || !component.gameObject.activeInHierarchy)
                {
                    continue;
                }

                Vector2 size;
                try
                {
                    if (isRootCanvasProperty?.GetValue(component) is false)
                    {
                        continue;
                    }

                    if (pixelRectProperty.GetValue(component) is not Rect pixelRect)
                    {
                        continue;
                    }

                    size = new Vector2(pixelRect.width, pixelRect.height);
                }
                catch (Exception)
                {
                    continue;
                }

                if (IsUsable(size))
                {
                    yield return size;
                }
            }
        }

        private static Vector2? LargestUsableSize(Func<IEnumerable<Vector2>>? sizes)
        {
            if (sizes == null)
            {
                return null;
            }

            try
            {
                return sizes()
                    .Where(size => IsUsable(size))
                    .OrderByDescending(size => size.x * size.y)
                    .Select(size => (Vector2?)size)
                    .FirstOrDefault();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsUsable(Vector2? size)
        {
            return size.HasValue && IsUsable(size.Value);
        }

        private static bool IsUsable(Vector2 size)
        {
            return size.x >= 1f
                && size.y >= 1f
                && !float.IsNaN(size.x)
                && !float.IsNaN(size.y)
                && !float.IsInfinity(size.x)
                && !float.IsInfinity(size.y);
        }

        private static Vector2 Sanitize(Vector2 size)
        {
            return new Vector2(
                IsUsable(new Vector2(size.x, 1f)) ? size.x : 1f,
                IsUsable(new Vector2(1f, size.y)) ? size.y : 1f);
        }
    }
}
