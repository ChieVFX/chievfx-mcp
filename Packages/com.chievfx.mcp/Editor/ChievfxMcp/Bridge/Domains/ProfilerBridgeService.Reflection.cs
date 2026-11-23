#nullable enable
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
    internal sealed partial class ProfilerBridgeService
    {
        private static IEnumerable<object> EnumerateFrameDebuggerTreeViewTargets(EditorWindow window)
        {
            var targets = new List<object>();
            foreach (var memberName in new[] { "m_TreeView", "m_FrameDebuggerTreeView", "m_FrameEventsTreeView", "m_FrameEvents" })
            {
                var value = EditorWindowBridgeService.GetReflectedValue(window, memberName);
                if (value != null)
                {
                    targets.Add(value);
                }
            }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var field in window.GetType().GetFields(flags))
            {
                if (field.Name.IndexOf("TreeView", StringComparison.OrdinalIgnoreCase) < 0
                    && (field.FieldType.FullName?.IndexOf("FrameDebugger", StringComparison.OrdinalIgnoreCase) ?? -1) < 0)
                {
                    continue;
                }

                try
                {
                    var value = field.GetValue(window);
                    if (value != null)
                    {
                        targets.Add(value);
                    }
                }
                catch
                {
                    // Ignore version-specific reflected members that reject access.
                }
            }

            return targets.Where(target => target != null).Distinct().ToArray();
        }

        private static object? InvokeFrameDebuggerObjectGetter(Type utilityType, int eventIndex)
        {
            return TryInvokeReflectedMethod(utilityType, "GetFrameEventObject", new object?[] { eventIndex }, out var result, out _)
                ? result
                : null;
        }

        private static string? InvokeFrameDebuggerStringMethod(Type utilityType, string methodName, int eventIndex)
        {
            return TryInvokeReflectedMethod(utilityType, methodName, new object?[] { eventIndex }, out var result, out _)
                ? Convert.ToString(result, CultureInfo.InvariantCulture)
                : null;
        }

        private static void AddObjectSummary(Dictionary<string, object?> summary, string prefix, Object? obj)
        {
            if (obj == null)
            {
                return;
            }

            summary[prefix + "Name"] = obj.name;
            summary[prefix + "Type"] = obj.GetType().Name;
            summary[prefix + "InstanceId"] = GetLegacyInstanceId(obj);
        }

        private static void AddString(Dictionary<string, object?> summary, string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                summary[key] = value;
            }
        }

        private static void AddInt(Dictionary<string, object?> summary, string key, int? value)
        {
            if (value.HasValue && value.Value != 0)
            {
                summary[key] = value.Value;
            }
        }

        private static void AddBool(Dictionary<string, object?> summary, string key, bool? value)
        {
            if (value.HasValue)
            {
                summary[key] = value.Value;
            }
        }

        private static string? ReadFirstString(object target, params string[] names)
        {
            foreach (var name in names)
            {
                var value = GetReflectedInstanceValue(target, name);
                var text = value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            return null;
        }

        private static int? ReadFirstInt(object target, params string[] names)
        {
            foreach (var name in names)
            {
                if (TryConvertToInt(GetReflectedInstanceValue(target, name), out var value))
                {
                    return value;
                }
            }

            return null;
        }

        private static bool? ReadFirstBool(object target, params string[] names)
        {
            foreach (var name in names)
            {
                var value = GetReflectedInstanceValue(target, name);
                if (value != null)
                {
                    return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                }
            }

            return null;
        }

        private static int? TryConvertToIntValue(object? value)
        {
            return TryConvertToInt(value, out var intValue) ? intValue : (int?)null;
        }

        private static int? ReadNullableIntMember(object target, string name)
        {
            var value = target is Type type ? GetReflectedStaticValue(type, name) : EditorWindowBridgeService.GetReflectedValue(target, name);
            return TryConvertToInt(value, out var intValue) ? intValue : null;
        }

        private static string? ReadStringMember(object target, string name)
        {
            var value = target is Type type ? GetReflectedStaticValue(type, name) : EditorWindowBridgeService.GetReflectedValue(target, name);
            return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string? ReadStaticStringMember(Type type, string name)
        {
            var value = GetReflectedStaticValue(type, name);
            return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static object? GetReflectedStaticValue(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var property = type.GetProperties(flags).FirstOrDefault(candidate =>
                    candidate.GetIndexParameters().Length == 0
                    && string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
                if (property != null)
                {
                    return property.GetValue(null);
                }
            }
            catch
            {
                return null;
            }

            try
            {
                var field = type.GetFields(flags).FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
                return field?.GetValue(null);
            }
            catch
            {
                return null;
            }
        }

        private static object? GetReflectedInstanceValue(object target, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var type = target.GetType();
                var property = type.GetProperties(flags).FirstOrDefault(candidate =>
                    candidate.GetIndexParameters().Length == 0
                    && string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
                if (property != null)
                {
                    return property.GetValue(target);
                }

                var field = type.GetFields(flags).FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
                return field?.GetValue(target);
            }
            catch
            {
                return null;
            }
        }

        private static bool TryReadStaticIntMember(Type type, string name, out int value)
        {
            return TryConvertToInt(GetReflectedStaticValue(type, name), out value);
        }

        private static bool TrySetReflectedMember(object target, string name, object? value, out string? error)
        {
            error = null;
            var type = target as Type ?? target.GetType();
            var receiver = target is Type ? null : target;
            var flags = (receiver == null ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var property = type.GetProperties(flags).FirstOrDefault(candidate =>
                    candidate.GetIndexParameters().Length == 0
                    && string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
                var setter = property?.GetSetMethod(true);
                if (setter != null)
                {
                    setter.Invoke(receiver, new[] { CoerceReflectedValue(value, property!.PropertyType) });
                    return true;
                }

                var field = type.GetFields(flags).FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
                if (field != null)
                {
                    field.SetValue(receiver, CoerceReflectedValue(value, field.FieldType));
                    return true;
                }

                error = $"No writable member '{name}' found on {type.FullName}.";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.GetBaseException().Message;
                return false;
            }
        }

        private static bool TryInvokeReflectedMethod(object target, string methodName, object?[] arguments, out object? result, out string? error)
        {
            result = null;
            error = null;
            var type = target as Type ?? target.GetType();
            var receiver = target is Type ? null : target;
            var flags = (receiver == null ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.Public | BindingFlags.NonPublic;
            var methods = type.GetMethods(flags)
                .Where(method => string.Equals(method.Name, methodName, StringComparison.OrdinalIgnoreCase)
                    && method.GetParameters().Length == arguments.Length)
                .ToArray();

            foreach (var method in methods)
            {
                try
                {
                    var parameters = method.GetParameters();
                    var coerced = new object?[arguments.Length];
                    for (var i = 0; i < arguments.Length; i++)
                    {
                        coerced[i] = CoerceReflectedValue(arguments[i], parameters[i].ParameterType);
                    }

                    result = method.Invoke(receiver, coerced);
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.GetBaseException().Message;
                }
            }

            error ??= $"No method '{methodName}' with {arguments.Length} parameter(s) found on {type.FullName}.";
            return false;
        }

        private static object? CoerceReflectedValue(object? value, Type targetType)
        {
            if (value == null)
            {
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
            }

            var nullableTarget = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (nullableTarget.IsInstanceOfType(value))
            {
                return value;
            }

            if (nullableTarget.IsEnum)
            {
                return value is string text
                    ? Enum.Parse(nullableTarget, text, ignoreCase: true)
                    : Enum.ToObject(nullableTarget, value);
            }

            return Convert.ChangeType(value, nullableTarget, CultureInfo.InvariantCulture);
        }

        private static bool TryConvertToInt(object? value, out int intValue)
        {
            switch (value)
            {
                case int number:
                    intValue = number;
                    return true;
                case long number:
                    intValue = Convert.ToInt32(number, CultureInfo.InvariantCulture);
                    return true;
                case short number:
                    intValue = number;
                    return true;
                case byte number:
                    intValue = number;
                    return true;
                case null:
                    intValue = 0;
                    return false;
                default:
                    try
                    {
                        intValue = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                        return true;
                    }
                    catch
                    {
                        intValue = 0;
                        return false;
                    }
            }
        }
    }
}
