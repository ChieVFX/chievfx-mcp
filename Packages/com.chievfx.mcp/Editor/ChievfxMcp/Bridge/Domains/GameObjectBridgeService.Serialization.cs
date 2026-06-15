#nullable enable
using static Chievfx.Mcp.Editor.McpLimits;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;
using PackageManagerClient = UnityEditor.PackageManager.Client;
using PackageManagerPackageInfo = UnityEditor.PackageManager.PackageInfo;


namespace Chievfx.Mcp.Editor
{
    internal sealed partial class GameObjectBridgeService
    {
        private static Dictionary<string, object?> CreateComponentSummary(Component? component)
        {
            if (component == null)
            {
                return new Dictionary<string, object?>
                {
                    ["type"] = "MissingScript"
                };
            }

            var type = component.GetType();
            return new Dictionary<string, object?>
            {
                ["type"] = type.Name,
                ["fullType"] = type.FullName ?? type.Name,
                ["instanceId"] = GetLegacyInstanceId(component),
                ["enabled"] = TryGetEnabledState(component)
            };
        }

        private static Dictionary<string, object?> CreateComponentDetail(Component component, bool includeSerializedData, bool isDebug, ref bool serializedTruncated)
        {
            var detail = CreateComponentSummary(component);
            if (includeSerializedData)
            {
                detail["serializedFieldsMode"] = isDebug ? "debug" : "inspector";
                detail["serializedFields"] = SerializeComponentFields(component, isDebug, ref serializedTruncated);
            }

            return detail;
        }

        private static string[] GetComponentTypePreview(GameObject gameObject, out bool truncated)
        {
            var componentTypes = gameObject.GetComponents<Component>()
                .Select(component => component == null ? "MissingScript" : component.GetType().Name)
                .ToArray();
            truncated = componentTypes.Length > MaxComponentPreviewTypes;
            return componentTypes.Take(MaxComponentPreviewTypes).ToArray();
        }

        internal static bool? TryGetEnabledState(Component component)
        {
            var property = component.GetType().GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public);
            if (property == null || property.PropertyType != typeof(bool))
            {
                return null;
            }

            return property.GetValue(component) is bool enabled ? enabled : null;
        }

        internal static SerializedMemberDto[] SerializeComponentFields(Component component, bool isDebug, ref bool truncated)
        {
            var fields = new List<SerializedMemberDto>();
            var serializedObject = new SerializedObject(component);
            var iterator = serializedObject.GetIterator();
            var enterChildren = true;
            while (isDebug ? iterator.Next(enterChildren) : iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (fields.Count >= MaxSerializedFields)
                {
                    truncated = true;
                    break;
                }

                fields.Add(new SerializedMemberDto
                {
                    typeName = iterator.propertyType.ToString(),
                    name = iterator.propertyPath,
                    value = ReadSerializedPropertyValue(iterator, ref truncated)
                });
            }

            return fields.ToArray();
        }

        private static object? ReadSerializedPropertyValue(SerializedProperty property, ref bool truncated)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return property.longValue;
                case SerializedPropertyType.Boolean:
                    return property.boolValue;
                case SerializedPropertyType.Float:
                    return property.doubleValue;
                case SerializedPropertyType.String:
                    return TrimText(property.stringValue ?? string.Empty, MaxSerializedStringChars, ref truncated);
                case SerializedPropertyType.Color:
                    return property.colorValue.ToString();
                case SerializedPropertyType.ObjectReference:
                    return SerializeUnityObjectReference(property.objectReferenceValue);
                case SerializedPropertyType.LayerMask:
                    return property.intValue;
                case SerializedPropertyType.Enum:
                    return property.enumDisplayNames.ElementAtOrDefault(property.enumValueIndex) ?? property.enumValueIndex.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Vector2:
                    return property.vector2Value.ToString();
                case SerializedPropertyType.Vector3:
                    return property.vector3Value.ToString();
                case SerializedPropertyType.Vector4:
                    return property.vector4Value.ToString();
                case SerializedPropertyType.Rect:
                    return property.rectValue.ToString();
                case SerializedPropertyType.ArraySize:
                    return property.intValue;
                case SerializedPropertyType.Character:
                    return property.intValue;
                case SerializedPropertyType.AnimationCurve:
                    return $"AnimationCurve(keys:{property.animationCurveValue?.length ?? 0})";
                case SerializedPropertyType.Bounds:
                    return property.boundsValue.ToString();
                case SerializedPropertyType.Quaternion:
                    return property.quaternionValue.eulerAngles.ToString();
                case SerializedPropertyType.Vector2Int:
                    return property.vector2IntValue.ToString();
                case SerializedPropertyType.Vector3Int:
                    return property.vector3IntValue.ToString();
                case SerializedPropertyType.RectInt:
                    return property.rectIntValue.ToString();
                case SerializedPropertyType.BoundsInt:
                    return property.boundsIntValue.ToString();
                case SerializedPropertyType.ExposedReference:
                    return SerializeUnityObjectReference(property.exposedReferenceValue);
                default:
                    if (property.isArray)
                    {
                        return new { arraySize = property.arraySize };
                    }

