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
    internal sealed class PrefabBridgeService : BridgeDomainServiceBase
    {
        public object Open(JToken args)
        {
            var prefabPath = ReadString(args, "prefabPath");
            ValidatePrefabAssetPath(prefabPath, "prefabPath");
            RejectDirtyPrefabStageForOpen(prefabPath!);
            LoadPrefabAssetOrThrow(prefabPath!, "prefabPath");

            var stage = PrefabStageUtility.OpenPrefab(prefabPath!);
            if (stage == null || stage.prefabContentsRoot == null)
            {
                throw new InvalidOperationException($"Unity failed to open prefab stage for '{prefabPath}'.");
            }

            GameObjectBridgeService.RepaintEditorAfterMutation();
            return new
            {
                opened = true,
                prefabPath = stage.assetPath,
                rootName = stage.prefabContentsRoot.name,
                rootInstanceId = GetLegacyInstanceId(stage.prefabContentsRoot),
                isDirty = IsPrefabStageDirty(stage)
            };
        }

        public object Close(JToken args)
        {
            var stage = GetCurrentPrefabStageOrThrow();
            var closedPrefabPath = stage.assetPath;
            var saveBeforeClose = ReadBool(args, "saveBeforeClose", false);
            var savedBeforeClose = false;
            if (IsPrefabStageDirty(stage))
            {
                if (!saveBeforeClose)
                {
                    throw new InvalidOperationException(
                        $"Prefab stage '{closedPrefabPath}' has unsaved changes. Pass saveBeforeClose:true to save before closing.");
                }

                SavePrefabStage(stage);
                savedBeforeClose = true;
            }

            StageUtility.GoToMainStage();
            RuntimeState.DirtyPrefabStageAssetPaths.Remove(closedPrefabPath);
            GameObjectBridgeService.RepaintEditorAfterMutation();
            return new
            {
                closed = true,
                prefabPath = closedPrefabPath,
                savedBeforeClose
            };
        }

        public object Save(JToken args)
        {
            var stage = GetCurrentPrefabStageOrThrow();
            var saved = SavePrefabStage(stage);
            return new
            {
                saved,
                prefabPath = stage.assetPath,
                isDirty = IsPrefabStageDirty(stage)
            };
        }

        public object Create(JToken args)
        {
            var prefabPath = ReadString(args, "prefabPath");
            ValidatePrefabAssetPath(prefabPath, "prefabPath");
            ValidatePrefabParentFolder(prefabPath!);
            var overwrite = ReadBool(args, "overwrite", false);
            ValidatePrefabOverwrite(prefabPath!, overwrite);

            var context = GameObjectBridgeService.GetGameObjectQueryContext();
            var source = ResolvePrefabSourceGameObject(context, args);
            ValidatePrefabSourceCanBeSaved(source);

            var connectGameObjectToPrefab = ReadBool(args, "connectGameObjectToPrefab", false);
            bool savedSuccessfully;
            if (connectGameObjectToPrefab)
            {
                PrefabUtility.SaveAsPrefabAssetAndConnect(source, prefabPath!, InteractionMode.AutomatedAction, out savedSuccessfully);
                GameObjectBridgeService.MarkGameObjectMutationDirty(source);
            }
            else
            {
                PrefabUtility.SaveAsPrefabAsset(source, prefabPath!, out savedSuccessfully);
            }

            if (!savedSuccessfully)
            {
                throw new InvalidOperationException($"Unity failed to save prefab asset at '{prefabPath}'. Check Unity console for details.");
            }

            AssetDatabase.ImportAsset(prefabPath!, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            GameObjectBridgeService.RepaintEditorAfterMutation();

            return new
            {
                created = true,
                overwritten = overwrite,
                connected = connectGameObjectToPrefab,
                prefabPath,
                guid = AssetDatabase.AssetPathToGUID(prefabPath!),
                connectedInstanceId = connectGameObjectToPrefab ? GetLegacyInstanceId(source) : 0
            };
        }

        public object Instantiate(JToken args)
        {
            var prefabPath = ReadString(args, "prefabPath");
            ValidatePrefabAssetPath(prefabPath, "prefabPath");
            var prefabAsset = LoadPrefabAssetOrThrow(prefabPath!, "prefabPath");

            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            var context = GameObjectBridgeService.GetGameObjectQueryContext();
            if (stage != null && string.Equals(stage.assetPath, prefabPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Cannot instantiate a prefab into its own prefab stage because that would create cyclic nesting.");
            }

            var targetScene = stage != null && stage.scene.IsValid()
                ? stage.scene
                : SceneManager.GetActiveScene();
            if (!targetScene.IsValid() || !targetScene.isLoaded)
            {
                throw new InvalidOperationException("No valid loaded scene is available for prefab instantiation.");
            }

            var parentSpecified = HasProperty(args, "parentPath") || HasProperty(args, "parentInstanceId");
            var parent = parentSpecified
                ? GameObjectBridgeService.ResolveOptionalParentGameObject(context, args)
                : stage != null
                    ? stage.prefabContentsRoot
                    : null;
            if (parent != null && parent.scene != targetScene)
            {
                throw new InvalidOperationException("Resolved parent is not in the target scene or prefab stage.");
            }

            var newName = ReadString(args, "name");
            GameObjectBridgeService.ValidateOptionalGameObjectName(newName, "name");
            var hasPosition = GameObjectBridgeService.TryReadVector3(args, "position", out var position);
            var hasRotation = GameObjectBridgeService.TryReadVector3(args, "rotationEuler", out var rotationEuler);
            var hasScale = GameObjectBridgeService.TryReadVector3(args, "scale", out var scale);

            var created = PrefabUtility.InstantiatePrefab(prefabAsset, targetScene) as GameObject;
            if (created == null)
            {
                throw new InvalidOperationException($"Unity failed to instantiate prefab asset at '{prefabPath}'.");
            }

            Undo.RegisterCreatedObjectUndo(created, "ChievFX MCP Instantiate Prefab");
            if (parent != null)
            {
                created.transform.SetParent(parent.transform, false);
            }

            if (!string.IsNullOrWhiteSpace(newName))
            {
                created.name = newName!;
            }

            if (hasPosition)
            {
                created.transform.position = position;
            }

            if (hasRotation)
            {
                created.transform.rotation = Quaternion.Euler(rotationEuler);
            }

            if (hasScale)
            {
                created.transform.localScale = scale;
            }

            GameObjectBridgeService.MarkGameObjectMutationDirty(created);
            if (parent != null)
            {
                GameObjectBridgeService.MarkGameObjectMutationDirty(parent);
            }

            GameObjectBridgeService.RepaintEditorAfterMutation();

            var afterContext = GameObjectBridgeService.GetGameObjectQueryContext();
            return new
            {
                prefabAssetPath = prefabPath,
                parentPath = parent != null ? GameObjectBridgeService.GetHierarchyPath(parent, afterContext) : null,
                path = GameObjectBridgeService.GetHierarchyPath(created, afterContext),
                instanceId = GetLegacyInstanceId(created)
            };
        }

        private static void RejectDirtyPrefabStageForOpen(string targetPrefabPath)
        {
            var currentStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (currentStage == null || !IsPrefabStageDirty(currentStage))
            {
                return;
            }

            throw new InvalidOperationException(
                $"Current prefab stage '{currentStage.assetPath}' has unsaved changes. "
                + $"Save or close it before opening '{targetPrefabPath}'.");
        }

        private static PrefabStage GetCurrentPrefabStageOrThrow()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null || stage.prefabContentsRoot == null)
            {
                throw new InvalidOperationException("No prefab stage is currently open.");
            }

            return stage;
        }

        private static bool SavePrefabStage(PrefabStage stage)
        {
            if (stage.prefabContentsRoot == null)
            {
                throw new InvalidOperationException("Current prefab stage has no prefab contents root.");
            }

            var savedSuccessfully = SavePrefabStageContents(stage);
            if (!savedSuccessfully)
            {
                throw new InvalidOperationException($"Unity failed to save prefab stage '{stage.assetPath}'. Check Unity console for details.");
            }

            ClearUnityObjectTreeDirty(stage.prefabContentsRoot);
            stage.ClearDirtiness();
            RuntimeState.DirtyPrefabStageAssetPaths.Remove(stage.assetPath);
            AssetDatabase.ImportAsset(stage.assetPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            GameObjectBridgeService.RepaintEditorAfterMutation();
            return true;
        }

        private static bool SavePrefabStageContents(PrefabStage stage)
        {
            var savePrefabMethod = typeof(PrefabStage).GetMethod("SavePrefab", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (savePrefabMethod != null)
            {
                try
                {
                    var result = savePrefabMethod.Invoke(stage, Array.Empty<object>());
                    if (result is bool saved)
                    {
                        return saved;
                    }
                }
                catch (TargetInvocationException ex) when (ex.InnerException != null)
                {
                    throw new InvalidOperationException(ex.InnerException.Message, ex.InnerException);
                }
            }

            PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, stage.assetPath, out var savedSuccessfully);
            return savedSuccessfully;
        }

        private static bool IsPrefabStageDirty(PrefabStage stage)
        {
            if (stage.scene.IsValid() && stage.scene.isDirty)
            {
                return true;
            }

            if (RuntimeState.DirtyPrefabStageAssetPaths.Contains(stage.assetPath))
            {
                return true;
            }

            return stage.prefabContentsRoot != null && IsUnityObjectTreeDirty(stage.prefabContentsRoot);
        }

        private static void ClearUnityObjectTreeDirty(GameObject root)
        {
            EditorUtility.ClearDirty(root);
            EditorUtility.ClearDirty(root.transform);
            foreach (var component in root.GetComponents<Component>())
            {
                if (component != null)
                {
                    EditorUtility.ClearDirty(component);
                }
            }

            foreach (Transform child in root.transform)
            {
                ClearUnityObjectTreeDirty(child.gameObject);
            }
        }

        private static bool IsUnityObjectTreeDirty(GameObject root)
        {
            if (EditorUtility.IsDirty(root) || EditorUtility.IsDirty(root.transform))
            {
                return true;
            }

            foreach (var component in root.GetComponents<Component>())
            {
                if (component != null && EditorUtility.IsDirty(component))
                {
                    return true;
                }
            }

            foreach (Transform child in root.transform)
            {
                if (IsUnityObjectTreeDirty(child.gameObject))
                {
                    return true;
                }
            }

            return false;
        }

        private static object CreatePrefabStageState(PrefabStage? stage)
        {
            if (stage == null || stage.prefabContentsRoot == null)
            {
                return new
                {
                    isOpen = false
                };
            }

            return new
            {
                isOpen = true,
                prefabPath = stage.assetPath,
                rootName = stage.prefabContentsRoot.name,
                root = GameObjectBridgeService.CreateGameObjectRef(stage.prefabContentsRoot, CreatePrefabStageContext(stage), includeComponentTypes: false),
                isDirty = IsPrefabStageDirty(stage)
            };
        }

        private static GameObjectQueryContext CreatePrefabStageContext(PrefabStage stage)
        {
            var stageRoots = stage.scene.IsValid() && stage.scene.isLoaded
                ? stage.scene.GetRootGameObjects()
                : stage.prefabContentsRoot != null
                    ? new[] { stage.prefabContentsRoot }
                    : Array.Empty<GameObject>();
            return new GameObjectQueryContext
            {
                Source = "prefabStage",
                SceneName = stage.scene.name,
                ScenePath = stage.scene.path,
                PrefabAssetPath = stage.assetPath,
                Roots = stageRoots.Length > 0 ? stageRoots : Array.Empty<GameObject>()
            };
        }

        private static Dictionary<string, object?> CreatePrefabAssetRef(string prefabPath)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            return new Dictionary<string, object?>
            {
                ["path"] = prefabPath,
                ["guid"] = AssetDatabase.AssetPathToGUID(prefabPath),
                ["name"] = Path.GetFileNameWithoutExtension(prefabPath),
                ["rootName"] = asset != null ? asset.name : string.Empty,
                ["assetInstanceId"] = GetLegacyInstanceId(asset),
                ["prefabAssetType"] = asset != null ? PrefabUtility.GetPrefabAssetType(asset).ToString() : string.Empty
            };
        }

        private static GameObject ResolvePrefabSourceGameObject(GameObjectQueryContext context, JToken args)
        {
            var sourcePath = ReadString(args, "sourcePath");
            var sourceInstanceId = ReadNullableInt(args, "sourceInstanceId");
            if (string.IsNullOrWhiteSpace(sourcePath) && !sourceInstanceId.HasValue)
            {
                throw new ArgumentException("Provide exactly one of sourcePath or sourceInstanceId.");
            }

            if (!string.IsNullOrWhiteSpace(sourcePath) && sourceInstanceId.HasValue)
            {
                throw new ArgumentException("Provide exactly one of sourcePath or sourceInstanceId, not both.");
            }

            return sourceInstanceId.HasValue
                ? GameObjectBridgeService.ResolveGameObjectByInstanceId(context, sourceInstanceId.Value)
                : GameObjectBridgeService.ResolveGameObjectByPath(context, sourcePath!);
        }

        private static void ValidatePrefabSourceCanBeSaved(GameObject source)
        {
            if (EditorUtility.IsPersistent(source))
            {
                throw new ArgumentException("source GameObject must be a scene or prefab-stage object, not a prefab asset object.");
            }

            if (PrefabUtility.IsPartOfPrefabInstance(source))
            {
                var outermostRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(source);
                if (outermostRoot != source)
                {
                    throw new ArgumentException("source GameObject inside a prefab instance must be the outermost prefab instance root.");
                }
            }
        }

        private static void ValidatePrefabOverwrite(string prefabPath, bool overwrite)
        {
            if (!AssetDatabase.LoadAssetAtPath<Object>(prefabPath))
            {
                return;
            }

            if (!overwrite)
            {
                throw new InvalidOperationException($"Prefab asset already exists at '{prefabPath}'. Pass overwrite:true to replace it.");
            }
        }

        private static GameObject LoadPrefabAssetOrThrow(string prefabPath, string parameterName)
        {
            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefabAsset == null)
            {
                throw new InvalidOperationException($"Prefab asset not found at '{prefabPath}'.");
            }

            if (PrefabUtility.GetPrefabAssetType(prefabAsset) == PrefabAssetType.NotAPrefab)
            {
                throw new ArgumentException($"{parameterName} must point to a prefab asset.", parameterName);
            }

            return prefabAsset;
        }

        private static void ValidatePrefabAssetPath(string? path, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException($"{parameterName} is required.", parameterName);
            }

            if (!path!.StartsWith("Assets/", StringComparison.Ordinal) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"{parameterName} must start with 'Assets/' and end with '.prefab'.", parameterName);
            }
        }

        private static void ValidatePrefabParentFolder(string prefabPath)
        {
            var folder = Path.GetDirectoryName(prefabPath)?.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(folder) || !AssetDatabase.IsValidFolder(folder))
            {
                throw new ArgumentException($"Prefab parent folder does not exist: '{folder}'.");
            }
        }

    }
}
