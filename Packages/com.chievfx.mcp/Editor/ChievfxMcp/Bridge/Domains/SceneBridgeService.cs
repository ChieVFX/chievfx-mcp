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
    internal sealed class SceneBridgeService : BridgeDomainServiceBase
    {
        public object ListOpened()
        {
            var scenes = GetOpenSceneDtos();
            return new
            {
                count = scenes.Length,
                scenes
            };
        }

        public object ListAvailable(JToken args)
        {
            var filter = ReadString(args, "filter");
            var maxResults = ClampInt(ReadInt(args, "maxResults", DefaultSceneMaxResults), 1, HardSceneMaxResults);
            var searchInFolders = ReadStringArray(args, "searchInFolders")
                .Where(folder => !string.IsNullOrWhiteSpace(folder))
                .Select(folder => folder.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            ValidateSearchFolders(searchInFolders);

            var query = string.IsNullOrWhiteSpace(filter)
                ? "t:Scene"
                : $"{filter} t:Scene";
            var searchFolders = searchInFolders.Length > 0
                ? searchInFolders
                : new[] { "Assets" };
            var guids = AssetDatabase.FindAssets(query, searchFolders);
            var selected = guids
                .Take(maxResults + 1)
                .ToArray();
            var truncated = selected.Length > maxResults;
            var scenes = selected
                .Take(maxResults)
                .Select(guid =>
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    return new
                    {
                        name = Path.GetFileNameWithoutExtension(path),
                        path,
                        guid
                    };
                })
                .ToArray();

            return new
            {
                count = scenes.Length,
                truncated,
                scenes
            };
        }

        public object Open(JToken args)
        {
            var scenePath = ReadString(args, "scenePath");
            ValidateSceneAssetPath(scenePath, "scenePath");
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath!) == null)
            {
                throw new InvalidOperationException($"Scene asset not found at '{scenePath}'.");
            }

            var mode = ReadEnum(args, "mode", OpenSceneMode.Single);
            var saveDirtyScenes = ReadBool(args, "saveDirtyScenes", false);
            if (mode == OpenSceneMode.Single)
            {
                SaveOrRejectDirtyScenes(saveDirtyScenes);
            }

            var openedScene = EditorSceneManager.OpenScene(scenePath!, mode);
            var scenes = GetOpenSceneDtos();
            return new
            {
                opened = SceneToDto(openedScene),
                count = scenes.Length,
                scenes
            };
        }

        public object Create(JToken args)
        {
            var path = ReadString(args, "path");
            ValidateSceneAssetPath(path, "path");
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path!) != null)
            {
                throw new InvalidOperationException($"Scene asset already exists at '{path}'.");
            }

            EnsureAssetParentFolder(path!);
            var previousActiveScene = SceneManager.GetActiveScene();
            var replacingUntitledScene = previousActiveScene.IsValid()
                && previousActiveScene.isLoaded
                && string.IsNullOrWhiteSpace(previousActiveScene.path);
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene,
                replacingUntitledScene ? NewSceneMode.Single : NewSceneMode.Additive);
            if (!scene.IsValid())
            {
                throw new InvalidOperationException("Unity failed to create a new empty scene.");
            }

            try
            {
                if (!EditorSceneManager.SaveScene(scene, path!))
                {
                    throw new InvalidOperationException($"Unity failed to save new scene to '{path}'.");
                }
            }
            finally
            {
                if (!replacingUntitledScene && previousActiveScene.IsValid() && previousActiveScene.isLoaded)
                {
                    EditorSceneManager.SetActiveScene(previousActiveScene);
                }

                if (!replacingUntitledScene && scene.IsValid() && scene.isLoaded)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }

            AssetDatabase.Refresh();
            return new
            {
                success = true,
                opened = replacingUntitledScene ? SceneToDto(scene) : null
            };
        }

        public object Save(JToken args)
        {
            var openedSceneName = ReadString(args, "openedSceneName");
            var path = ReadString(args, "path");
            if (!string.IsNullOrWhiteSpace(path))
            {
                ValidateSceneAssetPath(path, "path");
            }

            var scene = string.IsNullOrWhiteSpace(openedSceneName)
                ? SceneManager.GetActiveScene()
                : FindOpenedScene(openedSceneName!);
            if (!scene.IsValid())
            {
                throw new InvalidOperationException("No valid opened scene found to save.");
            }

            if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(scene.path))
            {
                throw new InvalidOperationException($"Scene '{scene.name}' has no asset path. Provide path to save it under Assets/.");
            }

            var saved = string.IsNullOrWhiteSpace(path)
                ? EditorSceneManager.SaveScene(scene)
                : EditorSceneManager.SaveScene(scene, path!);
            if (!saved)
            {
                throw new InvalidOperationException($"Unity failed to save scene '{scene.name}'{FormatOptionalPath(path)}.");
            }

            var savedScene = string.IsNullOrWhiteSpace(path) ? scene : SceneManager.GetSceneByPath(path!);
            if (!savedScene.IsValid())
            {
                savedScene = SceneManager.GetSceneByName(scene.name);
            }

            return new
            {
                saved,
                name = savedScene.IsValid() ? savedScene.name : scene.name,
                path = savedScene.IsValid() ? savedScene.path : path,
                isDirty = savedScene.IsValid() && savedScene.isDirty
            };
        }

        private static string FormatOptionalPath(string? path)
        {
            return string.IsNullOrWhiteSpace(path) ? string.Empty : $" to '{path}'";
        }

        private static object[] GetOpenSceneDtos()
        {
            return GetOpenScenes()
                .Select(SceneToDto)
                .ToArray();
        }

        internal static IEnumerable<Scene> GetOpenScenes()
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                yield return SceneManager.GetSceneAt(i);
            }
        }

        private static object SceneToDto(Scene scene)
        {
            var name = string.IsNullOrWhiteSpace(scene.name) ? "<untitled>" : scene.name;
            var path = string.IsNullOrWhiteSpace(scene.path) ? "<unsaved>" : scene.path;
            return new
            {
                name,
                path,
                isLoaded = scene.isLoaded,
                isDirty = scene.isDirty,
                isValid = scene.IsValid(),
                rootCount = scene.IsValid() && scene.isLoaded ? scene.rootCount : 0,
                buildIndex = scene.buildIndex
            };
        }


        private static Scene FindOpenedScene(string openedSceneName)
        {
            var scene = GetOpenScenes()
                .FirstOrDefault(candidate => string.Equals(candidate.name, openedSceneName, StringComparison.Ordinal));
            if (!scene.IsValid())
            {
                throw new InvalidOperationException($"Opened scene '{openedSceneName}' was not found.");
            }

            return scene;
        }

        private static void SaveOrRejectDirtyScenes(bool saveDirtyScenes)
        {
            var dirtyScenes = GetOpenScenes()
                .Where(scene => scene.IsValid() && scene.isDirty)
                .ToArray();
            if (dirtyScenes.Length == 0)
            {
                return;
            }

            if (!saveDirtyScenes)
            {
                throw new InvalidOperationException(
                    "scene-open mode Single would discard dirty open scenes. Pass saveDirtyScenes:true to save first. Dirty scenes: "
                    + FormatSceneListForError(dirtyScenes));
            }

            foreach (var scene in dirtyScenes)
            {
                if (string.IsNullOrWhiteSpace(scene.path))
                {
                    throw new InvalidOperationException($"Dirty scene '{scene.name}' has no asset path and cannot be saved automatically.");
                }

                if (!EditorSceneManager.SaveScene(scene))
                {
                    throw new InvalidOperationException($"Unity failed to save dirty scene '{scene.name}' at '{scene.path}'.");
                }
            }
        }

        private static void ValidateSceneAssetPath(string? path, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException($"{parameterName} is required.", parameterName);
            }

            if (!path!.StartsWith("Assets/", StringComparison.Ordinal) || !path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"{parameterName} must start with 'Assets/' and end with '.unity'.", parameterName);
            }
        }

        private static void EnsureAssetParentFolder(string path)
        {
            var parent = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(parent))
            {
                throw new ArgumentException("Scene path must include a parent folder under Assets/.", nameof(path));
            }

            Directory.CreateDirectory(parent);
            AssetDatabase.Refresh();
        }

        private static void ValidateSearchFolders(IEnumerable<string> folders)
        {
            foreach (var folder in folders)
            {
                if (!string.Equals(folder, "Assets", StringComparison.Ordinal) && !folder.StartsWith("Assets/", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"searchInFolders entries must be 'Assets' or start with 'Assets/': '{folder}'.");
                }

                if (!AssetDatabase.IsValidFolder(folder))
                {
                    throw new ArgumentException($"searchInFolders entry is not a valid asset folder: '{folder}'.");
                }
            }
        }

        private static string[] ReadStringArray(JToken element, string name)
        {
            if (ReadArray(element, name) is not JArray valueArray)
            {
                return Array.Empty<string>();
            }

            return valueArray
                .Where(item => item.Type == JTokenType.String)
                .Select(item => item.Value<string>() ?? string.Empty)
                .ToArray();
        }

        private static string FormatSceneListForError(IEnumerable<Scene> scenes)
        {
            return string.Join(", ", scenes.Select(scene =>
            {
                var path = string.IsNullOrWhiteSpace(scene.path) ? "<unsaved>" : scene.path;
                return $"{scene.name} ({path})";
            }));
        }

    }
}
