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
    internal sealed partial class GameObjectBridgeService
    {
        internal static GameObjectQueryContext GetGameObjectQueryContext()
        {
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null && prefabStage.prefabContentsRoot != null)
            {
                var stageRoots = prefabStage.scene.IsValid() && prefabStage.scene.isLoaded
                    ? prefabStage.scene.GetRootGameObjects()
                    : new[] { prefabStage.prefabContentsRoot };
                return new GameObjectQueryContext
                {
                    Source = "prefabStage",
                    SceneName = prefabStage.scene.name,
                    ScenePath = prefabStage.scene.path,
                    PrefabAssetPath = prefabStage.assetPath,
                    Roots = stageRoots.Length > 0 ? stageRoots : new[] { prefabStage.prefabContentsRoot }
                };
            }

            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                throw new InvalidOperationException("No valid loaded active scene is available for GameObject queries.");
            }

            return new GameObjectQueryContext
            {
                Source = "activeScene",
                SceneName = scene.name,
                ScenePath = scene.path,
                PrefabAssetPath = string.Empty,
                Roots = scene.GetRootGameObjects()
            };
        }

        private static Dictionary<string, object?>? BuildHierarchyNode(
            GameObject gameObject,
            GameObjectQueryContext context,
            int depth,
            int maxDepth,
            bool includeComponents,
            int maxResults,
            ref int emitted,
            ref bool truncated,
            ref bool depthLimited)
        {
            if (emitted >= maxResults)
            {
                truncated = true;
                return null;
            }

            emitted++;
            var node = CreateGameObjectRef(gameObject, context, includeComponents);

            if (gameObject.transform.childCount == 0)
            {
                return node;
            }

            if (depth >= maxDepth)
            {
                node["childrenTruncatedByDepth"] = true;
                node["childCount"] = gameObject.transform.childCount;
                depthLimited = true;
                return node;
            }

            var children = new List<Dictionary<string, object?>>();
            foreach (Transform child in gameObject.transform)
            {
                var childNode = BuildHierarchyNode(child.gameObject, context, depth + 1, maxDepth, includeComponents, maxResults, ref emitted, ref truncated, ref depthLimited);
                if (childNode != null)
                {
                    children.Add(childNode);
                }

                if (truncated)
                {
                    break;
                }
            }

            if (children.Count > 0)
            {
                node["children"] = children.ToArray();
            }

            return node;
        }

        private static GameObject ResolveSingleGameObject(GameObjectQueryContext context, JToken args)
        {
            var path = ReadString(args, "path");
            var instanceId = ReadNullableInt(args, "instanceId");
            if (string.IsNullOrWhiteSpace(path) && !instanceId.HasValue)
            {
                throw new ArgumentException("Provide path or instanceId to resolve a GameObject.");
            }

            if (!string.IsNullOrWhiteSpace(path) && instanceId.HasValue)
            {
                throw new ArgumentException("Provide either path or instanceId, not both.");
            }

            return instanceId.HasValue
                ? ResolveGameObjectByInstanceId(context, instanceId.Value)
                : ResolveGameObjectByPath(context, path!);
        }

        internal static GameObject ResolveGameObjectByInstanceId(GameObjectQueryContext context, int instanceId)
        {
            var gameObject = EnumerateContextGameObjects(context)
                .FirstOrDefault(candidate => GetLegacyInstanceId(candidate) == instanceId);
            if (gameObject == null)
            {
                throw new InvalidOperationException($"No GameObject with instanceId {instanceId} was found in current {context.Source}.");
            }

            return gameObject;
        }

        internal static GameObject ResolveGameObjectByPath(GameObjectQueryContext context, string path)
        {
            var normalizedPath = NormalizeHierarchyPath(path);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                throw new ArgumentException("path cannot be empty.", nameof(path));
            }

            var allObjects = EnumerateContextGameObjects(context).ToArray();
            var exactMatches = allObjects
                .Where(gameObject => string.Equals(GetHierarchyPath(gameObject, context), normalizedPath, StringComparison.Ordinal))
                .ToArray();
            if (exactMatches.Length == 1)
            {
                return exactMatches[0];
            }

            if (exactMatches.Length > 1)
            {
                throw new InvalidOperationException($"Hierarchy path '{path}' matched multiple GameObjects: {FormatGameObjectCandidates(exactMatches, context)}.");
            }

            var unindexedMatches = allObjects
                .Where(gameObject => string.Equals(RemoveDuplicateIndexes(GetHierarchyPath(gameObject, context)), normalizedPath, StringComparison.Ordinal))
                .ToArray();
            if (unindexedMatches.Length == 1)
            {
                return unindexedMatches[0];
            }

            if (unindexedMatches.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Hierarchy path '{path}' is ambiguous. Use indexed path or instanceId. Candidates: "
                    + FormatGameObjectCandidates(unindexedMatches, context));
            }

            throw new InvalidOperationException($"No GameObject found at hierarchy path '{path}' in current {context.Source}.");
        }

        internal static IEnumerable<GameObject> EnumerateContextGameObjects(GameObjectQueryContext context)
        {
            foreach (var root in context.Roots)
            {
                foreach (var gameObject in EnumerateGameObjects(root))
                {
                    yield return gameObject;
                }
            }
        }

        private static IEnumerable<GameObject> EnumerateGameObjects(GameObject root)
        {
            yield return root;
            foreach (Transform child in root.transform)
            {
                foreach (var nested in EnumerateGameObjects(child.gameObject))
                {
                    yield return nested;
                }
            }
        }

        internal static int CountGameObjects(GameObject root)
        {
            var count = 1;
            foreach (Transform child in root.transform)
            {
                count += CountGameObjects(child.gameObject);
            }

            return count;
        }

        internal static Dictionary<string, object?> CreateGameObjectRef(GameObject gameObject, GameObjectQueryContext context, bool includeComponentTypes = true)
        {
            var output = new Dictionary<string, object?>
            {
                ["name"] = gameObject.name,
                ["path"] = GetHierarchyPath(gameObject, context),
                ["instanceId"] = GetLegacyInstanceId(gameObject),
                ["activeSelf"] = gameObject.activeSelf,
                ["activeInHierarchy"] = gameObject.activeInHierarchy,
                ["scenePath"] = gameObject.scene.path
            };
            if (includeComponentTypes)
            {
                output["componentTypes"] = GetComponentTypePreview(gameObject, out var truncated);
                output["componentTypesTruncated"] = truncated;
            }

            return output;
        }

        private static Dictionary<string, object?> CreateGameObjectDetail(GameObject gameObject, GameObjectQueryContext context, bool includeComponents)
        {
            var detail = CreateGameObjectRef(gameObject, context);
            detail["tag"] = gameObject.tag;
            detail["layer"] = gameObject.layer;
            detail["isStatic"] = gameObject.isStatic;
            detail["childCount"] = gameObject.transform.childCount;
            detail["sceneName"] = gameObject.scene.name;
            if (gameObject.transform.parent != null)
            {
                detail["parentPath"] = GetHierarchyPath(gameObject.transform.parent.gameObject, context);
            }

            if (includeComponents)
            {
                detail["components"] = gameObject.GetComponents<Component>()
                    .Select(CreateComponentSummary)
                    .ToArray();
            }

            return detail;
        }

        internal static Dictionary<string, object> CreateTransformSummary(Transform transform)
        {
            return new Dictionary<string, object>
            {
                ["position"] = Vector3ToDto(transform.position),
                ["localPosition"] = Vector3ToDto(transform.localPosition),
                ["rotationEuler"] = Vector3ToDto(transform.rotation.eulerAngles),
                ["localRotationEuler"] = Vector3ToDto(transform.localEulerAngles),
                ["scale"] = Vector3ToDto(transform.localScale)
            };
        }

        private static Dictionary<string, object> CreateTransformDto(Transform transform, bool isWorld)
        {
            return new Dictionary<string, object>
            {
                ["position"] = Vector3ToDto(isWorld ? transform.position : transform.localPosition),
                ["rotationEuler"] = Vector3ToDto(isWorld ? transform.rotation.eulerAngles : transform.localEulerAngles),
                ["scale"] = Vector3ToDto(isWorld ? transform.lossyScale : transform.localScale)
            };
        }

        private static Vector3 WorldScaleToLocalScale(Transform transform, Vector3 worldScale)
        {
            var parent = transform.parent;
            if (parent == null)
            {
                return worldScale;
            }

            var parentScale = parent.lossyScale;
            return new Vector3(
                DivideScaleComponent(worldScale.x, parentScale.x),
                DivideScaleComponent(worldScale.y, parentScale.y),
                DivideScaleComponent(worldScale.z, parentScale.z));
        }

        private static float DivideScaleComponent(float value, float parentScale)
        {
            if (Mathf.Approximately(parentScale, 0f))
            {
                throw new InvalidOperationException("Cannot set world scale because parent has a zero scale component.");
            }

            return value / parentScale;
        }

        private static Dictionary<string, float> Vector3ToDto(Vector3 value)
        {
            return new Dictionary<string, float>
            {
                ["x"] = value.x,
                ["y"] = value.y,
                ["z"] = value.z
            };
        }

        private static string? GetParentPath(GameObject gameObject, GameObjectQueryContext context)
        {
            return gameObject.transform.parent != null
                ? GetHierarchyPath(gameObject.transform.parent.gameObject, context)
                : null;
        }

        internal static GameObject? ResolveOptionalParentGameObject(GameObjectQueryContext context, JToken args)
        {
            var parentPath = ReadString(args, "parentPath");
            var parentInstanceId = ReadNullableInt(args, "parentInstanceId");
            if (string.IsNullOrWhiteSpace(parentPath) && !parentInstanceId.HasValue)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(parentPath) && parentInstanceId.HasValue)
            {
                throw new ArgumentException("Provide either parentPath or parentInstanceId, not both.");
            }

            return parentInstanceId.HasValue
                ? ResolveGameObjectByInstanceId(context, parentInstanceId.Value)
                : ResolveGameObjectByPath(context, parentPath!);
        }

        private static void ValidateParenting(GameObject gameObject, GameObject? parent)
        {
            if (parent == null)
            {
                return;
            }

            if (parent.transform == gameObject.transform)
            {
                throw new InvalidOperationException("Cannot parent a GameObject to itself.");
            }

            if (parent.transform.IsChildOf(gameObject.transform))
            {
                throw new InvalidOperationException("Cannot parent a GameObject under one of its descendants.");
            }
        }

        internal static void ValidateOptionalGameObjectName(string? value, string parameterName)
        {
            if (value == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"{parameterName} cannot be empty when provided.", parameterName);
            }

            if (value.Length > 256)
            {
                throw new ArgumentException($"{parameterName} must be 256 characters or fewer.", parameterName);
            }
        }

        internal static bool TryReadVector3(JToken args, string name, out Vector3 value)
        {
            value = default;
            if (ReadProperty(args, name) is not JToken element || element.Type == JTokenType.Null)
            {
                return false;
            }

            if (element is not JObject)
            {
                throw new ArgumentException($"{name} must be an object with finite x, y, and z numbers.", name);
            }

            value = new Vector3(
                ReadFiniteFloat(element, "x", name),
                ReadFiniteFloat(element, "y", name),
                ReadFiniteFloat(element, "z", name));
            return true;
        }

        internal static bool TryReadFlexibleVector3(JToken args, string name, Vector3 fallback, out Vector3 value)
        {
            value = fallback;
            if (ReadProperty(args, name) is not JToken element || element.Type == JTokenType.Null)
            {
                return false;
            }

            if (element is JArray array)
            {
                value = ReadVector3Array(array, name, fallback);
                return true;
            }

            if (element is JObject obj)
            {
                value = ReadVector3Object(obj, name, fallback);
                return true;
            }

            throw new ArgumentException($"{name} must be an object or array with finite vector numbers.", name);
        }

        private static Vector3 ReadVector3Array(JArray array, string parameterName, Vector3 fallback)
        {
            if (array.Count == 0 || array.Count > 3)
            {
                throw new ArgumentException($"{parameterName} array must contain one to three finite numbers.", parameterName);
            }

            return new Vector3(
                array.Count > 0 ? ReadFiniteFloatValue(array[0], "x", parameterName) : fallback.x,
                array.Count > 1 ? ReadFiniteFloatValue(array[1], "y", parameterName) : fallback.y,
                array.Count > 2 ? ReadFiniteFloatValue(array[2], "z", parameterName) : fallback.z);
        }

        private static Vector3 ReadVector3Object(JObject obj, string parameterName, Vector3 fallback)
        {
            var value = fallback;
            var readAny = false;
            if (TryReadOptionalFloat(obj, parameterName, out var x, "x", "X", "0"))
            {
                value.x = x;
                readAny = true;
            }

            if (TryReadOptionalFloat(obj, parameterName, out var y, "y", "Y", "1"))
            {
                value.y = y;
                readAny = true;
            }

            if (TryReadOptionalFloat(obj, parameterName, out var z, "z", "Z", "2"))
            {
                value.z = z;
                readAny = true;
            }

            if (!readAny)
            {
                throw new ArgumentException($"{parameterName} must include at least one x, y, or z component.", parameterName);
            }

            return value;
        }

        private static bool TryReadOptionalFloat(JObject obj, string parameterName, out float value, params string[] componentNames)
        {
            value = default;
            foreach (var componentName in componentNames)
            {
                if (ReadProperty(obj, componentName) is not JToken component || component.Type == JTokenType.Null)
                {
                    continue;
                }

                value = ReadFiniteFloatValue(component, componentName, parameterName);
                return true;
            }

            return false;
        }

        private static float ReadFiniteFloat(JToken element, string componentName, string parameterName)
        {
            double number;
            if (ReadProperty(element, componentName) is not JToken component)
            {
                throw new ArgumentException($"{parameterName}.{componentName} is required and must be a finite number.", parameterName);
            }

            return ReadFiniteFloatValue(component, componentName, parameterName);
        }

        private static float ReadFiniteFloatValue(JToken component, string componentName, string parameterName)
        {
            double number;
            if (component.Type == JTokenType.Integer || component.Type == JTokenType.Float)
            {
                try
                {
                    number = component.Value<double>();
                }
                catch (Exception)
                {
                    throw new ArgumentException($"{parameterName}.{componentName} must be a finite number.", parameterName);
                }
            }
            else if (component.Type == JTokenType.String)
            {
                if (!double.TryParse(component.Value<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                {
                    throw new ArgumentException($"{parameterName}.{componentName} must be a finite number.", parameterName);
                }
            }
            else
            {
                throw new ArgumentException($"{parameterName}.{componentName} is required and must be a finite number.", parameterName);
            }

            if (double.IsNaN(number) || double.IsInfinity(number) || number < float.MinValue || number > float.MaxValue)
            {
                throw new ArgumentException($"{parameterName}.{componentName} must be a finite single-precision number.", parameterName);
            }

            return (float)number;
        }

        internal static void MarkGameObjectMutationDirty(GameObject gameObject)
        {
            EditorUtility.SetDirty(gameObject);
            EditorUtility.SetDirty(gameObject.transform);
            if (PrefabUtility.IsPartOfPrefabInstance(gameObject))
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(gameObject);
                PrefabUtility.RecordPrefabInstancePropertyModifications(gameObject.transform);
            }

            if (gameObject.scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(gameObject.scene);
                TrackDirtyPrefabStageForScene(gameObject.scene);
            }
        }

        private static void TrackDirtyPrefabStageForScene(Scene scene)
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null || string.IsNullOrWhiteSpace(stage.assetPath) || !stage.scene.IsValid())
            {
                return;
            }

            if (stage.scene.handle != scene.handle)
            {
                return;
            }

            RuntimeState.DirtyPrefabStageAssetPaths.Add(stage.assetPath);
        }

        internal static void RepaintEditorAfterMutation()
        {
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }

    }
}
