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
        private static string ReadGameObjectCreateType(JToken args)
        {
            var type = ReadString(args, "type") ?? "empty";
            if (string.IsNullOrWhiteSpace(type))
            {
                throw new ArgumentException("type cannot be empty.", nameof(type));
            }

            switch (type.Trim().ToLowerInvariant())
            {
                case "empty":
                    return "empty";
                case "cube":
                    return "cube";
                case "sphere":
                    return "sphere";
                case "capsule":
                    return "capsule";
                case "cylinder":
                    return "cylinder";
                case "plane":
                    return "plane";
                case "quad":
                    return "quad";
                default:
                    throw new ArgumentException("type must be one of: empty, cube, sphere, capsule, cylinder, plane, quad.", nameof(type));
            }
        }

        private static GameObject CreateGameObjectByType(string type, string name)
        {
            var gameObject = type switch
            {
                "empty" => new GameObject(name),
                "cube" => GameObject.CreatePrimitive(PrimitiveType.Cube),
                "sphere" => GameObject.CreatePrimitive(PrimitiveType.Sphere),
                "capsule" => GameObject.CreatePrimitive(PrimitiveType.Capsule),
                "cylinder" => GameObject.CreatePrimitive(PrimitiveType.Cylinder),
                "plane" => GameObject.CreatePrimitive(PrimitiveType.Plane),
                "quad" => GameObject.CreatePrimitive(PrimitiveType.Quad),
                _ => throw new ArgumentException("Unsupported GameObject create type.", nameof(type))
            };

            gameObject.name = name;
            return gameObject;
        }

        private static bool TryResolveGameObjectByPath(GameObjectQueryContext context, string path, out GameObject gameObject)
        {
            var exactMatches = EnumerateContextGameObjects(context)
                .Where(candidate => string.Equals(GetHierarchyPath(candidate, context), path, StringComparison.Ordinal))
                .ToArray();
            if (exactMatches.Length > 0)
            {
                gameObject = exactMatches[0];
                return true;
            }

            var unindexedMatches = EnumerateContextGameObjects(context)
                .Where(candidate => string.Equals(RemoveDuplicateIndexes(GetHierarchyPath(candidate, context)), path, StringComparison.Ordinal))
                .ToArray();
            if (unindexedMatches.Length == 1)
            {
                gameObject = unindexedMatches[0];
                return true;
            }

            if (unindexedMatches.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Hierarchy path '{path}' is ambiguous. Use indexed path or instanceId. Candidates: "
                    + FormatGameObjectCandidates(unindexedMatches, context));
            }

            gameObject = null!;
            return false;
        }

        private static string[] SplitHierarchyPath(string path)
        {
            var segments = new List<string>();
            var current = new System.Text.StringBuilder();
            var escaped = false;
            foreach (var ch in path)
            {
                if (escaped)
                {
                    current.Append('\\');
                    current.Append(ch);
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch == '/')
                {
                    segments.Add(current.ToString());
                    current.Clear();
                    continue;
                }

                current.Append(ch);
            }

            if (escaped)
            {
                current.Append('\\');
            }

            segments.Add(current.ToString());
            return segments.Where(segment => segment.Length > 0).ToArray();
        }

        private static string UnescapePathSegment(string segment)
        {
            var output = new System.Text.StringBuilder();
            var escaped = false;
            foreach (var ch in segment)
            {
                if (escaped)
                {
                    output.Append(ch);
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                output.Append(ch);
            }

            if (escaped)
            {
                output.Append('\\');
            }

            return output.ToString();
        }

        private static int ReadComponentIndex(JToken args)
        {
            var index = ReadInt(args, "componentIndex", ReadInt(args, "index", 0));
            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(args), "componentIndex must be zero or greater.");
            }

            return index;
        }

        private static void ValidateTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                throw new ArgumentException("tag cannot be empty.", nameof(tag));
            }

            if (!UnityEditorInternal.InternalEditorUtility.tags.Contains(tag))
            {
                throw new ArgumentException($"Unity tag '{tag}' does not exist.", nameof(tag));
            }
        }

        private static int ReadLayer(JToken token, string parameterName)
        {
            if (token.Type == JTokenType.Integer)
            {
                var layer = token.Value<int>();
                if (layer < 0 || layer > 31)
                {
                    throw new ArgumentOutOfRangeException(parameterName, "layer must be between 0 and 31.");
                }

                return layer;
            }

            if (token.Type == JTokenType.String)
            {
                var text = token.Value<string>() ?? string.Empty;
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLayer))
                {
                    if (parsedLayer < 0 || parsedLayer > 31)
                    {
                        throw new ArgumentOutOfRangeException(parameterName, "layer must be between 0 and 31.");
                    }

                    return parsedLayer;
                }

                var namedLayer = LayerMask.NameToLayer(text);
                if (namedLayer < 0)
                {
                    throw new ArgumentException($"Unity layer '{text}' does not exist.", parameterName);
                }

                return namedLayer;
            }

            throw new ArgumentException("layer must be an integer or layer name string.", parameterName);
        }

        private static void ApplyStaticFlags(GameObject gameObject, JToken token)
        {
            if (token.Type == JTokenType.Boolean)
            {
                gameObject.isStatic = token.Value<bool>();
                return;
            }

            var current = GameObjectUtility.GetStaticEditorFlags(gameObject);
            if (TryReadStaticEditorFlags(token, current, out var flags))
            {
                GameObjectUtility.SetStaticEditorFlags(gameObject, flags);
                return;
            }

            throw new ArgumentException("staticFlags must be an integer, enum string, string array, or object of flag booleans.");
        }

        private static bool TryReadStaticEditorFlags(JToken token, StaticEditorFlags current, out StaticEditorFlags flags)
        {
            flags = current;
            switch (token.Type)
            {
                case JTokenType.Integer:
                    flags = (StaticEditorFlags)token.Value<int>();
                    return true;
                case JTokenType.String:
                    return Enum.TryParse(token.Value<string>(), true, out flags);
                case JTokenType.Array:
                    flags = 0;
                    foreach (var item in token.Children())
                    {
                        if (!TryReadStaticEditorFlags(item, 0, out var parsed))
                        {
                            return false;
                        }

                        flags |= parsed;
                    }

                    return true;
                case JTokenType.Object:
                    foreach (var property in ((JObject)token).Properties())
                    {
                        if (property.Value.Type != JTokenType.Boolean
                            || !Enum.TryParse<StaticEditorFlags>(property.Name, true, out var parsed))
                        {
                            return false;
                        }

                        flags = property.Value.Value<bool>() ? flags | parsed : flags & ~parsed;
                    }

                    return true;
                default:
                    return false;
            }
        }

        private static void ApplyLightBakeFlags(GameObject gameObject, JToken token)
        {
            if (token is not JObject obj)
            {
                throw new ArgumentException("lightBakeFlags must be an object.");
            }

            var rendererProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["scaleInLightmap"] = "m_ScaleInLightmap",
                ["receiveGI"] = "m_ReceiveGI",
                ["importantGI"] = "m_ImportantGI",
                ["stitchLightmapSeams"] = "m_StitchLightmapSeams",
            };
            var supportedKeys = new HashSet<string>(rendererProperties.Keys, StringComparer.OrdinalIgnoreCase)
            {
                "contributeGI"
            };
            var unknownKeys = obj.Properties()
                .Select(property => property.Name)
                .Where(key => !supportedKeys.Contains(key))
                .ToArray();
            if (unknownKeys.Length > 0)
            {
                throw new ArgumentException($"Unsupported lightBakeFlags keys: {string.Join(", ", unknownKeys)}.");
            }

            var contributeGiToken = GetObjectPropertyValue(obj, "contributeGI");
            if (contributeGiToken != null)
            {
                if (contributeGiToken.Type != JTokenType.Boolean)
                {
                    throw new ArgumentException("lightBakeFlags.contributeGI must be a boolean.");
                }

                var contributeGi = contributeGiToken.Value<bool>();
                var flags = GameObjectUtility.GetStaticEditorFlags(gameObject);
                if (Enum.TryParse<StaticEditorFlags>("ContributeGI", true, out var contributeFlag))
                {
                    flags = contributeGi ? flags | contributeFlag : flags & ~contributeFlag;
                    GameObjectUtility.SetStaticEditorFlags(gameObject, flags);
                }
                else
                {
                    gameObject.isStatic = contributeGi;
                }
            }

            var requestedRendererProperties = rendererProperties.Keys
                .Where(key => GetObjectPropertyValue(obj, key) != null)
                .ToArray();
            if (requestedRendererProperties.Length == 0)
            {
                return;
            }

            var renderers = gameObject.GetComponents<Renderer>();
            if (renderers.Length == 0)
            {
                throw new InvalidOperationException("lightBakeFlags renderer fields require at least one Renderer component.");
            }

            foreach (var renderer in renderers)
            {
                Undo.RecordObject(renderer, "ChievFX MCP Update Light Bake Flags");
                var serializedObject = new SerializedObject(renderer);
                serializedObject.Update();
                foreach (var key in requestedRendererProperties)
                {
                    var property = serializedObject.FindProperty(rendererProperties[key]);
                    if (property == null)
                    {
                        throw new InvalidOperationException($"Renderer property '{rendererProperties[key]}' is unavailable on {renderer.GetType().Name}.");
                    }

                    SetSerializedPropertyValue(property, GetObjectPropertyValue(obj, key)!, key);
                }

                serializedObject.ApplyModifiedProperties();
                MarkComponentMutationDirty(renderer);
            }
        }

        private static JObject ReadComponentPropertyPatch(JToken args)
        {
            var token = ReadProperty(args, "properties")
                ?? ReadProperty(args, "json")
                ?? ReadProperty(args, "serializedFields");
            if (token == null || token.Type == JTokenType.Null)
            {
                return new JObject();
            }

            if (token is JObject obj)
            {
                return obj;
            }

            if (token.Type == JTokenType.String)
            {
                try
                {
                    return JObject.Parse(token.Value<string>() ?? "{}");
                }
                catch (JsonException ex)
                {
                    throw new ArgumentException($"json must parse to an object. {ex.Message}", nameof(args));
                }
            }

            throw new ArgumentException("properties/json must be a JSON object.");
        }

        private static Type ResolveComponentType(string componentType)
        {
            var matches = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(GetLoadableTypes)
                .Where(type => typeof(Component).IsAssignableFrom(type)
                    && !type.IsAbstract
                    && (string.Equals(type.Name, componentType, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(type.FullName, componentType, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(type.AssemblyQualifiedName, componentType, StringComparison.OrdinalIgnoreCase)))
                .Distinct()
                .ToArray();
            if (matches.Length == 0)
            {
                throw new InvalidOperationException($"No loaded Component type matches '{componentType}'.");
            }

            var exactFullName = matches
                .Where(type => string.Equals(type.FullName, componentType, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(type.AssemblyQualifiedName, componentType, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (exactFullName.Length == 1)
            {
                return exactFullName[0];
            }

            if (matches.Length == 1)
            {
                return matches[0];
            }

            throw new InvalidOperationException(
                $"Component type '{componentType}' is ambiguous. Use full type name. Matches: "
                + string.Join(", ", matches.Take(8).Select(type => type.FullName ?? type.Name)));
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(type => type != null)!;
            }
        }

        private static string[] ApplyComponentPropertyPatch(Component component, JObject properties, bool writeNonSerialized)
        {
            Undo.RecordObject(component, "ChievFX MCP Update Component");
            var updated = new List<string>();
            var serializedObject = new SerializedObject(component);
            serializedObject.Update();
            foreach (var property in properties.Properties())
            {
                if (string.Equals(property.Name, "m_Script", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("m_Script cannot be updated.");
                }

                if (TrySetRectTransformProperty(component, property.Name, property.Value))
                {
                    updated.Add(property.Name);
                    continue;
                }

                if (TrySetCameraProperty(component, property.Name, property.Value))
                {
                    updated.Add(property.Name);
                    continue;
                }

                var serializedProperty = serializedObject.FindProperty(property.Name);
                if (serializedProperty != null)
                {
                    SetSerializedPropertyValue(serializedProperty, property.Value, property.Name);
                    updated.Add(property.Name);
                    continue;
                }

                if (!writeNonSerialized)
                {
                    throw new InvalidOperationException(
                        $"Component '{component.GetType().Name}' has no serialized property '{property.Name}'. "
                        + "Set writeNonSerialized=true to write public or non-public fields/properties by reflection.");
                }

                SetReflectedMemberValue(component, property.Name, property.Value);
                updated.Add(property.Name);
            }

            serializedObject.ApplyModifiedProperties();
            MarkComponentMutationDirty(component);
            return updated.ToArray();
        }

        private static bool TrySetRectTransformProperty(Component component, string propertyName, JToken value)
        {
            if (component is not RectTransform rectTransform)
            {
                return false;
            }

            switch (NormalizePropertyToken(propertyName))
            {
                case "manchormin":
                case "anchormin":
                    rectTransform.anchorMin = ReadVector2Token(value, propertyName);
                    return true;
                case "manchormax":
                case "anchormax":
                    rectTransform.anchorMax = ReadVector2Token(value, propertyName);
                    return true;
                case "manchoredposition":
                case "anchoredposition":
                    rectTransform.anchoredPosition = ReadVector2Token(value, propertyName);
                    return true;
                case "msizedelta":
                case "sizedelta":
                    rectTransform.sizeDelta = ReadVector2Token(value, propertyName);
                    return true;
                case "mpivot":
                case "pivot":
                    rectTransform.pivot = ReadVector2Token(value, propertyName);
                    return true;
                case "moffsetmin":
                case "offsetmin":
                    rectTransform.offsetMin = ReadVector2Token(value, propertyName);
                    return true;
                case "moffsetmax":
                case "offsetmax":
                    rectTransform.offsetMax = ReadVector2Token(value, propertyName);
                    return true;
                case "manchoredposition3d":
                case "anchoredposition3d":
                    rectTransform.anchoredPosition3D = ReadVector3Token(value, propertyName);
                    return true;
                default:
                    return false;
            }
        }

        private static bool TrySetCameraProperty(Component component, string propertyName, JToken value)
        {
            if (component is not Camera camera)
            {
                return false;
            }

            switch (NormalizePropertyToken(propertyName))
            {
                case "morthographic":
                case "orthographic":
                    camera.orthographic = ReadBoolToken(value, propertyName);
                    return true;
                case "morthographicsize":
                case "orthographicsize":
                    camera.orthographicSize = (float)ReadDoubleToken(value, propertyName);
                    return true;
                case "mfieldofview":
                case "fieldofview":
                case "fov":
                    camera.fieldOfView = (float)ReadDoubleToken(value, propertyName);
                    return true;
                case "mnearclipplane":
                case "nearclipplane":
                case "near":
                    camera.nearClipPlane = (float)ReadDoubleToken(value, propertyName);
                    return true;
                case "mfarclipplane":
                case "farclipplane":
                case "far":
                    camera.farClipPlane = (float)ReadDoubleToken(value, propertyName);
                    return true;
                case "mbackgroundcolor":
                case "backgroundcolor":
                    camera.backgroundColor = ReadColorToken(value, propertyName);
                    return true;
                case "mclearflags":
                case "clearflags":
                    SetCameraClearFlags(camera, value, propertyName);
                    return true;
                case "mdepth":
                case "depth":
                    camera.depth = (float)ReadDoubleToken(value, propertyName);
                    return true;
                default:
                    return false;
            }
        }

        private static void SetCameraClearFlags(Camera camera, JToken value, string displayName)
        {
            if (value.Type == JTokenType.String)
            {
                var token = NormalizePropertyToken(value.Value<string>() ?? string.Empty);
                foreach (CameraClearFlags flag in Enum.GetValues(typeof(CameraClearFlags)))
                {
                    if (NormalizePropertyToken(flag.ToString()) == token)
                    {
                        camera.clearFlags = flag;
                        return;
                    }
                }
            }
            else
            {
                camera.clearFlags = (CameraClearFlags)ReadIntToken(value, displayName);
                return;
            }

            throw new ArgumentException($"Unsupported CameraClearFlags value '{value}'.");
        }

        private static string NormalizePropertyToken(string value)
        {
            return value.Replace("-", string.Empty, StringComparison.Ordinal)
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant();
        }

        private static Dictionary<string, object?> CreateComponentMutationSummary(
            Component component,
            string[] updatedProperties,
            bool includeSerializedDefaults)
        {
            var summary = CreateComponentSummary(component);
            var truncated = false;
            var updatedFields = SerializeComponentProperties(component, updatedProperties, ref truncated);
            if (updatedFields.Length > 0)
            {
                summary["updatedFields"] = updatedFields;
            }

            var serializedNames = updatedFields
                .Select(field => field.name)
                .ToHashSet(StringComparer.Ordinal);
            var reflectedProperties = updatedProperties
                .Where(property => !serializedNames.Contains(property))
                .ToArray();
            if (reflectedProperties.Length > 0)
            {
                summary["updatedProperties"] = reflectedProperties;
            }

            if (includeSerializedDefaults)
            {
                summary["serializedDefaults"] = SerializeComponentFields(component, isDebug: false, ref truncated);
            }

            if (truncated)
            {
                summary["serializedDataTruncated"] = true;
            }

            return summary;
        }

        private static SerializedMemberDto[] SerializeComponentProperties(Component component, string[] propertyPaths, ref bool truncated)
        {
            if (propertyPaths.Length == 0)
            {
                return Array.Empty<SerializedMemberDto>();
            }

            var serializedObject = new SerializedObject(component);
            serializedObject.Update();
            var fields = new List<SerializedMemberDto>();
            foreach (var propertyPath in propertyPaths.Distinct(StringComparer.Ordinal))
            {
                var property = serializedObject.FindProperty(propertyPath);
                if (property == null)
                {
                    continue;
                }

                fields.Add(new SerializedMemberDto
                {
                    typeName = property.propertyType.ToString(),
                    name = property.propertyPath,
                    value = ReadSerializedPropertyValue(property, ref truncated)
                });
            }

            return fields.ToArray();
        }

        private static void SetSerializedPropertyValue(SerializedProperty property, JToken value, string displayName)
        {
            if (property.isArray && property.propertyType != SerializedPropertyType.String && value is JArray array)
            {
                property.arraySize = array.Count;
                for (var i = 0; i < array.Count; i++)
                {
                    SetSerializedPropertyValue(property.GetArrayElementAtIndex(i), array[i], $"{displayName}[{i}]");
                }

                return;
            }

            if (property.propertyType == SerializedPropertyType.Generic && value is JObject obj)
            {
                foreach (var child in obj.Properties())
                {
                    var childProperty = property.FindPropertyRelative(child.Name);
                    if (childProperty == null)
                    {
                        throw new InvalidOperationException($"Serialized property '{displayName}' has no child '{child.Name}'.");
                    }

                    SetSerializedPropertyValue(childProperty, child.Value, $"{displayName}.{child.Name}");
                }

                return;
            }

            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask:
                case SerializedPropertyType.ArraySize:
                case SerializedPropertyType.Character:
                    property.intValue = ReadIntToken(value, displayName);
                    return;
                case SerializedPropertyType.Boolean:
                    property.boolValue = ReadBoolToken(value, displayName);
                    return;
                case SerializedPropertyType.Float:
                    property.doubleValue = ReadDoubleToken(value, displayName);
                    return;
                case SerializedPropertyType.String:
                    property.stringValue = value.Type == JTokenType.String ? value.Value<string>() : value.ToString(Formatting.None);
                    return;
                case SerializedPropertyType.Color:
                    property.colorValue = ReadColorToken(value, displayName);
                    return;
                case SerializedPropertyType.ObjectReference:
                    property.objectReferenceValue = ReadObjectReferenceToken(value, displayName);
                    return;
                case SerializedPropertyType.Enum:
                    SetEnumPropertyValue(property, value, displayName);
                    return;
                case SerializedPropertyType.Vector2:
                    property.vector2Value = ReadVector2Token(value, displayName);
                    return;
                case SerializedPropertyType.Vector3:
                    property.vector3Value = ReadVector3Token(value, displayName);
                    return;
                case SerializedPropertyType.Vector4:
                    property.vector4Value = ReadVector4Token(value, displayName);
                    return;
                case SerializedPropertyType.Rect:
                    property.rectValue = ReadRectToken(value, displayName);
                    return;
                case SerializedPropertyType.Bounds:
                    property.boundsValue = ReadBoundsToken(value, displayName);
                    return;
                case SerializedPropertyType.Quaternion:
                    property.quaternionValue = ReadQuaternionToken(value, displayName);
                    return;
                case SerializedPropertyType.Vector2Int:
                    property.vector2IntValue = ReadVector2IntToken(value, displayName);
                    return;
                case SerializedPropertyType.Vector3Int:
                    property.vector3IntValue = ReadVector3IntToken(value, displayName);
                    return;
                case SerializedPropertyType.RectInt:
                    property.rectIntValue = ReadRectIntToken(value, displayName);
                    return;
                case SerializedPropertyType.BoundsInt:
                    property.boundsIntValue = ReadBoundsIntToken(value, displayName);
                    return;
                default:
                    throw new NotSupportedException($"Serialized property '{displayName}' type {property.propertyType} is not supported for writes.");
            }
        }

        private static void SetEnumPropertyValue(SerializedProperty property, JToken value, string displayName)
        {
            if (value.Type == JTokenType.Integer)
            {
                property.intValue = value.Value<int>();
                return;
            }

            if (value.Type != JTokenType.String)
            {
                throw new ArgumentException($"{displayName} must be an enum name or integer.");
            }

            var text = value.Value<string>() ?? string.Empty;
            var index = Array.FindIndex(property.enumNames, item => string.Equals(item, text, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                index = Array.FindIndex(property.enumDisplayNames, item => string.Equals(item, text, StringComparison.OrdinalIgnoreCase));
            }

            if (index < 0)
            {
                throw new ArgumentException($"{displayName} enum value '{text}' is invalid.");
            }

            property.enumValueIndex = index;
        }

        private static Object? ReadObjectReferenceToken(JToken value, string displayName)
        {
            if (value.Type == JTokenType.Null)
            {
                return null;
            }

            var instanceId = value.Type == JTokenType.Integer
                ? value.Value<int>()
                : value is JObject obj && GetObjectPropertyValue(obj, "instanceId") is JToken idToken
                    ? ReadIntToken(idToken, $"{displayName}.instanceId")
                    : throw new ArgumentException($"{displayName} object reference must be null, an instanceId integer, or an object with instanceId.");
            return UnityObjectIdentity.LegacyInstanceIdToObject(instanceId)
                ?? throw new InvalidOperationException($"{displayName} instanceId {instanceId} does not resolve to a Unity object.");
        }

        private static void SetReflectedMemberValue(Component component, string memberName, JToken value)
        {
            var type = component.GetType();
            var bindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var fields = type.GetFields(bindingFlags)
                .Where(field => string.Equals(field.Name, memberName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (fields.Length == 1)
            {
                if (fields[0].IsInitOnly)
                {
                    throw new InvalidOperationException($"Field '{memberName}' is read-only.");
                }

                fields[0].SetValue(component, ConvertJsonToType(value, fields[0].FieldType, memberName));
                return;
            }

            var properties = type.GetProperties(bindingFlags)
                .Where(property => string.Equals(property.Name, memberName, StringComparison.OrdinalIgnoreCase) && property.GetIndexParameters().Length == 0)
                .ToArray();
            if (properties.Length == 1)
            {
                var setter = properties[0].GetSetMethod(true);
                if (setter == null)
                {
                    throw new InvalidOperationException($"Property '{memberName}' is read-only.");
                }

                setter.Invoke(component, new[] { ConvertJsonToType(value, properties[0].PropertyType, memberName) });
                return;
            }

            if (fields.Length + properties.Length > 1)
            {
                throw new InvalidOperationException($"Member '{memberName}' is ambiguous on component '{type.Name}'.");
            }

            throw new InvalidOperationException($"Component '{type.Name}' has no field or property '{memberName}'.");
        }

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
            var obj = RequireObject(token, displayName);
            return new Vector2(ReadFloatComponent(obj, "x", displayName), ReadFloatComponent(obj, "y", displayName));
        }

        private static Vector3 ReadVector3Token(JToken token, string displayName)
        {
            var obj = RequireObject(token, displayName);
            return new Vector3(ReadFloatComponent(obj, "x", displayName), ReadFloatComponent(obj, "y", displayName), ReadFloatComponent(obj, "z", displayName));
        }

        private static Vector4 ReadVector4Token(JToken token, string displayName)
        {
            var obj = RequireObject(token, displayName);
            return new Vector4(
                ReadFloatComponent(obj, "x", displayName),
                ReadFloatComponent(obj, "y", displayName),
                ReadFloatComponent(obj, "z", displayName),
                ReadFloatComponent(obj, "w", displayName));
        }

        private static Vector2Int ReadVector2IntToken(JToken token, string displayName)
        {
            var obj = RequireObject(token, displayName);
            return new Vector2Int(ReadIntComponent(obj, "x", displayName), ReadIntComponent(obj, "y", displayName));
        }

        private static Vector3Int ReadVector3IntToken(JToken token, string displayName)
        {
            var obj = RequireObject(token, displayName);
            return new Vector3Int(ReadIntComponent(obj, "x", displayName), ReadIntComponent(obj, "y", displayName), ReadIntComponent(obj, "z", displayName));
        }

        private static Color ReadColorToken(JToken token, string displayName)
        {
            if (token.Type == JTokenType.String && ColorUtility.TryParseHtmlString(token.Value<string>(), out var color))
            {
                return color;
            }

            var obj = RequireObject(token, displayName);
            return new Color(
                ReadFloatComponent(obj, "r", displayName),
                ReadFloatComponent(obj, "g", displayName),
                ReadFloatComponent(obj, "b", displayName),
                GetObjectPropertyValue(obj, "a") != null ? ReadFloatComponent(obj, "a", displayName) : 1f);
        }

        private static Rect ReadRectToken(JToken token, string displayName)
        {
            var obj = RequireObject(token, displayName);
            return new Rect(
                ReadFloatComponent(obj, "x", displayName),
                ReadFloatComponent(obj, "y", displayName),
                ReadFloatComponent(obj, "width", displayName),
                ReadFloatComponent(obj, "height", displayName));
        }

        private static RectInt ReadRectIntToken(JToken token, string displayName)
        {
            var obj = RequireObject(token, displayName);
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
            var obj = RequireObject(token, displayName);
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

        private static JObject RequireObject(JToken token, string displayName)
        {
            return token as JObject ?? throw new ArgumentException($"{displayName} must be an object.");
        }

        private static JToken? GetObjectPropertyValue(JObject obj, string name)
        {
            return obj.Properties()
                .FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                ?.Value;
        }

        private static void MarkComponentMutationDirty(Component component)
        {
            EditorUtility.SetDirty(component);
            if (PrefabUtility.IsPartOfPrefabInstance(component))
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(component);
            }
        }
    }
}
