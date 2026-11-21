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

        // The texts per window and how many were found in all (the list stops at MaxTexts). Wildcards match a text or
        // its window's name, as they match a control's name or path.
        internal static List<Dictionary<string, object?>> Build(string[] wildcards, Vector2 screenSize, out int total)
        {
            var found = new List<(string Window, float Y, string Text)>();
            // FindObjectsOfTypeAll works on every supported Unity without the FindObjectsByType deprecations; prefab assets drop
            // out on isActiveAndEnabled and the scene check.
            foreach (var graphic in Resources.FindObjectsOfTypeAll<Graphic>())
            {
                if (graphic.gameObject.scene.IsValid() && TryRead(graphic, wildcards, screenSize, out var window, out var y, out var text))
                {
                    found.Add((window, y, text));
                }
            }

            total = found.Count;
            found.Sort((a, b) => a.Window != b.Window ? string.CompareOrdinal(a.Window, b.Window) : b.Y.CompareTo(a.Y));
            var windows = new List<Dictionary<string, object?>>();
            List<string>? lines = null;
            string? current = null;
            for (var i = 0; i < found.Count && i < MaxTexts; i++)
            {
                if (found[i].Window != current || lines == null)
                {
                    current = found[i].Window;
                    lines = new List<string>();
                    windows.Add(new Dictionary<string, object?> { ["window"] = current, ["texts"] = lines });
                }

                lines.Add(found[i].Text);
            }

            return windows;
        }

        private static bool TryRead(Graphic graphic, string[] wildcards, Vector2 screenSize, out string windowName, out float y, out string text)
        {
            windowName = string.Empty;
            y = 0f;
            text = string.Empty;
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
            if (owner != null)
            {
                text += owner.enabled && owner.IsInteractable() ? ButtonMark : DisabledButtonMark;
            }

            var window = WindowOf(graphic.transform);
            windowName = window.name;
            if (wildcards.Length > 0 && ChievfxMcpRuntimeUiControlFind.MatchesWildcards(text, windowName, wildcards) == false)
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
                || IsClippedByMask(graphic.transform, center, camera) || IsCoveredByOtherWindow(center, window))
            {
                return false;
            }

            y = center.y;
            text = text.Length > MaxTextLength ? text.Substring(0, MaxTextLength) + "…" : text;
            return true;
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

        // The player does not see a window's text where another window lies over it.
        private static bool IsCoveredByOtherWindow(Vector2 screenPoint, Transform window)
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

                return WindowOf(hit.gameObject.transform) != window;
            }

            return false;
        }
    }
}