                    return property.hasVisibleChildren ? "<object>" : null;
            }
        }

        private static object? SerializeUnityObjectReference(Object? unityObject)
        {
            if (unityObject == null)
            {
                return null;
            }

            var type = unityObject.GetType();
            return new
            {
                instanceId = GetLegacyInstanceId(unityObject),
                name = unityObject.name,
                type = type.Name
            };
        }

        private static IEnumerable<Component> GetMatchingComponents(GameObject gameObject, string componentType)
        {
            return gameObject.GetComponents<Component>()
                .Where(component => component != null && ComponentTypeMatches(component.GetType(), componentType))!;
        }

        private static bool HasMatchingComponent(GameObject gameObject, string componentType)
        {
            return GetMatchingComponents(gameObject, componentType).Any();
        }

        internal static bool ComponentTypeMatches(Type type, string componentType)
        {
            return string.Equals(type.Name, componentType, StringComparison.OrdinalIgnoreCase)
                || string.Equals(type.FullName, componentType, StringComparison.OrdinalIgnoreCase);
        }

        internal static void ValidateComponentTypeText(string? componentType, bool required)
        {
            if (string.IsNullOrWhiteSpace(componentType))
            {
                if (required)
                {
                    throw new ArgumentException("componentType is required.", nameof(componentType));
                }

                return;
            }

            if (componentType!.Length > 256)
            {
                throw new ArgumentException("componentType must be 256 characters or fewer.", nameof(componentType));
            }
        }

        internal static void ValidateWildcardPattern(string? pattern, string parameterName)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return;
            }

            if (pattern!.Length > 256)
            {
                throw new ArgumentException($"{parameterName} must be 256 characters or fewer.", parameterName);
            }
        }

        internal static void ValidateResourceFilterText(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"{parameterName} cannot be empty.", parameterName);
            }

            if (value.Length > MaxResourceFilterValueChars)
            {
                throw new ArgumentException($"{parameterName} must be {MaxResourceFilterValueChars} characters or fewer.", parameterName);
            }
        }

        internal static bool WildcardMatches(string value, string pattern)
        {
            return WildcardMatches(value, pattern, ignoreCase: true);
        }

        internal static bool WildcardMatches(string value, string pattern, bool ignoreCase)
        {
            var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            var options = RegexOptions.CultureInvariant;
            if (ignoreCase)
            {
                options |= RegexOptions.IgnoreCase;
            }

            return Regex.IsMatch(value, regex, options);
        }

        internal static string GetHierarchyPath(GameObject gameObject, GameObjectQueryContext context)
        {
            var segments = new Stack<string>();
            var current = gameObject.transform;
            while (current != null)
            {
                segments.Push(GetPathSegment(current, context));
                current = current.parent;
            }

            return string.Join("/", segments);
        }

        private static string GetPathSegment(Transform transform, GameObjectQueryContext context)
        {
            var name = EscapePathSegment(transform.gameObject.name);
            var siblings = GetSiblingTransforms(transform, context).ToArray();
            var sameNameSiblings = siblings
                .Where(sibling => string.Equals(sibling.gameObject.name, transform.gameObject.name, StringComparison.Ordinal))
                .ToArray();
            if (sameNameSiblings.Length <= 1)
            {
                return name;
            }

            var duplicateIndex = Array.IndexOf(sameNameSiblings, transform) + 1;
            return $"{name}[{duplicateIndex}]";
        }

        private static IEnumerable<Transform> GetSiblingTransforms(Transform transform, GameObjectQueryContext context)
        {
            if (transform.parent != null)
            {
                foreach (Transform sibling in transform.parent)
                {
                    yield return sibling;
                }

                yield break;
            }

            // Root-level duplicate indexing is scoped to siblings in the same scene so that a
            // GameObject's path stays stable regardless of how many other scenes are loaded.
            var scene = transform.gameObject.scene;
            foreach (var root in context.Roots)
            {
                if (root.scene == scene)
                {
                    yield return root.transform;
                }
            }
        }

        private static string EscapePathSegment(string name)
        {
            return name.Replace("\\", "\\\\").Replace("/", "\\/");
        }

        internal static string NormalizeHierarchyPath(string path)
        {
            return path.Trim().Trim('/');
        }

        internal static string RemoveDuplicateIndexes(string path)
        {
            return Regex.Replace(path, @"(^|/)([^/]+)\[\d+\](?=/|$)", "$1$2");
        }

        private static string FormatGameObjectCandidates(IEnumerable<GameObject> gameObjects, GameObjectQueryContext context)
        {
            var multiScene = context.Scenes.Length > 1;
            var candidates = gameObjects
                .Take(8)
                .Select(gameObject => multiScene
                    ? $"{GetHierarchyPath(gameObject, context)} (instanceId:{GetLegacyInstanceId(gameObject)}, scene:{gameObject.scene.name})"
                    : $"{GetHierarchyPath(gameObject, context)} (instanceId:{GetLegacyInstanceId(gameObject)})")
                .ToArray();
            return string.Join(", ", candidates);
        }

    }
}
