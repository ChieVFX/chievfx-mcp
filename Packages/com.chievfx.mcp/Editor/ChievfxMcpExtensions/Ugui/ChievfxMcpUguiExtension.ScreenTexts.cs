#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.EventSystems;
using Chievfx.Mcp.Editor;
using UnityEngine.UI;

namespace Chievfx.Mcp.Extensions.Ugui
{
    /// <summary>
    /// The texts a player sees on screen (titles, descriptions, counters, button captions), grouped by window and read top
    /// to bottom: ui-control-find texts:true. Reads a screen without a screenshot. A text component is any uGUI Graphic
    /// with a string text property (Text, TextMeshProUGUI), so TextMesh Pro needs no reference. A text hidden by a
    /// CanvasGroup, smaller than a pixel, off screen or under another window is left out, as the player does not see it.
    /// </summary>
    internal static class UguiScreenTexts
    {
        private const int MaxTexts = 60;
        private const int MaxTextLength = 120;
        private const float MinVisiblePixels = 1f;
        private const float TransparentAlpha = 0.01f;
        private const string ButtonMark = " (button)";
        private const string DisabledButtonMark = " (disabled button)";

        private static readonly Regex RichTextOrSpaces = new(@"<[^>]+>|\s+", RegexOptions.CultureInvariant);
        private static readonly Dictionary<Type, PropertyInfo?> TextProperties = new();

        // The texts per window and how many there are in all (the list stops at MaxTexts). Wildcards match a text, its
        // window's name or its path, as they match a control's name or path: *DevWindow* finds the texts of a window that
        // lies deeper than the second level (DevMainMenu/SafeArea/Windows/DevWindow), as it finds that window's controls.
        internal static List<Dictionary<string, object?>> Build(string[] wildcards, Vector2 screenSize, out int total)
        {
            var found = new List<ScreenText>();
            // FindObjectsOfTypeAll works on every supported Unity without the FindObjectsByType deprecations; prefab assets drop
            // out on isActiveAndEnabled and the scene check.
            foreach (var graphic in Resources.FindObjectsOfTypeAll<Graphic>())
            {
                if (graphic.gameObject.scene.IsValid() && TryRead(graphic, wildcards, screenSize, out var text))
                {
                    found.Add(text);
                }
            }

            found.Sort((a, b) => a.Window != b.Window ? string.CompareOrdinal(a.Window, b.Window) : b.Y.CompareTo(a.Y));
            var windows = new List<Dictionary<string, object?>>();
            List<string>? lines = null;
            string? current = null;
            total = 0;
            for (var start = 0; start < found.Count;)
            {
                var end = LineEnd(found, start);
                foreach (var text in ReadLine(found, start, end))
                {
                    if (total++ >= MaxTexts)
                    {
                        continue;
                    }

                    if (found[start].Window != current || lines == null)
                    {
                        current = found[start].Window;
                        lines = new List<string>();
                        windows.Add(new Dictionary<string, object?> { ["window"] = current, ["texts"] = lines });
                    }

                    lines.Add(text);
                }

                start = end;
            }

            return windows;
        }

        // A row of the screen: texts of one window whose centres sit within half a text height of the first one's. Sorting by
        // height alone read a counter "25/75" as "75 | / | 25" (its three texts differ by a fraction of a pixel).
        private static int LineEnd(List<ScreenText> found, int start)
        {
            var first = found[start];
            var end = start + 1;
            while (end < found.Count && found[end].Window == first.Window
                   && Mathf.Abs(found[end].Y - first.Y) <= Mathf.Min(found[end].Height, first.Height) * 0.5f)
            {
                end++;
            }

            return end;
        }

        // The row left to right; the pieces of one button's caption in a row ("2", ".99", "USD" of a price) make one caption.
        private static IEnumerable<string> ReadLine(List<ScreenText> found, int start, int end)
        {
            var line = found.GetRange(start, end - start);
            line.Sort((a, b) => a.X.CompareTo(b.X));
            for (var i = 0; i < line.Count;)
            {
                var caption = line[i].Text;
                var next = i + 1;
                while (line[i].Owner != null && next < line.Count && ReferenceEquals(line[next].Owner, line[i].Owner))
                {
                    caption += " " + line[next].Text;
                    next++;
                }

                yield return caption + line[i].Mark;
                i = next;
            }
        }

        private static bool TryRead(Graphic graphic, string[] wildcards, Vector2 screenSize, out ScreenText screenText)
        {
            screenText = default;
            string text;
            var property = TextPropertyOf(graphic.GetType());
            if (property == null || graphic.isActiveAndEnabled == false || graphic.color.a <= TransparentAlpha
                || UguiRuntimeReachability.IsHiddenFromPlayer(graphic.gameObject))
            {
                return false;
            }

            text = Collapse(property.GetValue(graphic) as string ?? string.Empty);
            if (text.Length == 0)
            {
                return false;
            }

            var owner = graphic.GetComponentInParent<Selectable>();
            var mark = owner == null ? string.Empty : owner.enabled && owner.IsInteractable() ? ButtonMark : DisabledButtonMark;
            var window = WindowOf(graphic.transform);
            var windowName = window.name;
            if (wildcards.Length > 0 && ChievfxMcpRuntimeUiControlFind.MatchesWildcards(text + mark, windowName, wildcards) == false
                && ChievfxMcpRuntimeUiControlFind.MatchesWildcards(text + mark, UguiSharedHelpers.GetTransformPath(graphic.transform), wildcards) == false)
            {
                return false;
            }

            var canvas = graphic.canvas;
            if (canvas == null)
            {
                return false;
            }

            var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            var rect = graphic.rectTransform.rect;
            var center = RectTransformUtility.WorldToScreenPoint(camera, graphic.rectTransform.TransformPoint(rect.center));
            var corner = RectTransformUtility.WorldToScreenPoint(camera, graphic.rectTransform.TransformPoint(rect.max));
            // Scaled to a point is how tutorials and hints are hidden: the player sees nothing.
            if (Mathf.Abs(corner.x - center.x) * 2f < MinVisiblePixels || Mathf.Abs(corner.y - center.y) * 2f < MinVisiblePixels)
            {
                return false;
            }

            if (center.x < 0f || center.y < 0f || center.x > screenSize.x || center.y > screenSize.y
                || IsClippedByMask(graphic.transform, center, camera) || IsCoveredByOtherWindow(center, window, graphic))
            {
                return false;
            }

            text = text.Length > MaxTextLength ? text.Substring(0, MaxTextLength) + "…" : text;
            screenText = new ScreenText(windowName, center.x, center.y, Mathf.Abs(corner.y - center.y) * 2f, text, mark, owner);
            return true;
        }

