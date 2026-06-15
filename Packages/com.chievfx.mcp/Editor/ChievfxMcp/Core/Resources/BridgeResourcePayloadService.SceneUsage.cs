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
        private static SceneUsageScan ScanCurrentSceneAssetUsage(GameObjectQueryContext context)
        {
            var scan = new SceneUsageScan
            {
                TotalObjects = context.Roots.Sum(GameObjectBridgeService.CountGameObjects)
            };

            foreach (var gameObject in GameObjectBridgeService.EnumerateContextGameObjects(context))
            {
                RecordPrefabInstanceUsage(scan, context, gameObject);
                var components = gameObject.GetComponents<Component>();
                var componentKeys = BuildComponentKeys(components);
                for (var i = 0; i < components.Length; i++)
                {
                    var component = components[i];
                    var componentKey = componentKeys[i];
                    if (component == null)
                    {
                        AddSkippedSceneUsageComponent(scan, gameObject, componentKey, "Missing script component.");
                        continue;
                    }

                    scan.TotalComponents++;
                    RecordSceneUsageFastPathReferences(scan, context, gameObject, component, componentKey);
                    RecordSceneUsageSerializedReferences(scan, context, gameObject, component, componentKey);
                }
            }

            RecordSceneUsageDependencies(scan, context);
            return scan;
        }

        private static void RecordPrefabInstanceUsage(SceneUsageScan scan, GameObjectQueryContext context, GameObject gameObject)
        {
            if (!PrefabUtility.IsPartOfPrefabInstance(gameObject))
            {
                return;
            }

            var nearestRoot = PrefabUtility.GetNearestPrefabInstanceRoot(gameObject);
            if (nearestRoot != gameObject)
            {
                return;
            }

            var source = PrefabUtility.GetCorrespondingObjectFromSource(gameObject);
            RecordSceneUsageReference(scan, context, gameObject, null, string.Empty, source, "prefabInstanceSource", "prefabInstance");
        }

        private static void RecordSceneUsageFastPathReferences(
            SceneUsageScan scan,
            GameObjectQueryContext context,
            GameObject gameObject,
            Component component,
            string componentKey)
        {
            if (component is Renderer renderer)
            {
                var materials = renderer.sharedMaterials;
                for (var i = 0; i < materials.Length; i++)
                {
                    RecordSceneUsageReference(
                        scan,
                        context,
                        gameObject,
                        component,
                        componentKey,
                        materials[i],
                        $"sharedMaterials[{i.ToString(CultureInfo.InvariantCulture)}]",
                        "fastPath");
                }
            }

            if (component is MeshFilter meshFilter)
            {
                RecordSceneUsageReference(scan, context, gameObject, component, componentKey, meshFilter.sharedMesh, "sharedMesh", "fastPath");
            }

            if (component is SkinnedMeshRenderer skinnedMeshRenderer)
            {
                RecordSceneUsageReference(scan, context, gameObject, component, componentKey, skinnedMeshRenderer.sharedMesh, "sharedMesh", "fastPath");
            }

            if (component is Camera camera)
            {
                RecordSceneUsageReference(scan, context, gameObject, component, componentKey, camera.targetTexture, "targetTexture", "fastPath");
            }
        }

        private static void RecordSceneUsageSerializedReferences(
            SceneUsageScan scan,
            GameObjectQueryContext context,
            GameObject gameObject,
            Component component,
            string componentKey)
        {
            try
            {
                var serializedObject = new SerializedObject(component);
                var iterator = serializedObject.GetIterator();
                var enterChildren = true;
                while (iterator.NextVisible(enterChildren))
                {
                    enterChildren = false;
                    if (iterator.propertyType != SerializedPropertyType.ObjectReference
                        && iterator.propertyType != SerializedPropertyType.ExposedReference)
                    {
                        continue;
                    }

                    if (ShouldSkipSceneUsageSerializedProperty(component, iterator.propertyPath))
                    {
                        continue;
                    }

                    var referencedObject = iterator.propertyType == SerializedPropertyType.ExposedReference
                        ? iterator.exposedReferenceValue
                        : iterator.objectReferenceValue;
                    RecordSceneUsageReference(
                        scan,
                        context,
                        gameObject,
                        component,
                        componentKey,
                        referencedObject,
                        iterator.propertyPath,
                        "serializedProperty");
                }
            }
            catch (Exception ex)
            {
                AddSkippedSceneUsageComponent(scan, gameObject, componentKey, ex.GetBaseException().Message);
            }
        }

        private static bool ShouldSkipSceneUsageSerializedProperty(Component component, string propertyPath)
        {
            if (string.Equals(propertyPath, "m_Script", StringComparison.Ordinal))
            {
                return true;
            }

            if (component is Renderer && propertyPath.StartsWith("m_Materials", StringComparison.Ordinal))
            {
                return true;
            }

            if ((component is MeshFilter || component is SkinnedMeshRenderer) && string.Equals(propertyPath, "m_Mesh", StringComparison.Ordinal))
            {
                return true;
            }

            return component is Camera && string.Equals(propertyPath, "m_TargetTexture", StringComparison.Ordinal);
        }

        private static void RecordSceneUsageDependencies(SceneUsageScan scan, GameObjectQueryContext context)
        {
            try
            {
                foreach (var dependency in EditorUtility.CollectDependencies(context.Roots))
                {
                    RecordSceneUsageDependencyObject(scan, dependency, loaded: true, saved: false);
                }
            }
            catch (Exception ex)
            {
                AddSceneUsageWarning(scan, $"Loaded dependency scan failed: {ex.GetBaseException().Message}");
            }

            var savedPath = string.Equals(context.Source, "prefabStage", StringComparison.Ordinal)
                ? context.PrefabAssetPath
                : context.ScenePath;
            if (string.IsNullOrWhiteSpace(savedPath))
            {
                return;
            }

            try
            {
                foreach (var dependencyPath in AssetDatabase.GetDependencies(savedPath, true))
                {
                    if (string.Equals(dependencyPath, savedPath, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var dependency = AssetDatabase.LoadMainAssetAtPath(dependencyPath);
                    RecordSceneUsageDependencyObject(scan, dependency, loaded: false, saved: true);
                }
            }
            catch (Exception ex)
            {
                AddSceneUsageWarning(scan, $"Saved dependency scan failed for '{savedPath}': {ex.GetBaseException().Message}");
            }
        }

        private static void RecordSceneUsageDependencyObject(SceneUsageScan scan, Object? unityObject, bool loaded, bool saved)
        {
            if (unityObject == null || !TryCreateSceneUsageAssetIdentity(unityObject, out var identity))
            {
                return;
            }

            var entry = GetOrCreateSceneUsageAssetEntry(scan, identity);
            entry.LoadedDependency |= loaded;
            entry.SavedDependency |= saved;
        }

        private static void RecordSceneUsageReference(
            SceneUsageScan scan,
            GameObjectQueryContext context,
            GameObject gameObject,
            Component? component,
            string componentKey,
            Object? referencedObject,
            string propertyPath,
            string source)
        {
            if (referencedObject == null || !TryCreateSceneUsageAssetIdentity(referencedObject, out var identity))
            {
                return;
            }

            var entry = GetOrCreateSceneUsageAssetEntry(scan, identity);
            entry.ReferenceCount++;
            entry.SourceReferenceCounts[source] = entry.SourceReferenceCounts.TryGetValue(source, out var sourceCount)
                ? sourceCount + 1
                : 1;
            entry.GameObjectIds.Add(GetLegacyInstanceId(gameObject));
            scan.TotalReferences++;
            if (entry.Locations.Count < HardSceneUsageLocationCap)
            {
                entry.Locations.Add(CreateSceneUsageLocation(context, gameObject, component, componentKey, propertyPath, source));
            }
        }

        private static SceneUsageLocation CreateSceneUsageLocation(
            GameObjectQueryContext context,
            GameObject gameObject,
            Component? component,
            string componentKey,
            string propertyPath,
            string source)
        {
            var gameObjectUri = GetGameObjectResourceUri(gameObject, context);
            return new SceneUsageLocation
            {
                GameObjectName = gameObject.name,
                GameObjectPath = GameObjectBridgeService.GetHierarchyPath(gameObject, context),
                GameObjectInstanceId = GetLegacyInstanceId(gameObject),
                GameObjectUri = gameObjectUri,
                ComponentKey = component != null ? componentKey : string.Empty,
                ComponentType = component != null ? component.GetType().Name : string.Empty,
                ComponentFullType = component != null ? component.GetType().FullName ?? component.GetType().Name : string.Empty,
                ComponentInstanceId = GetLegacyInstanceId(component),
                ComponentUri = component != null ? $"{gameObjectUri}/component/{EncodeResourceSegment(componentKey)}" : string.Empty,
                PropertyPath = propertyPath,
                Source = source
            };
        }

        private static bool TryCreateSceneUsageAssetIdentity(Object unityObject, out SceneUsageAssetIdentity identity)
        {
            identity = null!;
            var type = unityObject.GetType();
            if (unityObject is Component || unityObject is Transform || unityObject is MonoScript || unityObject is SceneAsset)
            {
                return false;
            }

            var path = AssetDatabase.GetAssetPath(unityObject) ?? string.Empty;
            var hasGuid = AssetDatabase.TryGetGUIDAndLocalFileIdentifier(unityObject, out string guid, out long localId)
                && !string.IsNullOrWhiteSpace(guid);
            if (hasGuid)
            {
                var guidPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrWhiteSpace(guidPath))
                {
                    path = guidPath;
                }
            }

            var builtIn = IsSceneUsageBuiltInAssetGuid(guid) || IsSceneUsageBuiltInAssetPath(path);
            var hasPersistedGuid = hasGuid && !builtIn;
            var persistent = hasPersistedGuid || builtIn || AssetDatabase.Contains(unityObject);
            var runtimeOnly = !persistent;
            if (!ShouldTrackSceneUsageObject(type, hasPersistedGuid, builtIn, runtimeOnly))
            {
                return false;
            }

            identity = new SceneUsageAssetIdentity
            {
                Key = hasPersistedGuid
                    ? $"{guid}:{localId.ToString(CultureInfo.InvariantCulture)}"
                    : $"{(builtIn ? "builtIn" : "runtime")}:{type.FullName}:{GetEntityIdText(unityObject)}",
                Guid = hasPersistedGuid ? guid : string.Empty,
                LocalId = hasPersistedGuid ? localId : null,
                Path = path,
                Name = unityObject.name,
                TypeName = type.Name,
                FullTypeName = type.FullName ?? type.Name,
                InstanceId = GetLegacyInstanceId(unityObject),
                UsageAssetType = GetSceneUsageAssetType(type),
                RuntimeOnly = runtimeOnly,
                BuiltIn = builtIn,
                IsMainAsset = hasPersistedGuid && AssetDatabase.IsMainAsset(unityObject),
                UnityObject = unityObject
            };
            return true;
        }

        private static bool ShouldTrackSceneUsageObject(Type type, bool hasGuid, bool builtIn, bool runtimeOnly)
        {
            if (typeof(RenderTexture).IsAssignableFrom(type)
                || typeof(Texture).IsAssignableFrom(type)
                || typeof(Material).IsAssignableFrom(type)
                || typeof(Mesh).IsAssignableFrom(type))
            {
                return true;
            }

            if (runtimeOnly)
            {
                return false;
            }

            if (typeof(GameObject).IsAssignableFrom(type)
                || typeof(Shader).IsAssignableFrom(type)
                || typeof(AnimationClip).IsAssignableFrom(type)
                || typeof(AudioClip).IsAssignableFrom(type)
                || typeof(ScriptableObject).IsAssignableFrom(type))
            {
                return true;
            }

            return hasGuid || builtIn;
        }

        private static bool IsSceneUsageBuiltInAssetPath(string path)
        {
            return !string.IsNullOrWhiteSpace(path)
                && (path.StartsWith("Resources/unity_builtin_extra", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("Library/unity default resources", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("Library/unity editor resources", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsSceneUsageBuiltInAssetGuid(string guid)
        {
            return string.Equals(guid, "0000000000000000e000000000000000", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetSceneUsageAssetType(Type type)
        {
            if (typeof(RenderTexture).IsAssignableFrom(type))
            {
                return "renderTexture";
            }

            if (typeof(Texture).IsAssignableFrom(type))
            {
                return "texture";
            }

            if (typeof(Material).IsAssignableFrom(type))
            {
                return "material";
            }

            if (typeof(Mesh).IsAssignableFrom(type))
            {
                return "mesh";
            }

            if (typeof(GameObject).IsAssignableFrom(type))
            {
                return "prefab";
            }

            if (typeof(Shader).IsAssignableFrom(type))
            {
                return "shader";
            }

            if (typeof(AnimationClip).IsAssignableFrom(type))
            {
                return "animationClip";
            }

            if (typeof(AudioClip).IsAssignableFrom(type))
            {
                return "audioClip";
            }

            if (typeof(ScriptableObject).IsAssignableFrom(type))
            {
                return "scriptableObject";
            }

            return "other";
        }

        private static SceneUsageAssetEntry GetOrCreateSceneUsageAssetEntry(SceneUsageScan scan, SceneUsageAssetIdentity identity)
        {
            if (scan.Assets.TryGetValue(identity.Key, out var entry))
            {
                if (entry.UnityObject == null)
                {
                    entry.UnityObject = identity.UnityObject;
                }

                return entry;
            }

            entry = new SceneUsageAssetEntry
            {
                Key = identity.Key,
                Guid = identity.Guid,
                LocalId = identity.LocalId,
                Path = identity.Path,
                Name = identity.Name,
                TypeName = identity.TypeName,
                FullTypeName = identity.FullTypeName,
                InstanceId = identity.InstanceId,
                UsageAssetType = identity.UsageAssetType,
                RuntimeOnly = identity.RuntimeOnly,
                BuiltIn = identity.BuiltIn,
                IsMainAsset = identity.IsMainAsset,
                UnityObject = identity.UnityObject
            };
            scan.Assets[identity.Key] = entry;
            return entry;
        }

        private static string NormalizeSceneUsageAssetType(string assetType)
        {
            var normalized = assetType.Trim();
            if (string.Equals(normalized, "all", StringComparison.OrdinalIgnoreCase))
            {
                return "all";
            }

            if (string.Equals(normalized, "material", StringComparison.OrdinalIgnoreCase))
            {
                return "material";
            }

            if (string.Equals(normalized, "mesh", StringComparison.OrdinalIgnoreCase))
            {
                return "mesh";
            }

            if (string.Equals(normalized, "texture", StringComparison.OrdinalIgnoreCase))
            {
                return "texture";
            }

            if (string.Equals(normalized, "renderTexture", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "rendertexture", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "rt", StringComparison.OrdinalIgnoreCase))
            {
                return "renderTexture";
            }

            throw new ArgumentException("assetType must be material, mesh, texture, renderTexture, or all.", nameof(assetType));
        }

        private static bool SceneUsageAssetTypeMatches(SceneUsageAssetEntry entry, string assetType)
        {
            return string.Equals(assetType, "all", StringComparison.Ordinal)
                || string.Equals(entry.UsageAssetType, assetType, StringComparison.Ordinal);
        }

        private static long? ParseSceneUsageLocalId(string? localIdText)
        {
            if (localIdText == null)
            {
                return null;
            }

            if (!long.TryParse(localIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var localId))
            {
                throw new ArgumentException("localId must be a long integer.", nameof(localIdText));
            }

            return localId;
        }

        private static Object? LoadSceneUsageAssetForDetail(string path, long? requestedLocalId, SceneUsageAssetEntry[] matches)
        {
            if (!requestedLocalId.HasValue)
            {
                return AssetDatabase.LoadMainAssetAtPath(path);
            }

            var asset = AssetDatabase.LoadAllAssetsAtPath(path)
                .FirstOrDefault(candidate => candidate != null && TryGetAssetLocalId(candidate, out var localId) && localId == requestedLocalId.Value);
            if (asset != null || matches.Length > 0)
            {
                return asset;
            }

            throw new InvalidOperationException($"Asset not found for localId '{requestedLocalId.Value}' at '{path}'.");
        }

        private static Dictionary<string, object?> CreateSceneUsageMissingAssetDto(string path, string guid, long? localId)
        {
            var output = new Dictionary<string, object?>
            {
                ["path"] = path,
                ["guid"] = guid,
                ["available"] = false
            };
            if (localId.HasValue)
            {
                output["localId"] = localId.Value;
            }

            return output;
        }

        private static Dictionary<string, object?> CreateSceneUsageCountRow(SceneUsageScan scan, string assetType)
        {
            var matches = scan.Assets.Values
                .Where(entry => SceneUsageAssetTypeMatches(entry, assetType))
                .ToArray();
            return new Dictionary<string, object?>
            {
                ["assetType"] = assetType,
                ["assetCount"] = matches.Length,
                ["referenceCount"] = matches.Sum(entry => entry.ReferenceCount),
                ["gameObjectCount"] = matches.SelectMany(entry => entry.GameObjectIds).Distinct().Count(),
                ["loadedDependencyCount"] = matches.Count(entry => entry.LoadedDependency),
                ["savedDependencyCount"] = matches.Count(entry => entry.SavedDependency),
                ["memoryEstimate"] = CreateMaterialProfileMemoryEstimate(matches
                    .Select(entry => entry.UnityObject)
                    .Where(unityObject => unityObject != null)
                    .Select(unityObject => unityObject!)),
                ["resourceUri"] = $"chievfx://scene/all/usage/assets/{assetType}"
            };
        }

        private static Dictionary<string, object?> CreateSceneUsageDependencyStats(SceneUsageScan scan)
        {
            return new Dictionary<string, object?>
            {
                ["loadedDependencyCount"] = scan.Assets.Values.Count(entry => entry.LoadedDependency),
                ["savedDependencyCount"] = scan.Assets.Values.Count(entry => entry.SavedDependency),
                ["dependencyOnlyCount"] = scan.Assets.Values.Count(entry => entry.ReferenceCount == 0 && (entry.LoadedDependency || entry.SavedDependency))
            };
        }

        private static void AddSceneUsageScanSummary(Dictionary<string, object?> result, SceneUsageScan scan, bool truncated)
        {
            result["totalReferences"] = scan.TotalReferences;
            result["totalObjects"] = scan.TotalObjects;
            result["totalComponents"] = scan.TotalComponents;
            result["totalAssets"] = scan.Assets.Count;
            result["truncated"] = truncated;
            result["scanWarnings"] = scan.ScanWarnings.ToArray();
            result["skippedComponents"] = scan.SkippedComponents.ToArray();
            result["skippedComponentCount"] = scan.SkippedComponentCount;
        }

        private static Dictionary<string, object?> CreateSceneUsageAssetRow(SceneUsageAssetEntry entry, bool includeSampleLocations)
        {
            var output = new Dictionary<string, object?>
            {
                ["name"] = entry.Name,
                ["assetType"] = entry.UsageAssetType,
                ["type"] = entry.TypeName,
                ["fullType"] = entry.FullTypeName,
                ["referenceCount"] = entry.ReferenceCount,
                ["gameObjectCount"] = entry.GameObjectIds.Count,
                ["runtimeOnly"] = entry.RuntimeOnly,
                ["builtIn"] = entry.BuiltIn,
                ["isMainAsset"] = entry.IsMainAsset,
                ["loadedDependency"] = entry.LoadedDependency,
                ["savedDependency"] = entry.SavedDependency,
                ["dependencyOnly"] = entry.ReferenceCount == 0 && (entry.LoadedDependency || entry.SavedDependency)
            };
            if (!string.IsNullOrWhiteSpace(entry.Guid))
            {
                output["guid"] = entry.Guid;
                output["localId"] = entry.LocalId;
                output["path"] = entry.Path;
                output["assetResourceUri"] = GetSceneUsageAssetDetailUri(entry);
                output["usageResourceUri"] = GetSceneUsageAssetUsageUri(entry);
            }
            else
            {
                output["instanceId"] = entry.InstanceId;
                if (!string.IsNullOrWhiteSpace(entry.Path))
                {
                    output["path"] = entry.Path;
                }
            }

            if (includeSampleLocations && entry.Locations.Count > 0)
            {
                output["sampleLocations"] = entry.Locations
                    .Take(MaxSceneUsageSampleLocations)
                    .Select(CreateSceneUsageLocationRow)
                    .ToArray();
                output["sampleLocationsTruncated"] = entry.ReferenceCount > MaxSceneUsageSampleLocations;
            }

            output["memoryEstimate"] = CreateMaterialProfileMemoryEstimate(entry.UnityObject);
            return output;
        }

    }
}
