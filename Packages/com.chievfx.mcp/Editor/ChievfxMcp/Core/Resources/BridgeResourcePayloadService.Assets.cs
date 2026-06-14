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
        private static string CreateAssetDatabaseFilterQuery(ResourceAssetFilter filter)
        {
            var tokens = new List<string>();
            tokens.AddRange(filter.NameTerms);
            tokens.AddRange(filter.TypeNames.Select(typeName => $"t:{typeName}"));
            tokens.AddRange(filter.Labels.Select(label => $"l:{label}"));
            if (filter.Folders.Length == 0 || filter.AreaExplicit)
            {
                tokens.Add($"a:{filter.Area}");
            }

            return string.Join(" ", tokens.Where(token => !string.IsNullOrWhiteSpace(token)));
        }

        internal static string[] SplitAssetNameTerms(string text)
        {
            return Regex.Split(text, "\\s+")
                .Select(term => term.Trim())
                .Where(term => term.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        internal static string[] ResolveAssetTypeAliases(IEnumerable<string> values)
        {
            var typeNames = new List<string>();
            foreach (var value in values)
            {
                var normalized = value.Trim();
                GameObjectBridgeService.ValidateResourceFilterText(normalized, "type");
                switch (normalized.ToLowerInvariant())
                {
                    case "material":
                        typeNames.Add("Material");
                        break;
                    case "mesh":
                        typeNames.Add("Mesh");
                        break;
                    case "texture":
                        typeNames.AddRange(new[] { "Texture2D", "Texture", "Cubemap", "Texture3D" });
                        break;
                    case "rendertexture":
                    case "render-texture":
                    case "render_texture":
                        typeNames.Add("RenderTexture");
                        break;
                    case "prefab":
                        typeNames.Add("Prefab");
                        break;
                    case "scene":
                        typeNames.Add("Scene");
                        break;
                    case "object":
                        typeNames.Add("Object");
                        break;
                    default:
                        typeNames.Add(normalized);
                        break;
                }
            }

            return typeNames
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        internal static string ParseAssetResourceArea(string value)
        {
            var area = value.Trim().ToLowerInvariant();
            if (string.Equals(area, "assets", StringComparison.Ordinal)
                || string.Equals(area, "packages", StringComparison.Ordinal)
                || string.Equals(area, "all", StringComparison.Ordinal))
            {
                return area;
            }

            throw new ArgumentException("area must be assets, packages, or all.", nameof(value));
        }

        internal static string[] ParseAssetResourceFolders(string value)
        {
            var folders = ParseResourceFilterValues(value, "folder");
            if (folders.Length > MaxResourceFilterFolders)
            {
                throw new ArgumentException($"folder accepts at most {MaxResourceFilterFolders} values.", nameof(value));
            }

            ValidateAssetResourceFolders(folders);
            return folders;
        }

        private static void ValidateAssetResourceFolders(IEnumerable<string> folders)
        {
            foreach (var folder in folders)
            {
                if (!IsProjectAssetFolderPath(folder))
                {
                    throw new ArgumentException($"folder must be project-relative under Assets or Packages: '{folder}'.");
                }

                if (!AssetDatabase.IsValidFolder(folder))
                {
                    throw new ArgumentException($"folder is not a valid AssetDatabase folder: '{folder}'.");
                }
            }
        }

        private static bool IsProjectAssetFolderPath(string folder)
        {
            return string.Equals(folder, "Assets", StringComparison.Ordinal)
                || folder.StartsWith("Assets/", StringComparison.Ordinal)
                || string.Equals(folder, "Packages", StringComparison.Ordinal)
                || folder.StartsWith("Packages/", StringComparison.Ordinal);
        }

        private static Dictionary<string, object?> CreateResourceAssetFilterDto(ResourceAssetFilter filter)
        {
            var output = new Dictionary<string, object?>
            {
                ["kind"] = filter.Kind,
                ["area"] = filter.Area,
                ["areaExplicit"] = filter.AreaExplicit,
                ["includeSubassets"] = filter.IncludeSubassets,
                ["maxResults"] = filter.MaxResults
            };
            if (filter.NameTerms.Length > 0)
            {
                output["nameTerms"] = filter.NameTerms;
            }

            if (filter.TypeNames.Length > 0)
            {
                output["typeNames"] = filter.TypeNames;
            }

            if (filter.Labels.Length > 0)
            {
                output["labels"] = filter.Labels;
            }

            if (filter.Folders.Length > 0)
            {
                output["folders"] = filter.Folders;
            }

            return output;
        }

        private static Dictionary<string, object?>[] CreateAssetResourceRows(string path, string guid, bool includeSubassets)
        {
            var mainAsset = AssetDatabase.LoadMainAssetAtPath(path);
            if (mainAsset == null)
            {
                return Array.Empty<Dictionary<string, object?>>();
            }

            var rows = new List<Dictionary<string, object?>>
            {
                CreateAssetResourceRow(mainAsset, path, guid, isMainAsset: true)
            };
            if (!includeSubassets)
            {
                return rows.ToArray();
            }

            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (asset == null || ReferenceEquals(asset, mainAsset) || !AssetDatabase.IsSubAsset(asset))
                {
                    continue;
                }

                rows.Add(CreateAssetResourceRow(asset, path, guid, isMainAsset: false));
            }

            return rows.ToArray();
        }

        private static Dictionary<string, object?> CreateAssetResourceRow(Object asset, string path, string guid, bool isMainAsset)
        {
            var mainType = AssetDatabase.GetMainAssetTypeAtPath(path);
            TryGetAssetLocalId(asset, out var localId);
            var detailUri = isMainAsset
                ? $"chievfx://asset/{guid}"
                : $"chievfx://asset/{guid}/id/{localId.ToString(CultureInfo.InvariantCulture)}";
            var output = new Dictionary<string, object?>
            {
                ["name"] = asset.name,
                ["path"] = path,
                ["guid"] = guid,
                ["mainType"] = mainType?.Name ?? string.Empty,
                ["labels"] = GetAssetLabels(path),
                ["isMainAsset"] = isMainAsset,
                ["localId"] = localId,
                ["resourceUri"] = detailUri,
                ["detailHint"] = new Dictionary<string, object?>
                {
                    ["kind"] = isMainAsset ? "asset" : "subasset",
                    ["uri"] = detailUri
                }
            };
            output["file"] = CreateAssetFileMetadata(path);
            if (!isMainAsset)
            {
                output["type"] = asset.GetType().Name;
            }

            return output;
        }

        private static bool AssetResourceRowMatchesFilter(Dictionary<string, object?> row, ResourceAssetFilter filter)
        {
            if (filter.TypeNames.Length == 0 || filter.TypeNames.Any(typeName => string.Equals(typeName, "Object", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            var rowType = row.TryGetValue("type", out var subassetType) && subassetType is string subassetTypeText
                ? subassetTypeText
                : row.TryGetValue("mainType", out var mainType) && mainType is string mainTypeText
                    ? mainTypeText
                    : string.Empty;
            return filter.TypeNames.Any(typeName => string.Equals(typeName, rowType, StringComparison.OrdinalIgnoreCase));
        }

        private static Dictionary<string, object?> CreateAssetDetail(Object asset, string path, string guid)
        {
            var row = CreateAssetResourceRow(asset, path, guid, AssetDatabase.IsMainAsset(asset));
            row["fullType"] = asset.GetType().FullName ?? asset.GetType().Name;
            row["instanceId"] = GetLegacyInstanceId(asset);
            row["isSubAsset"] = AssetDatabase.IsSubAsset(asset);
            row["isPersistent"] = AssetDatabase.Contains(asset);
            if (AssetDatabase.IsMainAsset(asset))
            {
                row["subassets"] = AssetDatabase.LoadAllAssetsAtPath(path)
                    .Where(candidate => candidate != null && AssetDatabase.IsSubAsset(candidate))
                    .Select(candidate => CreateAssetResourceRow(candidate, path, guid, isMainAsset: false))
                    .ToArray();
            }

            return row;
        }

        private static string[] GetAssetLabels(string path)
        {
            var mainAsset = AssetDatabase.LoadMainAssetAtPath(path);
            return mainAsset != null ? AssetDatabase.GetLabels(mainAsset) : Array.Empty<string>();
        }

        private static Dictionary<string, object?> CreateAssetFileMetadata(string path)
        {
            var output = new Dictionary<string, object?>
            {
                ["extension"] = Path.GetExtension(path)
            };
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (File.Exists(fullPath))
                {
                    var info = new FileInfo(fullPath);
                    output["bytes"] = info.Length;
                    output["lastWriteUtc"] = info.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex) when (
                ex is IOException
                || ex is UnauthorizedAccessException
                || ex is ArgumentException
                || ex is NotSupportedException)
            {
                output["metadataError"] = ex.GetBaseException().Message;
            }

            return output;
        }

        private static Dictionary<string, object?> CreateAssetImporterDto(string path)
        {
            var importer = AssetImporter.GetAtPath(path);
            if (importer == null)
            {
                return new Dictionary<string, object?>
                {
                    ["available"] = false
                };
            }

            return new Dictionary<string, object?>
            {
                ["available"] = true,
                ["type"] = importer.GetType().Name,
                ["assetBundleName"] = importer.assetBundleName,
                ["assetBundleVariant"] = importer.assetBundleVariant,
                ["userData"] = importer.userData
            };
        }

        private static Dictionary<string, object?> CreateAssetDatabaseResourceContext()
        {
            return new Dictionary<string, object?>
            {
                ["source"] = "assetDatabase",
                ["persistedOnly"] = true,
                ["runtimeOnlyObjectsIncluded"] = false
            };
        }

        private static void ValidateAssetGuid(string guid)
        {
            if (!Regex.IsMatch(guid, "^[0-9a-fA-F]{32}$"))
            {
                throw new ArgumentException("guid must be a 32-character asset GUID.", nameof(guid));
            }
        }

        private static bool TryGetAssetLocalId(Object asset, out long localId)
        {
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string _, out long id))
            {
                localId = id;
                return true;
            }

            localId = 0;
            return false;
        }

    }
}
