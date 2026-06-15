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
        private static MaterialProfile CreateCurrentSceneMaterialProfile(GameObjectQueryContext context)
        {
            var usageScan = ScanCurrentSceneAssetUsage(context);
            var profile = new MaterialProfile { UsageScan = usageScan };
            foreach (var entry in usageScan.Assets.Values.Where(entry => SceneUsageAssetTypeMatches(entry, "material")))
            {
                var material = entry.UnityObject as Material;
                if (material == null && !string.IsNullOrWhiteSpace(entry.Guid))
                {
                    material = LoadSceneUsageAssetForDetail(entry.Path, entry.LocalId, new[] { entry }) as Material;
                }

                var materialEntry = new MaterialProfileMaterial
                {
                    SceneUsage = entry,
                    Material = material,
                    ShaderKey = GetMaterialProfileShaderKey(material),
                    ShaderName = GetMaterialProfileShaderName(material)
                };
                materialEntry.RendererReferenceCount = CountMaterialProfileLocations(entry, "fastPath");
                materialEntry.SerializedReferenceCount = CountMaterialProfileLocations(entry, "serializedProperty");
                materialEntry.TextureCount = CountMaterialProfileTextures(material);
                profile.Materials.Add(materialEntry);

                if (!profile.ShaderGroups.TryGetValue(materialEntry.ShaderKey, out var group))
                {
                    group = new MaterialProfileShaderGroup
                    {
                        ShaderKey = materialEntry.ShaderKey,
                        ShaderName = materialEntry.ShaderName,
                        FollowUpUri = GetMaterialProfileShaderUri(materialEntry.ShaderKey)
                    };
                    profile.ShaderGroups[materialEntry.ShaderKey] = group;
                }

                group.Materials.Add(materialEntry);
            }

            AddMaterialProfileRendererSlotStats(profile, context);
            return profile;
        }

        private static void AddMaterialProfileRendererSlotStats(MaterialProfile profile, GameObjectQueryContext context)
        {
            foreach (var gameObject in GameObjectBridgeService.EnumerateContextGameObjects(context))
            {
                foreach (var renderer in gameObject.GetComponents<Renderer>())
                {
                    if (renderer == null)
                    {
                        continue;
                    }

                    profile.RendererCount++;
                    Material[] materials;
                    try
                    {
                        materials = renderer.sharedMaterials;
                    }
                    catch (Exception ex)
                    {
                        AddSceneUsageWarning(profile.UsageScan, $"Renderer material scan failed on '{gameObject.name}': {ex.GetBaseException().Message}");
                        continue;
                    }

                    profile.RendererSlotCount += materials.Length;
                    profile.NullMaterialSlotCount += materials.Count(material => material == null);
                }
            }
        }

        private static void AddMaterialProfileSummary(Dictionary<string, object?> result, MaterialProfile profile)
        {
            result["materialCount"] = profile.Materials.Count;
            result["assetMaterialCount"] = profile.Materials.Count(entry => !entry.SceneUsage.RuntimeOnly && !entry.SceneUsage.BuiltIn);
            result["runtimeInstanceMaterialCount"] = profile.Materials.Count(entry => entry.SceneUsage.RuntimeOnly);
            result["builtInMaterialCount"] = profile.Materials.Count(entry => entry.SceneUsage.BuiltIn);
            result["dependencyOnlyMaterialCount"] = profile.Materials.Count(entry => IsSceneUsageDependencyOnly(entry.SceneUsage));
            result["rendererCount"] = profile.RendererCount;
            result["rendererMaterialSlotCount"] = profile.RendererSlotCount;
            result["rendererMaterialReferenceCount"] = profile.Materials.Sum(entry => entry.RendererReferenceCount);
            result["serializedMaterialReferenceCount"] = profile.Materials.Sum(entry => entry.SerializedReferenceCount);
            result["nullMaterialSlotCount"] = profile.NullMaterialSlotCount;
            result["textureCount"] = profile.Materials.Sum(entry => entry.TextureCount);
            result["countBasis"] = "exact scene/prefab-stage object and serialized-reference counts";
            result["memoryBasis"] = "Profiler.GetRuntimeMemorySizeLong native/runtime estimate; may be 0 or unavailable";
        }

        private static Dictionary<string, object?> CreateMaterialProfileShaderGroupRow(MaterialProfileShaderGroup group)
        {
            var materials = group.Materials;
            return new Dictionary<string, object?>
            {
                ["shaderKey"] = group.ShaderKey,
                ["shaderName"] = group.ShaderName,
                ["materialCount"] = materials.Count,
                ["rendererReferenceCount"] = materials.Sum(entry => entry.RendererReferenceCount),
                ["serializedReferenceCount"] = materials.Sum(entry => entry.SerializedReferenceCount),
                ["textureCount"] = CountDistinctMaterialProfileTextures(materials.Select(entry => entry.Material)),
                ["assetMaterialCount"] = materials.Count(entry => !entry.SceneUsage.RuntimeOnly && !entry.SceneUsage.BuiltIn),
                ["runtimeInstanceMaterialCount"] = materials.Count(entry => entry.SceneUsage.RuntimeOnly),
                ["builtInMaterialCount"] = materials.Count(entry => entry.SceneUsage.BuiltIn),
                ["dependencyOnlyMaterialCount"] = materials.Count(entry => IsSceneUsageDependencyOnly(entry.SceneUsage)),
                ["memoryEstimate"] = CreateMaterialProfileMemoryEstimate(materials.Select(entry => entry.Material).Where(material => material != null).Select(material => material!)),
                ["followUpUri"] = group.FollowUpUri
            };
        }

        private static Dictionary<string, object?> CreateMaterialProfileMaterialRow(MaterialProfileMaterial material)
        {
            var row = CreateSceneUsageAssetRow(material.SceneUsage, includeSampleLocations: true);
            row["materialKey"] = material.SceneUsage.Key;
            row["materialProfileUri"] = GetMaterialProfileMaterialUri(material.SceneUsage.Key);
            row["shaderKey"] = material.ShaderKey;
            row["shaderName"] = material.ShaderName;
            row["shaderProfileUri"] = GetMaterialProfileShaderUri(material.ShaderKey);
            row["rendererReferenceCount"] = material.RendererReferenceCount;
            row["serializedReferenceCount"] = material.SerializedReferenceCount;
            row["textureCount"] = material.TextureCount;
            row["memoryEstimate"] = CreateMaterialProfileMemoryEstimate(material.Material);
            return row;
        }

        private static Dictionary<string, object?> CreateMaterialProfileShaderDto(MaterialProfileMaterial material)
        {
            return new Dictionary<string, object?>
            {
                ["shaderKey"] = material.ShaderKey,
                ["shaderName"] = material.ShaderName,
                ["resourceUri"] = GetMaterialProfileShaderUri(material.ShaderKey)
            };
        }

        private static Dictionary<string, object?>[] CreateMaterialProfileTextureLinks(IEnumerable<Material?> materials, int maxLinks, out bool truncated)
        {
            var links = new List<Dictionary<string, object?>>();
            var total = 0;
            foreach (var material in materials.Where(material => material != null).Select(material => material!))
            {
                foreach (var propertyName in GetMaterialProfileTexturePropertyNames(material))
                {
                    var texture = material.GetTexture(propertyName);
                    if (texture == null)
                    {
                        continue;
                    }

                    total++;
                    if (links.Count >= maxLinks)
                    {
                        continue;
                    }

                    links.Add(CreateMaterialProfileTextureLink(material, propertyName, texture));
                }
            }

            truncated = total > links.Count;
            return links.ToArray();
        }

        private static Dictionary<string, object?> CreateMaterialProfileTextureLink(Material material, string propertyName, Texture texture)
        {
            var output = new Dictionary<string, object?>
            {
                ["materialName"] = material.name,
                ["materialInstanceId"] = GetLegacyInstanceId(material),
                ["propertyName"] = propertyName,
                ["textureName"] = texture.name,
                ["textureType"] = texture.GetType().Name,
                ["textureInstanceId"] = GetLegacyInstanceId(texture)
            };
            if (TryCreateSceneUsageAssetIdentity(texture, out var identity))
            {
                output["textureKey"] = identity.Key;
                output["runtimeOnly"] = identity.RuntimeOnly;
                output["builtIn"] = identity.BuiltIn;
                if (!string.IsNullOrWhiteSpace(identity.Guid))
                {
                    output["guid"] = identity.Guid;
                    output["localId"] = identity.LocalId;
                    output["path"] = identity.Path;
                    output["assetResourceUri"] = identity.LocalId.HasValue && !identity.IsMainAsset
                        ? $"chievfx://asset/{identity.Guid}/id/{identity.LocalId.Value.ToString(CultureInfo.InvariantCulture)}"
                        : $"chievfx://asset/{identity.Guid}";
                }
            }

            return output;
        }

        private static Dictionary<string, object?> CreateMaterialProfileMemoryEstimate(Object? unityObject)
        {
            return CreateMaterialProfileMemoryEstimate(unityObject != null ? new[] { unityObject } : Array.Empty<Object>());
        }

        private static Dictionary<string, object?> CreateMaterialProfileMemoryEstimate(IEnumerable<Object> unityObjects)
        {
            var bytes = 0L;
            var available = 0;
            var zero = 0;
            foreach (var unityObject in unityObjects)
            {
                try
                {
                    var size = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(unityObject);
                    bytes += size;
                    available++;
                    if (size == 0)
                    {
                        zero++;
                    }
                }
                catch
                {
                    // Some editor/runtime objects cannot report profiler memory; counts remain exact elsewhere.
                }
            }

            return new Dictionary<string, object?>
            {
                ["bytes"] = bytes,
                ["objectCount"] = available,
                ["zeroSizeObjectCount"] = zero,
                ["available"] = available > 0,
                ["exact"] = false,
                ["source"] = "Profiler.GetRuntimeMemorySizeLong",
                ["estimateKind"] = "native/runtime estimate"
            };
        }

        private static Dictionary<string, object?> CreateMaterialProfileOutputCaps()
        {
            return new Dictionary<string, object?>
            {
                ["maxShaderGroups"] = DefaultResourceMaxResults,
                ["maxMaterials"] = DefaultResourceMaxResults,
                ["maxTextureLinks"] = DefaultMaterialProfileTextureLinkCap,
                ["maxLocations"] = DefaultMaterialProfileLocationCap,
                ["maxScanWarnings"] = MaxSceneUsageScanWarnings,
                ["maxSkippedComponents"] = MaxSceneUsageSkippedComponents
            };
        }

        private static int CountMaterialProfileLocations(SceneUsageAssetEntry entry, string source)
        {
            return entry.SourceReferenceCounts.TryGetValue(source, out var count) ? count : 0;
        }

        private static bool IsSceneUsageDependencyOnly(SceneUsageAssetEntry entry)
        {
            return entry.ReferenceCount == 0 && (entry.LoadedDependency || entry.SavedDependency);
        }

        private static int CountMaterialProfileTextures(Material? material)
        {
            if (material == null)
            {
                return 0;
            }

            return GetMaterialProfileTexturePropertyNames(material)
                .Select(propertyName => material.GetTexture(propertyName))
                .Where(texture => texture != null)
                .Select(texture => TryCreateSceneUsageAssetIdentity(texture!, out var identity) ? identity.Key : GetEntityIdText(texture!))
                .Distinct(StringComparer.Ordinal)
                .Count();
        }

        private static int CountDistinctMaterialProfileTextures(IEnumerable<Material?> materials)
        {
            return materials
                .Where(material => material != null)
                .SelectMany(material => GetMaterialProfileTexturePropertyNames(material!).Select(propertyName => material!.GetTexture(propertyName)))
                .Where(texture => texture != null)
                .Select(texture => TryCreateSceneUsageAssetIdentity(texture!, out var identity) ? identity.Key : GetEntityIdText(texture!))
                .Distinct(StringComparer.Ordinal)
                .Count();
        }

        private static string[] GetMaterialProfileTexturePropertyNames(Material material)
        {
            try
            {
                return material.GetTexturePropertyNames();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static string GetMaterialProfileShaderKey(Material? material)
        {
            var shader = material != null ? material.shader : null;
            if (shader == null)
            {
                return "null";
            }

            return TryCreateSceneUsageAssetIdentity(shader, out var identity)
                ? identity.Key
                : $"shader:{shader.name}";
        }

        private static string GetMaterialProfileShaderName(Material? material)
        {
            return material != null && material.shader != null ? material.shader.name : "<null>";
        }

        private static string GetMaterialProfileShaderUri(string shaderKey)
        {
            return $"chievfx://scene/all/material-profile/shader/{EncodeResourceSegment(shaderKey)}";
        }

        private static string GetMaterialProfileMaterialUri(string materialKey)
        {
            return $"chievfx://scene/all/material-profile/material/{EncodeResourceSegment(materialKey)}";
        }

        private static void ApplySceneUsageLocationTextBudget(
            Dictionary<string, object?> result,
            Dictionary<string, object?>[] candidateLocations,
            int totalLocations)
        {
            result["locationOutputBudgetApplied"] = false;
            SetSceneUsageLocations(result, candidateLocations, totalLocations);
            if (!SceneUsageResourceExceedsTextBudget(result))
            {
                return;
            }

            var bestCount = 0;
            var low = 0;
            var high = candidateLocations.Length;
            result["locationOutputBudgetApplied"] = true;
            while (low <= high)
            {
                var mid = low + ((high - low) / 2);
                SetSceneUsageLocations(result, candidateLocations.Take(mid).ToArray(), totalLocations);
                if (SceneUsageResourceExceedsTextBudget(result))
                {
                    high = mid - 1;
                }
                else
                {
                    bestCount = mid;
                    low = mid + 1;
                }
            }

            SetSceneUsageLocations(result, candidateLocations.Take(bestCount).ToArray(), totalLocations);
        }

        private static void SetSceneUsageLocations(
            Dictionary<string, object?> result,
            Dictionary<string, object?>[] locations,
            int totalLocations)
        {
            result["locationCount"] = locations.Length;
            result["truncated"] = totalLocations > locations.Length;
            result["locations"] = locations;
        }

        private static bool SceneUsageResourceExceedsTextBudget(Dictionary<string, object?> result)
        {
            return JsonConvert.SerializeObject(result, Formatting.Indented).Length > MaxSceneUsageResourceTextChars;
        }

        private static Dictionary<string, object?> CreateSceneUsageLocationRow(SceneUsageLocation location)
        {
            var output = new Dictionary<string, object?>
            {
                ["propertyPath"] = location.PropertyPath,
                ["source"] = location.Source,
                ["gameObjectPath"] = location.GameObjectPath
            };
            if (!string.IsNullOrWhiteSpace(location.ComponentKey))
            {
                output["componentKey"] = location.ComponentKey;
                output["componentType"] = location.ComponentType;
                output["componentResourceUri"] = location.ComponentUri;
            }
            else
            {
                output["gameObjectResourceUri"] = location.GameObjectUri;
            }

            return output;
        }

        private static string GetSceneUsageAssetDetailUri(SceneUsageAssetEntry entry)
        {
            if (entry.LocalId.HasValue && !entry.IsMainAsset)
            {
                return $"chievfx://asset/{entry.Guid}/id/{entry.LocalId.Value.ToString(CultureInfo.InvariantCulture)}";
            }

            return $"chievfx://asset/{entry.Guid}";
        }

        private static string GetSceneUsageAssetUsageUri(SceneUsageAssetEntry entry)
        {
            if (entry.LocalId.HasValue && !entry.IsMainAsset)
            {
                return $"chievfx://scene/all/usage/asset/{entry.Guid}/id/{entry.LocalId.Value.ToString(CultureInfo.InvariantCulture)}";
            }

            return $"chievfx://scene/all/usage/asset/{entry.Guid}";
        }

        private static void AddSceneUsageWarning(SceneUsageScan scan, string warning)
        {
            if (scan.ScanWarnings.Count < MaxSceneUsageScanWarnings)
            {
                scan.ScanWarnings.Add(warning);
            }
        }

        private static void AddSkippedSceneUsageComponent(SceneUsageScan scan, GameObject gameObject, string componentKey, string reason)
        {
            scan.SkippedComponentCount++;
            if (scan.SkippedComponents.Count >= MaxSceneUsageSkippedComponents)
            {
                return;
            }

            scan.SkippedComponents.Add(new Dictionary<string, object?>
            {
                ["gameObjectName"] = gameObject.name,
                ["gameObjectInstanceId"] = GetLegacyInstanceId(gameObject),
                ["componentKey"] = componentKey,
                ["reason"] = reason
            });
        }

    }
}
