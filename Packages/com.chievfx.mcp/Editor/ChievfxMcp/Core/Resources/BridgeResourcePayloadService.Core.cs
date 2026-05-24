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
    internal sealed partial class BridgeResourcePayloadService : BridgeDomainServiceBase
    {
        internal static object ReadEditorContextResource(string uri)
        {
            var activeScene = SceneManager.GetActiveScene();
            var stage = PrefabStageUtility.GetCurrentPrefabStage();

            var result = new Dictionary<string, object?>
            {
                ["context"] = "editor",
                ["unityVersion"] = Application.unityVersion,
                ["isPlaying"] = EditorApplication.isPlaying,
                ["isPaused"] = EditorApplication.isPaused,
                ["isCompiling"] = EditorApplication.isCompiling,
                ["isUpdating"] = EditorApplication.isUpdating,
                ["mode"] = stage == null ? "scene" : "prefabStage"
            };

            if (stage != null)
            {
                result["prefabStage"] = new Dictionary<string, object?>
                {
                    ["active"] = true,
                    ["assetPath"] = stage.assetPath,
                    ["hierarchyUri"] = "chievfx://scene/current/hierarchy"
                };
            }
            else
            {
                var sceneDto = SceneToResourceDto(activeScene);
                sceneDto["hierarchyUri"] = "chievfx://scene/current/hierarchy";
                result["scene"] = sceneDto;
            }

            var selectedGameObject = Selection.activeGameObject;
            if (selectedGameObject != null)
            {
                var selection = CreateSelectionResourceSummary(selectedGameObject);
                selection.Remove("name");
                result["selection"] = selection;
            }

            return result;
        }

        internal static object ReadOpenedScenesResource(string uri)
        {
            var activeScene = SceneManager.GetActiveScene();
            var scenes = SceneBridgeService.GetOpenScenes()
                .Select(SceneToResourceDto)
                .ToArray();
            var result = CreateResourceEnvelope(uri, new Dictionary<string, object?>
            {
                ["source"] = "editor",
                ["activeScenePath"] = activeScene.IsValid() ? activeScene.path : string.Empty
            });
            result["count"] = scenes.Length;
            result["scenes"] = scenes;
            return result;
        }

        internal static object ReadHierarchyResource(string uri, GameObjectQueryContext context, string? hierarchyPath = null, bool limitToTwoLevels = false)
        {
            var emitted = 0;
            var truncated = false;
            var depthLimited = false;
            var rootsToRead = context.Roots;
            if (!string.IsNullOrWhiteSpace(hierarchyPath))
            {
                var decodedHierarchyPath = BridgeResourcePayloadService.DecodeResourceSegment(hierarchyPath!, "hierarchyPath");
                rootsToRead = new[] { GameObjectBridgeService.ResolveGameObjectByPath(context, decodedHierarchyPath) };
            }

            var maxDepth = limitToTwoLevels ? (string.IsNullOrWhiteSpace(hierarchyPath) ? 1 : 3) : DefaultResourceMaxDepth;
            var maxResults = limitToTwoLevels ? int.MaxValue : DefaultResourceMaxResults;

            var totalObjects = rootsToRead.Sum(GameObjectBridgeService.CountGameObjects);
            var roots = new List<Dictionary<string, object?>>();
            foreach (var root in rootsToRead)
            {
                var node = BuildResourceHierarchyNode(
                    root,
                    context,
                    0,
                    maxDepth,
                    maxResults,
                    ref emitted,
                    ref truncated,
                    ref depthLimited);
                if (node != null)
                {
                    roots.Add(node);
                }

                if (truncated)
                {
                    break;
                }
            }

            var result = CreateResourceEnvelope(uri, CreateResourceContext(context));
            result["count"] = emitted;
            result["totalObjects"] = totalObjects;
            result["maxDepth"] = maxDepth;
            result["maxResults"] = maxResults;
            result["truncated"] = !limitToTwoLevels && truncated;
            result["depthLimited"] = limitToTwoLevels || depthLimited;
            result["roots"] = roots.ToArray();
            return result;
        }

        internal static object ReadGameObjectResource(string uri, GameObjectQueryContext context, GameObject gameObject)
        {
            var result = CreateResourceEnvelope(uri, CreateResourceContext(context));
            result["gameObject"] = CreateResourceGameObjectDetail(gameObject, context);
            return result;
        }

        internal static object ReadComponentResource(
            string uri,
            GameObjectQueryContext context,
            GameObject gameObject,
            Component component,
            string componentKey)
        {
            var serializedTruncated = false;
            var result = CreateResourceEnvelope(uri, CreateResourceContext(context));
            result["gameObject"] = CreateResourceGameObjectSummary(gameObject, context, includeComponents: false);
            result["component"] = CreateResourceComponentDetail(component, componentKey, context, ref serializedTruncated);
            result["serializedDataTruncated"] = serializedTruncated;
            return result;
        }

        internal static object ReadFilteredGameObjectsResource(string uri, GameObjectQueryContext context, ResourceGameObjectFilter filter)
        {
            var matches = GameObjectBridgeService.EnumerateContextGameObjects(context)
                .Where(gameObject => filter.IncludeInactive || gameObject.activeInHierarchy)
                .Where(gameObject => ResourceFilterNameMatches(gameObject.name, filter))
                .Where(gameObject => filter.ComponentTypes.Length == 0
                    || filter.ComponentTypes.Any(componentType => HasMatchingComponentForResourceFilter(gameObject, componentType)))
                .ToList();

            var selected = matches
                .Take(filter.MaxResults)
                .Select(gameObject => CreateResourceFilterGameObjectRow(gameObject, context))
                .ToArray();

            var result = CreateResourceEnvelope(uri, CreateResourceContext(context));
            result["filter"] = CreateResourceFilterDto(filter);
            result["count"] = selected.Length;
            result["totalMatches"] = matches.Count;
            result["maxResults"] = filter.MaxResults;
            result["truncated"] = matches.Count > selected.Length;
            result["objects"] = selected;
            return result;
        }

        internal static object ReadFilteredAssetsResource(string uri, ResourceAssetFilter filter)
        {
            var query = CreateAssetDatabaseFilterQuery(filter);
            var guids = filter.Folders.Length > 0
                ? AssetDatabase.FindAssets(query, filter.Folders)
                : AssetDatabase.FindAssets(query);
            var rows = new List<Dictionary<string, object?>>();
            var truncated = false;
            var processedGuidCount = 0;

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                foreach (var row in CreateAssetResourceRows(path, guid, filter.IncludeSubassets))
                {
                    if (rows.Count >= filter.MaxResults)
                    {
                        truncated = true;
                        break;
                    }

                    rows.Add(row);
                }

                if (truncated)
                {
                    break;
                }

                processedGuidCount++;
            }

            var result = CreateResourceEnvelope(uri, CreateAssetDatabaseResourceContext());
            result["filter"] = CreateResourceAssetFilterDto(filter);
            result["assetDatabaseFilter"] = query;
            result["folders"] = filter.Folders;
            result["count"] = rows.Count;
            result["totalAssetGuids"] = guids.Length;
            result["maxResults"] = filter.MaxResults;
            result["truncated"] = truncated || processedGuidCount < guids.Length;
            result["assets"] = rows.ToArray();
            return result;
        }

        internal static object ReadAssetDetailResource(string uri, string guid, string? localIdText)
        {
            ValidateAssetGuid(guid);
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException($"Asset GUID not found: '{guid}'.");
            }

            Object? asset;
            long? requestedLocalId = null;
            if (localIdText == null)
            {
                asset = AssetDatabase.LoadMainAssetAtPath(path);
            }
            else
            {
                if (!long.TryParse(localIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLocalId))
                {
                    throw new ArgumentException("localId must be a long integer.", nameof(localIdText));
                }

                requestedLocalId = parsedLocalId;
                asset = AssetDatabase.LoadAllAssetsAtPath(path)
                    .FirstOrDefault(candidate => candidate != null && TryGetAssetLocalId(candidate, out var localId) && localId == parsedLocalId);
            }

            if (asset == null)
            {
                var suffix = requestedLocalId.HasValue ? $" localId '{requestedLocalId.Value}'" : " main asset";
                throw new InvalidOperationException($"Asset not found for GUID '{guid}'{suffix} at '{path}'.");
            }

            var result = CreateResourceEnvelope(uri, CreateAssetDatabaseResourceContext());
            result["asset"] = CreateAssetDetail(asset, path, guid);
            result["importer"] = CreateAssetImporterDto(path);
            return result;
        }

        internal static object ReadSceneUsageCountsResource(string uri, GameObjectQueryContext context)
        {
            var scan = ScanCurrentSceneAssetUsage(context);
            var rows = new[]
            {
                CreateSceneUsageCountRow(scan, "all"),
                CreateSceneUsageCountRow(scan, "material"),
                CreateSceneUsageCountRow(scan, "mesh"),
                CreateSceneUsageCountRow(scan, "texture"),
                CreateSceneUsageCountRow(scan, "renderTexture")
            };

            var result = CreateResourceEnvelope(uri, CreateResourceContext(context));
            AddSceneUsageScanSummary(result, scan, truncated: false);
            result["counts"] = rows;
            result["dependencyStats"] = CreateSceneUsageDependencyStats(scan);
            return result;
        }

        internal static object ReadSceneUsageAssetsResource(string uri, GameObjectQueryContext context, string assetType)
        {
            var normalizedAssetType = NormalizeSceneUsageAssetType(assetType);
            var scan = ScanCurrentSceneAssetUsage(context);
            var matches = scan.Assets.Values
                .Where(entry => SceneUsageAssetTypeMatches(entry, normalizedAssetType))
                .OrderByDescending(entry => entry.ReferenceCount)
                .ThenBy(entry => entry.Path, StringComparer.Ordinal)
                .ThenBy(entry => entry.Name, StringComparer.Ordinal)
                .ToArray();
            var selected = matches
                .Take(DefaultResourceMaxResults)
                .Select(entry => CreateSceneUsageAssetRow(entry, includeSampleLocations: true))
                .ToArray();
            var truncated = selected.Length < matches.Length;

            var result = CreateResourceEnvelope(uri, CreateResourceContext(context));
            AddSceneUsageScanSummary(result, scan, truncated);
            result["assetType"] = normalizedAssetType;
            result["count"] = selected.Length;
            result["totalAssets"] = matches.Length;
            result["maxAssets"] = DefaultResourceMaxResults;
            result["assets"] = selected;
            return result;
        }

        internal static object ReadSceneUsageAssetResource(string uri, GameObjectQueryContext context, string guid, string? localIdText)
        {
            ValidateAssetGuid(guid);
            var requestedLocalId = ParseSceneUsageLocalId(localIdText);
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException($"Asset GUID not found: '{guid}'.");
            }

            var scan = ScanCurrentSceneAssetUsage(context);
            var matches = scan.Assets.Values
                .Where(entry => string.Equals(entry.Guid, guid, StringComparison.OrdinalIgnoreCase)
                    && (!requestedLocalId.HasValue || entry.LocalId == requestedLocalId.Value))
                .OrderByDescending(entry => entry.ReferenceCount)
                .ThenBy(entry => entry.LocalId ?? 0)
                .ToArray();
            var locations = matches
                .SelectMany(entry => entry.Locations.Select(CreateSceneUsageLocationRow))
                .Take(DefaultSceneUsageLocationCap)
                .ToArray();
            var totalLocations = matches.Sum(entry => entry.ReferenceCount);
            var truncated = totalLocations > locations.Length;
            var asset = LoadSceneUsageAssetForDetail(path, requestedLocalId, matches);

            var result = CreateResourceEnvelope(uri, CreateResourceContext(context));
            AddSceneUsageScanSummary(result, scan, truncated);
            result["asset"] = asset != null
                ? CreateAssetDetail(asset, path, guid)
                : CreateSceneUsageMissingAssetDto(path, guid, requestedLocalId);
            result["matchedAssetCount"] = matches.Length;
            result["matchedAssets"] = matches.Select(entry => CreateSceneUsageAssetRow(entry, includeSampleLocations: false)).ToArray();
            result["referenceCount"] = matches.Sum(entry => entry.ReferenceCount);
            result["gameObjectCount"] = matches.SelectMany(entry => entry.GameObjectIds).Distinct().Count();
            result["locationCount"] = locations.Length;
            result["totalLocations"] = totalLocations;
            result["maxLocations"] = DefaultSceneUsageLocationCap;
            result["hardMaxLocations"] = HardSceneUsageLocationCap;
            ApplySceneUsageLocationTextBudget(result, locations, totalLocations);
            return result;
        }

        internal static object ReadCurrentSceneMaterialProfileSummaryResource(string uri, GameObjectQueryContext context)
        {
            var profile = CreateCurrentSceneMaterialProfile(context);
            var shaderGroups = profile.ShaderGroups.Values
                .OrderByDescending(group => group.Materials.Count)
                .ThenBy(group => group.ShaderName, StringComparer.Ordinal)
                .Take(DefaultResourceMaxResults)
                .Select(CreateMaterialProfileShaderGroupRow)
                .ToArray();

            var result = CreateResourceEnvelope(uri, CreateResourceContext(context));
            AddSceneUsageScanSummary(result, profile.UsageScan, shaderGroups.Length < profile.ShaderGroups.Count);
            AddMaterialProfileSummary(result, profile);
            result["countByShader"] = shaderGroups;
            result["shaderGroupCount"] = shaderGroups.Length;
            result["totalShaderGroups"] = profile.ShaderGroups.Count;
            result["maxShaderGroups"] = DefaultResourceMaxResults;
            result["shaderGroupsTruncated"] = shaderGroups.Length < profile.ShaderGroups.Count;
            result["memoryEstimates"] = CreateMaterialProfileMemoryEstimate(profile.Materials.Select(entry => entry.Material).Where(material => material != null).Select(material => material!));
            result["textureLinks"] = CreateMaterialProfileTextureLinks(profile.Materials.Select(entry => entry.Material), DefaultMaterialProfileTextureLinkCap, out var textureLinksTruncated);
            result["textureLinksTruncated"] = textureLinksTruncated;
            result["maxTextureLinks"] = DefaultMaterialProfileTextureLinkCap;
            result["outputCaps"] = CreateMaterialProfileOutputCaps();
            return result;
        }

        internal static object ReadCurrentSceneMaterialProfileShaderResource(string uri, GameObjectQueryContext context, string shaderKey)
        {
            var profile = CreateCurrentSceneMaterialProfile(context);
            if (!profile.ShaderGroups.TryGetValue(shaderKey, out var group))
            {
                throw new InvalidOperationException($"No material shader group found for key '{shaderKey}'.");
            }

            var materials = group.Materials
                .OrderByDescending(entry => entry.SceneUsage.ReferenceCount)
                .ThenBy(entry => entry.SceneUsage.Name, StringComparer.Ordinal)
                .Take(DefaultResourceMaxResults)
                .Select(CreateMaterialProfileMaterialRow)
                .ToArray();
            var result = CreateResourceEnvelope(uri, CreateResourceContext(context));
            AddSceneUsageScanSummary(result, profile.UsageScan, materials.Length < group.Materials.Count);
            result["shader"] = CreateMaterialProfileShaderGroupRow(group);
            result["materialCount"] = materials.Length;
            result["totalMaterials"] = group.Materials.Count;
            result["maxMaterials"] = DefaultResourceMaxResults;
            result["materialsTruncated"] = materials.Length < group.Materials.Count;
            result["materials"] = materials;
            result["textureLinks"] = CreateMaterialProfileTextureLinks(group.Materials.Select(entry => entry.Material), DefaultMaterialProfileTextureLinkCap, out var textureLinksTruncated);
            result["textureLinksTruncated"] = textureLinksTruncated;
            result["maxTextureLinks"] = DefaultMaterialProfileTextureLinkCap;
            result["outputCaps"] = CreateMaterialProfileOutputCaps();
            return result;
        }

        internal static object ReadCurrentSceneMaterialProfileMaterialResource(string uri, GameObjectQueryContext context, string materialKey)
        {
            var profile = CreateCurrentSceneMaterialProfile(context);
            var material = profile.Materials.FirstOrDefault(entry => string.Equals(entry.SceneUsage.Key, materialKey, StringComparison.Ordinal));
            if (material == null)
            {
                throw new InvalidOperationException($"No material found for key '{materialKey}'.");
            }

            var locations = material.SceneUsage.Locations
                .Take(DefaultMaterialProfileLocationCap)
                .Select(CreateSceneUsageLocationRow)
                .ToArray();
            var result = CreateResourceEnvelope(uri, CreateResourceContext(context));
            AddSceneUsageScanSummary(result, profile.UsageScan, material.SceneUsage.ReferenceCount > locations.Length);
            result["material"] = CreateMaterialProfileMaterialRow(material);
            result["shader"] = CreateMaterialProfileShaderDto(material);
            result["textureLinks"] = CreateMaterialProfileTextureLinks(new[] { material.Material }, DefaultMaterialProfileTextureLinkCap, out var textureLinksTruncated);
            result["textureLinksTruncated"] = textureLinksTruncated;
            result["maxTextureLinks"] = DefaultMaterialProfileTextureLinkCap;
            result["locations"] = locations;
            result["locationCount"] = locations.Length;
            result["totalLocations"] = material.SceneUsage.ReferenceCount;
            result["maxLocations"] = DefaultMaterialProfileLocationCap;
            result["locationsTruncated"] = material.SceneUsage.ReferenceCount > locations.Length;
            result["outputCaps"] = CreateMaterialProfileOutputCaps();
            return result;
        }

    }
}
