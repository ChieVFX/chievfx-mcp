#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Chievfx.Mcp.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using PackageManagerPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Chievfx.Mcp.Extensions.Ecs
{
    [InitializeOnLoad]
    internal static class ChievfxMcpEcsExtension
    {
        private const string ExtensionId = "chievfx.ecs";
        private const string UriPrefix = "chievfx://extensions/chievfx.ecs/";
        private const string WorldsUri = UriPrefix + "worlds";
        private const string SystemsUri = UriPrefix + "systems";
        private const string EntitiesQueryPrefix = UriPrefix + "entities/query/";
        private const string SubScenesUri = UriPrefix + "subscenes";
        private const string SubSceneDetailPrefix = UriPrefix + "subscene/";
        private const int MaxWorldRows = 16;
        private const int MaxSystemRows = 128;
        private const int MaxSubSceneRows = 64;
        private const int MaxComponentMatches = 16;
        private const string EcsCategory = "ecs";
        private const string MinimumEntitiesPackageVersion = "1.0.0";

#if CHIEVFX_MCP_HAS_ENTITIES
        private const bool EntitiesVersionDefineActive = true;
#else
        private const bool EntitiesVersionDefineActive = false;
#endif

        static ChievfxMcpEcsExtension()
        {
            ChievfxMcpExtensionRegistry.RegisterExtension(CreateDescriptor());
        }

        private static ChievfxMcpExtensionDescriptor CreateDescriptor()
        {
            var status = GetDependencyStatus();
            var descriptor = new ChievfxMcpExtensionDescriptor
            {
                Id = ExtensionId,
                DisplayName = "ChievFX MCP ECS",
                Version = "0.1.0",
                Description = status.Available
                    ? "First-party optional ECS/SubScene inspection extension for ChievFX MCP."
                    : "First-party optional ECS/SubScene inspection extension unavailable until com.unity.entities is installed and loaded.",
                ResourceReader = ReadResource,
            };

            if (!status.Available)
            {
                return descriptor;
            }

            descriptor.Prompts.Add(
                new ChievfxMcpPromptDescriptor
                {
                    Name = "ecs-inspection-plan",
                    Title = "Plan ECS inspection",
                    Description = "Guidance for read-only ECS inspection using ChievFX MCP resources.",
                    Category = EcsCategory,
                    Arguments = new JArray
                    {
                        new JObject
                        {
                            ["name"] = "goal",
                            ["description"] = "Optional ECS question or workflow goal.",
                            ["required"] = false,
                        },
                    },
                    StaticText = "Use read-only ECS resources only. Start with chievfx://extensions/chievfx.ecs/worlds, then systems, entities/query/all, and SubScene resources as needed. Use package-list for package availability. Goal: {goal}",
                });

            descriptor.Prompts.Add(
                new ChievfxMcpPromptDescriptor
                {
                    Name = "ecs-subscene-review",
                    Title = "Review ECS SubScenes",
                    Description = "Guidance for inspecting current scene SubScenes without mutation.",
                    Category = EcsCategory,
                    Arguments = new JArray
                    {
                        new JObject
                        {
                            ["name"] = "focus",
                            ["description"] = "Optional SubScene concern such as loading, scene asset links, or baking setup.",
                            ["required"] = false,
                        },
                    },
                    StaticText = "Inspect current scene SubScenes read-only. Read chievfx://extensions/chievfx.ecs/subscenes, then follow each detailUri for a specific SubScene. Focus: {focus}",
                });

            descriptor.Resources.Add(
                new ChievfxMcpResourceDescriptor
                {
                    Id = "ecs-worlds-list",
                    Uri = WorldsUri,
                    Name = "ECS worlds list",
                    Description = "Compact list of loaded ECS worlds with entity counts and drill-down URIs.",
                    MimeType = "application/json",
                    Category = EcsCategory,
                });
            descriptor.Resources.Add(
                new ChievfxMcpResourceDescriptor
                {
                    Id = "ecs-systems-summary",
                    Uri = SystemsUri,
                    Name = "ECS systems summary",
                    Description = "Capped summary of managed ECS systems grouped by loaded world.",
                    MimeType = "application/json",
                    Category = EcsCategory,
                });
            descriptor.Resources.Add(
                new ChievfxMcpResourceDescriptor
                {
                    Id = "ecs-subscenes-current",
                    Uri = SubScenesUri,
                    Name = "Current scene SubScenes",
                    Description = "Compact read-only summary of SubScene components in the active scene.",
                    MimeType = "application/json",
                    Category = EcsCategory,
                });
            descriptor.ResourceTemplates.Add(
                new ChievfxMcpResourceTemplateDescriptor
                {
                    Id = "ecs-entities-query-summary",
                    UriTemplate = EntitiesQueryPrefix + "{querySpec}",
                    Name = "ECS entities query summary",
                    Description = "Counts entities per world for 'all' or comma-separated component type names.",
                    MimeType = "application/json",
                    Category = EcsCategory,
                });
            descriptor.ResourceTemplates.Add(
                new ChievfxMcpResourceTemplateDescriptor
                {
                    Id = "ecs-subscene-detail",
                    UriTemplate = SubSceneDetailPrefix + "{guidOrPath}",
                    Name = "SubScene detail",
                    Description = "Read-only SubScene detail by scene GUID or URL-encoded asset path.",
                    MimeType = "application/json",
                    Category = EcsCategory,
                });

            return descriptor;
        }

        private static object? ReadResource(string uri)
        {
            var status = GetDependencyStatus();
            if (!status.Available)
            {
                return CreateUnavailableResource(uri, status);
            }

            if (string.Equals(uri, WorldsUri, StringComparison.Ordinal))
            {
                return ReadWorldsResource(uri, status);
            }

            if (string.Equals(uri, SystemsUri, StringComparison.Ordinal))
            {
                return ReadSystemsResource(uri, status);
            }

            if (string.Equals(uri, SubScenesUri, StringComparison.Ordinal))
            {
                return ReadSubScenesResource(uri, status);
            }

            if (uri.StartsWith(EntitiesQueryPrefix, StringComparison.Ordinal))
            {
                return ReadEntitiesQueryResource(uri, DecodeSegment(uri.Substring(EntitiesQueryPrefix.Length)), status);
            }

            if (uri.StartsWith(SubSceneDetailPrefix, StringComparison.Ordinal))
            {
                return ReadSubSceneDetailResource(uri, DecodeSegment(uri.Substring(SubSceneDetailPrefix.Length)), status);
            }

            return CreateUnavailableResource(uri, status, "Unsupported ECS extension resource URI.");
        }

        private static Dictionary<string, object?> ReadWorldsResource(string uri, EcsDependencyStatus status)
        {
            var worlds = GetWorlds().ToList();
            var rows = worlds
                .Take(MaxWorldRows)
                .Select(CreateWorldRow)
                .ToArray();

            var result = CreateEnvelope(uri, status);
            result["count"] = rows.Length;
            result["totalWorlds"] = worlds.Count;
            result["maxResults"] = MaxWorldRows;
            result["truncated"] = worlds.Count > rows.Length;
            result["worlds"] = rows;
            return result;
        }

        private static Dictionary<string, object?> ReadSystemsResource(string uri, EcsDependencyStatus status)
        {
            var worlds = GetWorlds().ToList();
            var emitted = 0;
            var truncated = false;
            var worldRows = new List<Dictionary<string, object?>>();
            foreach (var world in worlds.Take(MaxWorldRows))
            {
                var systemRows = new List<Dictionary<string, object?>>();
                foreach (var system in GetWorldSystems(world))
                {
                    if (emitted >= MaxSystemRows)
                    {
                        truncated = true;
                        break;
                    }

                    systemRows.Add(CreateSystemRow(system));
                    emitted++;
                }

                worldRows.Add(new Dictionary<string, object?>
                {
                    ["world"] = ReadStringProperty(world, "Name") ?? "(unnamed)",
                    ["systemCount"] = systemRows.Count,
                    ["systems"] = systemRows,
                });

                if (truncated)
                {
                    break;
                }
            }

            var result = CreateEnvelope(uri, status);
            result["count"] = emitted;
            result["worldCount"] = worldRows.Count;
            result["totalWorlds"] = worlds.Count;
            result["maxResults"] = MaxSystemRows;
            result["truncated"] = truncated || worlds.Count > worldRows.Count;
            result["worlds"] = worldRows;
            return result;
        }

        private static Dictionary<string, object?> ReadEntitiesQueryResource(string uri, string querySpec, EcsDependencyStatus status)
        {
            var normalizedQuery = string.IsNullOrWhiteSpace(querySpec) ? "all" : querySpec.Trim();
            var allEntities = string.Equals(normalizedQuery, "all", StringComparison.OrdinalIgnoreCase);
            var requestedComponents = allEntities
                ? Array.Empty<string>()
                : normalizedQuery.Split(new[] { ',', ';', '+' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(item => item.Trim())
                    .Where(item => item.Length > 0)
                    .Take(MaxComponentMatches)
                    .ToArray();
            var resolvedComponents = ResolveComponentTypes(requestedComponents);
            var componentTypes = resolvedComponents
                .Where(item => item.Type != null)
                .Select(item => item.Type!)
                .ToArray();
            var unresolved = resolvedComponents
                .Where(item => item.Type == null)
                .Select(item => item.Query)
                .ToArray();

            var worlds = GetWorlds().ToList();
            var rows = worlds
                .Take(MaxWorldRows)
                .Select(world =>
                {
                    var entityManager = ReadProperty(world, "EntityManager");
                    var count = entityManager == null || unresolved.Length > 0
                        ? null
                        : CountEntities(entityManager, componentTypes);
                    return new Dictionary<string, object?>
                    {
                        ["world"] = ReadStringProperty(world, "Name") ?? "(unnamed)",
                        ["entityCount"] = count,
                        ["countUnavailable"] = count == null,
                    };
                })
                .ToArray();

            var result = CreateEnvelope(uri, status);
            result["querySpec"] = normalizedQuery;
            result["componentQueries"] = requestedComponents;
            result["resolvedComponents"] = resolvedComponents
                .Where(item => item.Type != null)
                .Select(item => item.Type!.FullName)
                .ToArray();
            result["unresolvedComponents"] = unresolved;
            result["count"] = rows.Length;
            result["totalWorlds"] = worlds.Count;
            result["maxResults"] = MaxWorldRows;
            result["truncated"] = worlds.Count > rows.Length;
            result["worlds"] = rows;
            return result;
        }

        private static Dictionary<string, object?> ReadSubScenesResource(string uri, EcsDependencyStatus status)
        {
            var subSceneType = FindType("Unity.Scenes.SubScene");
            var activeScene = SceneManager.GetActiveScene();
            var rows = subSceneType == null
                ? new List<Dictionary<string, object?>>()
                : FindSubScenes(activeScene, subSceneType)
                    .Take(MaxSubSceneRows)
                    .Select(component => CreateSubSceneRow(component, activeScene.path))
                    .ToList();

            var result = CreateEnvelope(uri, status);
            result["activeScene"] = SceneToRow(activeScene);
            result["subSceneTypeLoaded"] = subSceneType != null;
            result["count"] = rows.Count;
            result["maxResults"] = MaxSubSceneRows;
            result["truncated"] = subSceneType != null && FindSubScenes(activeScene, subSceneType).Skip(MaxSubSceneRows).Any();
            result["subScenes"] = rows;
            return result;
        }

        private static Dictionary<string, object?> ReadSubSceneDetailResource(string uri, string guidOrPath, EcsDependencyStatus status)
        {
            var subSceneType = FindType("Unity.Scenes.SubScene");
            var activeScene = SceneManager.GetActiveScene();
            var rows = subSceneType == null
                ? new List<Dictionary<string, object?>>()
                : FindSubScenes(activeScene, subSceneType)
                    .Select(component => CreateSubSceneRow(component, activeScene.path))
                    .ToList();
            var match = rows.FirstOrDefault(row =>
                string.Equals(ReadRowString(row, "sceneGuid"), guidOrPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ReadRowString(row, "sceneAssetPath"), guidOrPath, StringComparison.Ordinal));

            var assetPath = IsGuid(guidOrPath) ? AssetDatabase.GUIDToAssetPath(guidOrPath) : guidOrPath;
            var guid = IsGuid(guidOrPath) ? guidOrPath : AssetDatabase.AssetPathToGUID(guidOrPath);
            var result = CreateEnvelope(uri, status);
            result["requested"] = guidOrPath;
            result["activeScene"] = SceneToRow(activeScene);
            result["openInActiveScene"] = match != null;
            result["subScene"] = match;
            result["asset"] = new Dictionary<string, object?>
            {
                ["path"] = assetPath,
                ["guid"] = guid,
                ["exists"] = !string.IsNullOrWhiteSpace(assetPath) && AssetDatabase.LoadMainAssetAtPath(assetPath) != null,
            };
            return result;
        }

        private static Dictionary<string, object?> CreateWorldRow(object world)
        {
            var entityManager = ReadProperty(world, "EntityManager");
            var worldName = ReadStringProperty(world, "Name") ?? "(unnamed)";
            return new Dictionary<string, object?>
            {
                ["name"] = worldName,
                ["isCreated"] = ReadBoolProperty(world, "IsCreated"),
                ["entityCount"] = entityManager == null ? null : CountEntities(entityManager, Array.Empty<Type>()),
                ["systemCount"] = GetWorldSystems(world).Count(),
                ["systemsUri"] = SystemsUri,
                ["allEntitiesQueryUri"] = EntitiesQueryPrefix + "all",
            };
        }

        private static Dictionary<string, object?> CreateSystemRow(object system)
        {
            var type = system.GetType();
            return new Dictionary<string, object?>
            {
                ["name"] = type.Name,
                ["fullName"] = type.FullName,
                ["namespace"] = type.Namespace,
                ["enabled"] = ReadBoolProperty(system, "Enabled"),
            };
        }

        private static Dictionary<string, object?> CreateSubSceneRow(Component component, string scenePath)
        {
            var sceneAsset = ReadProperty(component, "SceneAsset") as UnityEngine.Object;
            var assetPath = sceneAsset == null ? string.Empty : AssetDatabase.GetAssetPath(sceneAsset);
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                assetPath = ReadStringProperty(component, "ScenePath") ?? ReadStringProperty(component, "EditScenePath") ?? string.Empty;
            }

            var guid = ReadStringProperty(component, "SceneGUID");
            if (string.IsNullOrWhiteSpace(guid) && !string.IsNullOrWhiteSpace(assetPath))
            {
                guid = AssetDatabase.AssetPathToGUID(assetPath);
            }

            var detailKey = !string.IsNullOrWhiteSpace(guid) ? guid! : assetPath;
            return new Dictionary<string, object?>
            {
                ["name"] = component.gameObject.name,
                ["gameObjectPath"] = GetTransformPath(component.transform),
                ["hostScenePath"] = scenePath,
                ["sceneAssetPath"] = assetPath,
                ["sceneGuid"] = guid ?? string.Empty,
                ["autoLoadScene"] = ReadBoolProperty(component, "AutoLoadScene"),
                ["isLoaded"] = ReadBoolProperty(component, "IsLoaded"),
                ["detailUri"] = SubSceneDetailPrefix + EncodeSegment(detailKey),
            };
        }

        private static Dictionary<string, object?> CreateUnavailableResource(string uri, EcsDependencyStatus status, string? reason = null)
        {
            var result = CreateEnvelope(uri, status);
            result["available"] = false;
            result["dependencyReason"] = reason ?? status.Reason;
            return result;
        }

        private static Dictionary<string, object?> CreateEnvelope(string uri, EcsDependencyStatus status)
        {
            return new Dictionary<string, object?>
            {
                ["uri"] = uri,
                ["readAtUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["extensionId"] = ExtensionId,
                ["dependency"] = status.ToDictionary(),
            };
        }

        private static EcsDependencyStatus GetDependencyStatus()
        {
            var entitiesAssembly = FindAssembly("Unity.Entities");
            var worldType = FindType("Unity.Entities.World");
            var packageInfo = TryFindPackageInfo();
            var available = EntitiesVersionDefineActive && worldType != null;
            var reason = available
                ? $"com.unity.entities {MinimumEntitiesPackageVersion}+ is installed and Unity.Entities.World is loaded."
                : !EntitiesVersionDefineActive
                    ? $"com.unity.entities {MinimumEntitiesPackageVersion}+ is not installed or does not meet the required version; ECS/SubScene resources are unavailable."
                    : "com.unity.entities is installed but not compiled or Unity.Entities.World is not loaded; ECS/SubScene resources are unavailable.";

            return new EcsDependencyStatus(
                available,
                reason,
                packageInfo?.name ?? "com.unity.entities",
                packageInfo?.version ?? string.Empty,
                packageInfo?.source.ToString() ?? string.Empty,
                entitiesAssembly != null,
                EntitiesVersionDefineActive);
        }

        private static PackageManagerPackageInfo? TryFindPackageInfo()
        {
            try
            {
                return PackageManagerPackageInfo.FindForAssetPath("Packages/com.unity.entities/package.json");
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<object> GetWorlds()
        {
            var worldType = FindType("Unity.Entities.World");
            if (worldType == null)
            {
                yield break;
            }

            foreach (var world in SnapshotReflectedCollection(ReadStaticProperty(worldType, "All")))
            {
                if (world != null)
                {
                    yield return world;
                }
            }
        }

        private static IEnumerable<object> GetWorldSystems(object world)
        {
            var systems = ReadProperty(world, "Systems");
            if (systems == null)
            {
                var method = world.GetType().GetMethod("GetExistingSystems", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                systems = method?.Invoke(world, null);
            }

            foreach (var system in SnapshotReflectedCollection(systems))
            {
                if (system != null)
                {
                    yield return system;
                }
            }
        }

        private static IReadOnlyList<object> SnapshotReflectedCollection(object? source)
        {
            var items = new List<object>();
            if (source == null)
            {
                return items;
            }

            var count = ReadIntProperty(source, "Count");
            var indexer = source.GetType().GetProperty(
                "Item",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                null,
                new[] { typeof(int) },
                null);
            if (count is >= 0 && indexer != null)
            {
                for (var i = 0; i < count.Value; i++)
                {
                    try
                    {
                        var value = indexer.GetValue(source, new object[] { i });
                        if (value != null)
                        {
                            items.Add(value);
                        }
                    }
                    catch
                    {
                        break;
                    }
                }

                return items;
            }

            try
            {
                if (source is IEnumerable enumerable)
                {
                    foreach (var value in enumerable)
                    {
                        if (value != null)
                        {
                            items.Add(value);
                        }
                    }
                }
            }
            catch
            {
                // Unity.Entities.NoAllocReadOnlyCollection throws if boxed as IEnumerable; indexed access above handles it.
            }

            return items;
        }

        private static int? CountEntities(object entityManager, Type[] componentTypes)
        {
            if (componentTypes.Length == 0)
            {
                var universalQuery = ReadProperty(entityManager, "UniversalQuery");
                if (TryCalculateEntityCount(universalQuery, out var universalCount))
                {
                    return universalCount;
                }

                return CountAllEntitiesViaNativeArray(entityManager);
            }

            var query = CreateEntityQuery(entityManager, componentTypes);
            try
            {
                return TryCalculateEntityCount(query, out var count) ? count : null;
            }
            finally
            {
                DisposeReflected(query);
            }
        }

        private static object? CreateEntityQuery(object entityManager, Type[] componentTypes)
        {
            try
            {
                var componentType = FindType("Unity.Entities.ComponentType");
                if (componentType == null)
                {
                    return null;
                }

                var readOnly = componentType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(method =>
                    {
                        var parameters = method.GetParameters();
                        return method.Name == "ReadOnly"
                            && parameters.Length == 1
                            && parameters[0].ParameterType == typeof(Type);
                    });
                if (readOnly == null)
                {
                    return null;
                }

                var componentTypeArray = Array.CreateInstance(componentType, componentTypes.Length);
                for (var i = 0; i < componentTypes.Length; i++)
                {
                    componentTypeArray.SetValue(readOnly.Invoke(null, new object[] { componentTypes[i] }), i);
                }

                var createQuery = entityManager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method =>
                    {
                        var parameters = method.GetParameters();
                        return method.Name == "CreateEntityQuery"
                            && parameters.Length == 1
                            && parameters[0].ParameterType.IsArray
                            && parameters[0].ParameterType.GetElementType() == componentType;
                    });
                return createQuery?.Invoke(entityManager, new object[] { componentTypeArray });
            }
            catch
            {
                return null;
            }
        }

        private static int? CountAllEntitiesViaNativeArray(object entityManager)
        {
            try
            {
                var allocatorType = FindType("Unity.Collections.Allocator");
                if (allocatorType == null)
                {
                    return null;
                }

                var getAllEntities = entityManager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method =>
                    {
                        var parameters = method.GetParameters();
                        return method.Name == "GetAllEntities"
                            && parameters.Length == 1
                            && parameters[0].ParameterType == allocatorType;
                    });
                if (getAllEntities == null)
                {
                    return null;
                }

                var allocator = Enum.Parse(allocatorType, "Temp");
                var entities = getAllEntities.Invoke(entityManager, new[] { allocator });
                try
                {
                    return ReadIntProperty(entities, "Length");
                }
                finally
                {
                    DisposeReflected(entities);
                }
            }
            catch
            {
                return null;
            }
        }

        private static bool TryCalculateEntityCount(object? query, out int count)
        {
            count = 0;
            if (query == null)
            {
                return false;
            }

            try
            {
                var method = query.GetType().GetMethod("CalculateEntityCount", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (method?.Invoke(query, null) is int value)
                {
                    count = value;
                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static Component[] FindSubScenes(Scene scene, Type subSceneType)
        {
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return Array.Empty<Component>();
            }

            return scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Component>(true))
                .Where(component => component != null && subSceneType.IsInstanceOfType(component))
                .ToArray();
        }

        private static ComponentResolution[] ResolveComponentTypes(string[] queries)
        {
            if (queries.Length == 0)
            {
                return Array.Empty<ComponentResolution>();
            }

            var markerTypes = new[]
            {
                FindType("Unity.Entities.IComponentData"),
                FindType("Unity.Entities.IBufferElementData"),
                FindType("Unity.Entities.ISharedComponentData"),
            }.Where(type => type != null).ToArray();
            var componentTypes = GetAllLoadedTypes()
                .Where(type => !type.IsAbstract && !type.ContainsGenericParameters)
                .Where(type => markerTypes.Any(marker => marker!.IsAssignableFrom(type)))
                .ToArray();

            return queries
                .Select(query => new ComponentResolution(query, FindComponentType(componentTypes, query)))
                .ToArray();
        }

        private static Type? FindComponentType(Type[] componentTypes, string query)
        {
            return componentTypes.FirstOrDefault(type => string.Equals(type.FullName, query, StringComparison.Ordinal))
                ?? componentTypes.FirstOrDefault(type => string.Equals(type.Name, query, StringComparison.Ordinal))
                ?? componentTypes.FirstOrDefault(type => type.FullName?.EndsWith("." + query, StringComparison.Ordinal) == true)
                ?? componentTypes.FirstOrDefault(type => type.FullName?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static IEnumerable<Type> GetAllLoadedTypes()
        {
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
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    yield return type;
                }
            }
        }

        private static object? ReadProperty(object? source, string propertyName)
        {
            if (source == null)
            {
                return null;
            }

            try
            {
                return source.GetType()
                    .GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(source);
            }
            catch
            {
                return null;
            }
        }

        private static object? ReadStaticProperty(Type type, string propertyName)
        {
            try
            {
                return type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            }
            catch
            {
                return null;
            }
        }

        private static string? ReadStringProperty(object source, string propertyName)
        {
            var value = ReadProperty(source, propertyName);
            return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string ReadRowString(IReadOnlyDictionary<string, object?> row, string key)
        {
            return row.TryGetValue(key, out var value) && value != null
                ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
                : string.Empty;
        }

        private static bool? ReadBoolProperty(object source, string propertyName)
        {
            var value = ReadProperty(source, propertyName);
            return value == null ? null : Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }

        private static int? ReadIntProperty(object? source, string propertyName)
        {
            var value = ReadProperty(source, propertyName);
            return value == null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private static void DisposeReflected(object? source)
        {
            try
            {
                source?.GetType().GetMethod("Dispose", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)?.Invoke(source, null);
            }
            catch
            {
                // Best effort for reflected native containers / EntityQuery values.
            }
        }

        private static Assembly? FindAssembly(string assemblyName)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, assemblyName, StringComparison.Ordinal));
        }

        private static Type? FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(fullName, throwOnError: false);
                    if (type != null)
                    {
                        return type;
                    }
                }
                catch
                {
                    continue;
                }
            }

            return Type.GetType(fullName, throwOnError: false);
        }

        private static Dictionary<string, object?> SceneToRow(Scene scene)
        {
            return new Dictionary<string, object?>
            {
                ["name"] = scene.name,
                ["path"] = scene.path,
                ["isLoaded"] = scene.isLoaded,
                ["isValid"] = scene.IsValid(),
                ["rootCount"] = scene.IsValid() && scene.isLoaded ? scene.rootCount : 0,
            };
        }

        private static string GetTransformPath(Transform transform)
        {
            var names = new Stack<string>();
            var current = transform;
            while (current != null)
            {
                names.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", names);
        }

        private static string EncodeSegment(string value)
        {
            return Uri.EscapeDataString(value ?? string.Empty);
        }

        private static string DecodeSegment(string value)
        {
            return Uri.UnescapeDataString(value ?? string.Empty);
        }

        private static bool IsGuid(string value)
        {
            return Regex.IsMatch(value ?? string.Empty, "^[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant);
        }

        private readonly struct ComponentResolution
        {
            public ComponentResolution(string query, Type? type)
            {
                Query = query;
                Type = type;
            }

            public string Query { get; }

            public Type? Type { get; }
        }

        private sealed class EcsDependencyStatus
        {
            public EcsDependencyStatus(
                bool available,
                string reason,
                string packageName,
                string packageVersion,
                string packageSource,
                bool entitiesAssemblyLoaded,
                bool versionDefineActive)
            {
                Available = available;
                Reason = reason;
                PackageName = packageName;
                PackageVersion = packageVersion;
                PackageSource = packageSource;
                EntitiesAssemblyLoaded = entitiesAssemblyLoaded;
                VersionDefineActive = versionDefineActive;
            }

            public bool Available { get; }

            public string Reason { get; }

            public string PackageName { get; }

            public string PackageVersion { get; }

            public string PackageSource { get; }

            public bool EntitiesAssemblyLoaded { get; }

            public bool VersionDefineActive { get; }

            public Dictionary<string, object?> ToDictionary()
            {
                return new Dictionary<string, object?>
                {
                    ["packageName"] = PackageName,
                    ["packageVersion"] = PackageVersion,
                    ["packageSource"] = PackageSource,
                    ["entitiesAssemblyLoaded"] = EntitiesAssemblyLoaded,
                    ["versionDefineActive"] = VersionDefineActive,
                    ["available"] = Available,
                    ["reason"] = Reason,
                };
            }
        }
    }
}
