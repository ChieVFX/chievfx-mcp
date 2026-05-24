#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Chievfx.Mcp.Editor
{
    internal sealed class GameObjectQueryContext
    {
        public string Source { get; set; } = string.Empty;

        public string SceneName { get; set; } = string.Empty;

        public string ScenePath { get; set; } = string.Empty;

        public string PrefabAssetPath { get; set; } = string.Empty;

        public GameObject[] Roots { get; set; } = Array.Empty<GameObject>();
    }

    internal sealed class ResourceGameObjectFilter
    {
        public string Kind { get; set; } = string.Empty;

        public string[] NameContains { get; set; } = Array.Empty<string>();

        public string[] NamePatterns { get; set; } = Array.Empty<string>();

        public string[] ComponentTypes { get; set; } = Array.Empty<string>();

        public bool IncludeInactive { get; set; }

        public bool CaseInsensitive { get; set; } = true;

        public int MaxResults { get; set; } = McpLimits.DefaultResourceFilterMaxResults;
    }

    internal sealed class ResourceAssetFilter
    {
        public string Kind { get; set; } = string.Empty;

        public string[] NameTerms { get; set; } = Array.Empty<string>();

        public string[] TypeNames { get; set; } = Array.Empty<string>();

        public string[] Labels { get; set; } = Array.Empty<string>();

        public string Area { get; set; } = "assets";

        public bool AreaExplicit { get; set; }

        public string[] Folders { get; set; } = Array.Empty<string>();

        public bool IncludeSubassets { get; set; }

        public int MaxResults { get; set; } = McpLimits.DefaultResourceFilterMaxResults;
    }

    internal sealed class SceneUsageScan
    {
        public Dictionary<string, SceneUsageAssetEntry> Assets { get; } = new(StringComparer.Ordinal);

        public int TotalObjects { get; set; }

        public int TotalComponents { get; set; }

        public int TotalReferences { get; set; }

        public int SkippedComponentCount { get; set; }

        public List<string> ScanWarnings { get; } = new();

        public List<Dictionary<string, object?>> SkippedComponents { get; } = new();
    }

    internal sealed class SceneUsageAssetIdentity
    {
        public string Key { get; set; } = string.Empty;

        public string Guid { get; set; } = string.Empty;

        public long? LocalId { get; set; }

        public string Path { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string TypeName { get; set; } = string.Empty;

        public string FullTypeName { get; set; } = string.Empty;

        public int InstanceId { get; set; }

        public string UsageAssetType { get; set; } = string.Empty;

        public bool RuntimeOnly { get; set; }

        public bool BuiltIn { get; set; }

        public bool IsMainAsset { get; set; }

        public Object? UnityObject { get; set; }
    }

    internal sealed class SceneUsageAssetEntry
    {
        public string Key { get; set; } = string.Empty;

        public string Guid { get; set; } = string.Empty;

        public long? LocalId { get; set; }

        public string Path { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string TypeName { get; set; } = string.Empty;

        public string FullTypeName { get; set; } = string.Empty;

        public int InstanceId { get; set; }

        public string UsageAssetType { get; set; } = string.Empty;

        public bool RuntimeOnly { get; set; }

        public bool BuiltIn { get; set; }

        public bool IsMainAsset { get; set; }

        public Object? UnityObject { get; set; }

        public bool LoadedDependency { get; set; }

        public bool SavedDependency { get; set; }

        public int ReferenceCount { get; set; }

        public Dictionary<string, int> SourceReferenceCounts { get; } = new(StringComparer.Ordinal);

        public HashSet<int> GameObjectIds { get; } = new();

        public List<SceneUsageLocation> Locations { get; } = new();
    }

    internal sealed class SceneUsageLocation
    {
        public string GameObjectName { get; set; } = string.Empty;

        public string GameObjectPath { get; set; } = string.Empty;

        public int GameObjectInstanceId { get; set; }

        public string GameObjectUri { get; set; } = string.Empty;

        public string ComponentKey { get; set; } = string.Empty;

        public string ComponentType { get; set; } = string.Empty;

        public string ComponentFullType { get; set; } = string.Empty;

        public int ComponentInstanceId { get; set; }

        public string ComponentUri { get; set; } = string.Empty;

        public string PropertyPath { get; set; } = string.Empty;

        public string Source { get; set; } = string.Empty;
    }

    internal sealed class MaterialProfile
    {
        public SceneUsageScan UsageScan { get; set; } = null!;

        public List<MaterialProfileMaterial> Materials { get; } = new();

        public Dictionary<string, MaterialProfileShaderGroup> ShaderGroups { get; } = new(StringComparer.Ordinal);

        public int RendererCount { get; set; }

        public int RendererSlotCount { get; set; }

        public int NullMaterialSlotCount { get; set; }
    }

    internal sealed class MaterialProfileShaderGroup
    {
        public string ShaderKey { get; set; } = string.Empty;

        public string ShaderName { get; set; } = string.Empty;

        public string FollowUpUri { get; set; } = string.Empty;

        public List<MaterialProfileMaterial> Materials { get; } = new();
    }

    internal sealed class MaterialProfileMaterial
    {
        public SceneUsageAssetEntry SceneUsage { get; set; } = null!;

        public Material? Material { get; set; }

        public string ShaderKey { get; set; } = string.Empty;

        public string ShaderName { get; set; } = string.Empty;

        public int RendererReferenceCount { get; set; }

        public int SerializedReferenceCount { get; set; }

        public int TextureCount { get; set; }
    }
}