        private readonly struct ScreenText
        {
            public readonly string Window;
            public readonly float X;
            public readonly float Y;
            public readonly float Height;
            public readonly string Text;
            public readonly string Mark;
            public readonly Selectable? Owner;

            public ScreenText(string window, float x, float y, float height, string text, string mark, Selectable? owner)
            {
                Window = window;
                X = x;
                Y = y;
                Height = height;
                Text = text;
                Mark = mark;
                Owner = owner;
            }
        }

        // A list row scrolled out of its viewport is still active and on screen, but a Mask or RectMask2D above it cuts it off.
        private static bool IsClippedByMask(Transform transform, Vector2 screenPoint, Camera? camera)
        {
            for (var current = transform.parent; current != null; current = current.parent)
            {
                var clips = (current.TryGetComponent<RectMask2D>(out var rectMask) && rectMask.enabled)
                            || (current.TryGetComponent<Mask>(out var mask) && mask.enabled);
                if (clips && current is RectTransform area && RectTransformUtility.RectangleContainsScreenPoint(area, screenPoint, camera) == false)
                {
                    return true;
                }
            }

            return false;
        }

        private static PropertyInfo? TextPropertyOf(Type type)
        {
            if (TextProperties.TryGetValue(type, out var property) == false)
            {
                property = type.GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                property = property != null && property.PropertyType == typeof(string) && property.CanRead ? property : null;
                TextProperties[type] = property;
            }

            return property;
        }

        // Rich text tags out, any run of whitespace as one space.
        private static string Collapse(string text)
        {
            return RichTextOrSpaces.Replace(text, " ").Trim();
        }

        // A window is the second level of the hierarchy (ScreenManager/<window>, UI/<canvas>); a lone root is its own.
        private static Transform WindowOf(Transform transform)
        {
            while (transform.parent != null && transform.parent.parent != null)
            {
                transform = transform.parent;
            }

            return transform;
        }

        // The player does not see a window's text where another window lies over it. The topmost raycast hit is not always
        // drawn over the text: a banner whose text and background take no raycasts lets the ray through to the window under it.
        private static bool IsCoveredByOtherWindow(Vector2 screenPoint, Transform window, Graphic text)
        {
            var eventSystem = EventSystem.current;
            if (eventSystem == null)
            {
                return false;
            }

            var results = new List<RaycastResult>();
            try
            {
                UguiTargetSizeRaycast.RaycastAll(eventSystem, new PointerEventData(eventSystem) { position = screenPoint }, results);
            }
            catch (Exception)
            {
                return false;
            }

            foreach (var hit in results)
            {
                // Transparent full-screen input zones (a stick, a look area) lie over everything and hide nothing.
                if (hit.gameObject.TryGetComponent<Graphic>(out var graphic) && graphic.color.a <= TransparentAlpha)
                {
                    continue;
                }

                return WindowOf(hit.gameObject.transform) != window && IsDrawnOver(hit, text);
            }

            return false;
        }

        // Overlay canvases are drawn after every camera, so over camera and world canvases whatever their order. Among overlay
        // canvases: sorting layer, then the order of the sorting canvas; in one canvas the later drawn graphic (bigger depth)
        // is on top. Anything else (two camera canvases, canvases of one order) is left as covering: the order is not known here.
        private static bool IsDrawnOver(RaycastResult hit, Graphic text)
        {
            var textCanvas = text.canvas;
            var hitCanvas = hit.module is GraphicRaycaster raycaster ? raycaster.GetComponent<Canvas>() : null;
            if (textCanvas == null || hitCanvas == null)
            {
                return true;
            }

            var isHitOverlay = hitCanvas.rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay;
            var isTextOverlay = textCanvas.rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay;
            if (isHitOverlay != isTextOverlay)
            {
                return isHitOverlay;
            }

            if (isHitOverlay == false)
            {
                return true;
            }

            var sortingCanvas = textCanvas.overrideSorting || textCanvas.isRootCanvas ? textCanvas : textCanvas.rootCanvas;
            var hitLayer = SortingLayer.GetLayerValueFromID(hit.sortingLayer);
            var textLayer = SortingLayer.GetLayerValueFromID(sortingCanvas.sortingLayerID);
            if (hitLayer != textLayer)
            {
                return hitLayer > textLayer;
            }

            if (hit.sortingOrder != sortingCanvas.sortingOrder)
            {
                return hit.sortingOrder > sortingCanvas.sortingOrder;
            }

            return hitCanvas != sortingCanvas || hit.depth > text.depth;
        }
    }
}
