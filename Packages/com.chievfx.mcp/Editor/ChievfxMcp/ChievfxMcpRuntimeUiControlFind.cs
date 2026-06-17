#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Chievfx.Mcp.Editor
{
    internal static class ChievfxMcpRuntimeUiControlFind
    {
        internal const int DefaultPageSize = 10;

        internal static string[] ParseWildcards(JToken args, string key)
        {
            if (args is not JObject obj || obj[key] == null || obj[key]!.Type == JTokenType.Null)
            {
                return Array.Empty<string>();
            }

            var token = obj[key]!;
            if (token.Type == JTokenType.String)
            {
                var pattern = token.Value<string>()?.Trim();
                if (string.IsNullOrEmpty(pattern))
                {
                    return Array.Empty<string>();
                }

                GameObjectBridgeService.ValidateWildcardPattern(pattern, key);
                return new[] { pattern };
            }

            if (token.Type == JTokenType.Array)
            {
                var patterns = new List<string>();
                foreach (var item in token)
                {
                    if (item?.Type != JTokenType.String)
                    {
                        continue;
                    }

                    var pattern = item.Value<string>()?.Trim();
                    if (string.IsNullOrEmpty(pattern))
                    {
                        continue;
                    }

                    GameObjectBridgeService.ValidateWildcardPattern(pattern, key);
                    patterns.Add(pattern);
                }

                return patterns.ToArray();
            }

            throw new ArgumentException($"{key} must be a string or string array.");
        }

        internal static bool MatchesWildcards(string name, string path, IReadOnlyList<string> wildcards)
        {
            if (wildcards == null || wildcards.Count == 0)
            {
                return true;
            }

            return wildcards.Any(pattern =>
                GameObjectBridgeService.WildcardMatches(name, pattern)
                || GameObjectBridgeService.WildcardMatches(path, pattern));
        }

        internal static bool IncludesAllFrameworks(string? frameworkFilter)
        {
            var filter = (frameworkFilter ?? string.Empty).Trim().ToLowerInvariant();
            return string.IsNullOrEmpty(filter)
                || string.Equals(filter, "all", StringComparison.Ordinal)
                || string.Equals(filter, "auto", StringComparison.Ordinal);
        }

        internal static bool MatchesFrameworkFilter(string? frameworkFilter, string adapterFrameworkId)
        {
            return IncludesAllFrameworks(frameworkFilter)
                || string.Equals((frameworkFilter ?? string.Empty).Trim().ToLowerInvariant(), adapterFrameworkId, StringComparison.Ordinal);
        }

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

            var trimmed = raw!.Trim();
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

        internal static Dictionary<string, object?> CreateZoneRow(
            float xMin,
            float yMin,
            float xMax,
            float yMax,
            Vector2 screenSize)
        {
            var center = new Vector2((xMin + xMax) * 0.5f, (yMin + yMax) * 0.5f);
            return new Dictionary<string, object?>
            {
                ["xMin"] = RoundForOutput(xMin),
                ["yMin"] = RoundForOutput(yMin),
                ["xMax"] = RoundForOutput(xMax),
                ["yMax"] = RoundForOutput(yMax),
                ["screenWidth"] = Math.Max(1, (int)Math.Round(screenSize.x, MidpointRounding.AwayFromZero)),
                ["screenHeight"] = Math.Max(1, (int)Math.Round(screenSize.y, MidpointRounding.AwayFromZero)),
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

        internal static string FormatText(
            int page,
            int totalPages,
            IReadOnlyList<Dictionary<string, object?>> controls,
            string? controlTypeFilter,
            bool normalizeCoords,
            float screenWidth,
            float screenHeight)
        {
            var lines = new List<string> { $"page:{page}/{totalPages}" };
            var omitType = !string.IsNullOrWhiteSpace(controlTypeFilter);
            foreach (var control in controls)
            {
                lines.Add(FormatRow(control, omitType, normalizeCoords, screenWidth, screenHeight));
            }

            return string.Join("\n", lines);
        }

        internal static string FormatRow(
            Dictionary<string, object?> control,
            bool omitType,
            bool normalizeCoords,
            float screenWidth,
            float screenHeight)
        {
            var builder = new StringBuilder("- ");
            builder.Append(ReadString(control, "path"));
            if (string.Equals(ReadString(control, "framework"), "uitoolkit", StringComparison.Ordinal))
            {
                var visualElementRef = ReadString(control, "visualElementRef");
                if (!string.IsNullOrWhiteSpace(visualElementRef))
                {
                    builder.Append(' ').Append('(').Append(visualElementRef).Append(')');
                }
            }
            else
            {
                var instanceId = ReadInt(control, "instanceId");
                if (instanceId > 0)
                {
                    builder.Append(" (id: ").Append(instanceId.ToString(CultureInfo.InvariantCulture)).Append(')');
                }
            }

            if (!omitType)
            {
                var controlType = ReadString(control, "controlType");
                if (!string.IsNullOrWhiteSpace(controlType))
                {
                    builder.Append(" : ").Append(controlType);
                }
            }

            var zoneText = FormatZoneText(
                control.TryGetValue("zone", out var zone) ? zone : null,
                normalizeCoords,
                screenWidth,
                screenHeight);
            if (!string.IsNullOrWhiteSpace(zoneText))
            {
                builder.Append("; zone:").Append(zoneText);
            }

            return builder.ToString();
        }

        private static string FormatZoneText(object? zoneValue, bool normalizeCoords, float screenWidth, float screenHeight)
        {
            if (zoneValue is not Dictionary<string, object?> zone)
            {
                return string.Empty;
            }

            return FormatZoneBounds(zone, normalizeCoords, screenWidth, screenHeight);
        }

        private static string FormatZoneBounds(
            Dictionary<string, object?> zone,
            bool normalizeCoords,
            float screenWidth,
            float screenHeight)
        {
            if (!TryReadFloat(zone, "xMin", out var xMin)
                || !TryReadFloat(zone, "yMin", out var yMin)
                || !TryReadFloat(zone, "xMax", out var xMax)
                || !TryReadFloat(zone, "yMax", out var yMax))
            {
                return string.Empty;
            }

            if (normalizeCoords)
            {
                var width = Math.Max(1f, screenWidth);
                var height = Math.Max(1f, screenHeight);
                return $"{FormatNormalizedCoord(xMin / width)},{FormatNormalizedCoord(yMin / height)}..{FormatNormalizedCoord(xMax / width)},{FormatNormalizedCoord(yMax / height)}";
            }

            return $"{(int)Math.Ceiling(xMin)},{(int)Math.Ceiling(yMin)}..{(int)Math.Floor(xMax)},{(int)Math.Floor(yMax)}";
        }

        private static string FormatNormalizedCoord(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return "0";
            }

            var clamped = Math.Max(0f, Math.Min(1f, value));
            var rounded = (float)Math.Round(clamped, 2, MidpointRounding.AwayFromZero);
            if (rounded <= 0f)
            {
                return "0";
            }

            if (rounded >= 1f)
            {
                return "1";
            }

            return rounded.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static string ReadString(Dictionary<string, object?> row, string key)
        {
            return row.TryGetValue(key, out var value) && value != null
                ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
                : string.Empty;
        }

        private static int ReadInt(Dictionary<string, object?> row, string key)
        {
            if (!row.TryGetValue(key, out var value) || value == null)
            {
                return 0;
            }

            return value switch
            {
                int intValue => intValue,
                long longValue => (int)longValue,
                float floatValue => (int)floatValue,
                double doubleValue => (int)doubleValue,
                _ => int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : 0,
            };
        }

        private static bool TryReadFloat(Dictionary<string, object?> row, string key, out float value)
        {
            value = 0f;
            if (!row.TryGetValue(key, out var raw) || raw == null)
            {
                return false;
            }

            switch (raw)
            {
                case float floatValue:
                    value = floatValue;
                    return true;
                case double doubleValue:
                    value = (float)doubleValue;
                    return true;
                case int intValue:
                    value = intValue;
                    return true;
                case long longValue:
                    value = longValue;
                    return true;
                default:
                    return float.TryParse(
                        Convert.ToString(raw, CultureInfo.InvariantCulture),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out value);
            }
        }
    }
}
