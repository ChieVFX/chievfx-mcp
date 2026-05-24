#nullable enable
using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Chievfx.Mcp.Editor
{
    internal static class McpArgumentReader
    {
        public static JToken ReadObject(JToken? element, string name)
        {
            return ReadProperty(element, name) is JObject obj ? obj : new JObject();
        }

        public static JToken ReadArray(JToken? element, string name)
        {
            return ReadProperty(element, name) is JArray arr ? arr : new JArray();
        }

        public static JToken? ReadProperty(JToken? element, string name)
        {
            if (element is not JObject objectElement)
            {
                return null;
            }

            foreach (var property in objectElement.Properties())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value;
                }
            }

            return null;
        }

        public static bool HasProperty(JToken? element, string name)
        {
            return ReadProperty(element, name) is not null;
        }

        public static string? ReadString(JToken? element, string name)
        {
            return ReadProperty(element, name) is JToken value && value.Type == JTokenType.String
                ? value.Value<string>()
                : null;
        }

        public static int ReadInt(JToken? element, string name, int defaultValue)
        {
            if (ReadProperty(element, name) is not JToken value)
            {
                return defaultValue;
            }

            if (value.Type == JTokenType.Integer)
            {
                try { return value.Value<int>(); }
                catch (Exception) { /* fall through */ }
            }

            if (value.Type == JTokenType.Float)
            {
                try { return (int)value.Value<double>(); }
                catch (Exception) { /* fall through */ }
            }

            if (value.Type == JTokenType.String
                && int.TryParse(value.Value<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                return number;
            }

            return defaultValue;
        }

        public static int? ReadNullableInt(JToken? element, string name)
        {
            if (ReadProperty(element, name) is not JToken value)
            {
                return null;
            }

            if (value.Type == JTokenType.Integer)
            {
                try { return value.Value<int>(); }
                catch (Exception) { /* fall through */ }
            }

            if (value.Type == JTokenType.Float)
            {
                try { return (int)value.Value<double>(); }
                catch (Exception) { /* fall through */ }
            }

            if (value.Type == JTokenType.String
                && int.TryParse(value.Value<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                return number;
            }

            return null;
        }

        public static bool ReadBool(JToken? element, string name, bool defaultValue)
        {
            if (ReadProperty(element, name) is JToken value && value.Type == JTokenType.Boolean)
            {
                return value.Value<bool>();
            }

            return defaultValue;
        }

        public static TEnum ReadEnum<TEnum>(JToken element, string name, TEnum defaultValue)
            where TEnum : struct
        {
            var text = ReadString(element, name);
            return !string.IsNullOrWhiteSpace(text) && Enum.TryParse<TEnum>(text, true, out var parsed)
                ? parsed
                : defaultValue;
        }

        public static int ClampInt(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }

            if (value > max)
            {
                return max;
            }

            return value;
        }

        public static string TrimText(string text, int maxChars, ref bool truncated)
        {
            if (text.Length <= maxChars)
            {
                return text;
            }

            truncated = true;
            return text.Substring(0, Math.Max(0, maxChars - 3)) + "...";
        }
    }
}
