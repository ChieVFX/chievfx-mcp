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
    internal sealed partial class BridgeResourcePayloadService
    {
        internal static ResourceGameObjectFilter CreateNameContainsResourceFilter(string text)
        {
            GameObjectBridgeService.ValidateResourceFilterText(text, "text");
            return new ResourceGameObjectFilter
            {
                Kind = "name-contains",
                NameContains = new[] { text },
                MaxResults = DefaultResourceFilterMaxResults
            };
        }

        internal static ResourceGameObjectFilter CreateNamePatternResourceFilter(string pattern)
        {
            GameObjectBridgeService.ValidateResourceFilterText(pattern, "pattern");
            return new ResourceGameObjectFilter
            {
                Kind = "name-pattern",
                NamePatterns = new[] { pattern },
                MaxResults = DefaultResourceFilterMaxResults
            };
        }

        internal static ResourceGameObjectFilter CreateComponentResourceFilter(string componentType)
        {
            GameObjectBridgeService.ValidateResourceFilterText(componentType, "componentType");
            return new ResourceGameObjectFilter
            {
                Kind = "component",
                ComponentTypes = new[] { componentType },
                MaxResults = DefaultResourceFilterMaxResults
            };
        }

        internal static ResourceGameObjectFilter ParseResourceFilterSpec(string filterSpec)
        {
            if (string.IsNullOrWhiteSpace(filterSpec))
            {
                throw new ArgumentException("filterSpec cannot be empty.", nameof(filterSpec));
            }

            if (filterSpec.Length > MaxResourceFilterSegmentChars)
            {
                throw new ArgumentException($"filterSpec must be {MaxResourceFilterSegmentChars} characters or fewer.", nameof(filterSpec));
            }

            var filter = new ResourceGameObjectFilter
            {
                Kind = "filter",
                MaxResults = DefaultResourceFilterMaxResults
            };

            foreach (var clause in filterSpec.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(clause))
                {
                    continue;
                }

                var separator = clause.IndexOf('=');
                if (separator <= 0)
                {
                    throw new ArgumentException($"Invalid filter clause '{clause}'. Use key=value syntax.", nameof(filterSpec));
                }

                var key = clause.Substring(0, separator).Trim().ToLowerInvariant();
                var value = clause.Substring(separator + 1).Trim();
                switch (key)
                {
                    case "name":
                        filter.NamePatterns = ParseResourceFilterValues(value, "name");
                        foreach (var pattern in filter.NamePatterns)
                        {
                            GameObjectBridgeService.ValidateWildcardPattern(pattern, "name");
                        }

                        break;
                    case "component":
                        filter.ComponentTypes = ParseResourceFilterValues(value, "component");
                        foreach (var componentType in filter.ComponentTypes)
                        {
                            GameObjectBridgeService.ValidateComponentTypeText(componentType, required: true);
                        }

                        break;
                    case "inactive":
                        filter.IncludeInactive = ParseResourceFilterBool(value, "inactive");
                        break;
                    case "case":
                        filter.CaseInsensitive = ParseResourceFilterCase(value);
                        break;
                    case "limit":
                        filter.MaxResults = ParseResourceFilterLimit(value);
                        break;
                    default:
                        throw new ArgumentException($"Unsupported filterSpec key '{key}'.", nameof(filterSpec));
                }
            }

            return filter;
        }

        private static string[] ParseResourceFilterValues(string value, string parameterName)
        {
            var values = value
                .Split(',')
                .Select(item => item.Trim())
                .Where(item => item.Length > 0)
                .ToArray();
            if (values.Length == 0)
            {
                throw new ArgumentException($"{parameterName} must include at least one value.", parameterName);
            }

            if (values.Length > MaxResourceFilterValues)
            {
                throw new ArgumentException($"{parameterName} accepts at most {MaxResourceFilterValues} values.", parameterName);
            }

            foreach (var item in values)
            {
                GameObjectBridgeService.ValidateResourceFilterText(item, parameterName);
            }

            return values;
        }

        private static bool ParseResourceFilterBool(string value, string parameterName)
        {
            if (string.Equals(value, "1", StringComparison.Ordinal)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(value, "0", StringComparison.Ordinal)
                || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            throw new ArgumentException($"{parameterName} must be 1/0 or true/false.", parameterName);
        }

        private static bool ParseResourceFilterCase(string value)
        {
            if (string.Equals(value, "i", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "ignore", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "insensitive", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(value, "s", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "c", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "sensitive", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            throw new ArgumentException("case must be 'i' for insensitive or 's' for sensitive.", nameof(value));
        }

        private static int ParseResourceFilterLimit(string value)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                throw new ArgumentException("limit must be an integer.", nameof(value));
            }

            return ClampInt(parsed, 1, HardResourceFilterMaxResults);
        }

        private static bool ResourceFilterNameMatches(string name, ResourceGameObjectFilter filter)
        {
            var hasNameCriteria = filter.NameContains.Length > 0 || filter.NamePatterns.Length > 0;
            if (!hasNameCriteria)
            {
                return true;
            }

            if (filter.NameContains.Any(text => name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return true;
            }

            return filter.NamePatterns.Any(pattern => GameObjectBridgeService.WildcardMatches(name, pattern, filter.CaseInsensitive));
        }

        private static bool HasMatchingComponentForResourceFilter(GameObject gameObject, string componentType)
        {
            return gameObject.GetComponents<Component>().Any(component =>
            {
                if (component == null)
                {
                    return string.Equals(componentType, "MissingScript", StringComparison.OrdinalIgnoreCase);
                }

                return GameObjectBridgeService.ComponentTypeMatches(component.GetType(), componentType);
            });
        }

        private static Dictionary<string, object?> CreateResourceFilterDto(ResourceGameObjectFilter filter)
        {
            var output = new Dictionary<string, object?>
            {
                ["kind"] = filter.Kind,
                ["includeInactive"] = filter.IncludeInactive,
                ["case"] = filter.CaseInsensitive ? "i" : "s",
                ["maxResults"] = filter.MaxResults
            };
            if (filter.NameContains.Length > 0)
            {
                output["nameContains"] = filter.NameContains;
            }

            if (filter.NamePatterns.Length > 0)
            {
                output["namePatterns"] = filter.NamePatterns;
            }

            if (filter.ComponentTypes.Length > 0)
            {
                output["componentTypes"] = filter.ComponentTypes;
            }

            return output;
        }

        private static Dictionary<string, object?> CreateResourceFilterGameObjectRow(GameObject gameObject, GameObjectQueryContext context)
        {
            var path = GameObjectBridgeService.GetHierarchyPath(gameObject, context);
            var gameObjectUri = GetCurrentGameObjectResourceUri(path);
            var components = CreateResourceComponentSummaries(gameObject, gameObjectUri, MaxComponentPreviewTypes, out var componentsTruncated);
            return new Dictionary<string, object?>
            {
                ["name"] = gameObject.name,
                ["path"] = path,
                ["instanceId"] = GetLegacyInstanceId(gameObject),
                ["activeSelf"] = gameObject.activeSelf,
                ["activeInHierarchy"] = gameObject.activeInHierarchy,
                ["componentTypes"] = components.Select(component => component["type"]).ToArray(),
                ["componentsTruncated"] = componentsTruncated,
                ["resourceUri"] = gameObjectUri,
                ["components"] = components
            };
        }

        private static Dictionary<string, object?> CreateResourceEnvelope(string uri, Dictionary<string, object?> context)
        {
            return new Dictionary<string, object?>
            {
                ["readAt"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["uri"] = uri,
                ["context"] = context
            };
        }

        private static Dictionary<string, object?> CreateResourceContext(GameObjectQueryContext context)
        {
            return new Dictionary<string, object?>
            {
                ["source"] = context.Source,
                ["sceneName"] = context.SceneName,
                ["scenePath"] = context.ScenePath,
                ["prefabAssetPath"] = context.PrefabAssetPath,
            };
        }

        internal static GameObjectQueryContext ResolveResourceSceneContext(string sceneSegment)
        {
            if (string.Equals(sceneSegment, "current", StringComparison.Ordinal))
            {
                return GameObjectBridgeService.GetGameObjectQueryContext();
            }

            return GetSceneContextByPath(DecodeResourceSegment(sceneSegment, "scenePath"));
        }

        private static GameObjectQueryContext GetSceneContextByPath(string scenePath)
        {
            var scene = SceneBridgeService.GetOpenScenes()
                .FirstOrDefault(candidate => string.Equals(candidate.path, scenePath, StringComparison.Ordinal));
            if (!scene.IsValid() || !scene.isLoaded)
            {
                throw new InvalidOperationException($"No opened scene found at '{scenePath}'.");
            }

            return CreateSceneContext(scene, "scene");
        }

        private static GameObjectQueryContext CreateSceneContext(Scene scene, string source)
        {
            return new GameObjectQueryContext
            {
                Source = source,
                SceneName = scene.name,
                ScenePath = scene.path,
                PrefabAssetPath = string.Empty,
                Roots = scene.GetRootGameObjects()
            };
        }

        private static Dictionary<string, object?> SceneToResourceDto(Scene scene)
        {
            var output = new Dictionary<string, object?>
            {
                ["name"] = scene.name,
                ["path"] = scene.path,
                ["isLoaded"] = scene.isLoaded,
                ["isDirty"] = scene.isDirty,
                ["isValid"] = scene.IsValid(),
                ["rootCount"] = scene.IsValid() && scene.isLoaded ? scene.rootCount : 0,
                ["buildIndex"] = scene.buildIndex
            };
            return output;
        }

        private static Dictionary<string, object?> CreateSelectionResourceSummary(GameObject gameObject)
        {
            var output = new Dictionary<string, object?>
            {
                ["name"] = gameObject.name,
                ["instanceId"] = GetLegacyInstanceId(gameObject),
                ["scenePath"] = gameObject.scene.path
            };
            try
            {
                var context = GameObjectBridgeService.GetGameObjectQueryContext();
                output["path"] = GameObjectBridgeService.GetHierarchyPath(gameObject, context);
                output["resourceUri"] = GetGameObjectResourceUri(gameObject, context);
            }
            catch (Exception ex)
            {
                output["pathError"] = ex.GetBaseException().Message;
            }

            return output;
        }

        private static Dictionary<string, object?> CreateResourceGameObjectSummary(
            GameObject gameObject,
            GameObjectQueryContext context,
            bool includeComponents)
        {
            var output = new Dictionary<string, object?>
            {
                ["name"] = gameObject.name,
                ["path"] = GameObjectBridgeService.GetHierarchyPath(gameObject, context),
                ["resourceUri"] = GetGameObjectResourceUri(gameObject, context),
                ["instanceId"] = GetLegacyInstanceId(gameObject),
                ["activeSelf"] = gameObject.activeSelf,
                ["activeInHierarchy"] = gameObject.activeInHierarchy,
                ["childCount"] = gameObject.transform.childCount,
                ["scenePath"] = gameObject.scene.path
            };
            if (includeComponents)
            {
                output["components"] = CreateResourceComponentSummaries(gameObject, context);
            }

            return output;
        }

        private static Dictionary<string, object?> CreateResourceGameObjectDetail(GameObject gameObject, GameObjectQueryContext context)
        {
            var detail = CreateResourceGameObjectSummary(gameObject, context, includeComponents: true);
            detail["tag"] = gameObject.tag;
            detail["layer"] = gameObject.layer;
            detail["isStatic"] = gameObject.isStatic;
            if (gameObject.transform.parent != null)
            {
                detail["parentPath"] = GameObjectBridgeService.GetHierarchyPath(gameObject.transform.parent.gameObject, context);
            }

            return detail;
        }

        private static Dictionary<string, object?>[] CreateResourceComponentSummaries(GameObject gameObject, GameObjectQueryContext context)
        {
            var gameObjectUri = GetGameObjectResourceUri(gameObject, context);
            return CreateResourceComponentSummaries(gameObject, gameObjectUri, int.MaxValue, out _);
        }

        private static Dictionary<string, object?>[] CreateResourceComponentSummaries(
            GameObject gameObject,
            string gameObjectUri,
            int maxComponents,
            out bool truncated)
        {
            var components = gameObject.GetComponents<Component>();
            var keys = BuildComponentKeys(components);
            truncated = components.Length > maxComponents;
            return components
                .Select((component, index) => CreateResourceComponentSummary(component, keys[index], gameObjectUri))
                .Take(maxComponents)
                .ToArray();
        }

        private static Dictionary<string, object?> CreateResourceComponentSummary(Component? component, string key, string gameObjectUri)
        {
            var type = component != null ? component.GetType() : null;
            var output = new Dictionary<string, object?>
            {
                ["key"] = key,
                ["type"] = type?.Name ?? "MissingScript",
                ["fullType"] = type?.FullName ?? "MissingScript",
                ["resourceUri"] = $"{gameObjectUri}/component/{EncodeResourceSegment(key)}"
            };
            if (component != null)
            {
                output["enabled"] = GameObjectBridgeService.TryGetEnabledState(component);
            }

            return output;
        }

        private static Dictionary<string, object?> CreateResourceComponentDetail(
            Component component,
            string key,
            GameObjectQueryContext context,
            ref bool serializedTruncated)
        {
            var gameObjectUri = component.gameObject != null
                ? GetGameObjectResourceUri(component.gameObject, context)
                : string.Empty;
            var detail = CreateResourceComponentSummary(component, key, gameObjectUri);
            detail["serializedFields"] = GameObjectBridgeService.SerializeComponentFields(component, isDebug: false, ref serializedTruncated);
            return detail;
        }

        internal static (Component Component, string Key) ResolveComponentByKey(GameObject gameObject, string componentKey)
        {
            var components = gameObject.GetComponents<Component>();
            var keys = BuildComponentKeys(components);
            var exactMatches = components
                .Select((component, index) => new { Component = component, Key = keys[index] })
                .Where(entry => entry.Component != null && string.Equals(entry.Key, componentKey, StringComparison.Ordinal))
                .ToArray();
            if (exactMatches.Length == 1)
            {
                return (exactMatches[0].Component!, exactMatches[0].Key);
            }

            var simpleMatches = components
                .Select((component, index) => new { Component = component, Key = keys[index], SimpleName = GetComponentSimpleName(component) })
                .Where(entry => entry.Component != null && string.Equals(entry.SimpleName, componentKey, StringComparison.Ordinal))
                .ToArray();
            if (simpleMatches.Length == 1)
            {
                return (simpleMatches[0].Component!, simpleMatches[0].Key);
            }

            if (simpleMatches.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Component key '{componentKey}' is ambiguous on GameObject '{gameObject.name}'. "
                    + "Use a suffixed key such as BoxCollider.1.");
            }

            throw new InvalidOperationException($"No component found with key '{componentKey}' on GameObject '{gameObject.name}'.");
        }

        private static string[] BuildComponentKeys(Component?[] components)
        {
            var names = components.Select(GetComponentSimpleName).ToArray();
            var counts = names
                .GroupBy(name => name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            return names
                .Select(name =>
                {
                    if (!seen.ContainsKey(name))
                    {
                        seen[name] = 0;
                    }

                    seen[name]++;
                    return counts[name] > 1
                        ? $"{name}.{seen[name]}"
                        : name;
                })
                .ToArray();
        }

        private static string GetComponentSimpleName(Component? component)
        {
            return component == null ? "MissingScript" : component.GetType().Name;
        }

        private static string GetGameObjectResourceUri(GameObject gameObject, GameObjectQueryContext context)
        {
            var path = GameObjectBridgeService.GetHierarchyPath(gameObject, context);
            var sceneSegment = string.IsNullOrWhiteSpace(context.ScenePath) || string.Equals(context.Source, "prefabStage", StringComparison.Ordinal)
                ? "current"
                : EncodeResourceSegment(context.ScenePath);
            return $"chievfx://scene/{sceneSegment}/go/{EncodeResourceSegment(path)}";
        }

        private static string GetCurrentGameObjectResourceUri(string path)
        {
            return $"chievfx://scene/current/go/{EncodeResourceSegment(path)}";
        }

        private static string EncodeResourceSegment(string value)
        {
            return Uri.EscapeDataString(value);
        }

        internal static string DecodeResourceSegment(string value, string parameterName)
        {
            try
            {
                var decoded = Uri.UnescapeDataString(value);
                if (string.IsNullOrWhiteSpace(decoded))
                {
                    throw new ArgumentException($"{parameterName} cannot be empty.", parameterName);
                }

                return decoded;
            }
            catch (UriFormatException ex)
            {
                throw new ArgumentException($"{parameterName} is not valid percent-encoded text.", parameterName, ex);
            }
        }

        internal static string DecodeResourceFilterSegment(string value, string parameterName, int maxDecodedChars)
        {
            if (value.Length > MaxResourceFilterSegmentChars)
            {
                throw new ArgumentException($"{parameterName} URI segment must be {MaxResourceFilterSegmentChars} characters or fewer.", parameterName);
            }

            var decoded = DecodeResourceSegment(value, parameterName);
            if (decoded.Length > maxDecodedChars)
            {
                throw new ArgumentException($"{parameterName} must be {maxDecodedChars} characters or fewer.", parameterName);
            }

            return decoded;
        }

    }
}
