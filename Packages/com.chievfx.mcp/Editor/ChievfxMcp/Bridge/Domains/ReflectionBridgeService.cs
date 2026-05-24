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
    internal sealed class ReflectionBridgeService : BridgeDomainServiceBase
    {
        public object FindMethods(JToken args)
        {
            var query = ReadFindQuery(args);
            var methods = ReadPagedMethods(query);
            var truncated = methods.Length > query.MaxResults;

            return new
            {
                count = Math.Min(methods.Length, query.MaxResults),
                truncated,
                page = query.Page,
                pageSize = query.MaxResults,
                hasMore = truncated,
                nextPage = truncated ? query.Page + 1 : (int?)null,
                methods = methods
                    .Take(query.MaxResults)
                    .Select((method, index) => MethodToDto(method, index))
                    .ToArray()
            };
        }

        public object FindSingleMethod(JToken args)
        {
            var query = ReadFindQuery(args);
            var index = ClampInt(ReadInt(args, "index", 0), 0, query.MaxResults - 1);
            var methods = ReadPagedMethods(query)
                .Take(query.MaxResults)
                .ToArray();

            if (index >= methods.Length)
            {
                throw new IndexOutOfRangeException($"No reflection-method-find result at index {index} on page {query.Page}; page contains {methods.Length} result(s).");
            }

            return new
            {
                index,
                page = query.Page,
                pageSize = query.MaxResults,
                method = MethodToDto(methods[index], index)
            };
        }

        private static int ReadFindMatchLevel(JToken args)
        {
            var match = ReadString(args, "match") ?? "exact";
            return string.Equals(match, "contains", StringComparison.OrdinalIgnoreCase) ? 1 : 6;
        }

        private static bool ReadKnownNamespace(JToken args, JToken filter)
        {
            if (HasProperty(args, "knownNamespace"))
            {
                return ReadBool(args, "knownNamespace", false);
            }

            return !string.IsNullOrEmpty(ReadString(filter, "namespace"));
        }

        private static ReflectionFindQuery ReadFindQuery(JToken args)
        {
            var filter = ReadObject(args, "filter");
            var maxResults = ClampInt(ReadInt(args, "maxResults", DefaultReflectionMaxResults), 1, HardReflectionMaxResults);
            var matchLevel = ReadFindMatchLevel(args);
            return new ReflectionFindQuery
            {
                Filter = filter,
                MaxResults = maxResults,
                Page = Math.Max(1, ReadInt(args, "page", 1)),
                KnownNamespace = ReadKnownNamespace(args, filter),
                TypeNameMatchLevel = HasProperty(args, "typeNameMatchLevel") ? ReadInt(args, "typeNameMatchLevel", matchLevel) : matchLevel,
                MethodNameMatchLevel = HasProperty(args, "methodNameMatchLevel") ? ReadInt(args, "methodNameMatchLevel", matchLevel) : matchLevel,
                ParametersMatchLevel = ReadInt(args, "parametersMatchLevel", 0),
                IncludeSpecialNames = ReadBool(args, "includeSpecialNames", false)
            };
        }

        private static MethodInfo[] ReadPagedMethods(ReflectionFindQuery query)
        {
            var skip = (long)(query.Page - 1) * query.MaxResults;
            var methodsQuery = FindMatchingMethods(
                query.Filter,
                query.KnownNamespace,
                query.TypeNameMatchLevel,
                query.MethodNameMatchLevel,
                query.ParametersMatchLevel,
                query.IncludeSpecialNames);

            if (skip > int.MaxValue)
            {
                methodsQuery = Enumerable.Empty<MethodInfo>();
            }
            else if (skip > 0)
            {
                methodsQuery = methodsQuery.Skip((int)skip);
            }

            return methodsQuery
                .Take(query.MaxResults + 1)
                .ToArray();
        }

        public object CallMethod(JToken args)
        {
            var inputParameters = ReadArray(args, "inputParameters");
            var method = FindMatchingMethods(
                    ReadObject(args, "filter"),
                    ReadBool(args, "knownNamespace", false),
                    ReadInt(args, "typeNameMatchLevel", 1),
                    ReadInt(args, "methodNameMatchLevel", 1),
                    ReadInt(args, "parametersMatchLevel", 2),
                    ReadBool(args, "includeSpecialNames", false))
                .FirstOrDefault()
                ?? throw new InvalidOperationException("No matching method found.");

            ValidateCallableMethod(method);

            var parameters = method.GetParameters();
            var values = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                values[i] = ConvertJsonValue(inputParameters, i, parameters[i].ParameterType);
            }

            object? target = null;
            if (!method.IsStatic)
            {
                target = ConvertTargetObject(args, method.DeclaringType!);
            }

            var value = method.Invoke(target, values);
            return SerializeMethodReturn(method, value);
        }

        private static IEnumerable<MethodInfo> FindMatchingMethods(
            JToken filter,
            bool knownNamespace,
            int typeNameMatchLevel,
            int methodNameMatchLevel,
            int parametersMatchLevel,
            bool includeSpecialNames)
        {
            var namespaceFilter = ReadString(filter, "namespace") ?? string.Empty;
            var typeNameFilter = ReadString(filter, "typeName") ?? string.Empty;
            var methodNameFilter = ReadString(filter, "methodName") ?? string.Empty;
            var parameterFilter = ReadArray(filter, "inputParameters");

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(type => type != null).Cast<Type>().ToArray();
                }

                foreach (var type in types)
                {
                    if (knownNamespace && !string.Equals(type.Namespace ?? string.Empty, namespaceFilter, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!Matches(type.Name, typeNameFilter, typeNameMatchLevel) && !Matches(type.FullName ?? type.Name, typeNameFilter, typeNameMatchLevel))
                    {
                        continue;
                    }

                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                    foreach (var method in type.GetMethods(flags))
                    {
                        if ((!includeSpecialNames && method.IsSpecialName) || !Matches(method.Name, methodNameFilter, methodNameMatchLevel))
                        {
                            continue;
                        }

                        if (!ParametersMatch(method.GetParameters(), parameterFilter, parametersMatchLevel))
                        {
                            continue;
                        }

                        yield return method;
                    }
                }
            }
        }

        private static void ValidateCallableMethod(MethodInfo method)
        {
            if (method.ContainsGenericParameters || method.IsGenericMethodDefinition)
            {
                throw new NotSupportedException("Generic methods are not supported by reflection-method-call.");
            }

            foreach (var parameter in method.GetParameters())
            {
                var parameterType = parameter.ParameterType;
                if (parameter.IsOut || parameterType.IsByRef)
                {
                    throw new NotSupportedException("ref/out parameters are not supported by reflection-method-call.");
                }

                if (ContainsPointerType(parameterType))
                {
                    throw new NotSupportedException("Unsafe pointer parameters are not supported by reflection-method-call.");
                }
            }

            if (ContainsPointerType(method.ReturnType))
            {
                throw new NotSupportedException("Unsafe pointer return values are not supported by reflection-method-call.");
            }
        }

        internal static bool ContainsPointerType(Type type)
        {
            if (type.IsPointer)
            {
                return true;
            }

            if (type.IsArray)
            {
                return ContainsPointerType(type.GetElementType()!);
            }

            if (type.IsGenericType)
            {
                return type.GetGenericArguments().Any(ContainsPointerType);
            }

            return false;
        }

        private static object? ConvertTargetObject(JToken args, Type declaringType)
        {
            var targetObject = ReadObject(args, "targetObject");
            if (targetObject is not JObject targetObj || !targetObj.Properties().Any())
            {
                throw new NotSupportedException("Instance method calls require targetObject; automatic instance creation is unsupported.");
            }

            if (typeof(Object).IsAssignableFrom(declaringType))
            {
                throw new NotSupportedException("UnityEngine.Object instance calls from targetObject are unsupported; use static methods or a dedicated MCP tool.");
            }

            if (ReadProperty(targetObject, "value") is not JToken value || value.Type == JTokenType.Null)
            {
                throw new NotSupportedException("targetObject.value is required for supported instance method calls.");
            }

            try
            {
                return value.ToObject(declaringType);
            }
            catch (Exception ex)
            {
                throw new NotSupportedException($"targetObject.value could not deserialize to {declaringType.FullName}: {ex.GetBaseException().Message}", ex);
            }
        }

        private static Dictionary<string, object?> SerializeMethodReturn(MethodInfo method, object? value)
        {
            var truncated = false;
            var result = new Dictionary<string, object?>
            {
                ["type"] = value?.GetType().FullName ?? method.ReturnType.FullName ?? "System.Void",
                ["value"] = SerializeReturnValue(value, ref truncated)
            };

            if (value is Object unityObject && !string.IsNullOrEmpty(unityObject.name))
            {
                result["name"] = unityObject.name;
            }

            if (truncated)
            {
                result["truncated"] = true;
            }

            return result;
        }

        internal static object? SerializeReturnValue(object? value, ref bool truncated)
        {
            if (value == null)
            {
                return null;
            }

            var type = value.GetType();
            if (type == typeof(string))
            {
                return TrimText((string)value, MaxReturnValueChars, ref truncated);
            }

            if (type.IsPrimitive || type.IsEnum || type == typeof(decimal))
            {
                return value;
            }

            if (value is Object unityObject)
            {
                return new
                {
                    instanceId = GetLegacyInstanceId(unityObject),
                    name = unityObject.name
                };
            }

            try
            {
                var rawJson = JsonConvert.SerializeObject(value, type, BridgeRuntimeState.JsonOptions);
                return TrimText(rawJson, MaxReturnValueChars, ref truncated);
            }
            catch (Exception ex)
            {
                throw new NotSupportedException($"Return value of type {type.FullName} could not be serialized safely: {ex.GetBaseException().Message}", ex);
            }
        }


        internal static object? InvokeWithOptionalParameters(MethodBase method, object? target, params object?[] suppliedArguments)
        {
            var parameters = method.GetParameters();
            var arguments = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                if (i < suppliedArguments.Length)
                {
                    arguments[i] = suppliedArguments[i];
                    continue;
                }

                arguments[i] = CreateDefaultArgument(parameters[i]);
            }

            if (method is ConstructorInfo constructor)
            {
                return constructor.Invoke(arguments);
            }

            if (method is MethodInfo methodInfo)
            {
                return methodInfo.Invoke(target, arguments);
            }

            throw new NotSupportedException($"Unsupported reflected method type '{method.GetType().FullName}'.");
        }

        private static object? CreateDefaultArgument(ParameterInfo parameter)
        {
            if (parameter.HasDefaultValue && parameter.DefaultValue != DBNull.Value)
            {
                return parameter.DefaultValue;
            }

            if (parameter.ParameterType == typeof(CancellationToken))
            {
                return CancellationToken.None;
            }

            return parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null;
        }

        internal static object? ReadReflectedProperty(object source, string propertyName)
        {
            return source.GetType()
                .GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(source);
        }

        internal static string? ReadReflectedStringProperty(object source, string propertyName)
        {
            return Convert.ToString(ReadReflectedProperty(source, propertyName), CultureInfo.InvariantCulture);
        }

        internal static double ReadReflectedDoubleProperty(object source, string propertyName)
        {
            var value = ReadReflectedProperty(source, propertyName);
            return value == null ? 0d : Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        internal static IEnumerable<object> ReadReflectedEnumerableProperty(object source, string propertyName)
        {
            if (ReadReflectedProperty(source, propertyName) is not IEnumerable enumerable)
            {
                return Array.Empty<object>();
            }

            var values = new List<object>();
            foreach (var value in enumerable)
            {
                if (value != null)
                {
                    values.Add(value);
                }
            }

            return values;
        }

        internal static string? InvokeReflectedStringMethod(object source, string methodName)
        {
            var method = source.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            return method == null ? null : Convert.ToString(method.Invoke(source, null), CultureInfo.InvariantCulture);
        }


        private static bool ParametersMatch(ParameterInfo[] methodParameters, JToken parameterFilter, int matchLevel)
        {
            if (matchLevel <= 0 || parameterFilter is not JArray parameterArray)
            {
                return true;
            }

            if (methodParameters.Length != parameterArray.Count)
            {
                return false;
            }

            if (matchLevel == 1)
            {
                return true;
            }

            for (var i = 0; i < methodParameters.Length; i++)
            {
                var typeName = ReadString(parameterArray[i], "typeName") ?? string.Empty;
                if (!string.Equals(methodParameters[i].ParameterType.FullName, typeName, StringComparison.Ordinal)
                    && !string.Equals(methodParameters[i].ParameterType.Name, typeName, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool Matches(string value, string filter, int matchLevel)
        {
            if (matchLevel <= 0 || string.IsNullOrEmpty(filter))
            {
                return true;
            }

            return matchLevel switch
            {
                1 => value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0,
                2 => value.Contains(filter, StringComparison.Ordinal),
                3 => value.StartsWith(filter, StringComparison.OrdinalIgnoreCase),
                4 => value.StartsWith(filter, StringComparison.Ordinal),
                5 => string.Equals(value, filter, StringComparison.OrdinalIgnoreCase),
                6 => string.Equals(value, filter, StringComparison.Ordinal),
                _ => value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
            };
        }

        private static MethodDto MethodToDto(MethodInfo method, int? index = null)
        {
            var parameters = method.GetParameters();
            return new MethodDto
            {
                index = index,
                ns = method.DeclaringType?.Namespace ?? string.Empty,
                type = method.DeclaringType?.Name ?? string.Empty,
                method = method.Name,
                signature = BuildSignature(method, parameters),
                @return = method.ReturnType.FullName ?? method.ReturnType.Name,
                @params = parameters
                    .Select(parameter => new ParameterDto
                    {
                        type = parameter.ParameterType.FullName ?? parameter.ParameterType.Name,
                        name = parameter.Name ?? string.Empty
                    })
                    .ToArray(),
                @static = method.IsStatic,
                visibility = GetVisibility(method),
                callFilter = BuildCallFilter(method, parameters)
            };
        }

        private static object BuildCallFilter(MethodInfo method, ParameterInfo[] parameters)
        {
            return new
            {
                @namespace = method.DeclaringType?.Namespace ?? string.Empty,
                typeName = method.DeclaringType?.Name ?? string.Empty,
                methodName = method.Name,
                inputParameters = parameters
                    .Select(parameter => new
                    {
                        typeName = parameter.ParameterType.FullName ?? parameter.ParameterType.Name
                    })
                    .ToArray()
            };
        }

        private static string BuildSignature(MethodInfo method, ParameterInfo[] parameters)
        {
            var parameterText = string.Join(", ", parameters.Select(parameter =>
            {
                var typeName = CompactTypeName(parameter.ParameterType);
                return string.IsNullOrEmpty(parameter.Name)
                    ? typeName
                    : $"{typeName} {parameter.Name}";
            }));
            return $"{method.Name}({parameterText})";
        }

        private static string CompactTypeName(Type type)
        {
            if (type == typeof(void)) return "void";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(byte)) return "byte";
            if (type == typeof(char)) return "char";
            if (type == typeof(decimal)) return "decimal";
            if (type == typeof(double)) return "double";
            if (type == typeof(float)) return "float";
            if (type == typeof(short)) return "short";
            if (type == typeof(int)) return "int";
            if (type == typeof(long)) return "long";
            if (type == typeof(string)) return "string";
            if (type == typeof(object)) return "object";

            if (type.IsArray)
            {
                return $"{CompactTypeName(type.GetElementType()!)}[]";
            }

            return type.Name.Replace('+', '.');
        }

        private static string GetVisibility(MethodBase method)
        {
            if (method.IsPublic)
            {
                return "public";
            }

            if (method.IsFamily)
            {
                return "protected";
            }

            if (method.IsAssembly)
            {
                return "internal";
            }

            if (method.IsFamilyOrAssembly)
            {
                return "protected-internal";
            }

            return "private";
        }

        internal static object? ConvertJsonValue(JToken inputParameters, int index, Type targetType)
        {
            if (inputParameters is not JArray inputArray || inputArray.Count <= index)
            {
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
            }

            if (ReadProperty(inputArray[index], "value") is not JToken value || value.Type == JTokenType.Null)
            {
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
            }

            if (targetType == typeof(string))
            {
                return value.Type == JTokenType.String ? value.Value<string>() : value.ToString(Formatting.None);
            }

            if (targetType == typeof(int))
            {
                return value.Value<int>();
            }

            if (targetType == typeof(double))
            {
                return value.Value<double>();
            }

            if (targetType == typeof(bool))
            {
                return value.Value<bool>();
            }

            if (targetType.IsEnum)
            {
                return value.Type == JTokenType.String
                    ? Enum.Parse(targetType, value.Value<string>()!, true)
                    : Enum.ToObject(targetType, value.Value<int>());
            }

            return value.ToObject(targetType);
        }

        private sealed class ReflectionFindQuery
        {
            public JToken Filter { get; set; } = JValue.CreateNull();

            public int MaxResults { get; set; }

            public int Page { get; set; }

            public bool KnownNamespace { get; set; }

            public int TypeNameMatchLevel { get; set; }

            public int MethodNameMatchLevel { get; set; }

            public int ParametersMatchLevel { get; set; }

            public bool IncludeSpecialNames { get; set; }
        }

    }
}
