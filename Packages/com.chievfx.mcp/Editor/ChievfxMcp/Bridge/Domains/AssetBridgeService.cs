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
using UnityEditor.Compilation;
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
    internal sealed class AssetBridgeService : BridgeDomainServiceBase
    {
        private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs",
            ".asmdef",
            ".asmref",
            ".rsp"
        };

        private static readonly HashSet<string> AgentWritableTextExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs",
            ".shader",
            ".compute",
            ".uxml",
            ".uss",
            ".json",
            ".txt",
            ".md",
            ".xml",
            ".yaml",
            ".yml",
            ".asmdef",
            ".asmref",
            ".rsp"
        };

        public object ScheduleRefresh(string operationId, JToken args)
        {
            var options = ReadEnum(args, "options", ImportAssetOptions.Default);
            EventJournal.Write(
                "editor",
                "asset-refresh-scheduled",
                "info",
                "MCP initiated AssetDatabase refresh scheduled.",
                operationId: operationId,
                data: new Dictionary<string, object?> { ["options"] = options.ToString() });
            EditorApplication.delayCall += () => RefreshAssetsSafely(operationId, options);
            return new
            {
                ok = true,
                contentType = "json",
                result = CreateRefreshResult(refreshed: true, scheduled: true, options)
            };
        }

        private static Dictionary<string, object> CreateRefreshResult(bool refreshed, bool scheduled, ImportAssetOptions options)
        {
            var result = new Dictionary<string, object>
            {
                ["refreshed"] = refreshed,
                ["scheduled"] = scheduled
            };

            if (options != ImportAssetOptions.Default)
            {
                result["options"] = options.ToString();
            }

            return result;
        }

        private static void RefreshAssetsSafely(string operationId, ImportAssetOptions options)
        {
            try
            {
                AssetDatabase.Refresh(options);
                EventJournal.Write(
                    "editor",
                    "asset-refresh-finish",
                    "info",
                    "MCP initiated AssetDatabase refresh finished.",
                    operationId: operationId,
                    data: new Dictionary<string, object?> { ["options"] = options.ToString() });
            }
            catch (Exception ex)
            {
                EventJournal.Write(
                    "editor",
                    "asset-refresh-finish",
                    "error",
                    $"MCP initiated AssetDatabase refresh failed. {ex.GetBaseException().Message}",
                    operationId: operationId,
                    data: new Dictionary<string, object?> { ["options"] = options.ToString() });
                Debug.LogError($"ChievFX MCP assets-refresh failed. {ex}");
            }
        }


        public object Refresh(JToken args)
        {
            var options = ReadEnum(args, "options", ImportAssetOptions.Default);
            var targets = ResolveRefreshTargets(args);
            if (targets.SelectorUsed)
            {
                foreach (var path in targets.Paths)
                {
                    AssetDatabase.ImportAsset(path, options);
                }

                return CreateTargetedRefreshResult(targets, options);
            }

            AssetDatabase.Refresh(options);
            return CreateRefreshResult(refreshed: true, scheduled: false, options);
        }

        public object Delete(JToken args)
        {
            var paths = ReadDeletePaths(args);
            foreach (var path in paths)
            {
                ValidateDeletePath(path);
            }

            foreach (var path in paths)
            {
                if (!AssetDatabase.DeleteAsset(path))
                {
                    throw new InvalidOperationException($"Unity failed to delete asset or folder at '{path}'.");
                }
            }

            AssetDatabase.Refresh();
            return new
            {
                success = true
            };
        }

        public object Create(JToken args)
        {
            var path = NormalizeAssetPath(FirstNonEmpty(ReadString(args, "path"), ReadString(args, "assetPath")));
            var typeName = FirstNonEmpty(ReadString(args, "type"), ReadString(args, "assetType"));
            ValidateCreateAssetPath(path);
            if (string.IsNullOrWhiteSpace(typeName))
            {
                throw new ArgumentException("asset-create requires type: prefab or ScriptableObject inheritor type name.", nameof(args));
            }

            if (AssetDatabase.LoadAssetAtPath<Object>(path) != null || File.Exists(ToProjectAbsolutePath(path)))
            {
                throw new InvalidOperationException($"Asset already exists at '{path}'.");
            }

            var folderResult = EnsureFolderPath(Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "Assets");
            Object created;
            if (string.Equals(typeName, "prefab", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(Path.GetExtension(path), ".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("asset-create prefab path must end with .prefab.", nameof(args));
                }

                var rootName = Path.GetFileNameWithoutExtension(path);
                var root = new GameObject(string.IsNullOrWhiteSpace(rootName) ? "Prefab" : rootName);
                try
                {
                    created = PrefabUtility.SaveAsPrefabAsset(root, path)
                        ?? throw new InvalidOperationException($"Unity failed to create prefab at '{path}'.");
                }
                finally
                {
                    Object.DestroyImmediate(root);
                }
            }
            else
            {
                if (!string.Equals(Path.GetExtension(path), ".asset", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("asset-create ScriptableObject path must end with .asset.", nameof(args));
                }

                var scriptableObjectType = ResolveScriptableObjectType(typeName);
                var instance = ScriptableObject.CreateInstance(scriptableObjectType)
                    ?? throw new InvalidOperationException($"Unity failed to instantiate ScriptableObject type '{scriptableObjectType.FullName}'.");
                AssetDatabase.CreateAsset(instance, path);
                created = instance;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return new Dictionary<string, object?>
            {
                ["success"] = true,
                ["path"] = path,
                ["type"] = typeName,
                ["createdFolderCount"] = ((string[])folderResult["createdFolders"]!).Length,
                ["createdFolders"] = folderResult["createdFolders"],
                ["guid"] = AssetDatabase.AssetPathToGUID(path),
                ["objectType"] = created.GetType().FullName,
            };
        }

        public object Find(JToken args)
        {
            var filter = new ResourceAssetFilter
            {
                Kind = "tool",
                Area = BridgeResourcePayloadService.ParseAssetResourceArea(ReadString(args, "area") ?? "assets"),
                AreaExplicit = HasProperty(args, "area"),
                IncludeSubassets = ReadBool(args, "includeSubassets", ReadBool(args, "subassets", false)),
                MaxResults = ClampInt(
                    ReadInt(args, "maxResults", ReadInt(args, "limit", McpLimits.DefaultResourceFilterMaxResults)),
                    1,
                    McpLimits.HardResourceFilterMaxResults)
            };

            var nameTerms = new List<string>();
            foreach (var name in ReadStringValues(args, "name"))
            {
                nameTerms.AddRange(BridgeResourcePayloadService.SplitAssetNameTerms(name));
            }

            filter.NameTerms = nameTerms
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            filter.TypeNames = BridgeResourcePayloadService.ResolveAssetTypeAliases(ReadStringValues(args, "type"));
            filter.Labels = ReadStringValues(args, "label")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            filter.Folders = ReadStringValues(args, "folder")
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (filter.Folders.Length > 0)
            {
                // Reuse resource folder validation by parsing the same comma grammar.
                filter.Folders = BridgeResourcePayloadService.ParseAssetResourceFolders(string.Join(",", filter.Folders));
            }

            return BridgeResourcePayloadService.FindAssets("tool:asset-find", filter);
        }

        public object EnsureFolder(JToken args)
        {
            var path = NormalizeAssetPath(FirstNonEmpty(ReadString(args, "path"), ReadString(args, "folder")));
            return EnsureFolderPath(path);
        }

        private static string[] ReadStringValues(JToken args, string name)
        {
            var token = ReadProperty(args, name);
            if (token == null || token.Type == JTokenType.Null)
            {
                return Array.Empty<string>();
            }

            if (token.Type == JTokenType.String)
            {
                var value = token.Value<string>();
                return string.IsNullOrWhiteSpace(value) ? Array.Empty<string>() : new[] { value! };
            }

            if (token.Type == JTokenType.Array)
            {
                return token
                    .Values<string>()
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .ToArray();
            }

            throw new ArgumentException($"{name} must be a string or array of strings.", nameof(args));
        }

        public object Recompile(JToken args)
        {
            var wasCompiling = EditorApplication.isCompiling;
            var wasUpdating = EditorApplication.isUpdating;
            var wasPlaying = EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode;
            var stopPlayMode = ReadBool(args, "stopPlayMode", true);

            // Unity refuses to compile on demand during Play Mode, so a request issued now either
            // vanishes or parks as a pending compile that pins isCompiling until play ends — a
            // "compile" that reports success without compiling, or one that never finishes. Leave Play
            // Mode and re-issue from edit mode instead.
            if (wasPlaying && stopPlayMode)
            {
                BridgePendingRecompile.RequestAfterPlayModeExit(EventJournal);
                return new Dictionary<string, object?>
                {
                    ["requested"] = true,
                    ["assetDatabaseRefreshed"] = false,
                    ["wasPlaying"] = true,
                    ["exitedPlayMode"] = true,
                    ["compileRequestedAfterPlayModeExit"] = true,
                    ["scriptChangesWhilePlaying"] = BridgePendingRecompile.ScriptChangesWhilePlaying(),
                    ["wasCompiling"] = wasCompiling,
                    ["wasUpdating"] = wasUpdating,
                    ["isCompiling"] = EditorApplication.isCompiling,
                    ["isUpdating"] = EditorApplication.isUpdating
                };
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            CompilationPipeline.RequestScriptCompilation();
            EventJournal.Write(
                "editor",
                "compile-request",
                "info",
                "MCP requested Unity script compilation.",
                data: new Dictionary<string, object?>
                {
                    ["wasCompiling"] = wasCompiling,
                    ["wasUpdating"] = wasUpdating,
                    ["wasPlaying"] = wasPlaying
                });
            var result = new Dictionary<string, object?>
            {
                ["requested"] = true,
                ["assetDatabaseRefreshed"] = true,
                ["wasCompiling"] = wasCompiling,
                ["wasUpdating"] = wasUpdating,
                ["isCompiling"] = EditorApplication.isCompiling,
                ["isUpdating"] = EditorApplication.isUpdating
            };

            if (wasPlaying)
            {
                // stopPlayMode was turned off explicitly. Say plainly that the request may not produce
                // a compile, rather than letting the caller read "requested" as "compiled".
                result["wasPlaying"] = true;
                result["exitedPlayMode"] = false;
                result["scriptChangesWhilePlaying"] = BridgePendingRecompile.ScriptChangesWhilePlaying();
                result["warning"] =
                    "Requested while Play Mode is running with stopPlayMode=false. Unity may defer or "
                    + "drop the compile, so no diagnostics are guaranteed. Re-run with stopPlayMode "
                    + "omitted, or exit Play Mode first.";
            }

            return result;
        }

        private static Dictionary<string, object?> CreateTargetedRefreshResult(RefreshTargets targets, ImportAssetOptions options)
        {
            var result = new Dictionary<string, object?>
            {
                ["refreshed"] = true,
                ["scheduled"] = false,
                ["targeted"] = true,
                ["importedCount"] = targets.Paths.Count,
                ["skippedScriptAssetCount"] = targets.SkippedScriptAssetCount
            };

            if (options != ImportAssetOptions.Default)
            {
                result["options"] = options.ToString();
            }

            if (targets.Paths.Count > 0)
            {
                result["importedPaths"] = targets.Paths.Take(50).ToArray();
                result["importedPathsTruncated"] = targets.Paths.Count > 50;
            }

            return result;
        }

        private static RefreshTargets ResolveRefreshTargets(JToken args)
        {
            var paths = new SortedSet<string>(StringComparer.Ordinal);
            var skippedScripts = 0;
            var path = FirstNonEmpty(ReadString(args, "path"), ReadString(args, "assetPath"));
            var folder = FirstNonEmpty(ReadString(args, "folder"), ReadString(args, "root"));
            var pathContains = ReadString(args, "pathContains");
            var typeName = FirstNonEmpty(ReadString(args, "type"), ReadString(args, "assetType"));
            var extensions = ReadExtensions(args);
            var selectorUsed = !string.IsNullOrWhiteSpace(path)
                || !string.IsNullOrWhiteSpace(folder)
                || !string.IsNullOrWhiteSpace(pathContains)
                || !string.IsNullOrWhiteSpace(typeName)
                || extensions.Count > 0;

            if (!selectorUsed)
            {
                return new RefreshTargets(paths.ToList(), 0, false);
            }

            var searchFolders = new List<string>();
            if (!string.IsNullOrWhiteSpace(folder))
            {
                var normalizedFolder = NormalizeAssetPath(folder!);
                if (IsProjectAssetPath(normalizedFolder)
                    && (AssetDatabase.IsValidFolder(normalizedFolder) || Directory.Exists(ToProjectAbsolutePath(normalizedFolder))))
                {
                    searchFolders.Add(normalizedFolder);
                }
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                var normalizedPath = NormalizeAssetPath(path!);
                if (!IsProjectAssetPath(normalizedPath))
                {
                    // Ignore non-project paths; AssetDatabase APIs only import project-relative assets.
                }
                else if (AssetDatabase.IsValidFolder(normalizedPath))
                {
                    searchFolders.Add(normalizedPath);
                }
                else if (AssetDatabase.LoadAssetAtPath<Object>(normalizedPath) != null)
                {
                    AddNonScriptPath(paths, normalizedPath, ref skippedScripts);
                }
                else if (File.Exists(ToProjectAbsolutePath(normalizedPath)))
                {
                    AddNonScriptPath(paths, normalizedPath, ref skippedScripts);
                }
            }

            if (searchFolders.Count > 0 || !string.IsNullOrWhiteSpace(typeName) || !string.IsNullOrWhiteSpace(pathContains) || extensions.Count > 0)
            {
                var filter = BuildFindAssetsFilter(typeName);
                var folders = searchFolders.Count > 0 ? searchFolders.Distinct().ToArray() : new[] { "Assets" };
                if (string.IsNullOrWhiteSpace(typeName) || extensions.Count > 0)
                {
                    foreach (var folderPath in folders)
                    {
                        AddFileSystemAssetPaths(paths, folderPath, pathContains, extensions, ref skippedScripts);
                    }
                }

                foreach (var guid in AssetDatabase.FindAssets(filter, folders))
                {
                    var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrWhiteSpace(assetPath))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(pathContains)
                        && assetPath.IndexOf(pathContains!.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    if (extensions.Count > 0 && !extensions.Contains(Path.GetExtension(assetPath)))
                    {
                        continue;
                    }

                    AddNonScriptPath(paths, assetPath, ref skippedScripts);
                }
            }

            return new RefreshTargets(paths.ToList(), skippedScripts, true);
        }

        private static string FirstNonEmpty(params string?[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value!.Trim();
                }
            }

            return string.Empty;
        }

        private static string[] ReadDeletePaths(JToken args)
        {
            var result = new List<string>();
            var path = FirstNonEmpty(ReadString(args, "path"), ReadString(args, "assetPath"), ReadString(args, "folder"));
            if (!string.IsNullOrWhiteSpace(path))
            {
                result.Add(NormalizeAssetPath(path));
            }

            if (ReadProperty(args, "paths") is JArray pathsArray)
            {
                foreach (var item in pathsArray)
                {
                    if (item.Type != JTokenType.String)
                    {
                        throw new ArgumentException("paths entries must be strings.", nameof(args));
                    }

                    var itemPath = item.Value<string>();
                    if (!string.IsNullOrWhiteSpace(itemPath))
                    {
                        result.Add(NormalizeAssetPath(itemPath!));
                    }
                }
            }

            result = result
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (result.Count == 0)
            {
                throw new ArgumentException("Provide path or paths to delete.", nameof(args));
            }

            return result.ToArray();
        }

        private static void ValidateDeletePath(string path)
        {
            if (!IsProjectAssetPath(path) || string.Equals(path, "Assets", StringComparison.Ordinal) || string.Equals(path, "Packages", StringComparison.Ordinal))
            {
                throw new ArgumentException($"asset-delete path must be an asset or folder path under Assets/ or Packages/: '{path}'.");
            }

            if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"asset-delete path must target an asset or folder, not a .meta file: '{path}'.");
            }

            var absolutePath = ToProjectAbsolutePath(path);
            if (!AssetDatabase.IsValidFolder(path)
                && AssetDatabase.LoadAssetAtPath<Object>(path) == null
                && !File.Exists(absolutePath)
                && !Directory.Exists(absolutePath))
            {
                throw new InvalidOperationException($"No asset or folder exists at '{path}'.");
            }
        }

        private static void ValidateCreateAssetPath(string path)
        {
            if (!path.StartsWith("Assets/", StringComparison.Ordinal))
            {
                throw new ArgumentException($"asset-create path must start with Assets/: '{path}'.");
            }

            if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(Path.GetExtension(path)))
            {
                throw new ArgumentException($"asset-create path must target an asset file under Assets/: '{path}'.");
            }

            var extension = Path.GetExtension(path);
            if (AgentWritableTextExtensions.Contains(extension))
            {
                throw new ArgumentException($"asset-create is for Unity object assets. Create text/script assets like scripts, shaders, uxml, uss, and json directly, then use assets-refresh/recompile as needed: '{path}'.");
            }
        }

        private static Dictionary<string, object?> EnsureFolderPath(string path)
        {
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) && !string.Equals(path, "Assets", StringComparison.Ordinal))
            {
                throw new ArgumentException($"folder-ensure path must be Assets or start with Assets/: '{path}'.");
            }

            if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"folder-ensure path must target a folder, not a .meta file: '{path}'.");
            }

            if (string.Equals(path, "Assets", StringComparison.Ordinal))
            {
                return new Dictionary<string, object?>
                {
                    ["success"] = true,
                    ["path"] = path,
                    ["createdFolders"] = Array.Empty<string>(),
                };
            }

            var created = new List<string>();
            var current = "Assets";
            foreach (var segment in path.Substring("Assets/".Length).Split('/'))
            {
                if (string.IsNullOrWhiteSpace(segment))
                {
                    throw new ArgumentException($"folder-ensure path contains an empty folder segment: '{path}'.");
                }

                var next = current + "/" + segment;
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, segment);
                    created.Add(next);
                }

                current = next;
            }

            AssetDatabase.Refresh();
            return new Dictionary<string, object?>
            {
                ["success"] = true,
                ["path"] = path,
                ["createdFolders"] = created.ToArray(),
            };
        }

        private static Type ResolveScriptableObjectType(string typeName)
        {
            var type = Type.GetType(typeName, throwOnError: false);
            type ??= AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(assembly =>
                {
                    try
                    {
                        return assembly.GetTypes();
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        return ex.Types.OfType<Type>();
                    }
                })
                .FirstOrDefault(candidate => candidate != null
                    && (string.Equals(candidate.Name, typeName, StringComparison.Ordinal)
                        || string.Equals(candidate.FullName, typeName, StringComparison.Ordinal)))!;

            if (type == null || !typeof(ScriptableObject).IsAssignableFrom(type) || type.IsAbstract)
            {
                throw new ArgumentException($"asset-create type must be prefab or a non-abstract ScriptableObject inheritor: '{typeName}'.");
            }

            return type;
        }

        private static string NormalizeAssetPath(string path)
        {
            return path.Trim().Replace('\\', '/');
        }

        private static bool IsProjectAssetPath(string path)
        {
            return path.StartsWith("Assets/", StringComparison.Ordinal)
                || string.Equals(path, "Assets", StringComparison.Ordinal)
                || path.StartsWith("Packages/", StringComparison.Ordinal)
                || string.Equals(path, "Packages", StringComparison.Ordinal);
        }

        private static string ToProjectAbsolutePath(string assetPath)
        {
            return Path.GetFullPath(Path.Combine(GetProjectRoot(), assetPath));
        }

        private static string GetProjectRoot()
        {
            return Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
        }

        private static void AddFileSystemAssetPaths(
            ISet<string> paths,
            string folder,
            string? pathContains,
            HashSet<string> extensions,
            ref int skippedScripts)
        {
            var absoluteFolder = ToProjectAbsolutePath(folder);
            if (!Directory.Exists(absoluteFolder))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(absoluteFolder, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var projectRoot = GetProjectRoot().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!file.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relativePath = NormalizeAssetPath(file.Substring(projectRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrWhiteSpace(pathContains)
                    && relativePath.IndexOf(pathContains!.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (extensions.Count > 0 && !extensions.Contains(Path.GetExtension(relativePath)))
                {
                    continue;
                }

                AddNonScriptPath(paths, relativePath, ref skippedScripts);
            }
        }

        private static string BuildFindAssetsFilter(string? typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return string.Empty;
            }

            var normalized = typeName!.Trim();
            if (normalized.StartsWith("t:", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(2);
            }

            normalized = normalized.Trim().TrimEnd('s');
            normalized = normalized.ToLowerInvariant() switch
            {
                "fbx" => "Model",
                "model" => "Model",
                "texture" => "Texture",
                "texture2d" => "Texture2D",
                "shader" => "Shader",
                "material" => "Material",
                "mat" => "Material",
                "prefab" => "Prefab",
                "sprite" => "Sprite",
                _ => typeName!.Trim()
            };
            return "t:" + normalized;
        }

        private static HashSet<string> ReadExtensions(JToken args)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var token = args["extensions"] ?? args["extension"];
            if (token == null || token.Type == JTokenType.Null)
            {
                return result;
            }

            if (token.Type == JTokenType.Array)
            {
                foreach (var item in token.Children())
                {
                    AddExtension(result, item.Value<string>());
                }
            }
            else
            {
                AddExtension(result, token.Value<string>());
            }

            return result;
        }

        private static void AddExtension(HashSet<string> result, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var extension = value!.Trim();
            if (!extension.StartsWith(".", StringComparison.Ordinal))
            {
                extension = "." + extension;
            }

            result.Add(extension);
        }

        private static void AddNonScriptPath(ISet<string> paths, string path, ref int skippedScripts)
        {
            if (ScriptExtensions.Contains(Path.GetExtension(path)))
            {
                skippedScripts++;
                return;
            }

            paths.Add(path);
        }

        private readonly struct RefreshTargets
        {
            public RefreshTargets(List<string> paths, int skippedScriptAssetCount, bool selectorUsed)
            {
                Paths = paths;
                SkippedScriptAssetCount = skippedScriptAssetCount;
                SelectorUsed = selectorUsed;
            }

            public List<string> Paths { get; }

            public int SkippedScriptAssetCount { get; }

            public bool SelectorUsed { get; }
        }

    }
}
