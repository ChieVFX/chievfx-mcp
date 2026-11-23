#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Chievfx.Mcp.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using PackageManagerPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Chievfx.Mcp.Extensions.Cameras
{
    internal static partial class ChievfxMcpCamerasExtension
    {
        private static void InvokeCacheInvalidation(Component component, string[] methodNames, List<string> warnings)
        {
            foreach (var methodName in methodNames)
            {
                var method = component.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (method == null)
                {
                    continue;
                }

                try
                {
                    method.Invoke(component, null);
                }
                catch (Exception ex)
                {
                    warnings.Add($"Cache invalidation method {methodName} failed: {ex.Message}");
                }

                return;
            }

            warnings.Add("Requested cache invalidation, but no matching cache invalidation method was found on this confiner type.");
        }

        private static IEnumerable<object> InvokeEnumerable(object target, string methodName)
        {
            var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            var result = method?.Invoke(target, null) as System.Collections.IEnumerable;
            if (result == null)
            {
                yield break;
            }

            foreach (var item in result)
            {
                if (item != null)
                {
                    yield return item;
                }
            }
        }

        private static bool TrySetMember(object target, string memberName, object? value, List<string> warnings)
        {
            var type = target.GetType();
            var property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanWrite)
            {
                try
                {
                    property.SetValue(target, ConvertValue(value, property.PropertyType));
                    return true;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Could not set {memberName}: {ex.Message}");
                    return false;
                }
            }

            var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                try
                {
                    field.SetValue(target, ConvertValue(value, field.FieldType));
                    return true;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Could not set {memberName}: {ex.Message}");
                    return false;
                }
            }

            return false;
        }

        private static object? ReadMember(object target, string memberName)
        {
            var type = target.GetType();
            var property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanRead)
            {
                return property.GetValue(target);
            }

            var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.GetValue(target);
        }

        private static void AddMemberIfPresent(Dictionary<string, object?> result, object target, string memberName, string outputName)
        {
            var value = ReadMember(target, memberName);
            if (value != null)
            {
                result[outputName] = DescribeMemberValue(value);
            }
        }

        private static object? DescribeMemberValue(object? value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is string || value.GetType().IsPrimitive || value is decimal)
            {
                return value;
            }

            if (value.GetType().IsEnum)
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }

            if (value is UnityEngine.Object unityObject)
            {
                return unityObject is Component || unityObject is GameObject || unityObject is Transform
                    ? DescribeObjectReference(unityObject)
                    : DescribeAssetObject(unityObject);
            }

            if (value is System.Collections.IEnumerable enumerable)
            {
                return new Dictionary<string, object?>
                {
                    ["type"] = value.GetType().FullName,
                    ["count"] = enumerable.Cast<object?>().Count(item => item != null),
                };
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static IEnumerable<object> EnumerateObjects(object? value)
        {
            if (value is not System.Collections.IEnumerable enumerable || value is string)
            {
                yield break;
            }

            foreach (var item in enumerable)
            {
                if (item != null)
                {
                    yield return item;
                }
            }
        }

        private static IEnumerable<(string Name, object Value)> EnumerateMemberValues(object target)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var property in target.GetType().GetProperties(flags))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                object? value;
                try
                {
                    value = property.GetValue(target);
                }
                catch
                {
                    continue;
                }

                foreach (var item in ExpandMemberValue(property.Name, value))
                {
                    yield return item;
                }
            }

            foreach (var field in target.GetType().GetFields(flags))
            {
                object? value;
                try
                {
                    value = field.GetValue(target);
                }
                catch
                {
                    continue;
                }

                foreach (var item in ExpandMemberValue(field.Name, value))
                {
                    yield return item;
                }
            }
        }

        private static IEnumerable<(string Name, object Value)> ExpandMemberValue(string name, object? value)
        {
            if (value == null)
            {
                yield break;
            }

            yield return (name, value);
            foreach (var item in EnumerateObjects(value))
            {
                yield return (name, item);
            }
        }

        private static float? ReadFloatMember(object target, string memberName)
        {
            var value = ReadMember(target, memberName);
            return value == null ? null : Convert.ToSingle(value, CultureInfo.InvariantCulture);
        }

        private static object? ConvertValue(object? value, Type targetType)
        {
            if (value == null)
            {
                return null;
            }

            var nonNullableType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (nonNullableType.IsInstanceOfType(value))
            {
                return value;
            }

            if (nonNullableType.IsEnum)
            {
                return Enum.Parse(nonNullableType, Convert.ToString(value, CultureInfo.InvariantCulture)!, ignoreCase: true);
            }

            if (nonNullableType == typeof(float))
            {
                return Convert.ToSingle(value, CultureInfo.InvariantCulture);
            }

            if (nonNullableType == typeof(double))
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }

            if (nonNullableType == typeof(int))
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }

            if (nonNullableType == typeof(bool))
            {
                return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }

            return value;
        }

        private static object? ConvertJToken(JToken token)
        {
            return token.Type switch
            {
                JTokenType.Integer => token.Value<int>(),
                JTokenType.Float => token.Value<float>(),
                JTokenType.Boolean => token.Value<bool>(),
                JTokenType.String => token.Value<string>(),
                _ => token.ToString(),
            };
        }

        private static Scene GetCurrentScene()
        {
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            return prefabStage != null ? prefabStage.scene : SceneManager.GetActiveScene();
        }

        private static Dictionary<string, object?> DescribeCurrentStage()
        {
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            var scene = GetCurrentScene();
            return new Dictionary<string, object?>
            {
                ["kind"] = prefabStage != null ? "prefab-stage" : "scene",
                ["scenePath"] = scene.path,
                ["sceneName"] = scene.name,
                ["prefabAssetPath"] = prefabStage?.assetPath,
            };
        }

        private static string GetTransformPath(Transform transform)
        {
            var parts = new Stack<string>();
            var current = transform;
            while (current != null)
            {
                parts.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", parts);
        }

        private static void MarkChanged(UnityEngine.Object obj)
        {
            EditorUtility.SetDirty(obj);
            if (obj is Component component)
            {
                EditorUtility.SetDirty(component.gameObject);
                if (!EditorApplication.isPlayingOrWillChangePlaymode && component.gameObject.scene.IsValid())
                {
                    EditorSceneManager.MarkSceneDirty(component.gameObject.scene);
                }
            }
        }
    }
}
