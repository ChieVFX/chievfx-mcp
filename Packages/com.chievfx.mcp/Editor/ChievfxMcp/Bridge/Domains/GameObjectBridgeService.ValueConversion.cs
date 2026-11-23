#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Chievfx.Mcp.Editor
{
    internal sealed partial class GameObjectBridgeService
    {
        private static object? ConvertJsonToType(JToken value, Type targetType, string displayName)
        {
            var nullableType = Nullable.GetUnderlyingType(targetType);
            if (value.Type == JTokenType.Null)
            {
                if (!targetType.IsValueType || nullableType != null)
                {
                    return null;
                }

                throw new ArgumentException($"{displayName} cannot be null.");
            }

            targetType = nullableType ?? targetType;
            if (targetType == typeof(string))
            {
                return value.Type == JTokenType.String ? value.Value<string>() : value.ToString(Formatting.None);
            }

            if (targetType == typeof(bool))
            {
                return ReadBoolToken(value, displayName);
            }

            if (targetType == typeof(int))
            {
                return ReadIntToken(value, displayName);
            }

            if (targetType == typeof(float))
            {
                return (float)ReadDoubleToken(value, displayName);
            }

            if (targetType == typeof(double))
            {
                return ReadDoubleToken(value, displayName);
            }

            if (targetType.IsEnum)
            {
                return value.Type == JTokenType.String
                    ? Enum.Parse(targetType, value.Value<string>()!, true)
                    : Enum.ToObject(targetType, ReadIntToken(value, displayName));
            }

            if (targetType == typeof(Vector2))
            {
                return ReadVector2Token(value, displayName);
            }

            if (targetType == typeof(Vector3))
            {
                return ReadVector3Token(value, displayName);
            }

            if (targetType == typeof(Vector4))
            {
                return ReadVector4Token(value, displayName);
            }

            if (targetType == typeof(Color))
            {
                return ReadColorToken(value, displayName);
            }

            if (targetType == typeof(Quaternion))
            {
                return ReadQuaternionToken(value, displayName);
            }

            if (typeof(Object).IsAssignableFrom(targetType))
            {
                var unityObject = ReadObjectReferenceToken(value, displayName);
                if (unityObject != null && !targetType.IsInstanceOfType(unityObject))
                {
                    throw new ArgumentException($"{displayName} resolved object type {unityObject.GetType().Name} is not assignable to {targetType.Name}.");
                }

                return unityObject;
            }

            return value.ToObject(targetType);
        }

        private static int ReadIntToken(JToken token, string displayName)
        {
            if (token.Type == JTokenType.Integer)
            {
                return token.Value<int>();
            }

            if (token.Type == JTokenType.Float)
            {
                return (int)token.Value<double>();
            }

            if (token.Type == JTokenType.String
                && int.TryParse(token.Value<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            throw new ArgumentException($"{displayName} must be an integer.");
        }

        private static bool ReadBoolToken(JToken token, string displayName)
        {
            if (token.Type == JTokenType.Boolean)
            {
                return token.Value<bool>();
            }

            if (token.Type == JTokenType.String
                && bool.TryParse(token.Value<string>(), out var parsed))
            {
                return parsed;
            }

            throw new ArgumentException($"{displayName} must be a boolean.");
        }

        private static double ReadDoubleToken(JToken token, string displayName)
        {
            double number;
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                number = token.Value<double>();
            }
            else if (token.Type == JTokenType.String
                && double.TryParse(token.Value<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                number = parsed;
            }
            else
            {
                throw new ArgumentException($"{displayName} must be a finite number.");
            }

            if (double.IsNaN(number) || double.IsInfinity(number))
            {
                throw new ArgumentException($"{displayName} must be finite.");
            }

            return number;
        }

        private static float ReadFloatComponent(JObject obj, string propertyName, string displayName)
        {
            var token = GetObjectPropertyValue(obj, propertyName)
                ?? throw new ArgumentException($"{displayName}.{propertyName} is required.");
            return (float)ReadDoubleToken(token, $"{displayName}.{propertyName}");
        }

        private static int ReadIntComponent(JObject obj, string propertyName, string displayName)
        {
            var token = GetObjectPropertyValue(obj, propertyName)
                ?? throw new ArgumentException($"{displayName}.{propertyName} is required.");
            return ReadIntToken(token, $"{displayName}.{propertyName}");
        }

        private static Vector2 ReadVector2Token(JToken token, string displayName)
        {
            var obj = RequireObject(token, displayName, "x", "y");
            return new Vector2(ReadFloatComponent(obj, "x", displayName), ReadFloatComponent(obj, "y", displayName));
        }

        private static Vector3 ReadVector3Token(JToken token, string displayName)
        {
            var obj = RequireObject(token, displayName, "x", "y", "z");
            return new Vector3(ReadFloatComponent(obj, "x", displayName), ReadFloatComponent(obj, "y", displayName), ReadFloatComponent(obj, "z", displayName));
        }

        private static Vector4 ReadVector4Token(JToken token, string displayName)
        {
            var obj = RequireObject(token, displayName, "x", "y", "z", "w");
            return new Vector4(
                ReadFloatComponent(obj, "x", displayName),
                ReadFloatComponent(obj, "y", displayName),
                ReadFloatComponent(obj, "z", displayName),
                ReadFloatComponent(obj, "w", displayName));
        }

        private static Vector2Int ReadVector2IntToken(JToken token, string displayName)
        {
            var obj = RequireObject(token, displayName, "x", "y");
            return new Vector2Int(ReadIntComponent(obj, "x", displayName), ReadIntComponent(obj, "y", displayName));
        }

        private static Vector3Int ReadVector3IntToken(JToken token, string displayName)
        {
            var obj = RequireObject(token, displayName, "x", "y", "z");
            return new Vector3Int(ReadIntComponent(obj, "x", displayName), ReadIntComponent(obj, "y", displayName), ReadIntComponent(obj, "z", displayName));
        }

        private static Color ReadColorToken(JToken token, string displayName)
        {
            if (token.Type == JTokenType.String && ColorUtility.TryParseHtmlString(token.Value<string>(), out var color))
            {
                return color;
            }

            // "RGBA(1.0,0.5,0.0,1.0)" as gameobject-component-get prints it: alpha may be left out, as in the object form.
            var obj = TryReadPrintedNumbers(token, 3) is { } rgb
                ? new JObject { ["r"] = rgb[0], ["g"] = rgb[1], ["b"] = rgb[2] }
                : RequireObject(token, displayName, "r", "g", "b", "a");
            return new Color(
                ReadFloatComponent(obj, "r", displayName),
                ReadFloatComponent(obj, "g", displayName),
                ReadFloatComponent(obj, "b", displayName),
                GetObjectPropertyValue(obj, "a") != null ? ReadFloatComponent(obj, "a", displayName) : 1f);
        }

        private static Rect ReadRectToken(JToken token, string displayName)
        {
            var obj = RequireObject(token, displayName, "x", "y", "width", "height");
            return new Rect(
                ReadFloatComponent(obj, "x", displayName),
                ReadFloatComponent(obj, "y", displayName),
                ReadFloatComponent(obj, "width", displayName),
                ReadFloatComponent(obj, "height", displayName));
        }

        private static RectInt ReadRectIntToken(JToken token, string displayName)
        {
            var obj = RequireObject(token, displayName, "x", "y", "width", "height");
            return new RectInt(
                ReadIntComponent(obj, "x", displayName),
                ReadIntComponent(obj, "y", displayName),
                ReadIntComponent(obj, "width", displayName),
                ReadIntComponent(obj, "height", displayName));
        }

        private static Bounds ReadBoundsToken(JToken token, string displayName)
        {
            var obj = RequireObject(token, displayName);
            var center = GetObjectPropertyValue(obj, "center")
                ?? throw new ArgumentException($"{displayName}.center is required.");
            var size = GetObjectPropertyValue(obj, "size")
                ?? throw new ArgumentException($"{displayName}.size is required.");
            return new Bounds(ReadVector3Token(center, $"{displayName}.center"), ReadVector3Token(size, $"{displayName}.size"));
        }

        private static BoundsInt ReadBoundsIntToken(JToken token, string displayName)
        {
            var obj = RequireObject(token, displayName);
            var position = GetObjectPropertyValue(obj, "position")
                ?? throw new ArgumentException($"{displayName}.position is required.");
            var size = GetObjectPropertyValue(obj, "size")
                ?? throw new ArgumentException($"{displayName}.size is required.");
            return new BoundsInt(ReadVector3IntToken(position, $"{displayName}.position"), ReadVector3IntToken(size, $"{displayName}.size"));
        }

        private static Quaternion ReadQuaternionToken(JToken token, string displayName)
        {
            // gameobject-component-get prints a rotation as its Euler angles "(x, y, z)".
            if (TryReadPrintedNumbers(token, 3) is { } euler)
            {
                return Quaternion.Euler((float)euler[0], (float)euler[1], (float)euler[2]);
            }

            var obj = RequireObject(token, displayName, "x", "y", "z", "w");
            if (GetObjectPropertyValue(obj, "euler") is JObject eulerObj)
            {
                return Quaternion.Euler(ReadVector3Token(eulerObj, $"{displayName}.euler"));
            }

            return new Quaternion(
                ReadFloatComponent(obj, "x", displayName),
                ReadFloatComponent(obj, "y", displayName),
                ReadFloatComponent(obj, "z", displayName),
                ReadFloatComponent(obj, "w", displayName));
        }

        // A value is written back in the form gameobject-component-get prints it ("RGBA(1.0,0.5,0.0,1.0)", "(36, 24)",
        // "(x:0, y:0, width:1, height:1)"): the read value was refused with "must be an object", which did not say the form.
        private static JObject RequireObject(JToken token, string displayName, params string[] components)
        {
            if (token is JObject obj)
            {
                return obj;
            }

            if (components.Length > 0 && TryReadPrintedNumbers(token, components.Length) is { } numbers)
            {
                var printed = new JObject();
                for (var i = 0; i < components.Length; i++)
                {
                    printed[components[i]] = numbers[i];
                }

                return printed;
            }

            throw new ArgumentException(components.Length > 0
                ? $"{displayName} must be an object {{{string.Join(", ", components.Select(component => "\"" + component + "\":<number>"))}}} or the string gameobject-component-get prints for it."
                : $"{displayName} must be an object.");
        }

        private static readonly System.Text.RegularExpressions.Regex PrintedNumber = new(
            @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        // Numbers of a printed value, when it holds exactly count of them (labels such as "RGBA" or "width:" hold none).
        private static double[]? TryReadPrintedNumbers(JToken token, int count)
        {
            if (token.Type != JTokenType.String)
            {
                return null;
            }

            var matches = PrintedNumber.Matches(token.Value<string>() ?? string.Empty);
            if (matches.Count != count)
            {
                return null;
            }

            var numbers = new double[count];
            for (var i = 0; i < count; i++)
            {
                numbers[i] = double.Parse(matches[i].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            return numbers;
        }
    }
}
