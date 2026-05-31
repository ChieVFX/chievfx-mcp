#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Chievfx.Mcp.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using static Chievfx.Mcp.Extensions.UiToolkit.ChievfxMcpUiToolkitExtension;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitRuntimeTools;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitResources;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitInteractions;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitPanelQueries;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitRows;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitSchemas;

namespace Chievfx.Mcp.Extensions.UiToolkit
{
    internal static class UiToolkitShared
    {
        internal static Dictionary<string, object?> CreateEnvelope(string uri, UiToolkitDependencyStatus status)
        {
            return new Dictionary<string, object?>();
        }

        internal static Dictionary<string, object?> ReadStatusResource(string uri, UiToolkitDependencyStatus status)
        {
            var result = new Dictionary<string, object?>
            {
                ["framework"] = "uitoolkit",
                ["context"] = ChievfxMcpUiStatusHelpers.DescribeEditorContext(),
            };
            if (!status.Available)
            {
                result["reason"] = status.Reason;
                return result;
            }

            result["uitoolkit"] = ChievfxMcpUiStatusHelpers.DescribePackageCapability(
                status.PackageName,
                status.PackageVersion,
                status.PackageSource,
                true);
            result["currentHierarchy"] = ChievfxMcpUiStatusHelpers.DescribeUiToolkitHierarchy(status.UIDocumentType);
            result["runtimeOnly"] = true;
            return result;
        }

        internal static Dictionary<string, object?> CreateUnavailable(string uri, UiToolkitDependencyStatus status, string? reason = null)
        {
            return new Dictionary<string, object?>
            {
                ["reason"] = reason ?? status.Reason,
                ["warnings"] = new[] { reason ?? status.Reason },
            };
        }

        internal static UiToolkitDependencyStatus GetDependencyStatus()
        {
            var uiDocumentType = FindType("UnityEngine.UIElements.UIDocument");
            var panelSettingsType = FindType("UnityEngine.UIElements.PanelSettings");
            var iPanelType = FindType("UnityEngine.UIElements.IPanel");
            var runtimePanelUtilsType = FindType("UnityEngine.UIElements.RuntimePanelUtils");
            var visualElementType = FindType("UnityEngine.UIElements.VisualElement");
            var packageInfo = TryFindPackageInfo("Packages/com.unity.modules.uielements/package.json");
            var modulePresent = UiToolkitVersionDefineActive || packageInfo != null || uiDocumentType != null || visualElementType != null;
            var available = modulePresent
                && uiDocumentType != null
                && panelSettingsType != null
                && iPanelType != null
                && runtimePanelUtilsType != null
                && visualElementType != null;
            var reason = available
                ? "com.unity.modules.uielements is available and UI Toolkit runtime panel types are loaded."
                : "UI Toolkit runtime inspection unavailable: com.unity.modules.uielements or required runtime types (UIDocument, PanelSettings, IPanel, RuntimePanelUtils, VisualElement) are not loaded.";
            return new UiToolkitDependencyStatus(
                available,
                reason,
                packageInfo?.name ?? "com.unity.modules.uielements",
                packageInfo?.version ?? string.Empty,
                packageInfo?.source.ToString() ?? string.Empty,
                UiToolkitVersionDefineActive,
                modulePresent,
                uiDocumentType,
                panelSettingsType,
                iPanelType,
                runtimePanelUtilsType,
                visualElementType);
        }

        internal static UnityEditor.PackageManager.PackageInfo? TryFindPackageInfo(string packageJsonPath)
        {
            try
            {
                return UnityEditor.PackageManager.PackageInfo.FindForAssetPath(packageJsonPath);
            }
            catch
            {
                return null;
            }
        }

