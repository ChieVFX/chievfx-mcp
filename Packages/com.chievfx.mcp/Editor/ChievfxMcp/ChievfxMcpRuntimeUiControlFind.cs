#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Chievfx.Mcp.Editor
{
    internal static class ChievfxMcpRuntimeUiControlFind
    {
        internal static string NormalizeControlType(Type type)
        {
            var name = type.Name;
            if (string.Equals(name, "Button", StringComparison.Ordinal))
            {
                return "button";
            }

            if (string.Equals(name, "Toggle", StringComparison.Ordinal))
            {
                return "toggle";
            }

            if (string.Equals(name, "Slider", StringComparison.Ordinal)
                || string.Equals(name, "SliderInt", StringComparison.Ordinal)
                || string.Equals(name, "MinMaxSlider", StringComparison.Ordinal))
            {
                return "slider";
            }

            if (string.Equals(name, "Scrollbar", StringComparison.Ordinal))
            {
                return "scrollbar";
            }

            if (string.Equals(name, "Dropdown", StringComparison.Ordinal)
                || string.Equals(name, "TMP_Dropdown", StringComparison.Ordinal))
            {
                return "dropdown";
            }

            if (string.Equals(name, "InputField", StringComparison.Ordinal)
                || string.Equals(name, "TMP_InputField", StringComparison.Ordinal)
                || name.EndsWith("Field", StringComparison.Ordinal))
            {
                return "inputfield";
            }

            if (string.Equals(name, "ScrollRect", StringComparison.Ordinal)
                || string.Equals(name, "ScrollView", StringComparison.Ordinal))
            {
                return "scrollrect";
            }

            if (name.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "button";
            }

            if (name.IndexOf("Toggle", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "toggle";
            }

            if (name.IndexOf("Slider", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "slider";
            }

            return name.ToLowerInvariant();
        }

        internal static string? NormalizeControlTypeFilter(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var trimmed = raw.Trim();
            if (string.Equals(trimmed, "input", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "textfield", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "TMP_InputField", StringComparison.OrdinalIgnoreCase))
            {
                return "inputfield";
            }

            if (string.Equals(trimmed, "scroll", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "scrollview", StringComparison.OrdinalIgnoreCase))
            {
                return "scrollrect";
            }

            if (string.Equals(trimmed, "TMP_Dropdown", StringComparison.OrdinalIgnoreCase))
            {
                return "dropdown";
            }

            return trimmed
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant();
        }

        internal static bool IsZonePartiallyOnScreen(float xMin, float yMin, float xMax, float yMax, Vector2 screenSize)
        {
            if (xMax <= xMin || yMax <= yMin)
            {
                return false;
            }

            return xMax > 0f
                && yMax > 0f
                && xMin < screenSize.x
                && yMin < screenSize.y;
        }

        internal static Dictionary<string, object?> CreateZoneRow(float xMin, float yMin, float xMax, float yMax)
        {
            var center = new Vector2((xMin + xMax) * 0.5f, (yMin + yMax) * 0.5f);
            return new Dictionary<string, object?>
            {
                ["xMin"] = RoundForOutput(xMin),
                ["yMin"] = RoundForOutput(yMin),
                ["xMax"] = RoundForOutput(xMax),
                ["yMax"] = RoundForOutput(yMax),
                ["center"] = new Dictionary<string, object?>
                {
                    ["x"] = RoundForOutput(center.x),
                    ["y"] = RoundForOutput(center.y),
                },
            };
        }

        internal static float RoundForOutput(float value)
        {
            return (float)Math.Round(value, 1, MidpointRounding.AwayFromZero);
        }
    }
}
