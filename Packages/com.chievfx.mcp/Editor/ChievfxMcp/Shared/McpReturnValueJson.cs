#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;

namespace Chievfx.Mcp.Editor
{
    // A script's or method's return value as JSON. Unity math structs go out as their fields ({"x":1,"y":2,"z":3}): their
    // computed properties (normalized, eulerAngles, linear) refer to the same type, and the default serializer failed on a
    // returned Vector3 with "self referencing loop" after the script had already run. Unity objects nested in a value
    // (an anonymous object with a GameObject) go out as a reference, not as every property of the engine object.
    // A Unity struct with no public fields (Ray, RaycastHit) goes out as its readable properties: its ToString rounds to
    // 0.01 or is only the type name, so a RaycastHit[] came back as ["UnityEngine.RaycastHit", ...].
    internal static class McpReturnValueJson
    {
        public static readonly JsonSerializerSettings Settings = new()
        {
            Formatting = Formatting.None,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            Converters = { new UnityValueConverter() },
        };

        private sealed class UnityValueConverter : JsonConverter
        {
            // Struct properties can hold other Unity structs; past this nesting they go out as text.
            private const int MaxPropertyDepth = 3;
            private static readonly Dictionary<Type, PropertyInfo[]> PropertiesByType = new();
            [ThreadStatic] private static int _propertyDepth;

            public override bool CanRead => false;

            public override bool CanConvert(Type objectType)
            {
                return typeof(UnityEngine.Object).IsAssignableFrom(objectType)
                       || (objectType.IsValueType && objectType.IsPrimitive == false && objectType.IsEnum == false
                           && objectType.Namespace != null && objectType.Namespace.StartsWith("UnityEngine", StringComparison.Ordinal));
            }

            public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
            {
                switch (value)
                {
                    case null:
                        writer.WriteNull();
                        return;
                    case UnityEngine.Object unityObject:
                        if (unityObject == null)
                        {
                            writer.WriteNull();
                            return;
                        }

                        writer.WriteStartObject();
                        writer.WritePropertyName("instanceId");
                        writer.WriteValue(UnityObjectIdentity.GetLegacyInstanceId(unityObject));
                        writer.WritePropertyName("name");
                        writer.WriteValue(unityObject.name);
                        writer.WritePropertyName("type");
                        writer.WriteValue(unityObject.GetType().Name);
                        writer.WriteEndObject();
                        return;
                    case UnityEngine.Bounds bounds:
                        WritePair(writer, serializer, "center", bounds.center, "size", bounds.size);
                        return;
                    case UnityEngine.Rect rect:
                        WritePair(writer, serializer, "position", rect.position, "size", rect.size);
                        return;
                }

                var fields = value.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
                if (fields.Length == 0)
                {
                    WriteProperties(writer, value, serializer);
                    return;
                }

                writer.WriteStartObject();
                foreach (var field in fields)
                {
                    writer.WritePropertyName(field.Name);
                    serializer.Serialize(writer, field.GetValue(value));
                }

                writer.WriteEndObject();
            }

            public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
            {
                throw new NotSupportedException();
            }

            private static void WriteProperties(JsonWriter writer, object value, JsonSerializer serializer)
            {
                var properties = GetReadableProperties(value.GetType());
                if (properties.Length == 0 || _propertyDepth >= MaxPropertyDepth)
                {
                    writer.WriteValue(value.ToString());
                    return;
                }

                writer.WriteStartObject();
                _propertyDepth++;
                try
                {
                    foreach (var property in properties)
                    {
                        object? propertyValue;
                        try
                        {
                            propertyValue = property.GetValue(value);
                        }
                        catch (Exception)
                        {
                            continue;
                        }

                        writer.WritePropertyName(property.Name);
                        serializer.Serialize(writer, propertyValue);
                    }
                }
                finally
                {
                    _propertyDepth--;
                }

                writer.WriteEndObject();
            }

            // Public instance getters without indexers and not obsolete; one of the struct's own type (Vector3.normalized-like)
            // would recurse.
            private static PropertyInfo[] GetReadableProperties(Type type)
            {
                lock (PropertiesByType)
                {
                    if (PropertiesByType.TryGetValue(type, out var known))
                    {
                        return known;
                    }

                    var readable = new List<PropertyInfo>();
                    foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (property.CanRead && property.GetIndexParameters().Length == 0 && property.PropertyType != type
                            && property.IsDefined(typeof(ObsoleteAttribute), true) == false)
                        {
                            readable.Add(property);
                        }
                    }

                    var properties = readable.ToArray();
                    PropertiesByType[type] = properties;
                    return properties;
                }
            }

            private static void WritePair(JsonWriter writer, JsonSerializer serializer, string firstName, object first, string secondName, object second)
            {
                writer.WriteStartObject();
                writer.WritePropertyName(firstName);
                serializer.Serialize(writer, first);
                writer.WritePropertyName(secondName);
                serializer.Serialize(writer, second);
                writer.WriteEndObject();
            }
        }
    }
}