        internal static Type? FindType(string fullName)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, throwOnError: false))
                .FirstOrDefault(type => type != null);
        }

        internal static object? GetMemberValue(object target, string memberName)
        {
            var type = target.GetType();
            var property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property != null)
            {
                return property.GetValue(target);
            }

            var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                return field.GetValue(target);
            }

            var serializedFieldName = "m_" + char.ToUpperInvariant(memberName[0]) + memberName.Substring(1);
            return type.GetField(serializedFieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target);
        }

        internal static string? ReadMemberString(object target, string memberName)
        {
            return GetMemberValue(target, memberName)?.ToString();
        }

        internal static bool ReadBoolMember(object target, string memberName, bool defaultValue)
        {
            return GetMemberValue(target, memberName) is bool value ? value : defaultValue;
        }

        internal static int ReadIntMember(object target, string memberName, int defaultValue)
        {
            return GetMemberValue(target, memberName) is int value ? value : defaultValue;
        }

        internal static int ReadDocumentSortingOrder(Component document)
        {
            var value = GetMemberValue(document, "sortingOrder");
            return value switch
            {
                int intValue => intValue,
                float floatValue => Mathf.RoundToInt(floatValue),
                double doubleValue => (int)Math.Round(doubleValue),
                _ => 0,
            };
        }

        internal static int? ReadNullableIntMember(object target, string memberName)
        {
            return GetMemberValue(target, memberName) is int value ? value : null;
        }

        internal static Vector2 ReadVector2Member(object target, string memberName)
        {
            return GetMemberValue(target, memberName) is Vector2 value ? value : default;
        }

        internal static Rect? ReadRectMember(object target, string memberName)
        {
            return GetMemberValue(target, memberName) is Rect value ? value : null;
        }

        internal static object? ReadSimpleMemberValue(object target, string memberName)
        {
            var value = GetMemberValue(target, memberName);
            return value switch
            {
                null => null,
                string or bool or int or long or float or double => value,
                Enum enumValue => enumValue.ToString(),
                UnityEngine.Object unityObject => CreateObjectRef(unityObject),
                _ => value.ToString(),
            };
        }

        internal static object? ReadSimpleToken(JToken? token)
        {
            return token?.Type switch
            {
                null or JTokenType.Null => null,
                JTokenType.String => token.Value<string>(),
                JTokenType.Boolean => token.Value<bool>(),
                JTokenType.Integer => token.Value<int>(),
                JTokenType.Float => token.Value<float>(),
                _ => token.ToString(),
            };
        }

        internal static string? ReadResolvedStyleMember(object visualElement, string memberName)
        {
            var resolvedStyle = GetMemberValue(visualElement, "resolvedStyle");
            return resolvedStyle == null ? null : ReadMemberString(resolvedStyle, memberName);
        }

        internal static string[] ReadClasses(object visualElement)
        {
            var method = visualElement.GetType().GetMethod("GetClasses", BindingFlags.Public | BindingFlags.Instance);
            return ((IEnumerable?)method?.Invoke(visualElement, null))?.Cast<object>().Select(item => item.ToString() ?? string.Empty).Where(item => item.Length > 0).ToArray()
                ?? Array.Empty<string>();
        }

        internal static bool TryReadVector2(JToken? token, out Vector2 value)
        {
            value = default;
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }

            if (token.Type == JTokenType.Array)
            {
                var array = (JArray)token;
                if (array.Count >= 2)
                {
                    value = new Vector2(array[0]!.Value<float>(), array[1]!.Value<float>());
                    return true;
                }
            }

            var x = token["x"] ?? token["X"];
            var y = token["y"] ?? token["Y"];
            if (x != null && y != null)
            {
                value = new Vector2(x.Value<float>(), y.Value<float>());
                return true;
            }

            return false;
        }

        internal static int ReadInt(JToken args, string key, int defaultValue)
        {
            return args[key]?.Value<int>() ?? defaultValue;
        }

        internal static float ReadFloat(JToken args, string key, float defaultValue)
        {
            return args[key]?.Value<float>() ?? defaultValue;
        }

        internal static bool ReadBool(JToken args, string key, bool defaultValue)
        {
            return args[key]?.Value<bool>() ?? defaultValue;
        }

        internal static string? ReadString(JToken args, string key)
        {
            return args[key]?.Value<string>();
        }

        internal static string RootMessage(Exception exception)
        {
            return exception is TargetInvocationException { InnerException: { } inner }
                ? inner.Message
                : exception.Message;
        }
    }
}
