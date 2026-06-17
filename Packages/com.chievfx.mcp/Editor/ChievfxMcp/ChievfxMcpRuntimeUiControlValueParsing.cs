#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Chievfx.Mcp.Editor
{
    internal static class ChievfxMcpRuntimeUiControlValueParsing
    {
        internal static bool TryParseFlexibleBool(JToken? token, out bool value)
        {
            value = default;
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }

            switch (token.Type)
            {
                case JTokenType.Boolean:
                    value = token.Value<bool>();
                    return true;
                case JTokenType.Integer:
                    var intValue = token.Value<int>();
                    if (intValue is 0 or 1)
                    {
                        value = intValue == 1;
                        return true;
                    }

                    return false;
                case JTokenType.Float:
                    var floatValue = token.Value<float>();
                    if (Math.Abs(floatValue) < 0.0001f)
                    {
                        value = false;
                        return true;
                    }

                    if (Math.Abs(floatValue - 1f) < 0.0001f)
                    {
                        value = true;
                        return true;
                    }

                    return false;
                case JTokenType.String:
                    var text = token.Value<string>()?.Trim();
                    if (string.IsNullOrEmpty(text))
                    {
                        return false;
                    }

                    if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(text, "1", StringComparison.OrdinalIgnoreCase))
                    {
                        value = true;
                        return true;
                    }

                    if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(text, "0", StringComparison.OrdinalIgnoreCase))
                    {
                        value = false;
                        return true;
                    }

                    return false;
                default:
                    return false;
            }
        }

        internal static bool TryParseFlexibleFloat(JToken? token, out float value)
        {
            value = default;
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }

            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                value = token.Value<float>();
                return true;
            }

            if (token.Type == JTokenType.String
                && float.TryParse(token.Value<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            return false;
        }

        internal static bool TryParseFlexibleInt(JToken? token, out int value)
        {
            value = default;
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }

            if (token.Type == JTokenType.Integer)
            {
                value = token.Value<int>();
                return true;
            }

            if (token.Type == JTokenType.Float)
            {
                var floatValue = token.Value<float>();
                if (Math.Abs(floatValue - Math.Round(floatValue)) < 0.0001f)
                {
                    value = (int)Math.Round(floatValue);
                    return true;
                }

                return false;
            }

            if (token.Type == JTokenType.String
                && int.TryParse(token.Value<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            return false;
        }

        internal static string FormatAcceptedValues(IEnumerable<object?> values)
        {
            return string.Join(", ", values.Select(value => value switch
            {
                null => "null",
                string text => "\"" + text + "\"",
                bool boolean => boolean ? "true" : "false",
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            }));
        }

        internal static ArgumentException InvalidValue(string controlKind, JToken? token, string detail, IEnumerable<object?>? acceptedValues = null)
        {
            var supplied = token == null || token.Type == JTokenType.Null ? "null" : token.ToString();
            var message = $"Invalid {controlKind} value '{supplied}'. {detail}";
            if (acceptedValues != null)
            {
                message += " Accepted values: " + FormatAcceptedValues(acceptedValues) + ".";
            }

            return new ArgumentException(message);
        }
    }
}
