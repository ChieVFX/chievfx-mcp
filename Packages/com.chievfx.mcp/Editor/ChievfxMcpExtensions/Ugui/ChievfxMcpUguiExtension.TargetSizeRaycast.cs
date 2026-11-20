#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using Chievfx.Mcp.Editor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Chievfx.Mcp.Extensions.Ugui
{
    /// <summary>
    /// EventSystem.RaycastAll as the running game sees it. GraphicRaycaster bounds an overlay canvas by
    /// Screen.width/height, and from editor code those are the Game View window's size, not the size the game
    /// renders at: with a 1920x1080 target in a 1376x1063 window everything right of x=1376 "hit nothing", and the
    /// bound moved with whichever editor view drew last. Overlay canvases are re-tested against the render size
    /// with UGUI's own per-graphic test; everything else keeps the stock result.
    /// </summary>
    internal static class UguiTargetSizeRaycast
    {
        private static readonly MethodInfo? GraphicTestMethod = typeof(GraphicRaycaster).GetMethod(
            "Raycast",
            BindingFlags.NonPublic | BindingFlags.Static,
            null,
            new[] { typeof(Canvas), typeof(Camera), typeof(Vector2), typeof(IList<Graphic>), typeof(List<Graphic>) },
            null);

        private static readonly Comparison<RaycastResult>? EventSystemOrder = CreateEventSystemOrder();

        internal static void RaycastAll(EventSystem eventSystem, PointerEventData pointer, List<RaycastResult> results)
        {
            eventSystem.RaycastAll(pointer, results);
            var renderSize = ChievfxMcpRuntimeScreenSize.Resolve();
            var screenSize = ChievfxMcpRuntimeScreenSize.UnityScreenSize;
            if (GraphicTestMethod == null || EventSystemOrder == null
                || (Mathf.Approximately(renderSize.x, screenSize.x) && Mathf.Approximately(renderSize.y, screenSize.y)))
            {
                return;
            }

            var position = pointer.position;
            results.RemoveAll(result => result.module is GraphicRaycaster raycaster && raycaster.eventCamera == null);
            if (position.x < 0f || position.y < 0f || position.x > renderSize.x || position.y > renderSize.y)
            {
                SortAndIndex(results);
                return;
            }

            var hits = new List<Graphic>();
            // FindObjectsOfTypeAll works on every supported Unity without the FindObjectsByType deprecations; prefab
            // assets drop out on isActiveAndEnabled and the scene check.
            foreach (var raycaster in Resources.FindObjectsOfTypeAll<GraphicRaycaster>())
            {
                // Only the raycasters that bound by Screen.*: overlay canvases and screen-space canvases without a camera.
                if (!raycaster.isActiveAndEnabled || !raycaster.gameObject.scene.IsValid() || raycaster.eventCamera != null || !raycaster.TryGetComponent<Canvas>(out var canvas) || canvas.targetDisplay != 0)
                {
                    continue;
                }

                var canvasGraphics = GraphicRegistry.GetRaycastableGraphicsForCanvas(canvas);
                if (canvasGraphics == null || canvasGraphics.Count == 0)
                {
                    continue;
                }

                hits.Clear();
                GraphicTestMethod.Invoke(null, new object?[] { canvas, null, position, canvasGraphics, hits });
                foreach (var graphic in hits)
                {
                    var hitObject = graphic.gameObject;
                    if (raycaster.ignoreReversedGraphics && Vector3.Dot(Vector3.forward, hitObject.transform.rotation * Vector3.forward) <= 0f)
                    {
                        continue;
                    }

                    results.Add(new RaycastResult
                    {
                        gameObject = hitObject,
                        module = raycaster,
                        distance = 0f,
                        screenPosition = position,
                        displayIndex = 0,
                        depth = graphic.depth,
                        sortingLayer = canvas.sortingLayerID,
                        sortingOrder = canvas.sortingOrder,
                        worldPosition = Vector3.zero,
                        worldNormal = -hitObject.transform.forward,
                    });
                }
            }

            SortAndIndex(results);
        }

        private static void SortAndIndex(List<RaycastResult> results)
        {
            for (var i = 0; i < results.Count; i++)
            {
                var result = results[i];
                result.index = i;
                results[i] = result;
            }

            results.Sort(EventSystemOrder!);
        }

        // EventSystem's own ordering (camera depth, sorting layer and order, graphic depth, distance), so the top hit is
        // the one the game's input module would pick.
        private static Comparison<RaycastResult>? CreateEventSystemOrder()
        {
            var method = typeof(EventSystem).GetMethod("RaycastComparer", BindingFlags.NonPublic | BindingFlags.Static);
            return method == null ? null : (Comparison<RaycastResult>)Delegate.CreateDelegate(typeof(Comparison<RaycastResult>), method);
        }
    }
}
