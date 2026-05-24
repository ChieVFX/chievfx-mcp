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
    internal sealed partial class GameObjectBridgeService : BridgeDomainServiceBase
    {
        public object Create(JToken args)
        {
            var context = GetGameObjectQueryContext();
            var path = ReadString(args, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("path is required.", nameof(path));
            }

            var normalizedPath = NormalizeHierarchyPath(path!);
            var segments = SplitHierarchyPath(normalizedPath);
            if (segments.Length == 0)
            {
                throw new ArgumentException("path cannot be empty.", nameof(path));
            }

            if (TryResolveGameObjectByPath(context, normalizedPath, out var existing))
            {
                throw new InvalidOperationException($"GameObject already exists at '{GetHierarchyPath(existing, context)}'.");
            }

            var name = UnescapePathSegment(segments[segments.Length - 1]);
            ValidateOptionalGameObjectName(name, "path");
            var parent = segments.Length > 1
                ? ResolveGameObjectByPath(context, string.Join("/", segments.Take(segments.Length - 1)))
                : null;

            var gameObject = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(gameObject, "ChievFX MCP Create GameObject");
            if (parent != null)
            {
                gameObject.transform.SetParent(parent.transform, false);
            }
            else if (context.Roots.Length > 0 && context.Roots[0].scene.IsValid())
            {
                SceneManager.MoveGameObjectToScene(gameObject, context.Roots[0].scene);
            }

            MarkGameObjectMutationDirty(gameObject);
            if (parent != null)
            {
                MarkGameObjectMutationDirty(parent);
            }

            RepaintEditorAfterMutation();

            var afterContext = GetGameObjectQueryContext();
            return new
            {
                success = true,
                path = GetHierarchyPath(gameObject, afterContext),
                instanceId = GetLegacyInstanceId(gameObject)
            };
        }

        public object Hierarchy(JToken args)
        {
            var context = GetGameObjectQueryContext();
            var path = ReadString(args, "path");
            var maxDepth = ClampInt(ReadInt(args, "maxDepth", DefaultGameObjectMaxDepth), 0, HardGameObjectMaxDepth);
            var includeComponents = ReadBool(args, "includeComponents", false);
            var maxResults = ClampInt(ReadInt(args, "maxResults", DefaultGameObjectMaxResults), 1, HardGameObjectMaxResults);
            var roots = string.IsNullOrWhiteSpace(path)
                ? context.Roots
                : new[] { ResolveGameObjectByPath(context, path!) };
            var totalObjects = roots.Sum(CountGameObjects);
            var emitted = 0;
            var truncated = false;
            var depthLimited = false;
            var nodes = new List<Dictionary<string, object?>>();

            foreach (var root in roots)
            {
                var node = BuildHierarchyNode(root, context, 0, maxDepth, includeComponents, maxResults, ref emitted, ref truncated, ref depthLimited);
                if (node != null)
                {
                    nodes.Add(node);
                }

                if (truncated)
                {
                    break;
                }
            }

            return new
            {
                source = context.Source,
                sceneName = context.SceneName,
                scenePath = context.ScenePath,
                prefabAssetPath = context.PrefabAssetPath,
                count = emitted,
                totalObjects,
                maxDepth,
                maxResults,
                truncated,
                depthLimited,
                roots = nodes.ToArray()
            };
        }

        public object Find(JToken args)
        {
            var context = GetGameObjectQueryContext();
            var path = ReadString(args, "path");
            var name = ReadString(args, "name");
            var namePattern = ReadString(args, "namePattern");
            var componentType = ReadString(args, "componentType");
            var instanceId = ReadNullableInt(args, "instanceId");
            var includeInactive = ReadBool(args, "includeInactive", false);
            var includeComponents = ReadBool(args, "includeComponents", false);
            var includeDetails = ReadBool(args, "includeDetails", false) || includeComponents || instanceId.HasValue;
            var maxResults = ClampInt(ReadInt(args, "maxResults", DefaultGameObjectMaxResults), 1, HardGameObjectMaxResults);
            ValidateWildcardPattern(namePattern, "namePattern");
            ValidateComponentTypeText(componentType, required: false);

            var sourceObjects = instanceId.HasValue || (includeDetails && !string.IsNullOrWhiteSpace(path))
                ? new[] { ResolveSingleGameObject(context, args) }
                : string.IsNullOrWhiteSpace(path)
                    ? EnumerateContextGameObjects(context)
                    : EnumerateGameObjects(ResolveGameObjectByPath(context, path!));
            var matches = sourceObjects
                .Where(gameObject => includeInactive || gameObject.activeInHierarchy)
                .Where(gameObject => string.IsNullOrWhiteSpace(name) || string.Equals(gameObject.name, name, StringComparison.Ordinal))
                .Where(gameObject => string.IsNullOrWhiteSpace(namePattern) || WildcardMatches(gameObject.name, namePattern!))
                .Where(gameObject => string.IsNullOrWhiteSpace(componentType) || HasMatchingComponent(gameObject, componentType!))
                .ToList();
            var selected = matches
                .Take(maxResults)
                .Select(gameObject => includeDetails
                    ? CreateGameObjectDetail(gameObject, context, includeComponents)
                    : CreateGameObjectRef(gameObject, context))
                .ToArray();

            return new
            {
                source = context.Source,
                sceneName = context.SceneName,
                scenePath = context.ScenePath,
                prefabAssetPath = context.PrefabAssetPath,
                count = selected.Length,
                totalMatches = matches.Count,
                maxResults,
                includeDetails,
                truncated = matches.Count > selected.Length,
                objects = selected
            };
        }

        public object GetComponent(JToken args)
        {
            var context = GetGameObjectQueryContext();
            var componentType = ReadString(args, "componentType");
            ValidateComponentTypeText(componentType, required: true);
            var componentIndex = ReadComponentIndex(args);

            var gameObject = ResolveSingleGameObject(context, args);
            var matches = GetMatchingComponents(gameObject, componentType!).ToArray();
            if (matches.Length == 0)
            {
                throw new InvalidOperationException($"GameObject '{GetHierarchyPath(gameObject, context)}' has no component matching '{componentType}'.");
            }

            if (componentIndex >= matches.Length)
            {
                throw new InvalidOperationException(
                    $"GameObject '{GetHierarchyPath(gameObject, context)}' has {matches.Length} components matching '{componentType}', "
                    + $"but componentIndex {componentIndex} was requested.");
            }

            var includeSerializedData = !HasProperty(args, "includeSerializedData") || ReadBool(args, "includeSerializedData", false);
            var isDebug = ReadBool(args, "isDebug", false);
            var serializedTruncated = false;
            return new
            {
                source = context.Source,
                sceneName = context.SceneName,
                scenePath = context.ScenePath,
                prefabAssetPath = context.PrefabAssetPath,
                gameObject = CreateGameObjectRef(gameObject, context),
                componentIndex,
                component = CreateComponentDetail(matches[componentIndex], includeSerializedData, isDebug, ref serializedTruncated),
                serializedDataTruncated = serializedTruncated
            };
        }

        public object Update(JToken args)
        {
            var context = GetGameObjectQueryContext();
            var gameObject = ResolveSingleGameObject(context, args);
            var changed = false;

            Undo.RecordObject(gameObject, "ChievFX MCP Update GameObject");

            var newName = ReadString(args, "newName");
            if (newName != null)
            {
                ValidateOptionalGameObjectName(newName, "newName");
                gameObject.name = newName;
                changed = true;
            }

            var tag = ReadString(args, "tag");
            if (tag != null)
            {
                ValidateTag(tag);
                gameObject.tag = tag;
                changed = true;
            }

            if (ReadProperty(args, "layer") is JToken layerToken && layerToken.Type != JTokenType.Null)
            {
                gameObject.layer = ReadLayer(layerToken, "layer");
                changed = true;
            }

            var staticToken = ReadProperty(args, "isStatic") ?? ReadProperty(args, "static");
            if (staticToken != null && staticToken.Type == JTokenType.Boolean)
            {
                gameObject.isStatic = staticToken.Value<bool>();
                changed = true;
            }
            else if (staticToken is not null && staticToken.Type != JTokenType.Null)
            {
                throw new ArgumentException("isStatic/static must be a boolean.");
            }

            var activeToken = ReadProperty(args, "activeSelf") ?? ReadProperty(args, "enabled");
            if (activeToken != null && activeToken.Type == JTokenType.Boolean)
            {
                gameObject.SetActive(activeToken.Value<bool>());
                changed = true;
            }
            else if (activeToken is not null && activeToken.Type != JTokenType.Null)
            {
                throw new ArgumentException("activeSelf/enabled must be a boolean.");
            }

            if (ReadProperty(args, "staticFlags") is JToken staticFlagsToken && staticFlagsToken.Type != JTokenType.Null)
            {
                ApplyStaticFlags(gameObject, staticFlagsToken);
                changed = true;
            }

            if (ReadProperty(args, "lightBakeFlags") is JToken lightBakeFlagsToken && lightBakeFlagsToken.Type != JTokenType.Null)
            {
                ApplyLightBakeFlags(gameObject, lightBakeFlagsToken);
                changed = true;
            }

            if (!changed)
            {
                throw new ArgumentException("Provide at least one GameObject field to update.");
            }

            MarkGameObjectMutationDirty(gameObject);
            RepaintEditorAfterMutation();

            var afterContext = GetGameObjectQueryContext();
            return new
            {
                success = true,
                path = GetHierarchyPath(gameObject, afterContext),
                instanceId = GetLegacyInstanceId(gameObject)
            };
        }

        public object UpdateOrCreateComponent(JToken args)
        {
            var context = GetGameObjectQueryContext();
            var componentType = ReadString(args, "componentType");
            ValidateComponentTypeText(componentType, required: true);

            var gameObject = ResolveSingleGameObject(context, args);
            var componentIndex = ReadComponentIndex(args);
            var createIfNone = ReadBool(args, "isCreateIfNone", ReadBool(args, "createIfNone", true));
            var writeNonSerialized = ReadBool(args, "writeNonSerialized", false);
            var properties = ReadComponentPropertyPatch(args);

            var matches = GetMatchingComponents(gameObject, componentType!).ToArray();
            Component component;
            var created = false;
            if (componentIndex < matches.Length)
            {
                component = matches[componentIndex];
            }
            else
            {
                if (!createIfNone)
                {
                    throw new InvalidOperationException(
                        $"GameObject '{GetHierarchyPath(gameObject, context)}' has {matches.Length} components matching '{componentType}', "
                        + $"but componentIndex {componentIndex} was requested.");
                }

                if (componentIndex > matches.Length)
                {
                    throw new InvalidOperationException(
                        $"Cannot create componentIndex {componentIndex} for '{componentType}' because next creatable index is {matches.Length}.");
                }

                var type = ResolveComponentType(componentType!);
                component = Undo.AddComponent(gameObject, type);
                created = true;
            }

            var updatedProperties = properties.Count > 0
                ? ApplyComponentPropertyPatch(component, properties, writeNonSerialized)
                : Array.Empty<string>();

            MarkComponentMutationDirty(component);
            MarkGameObjectMutationDirty(gameObject);
            RepaintEditorAfterMutation();

            var finalMatches = GetMatchingComponents(gameObject, componentType!).ToArray();
            var finalIndex = Array.IndexOf(finalMatches, component);
            return new
            {
                success = true,
                created,
                componentIndex = finalIndex >= 0 ? finalIndex : componentIndex,
                component = CreateComponentMutationSummary(component, updatedProperties, created)
            };
        }

        public object GetTransform(JToken args)
        {
            var context = GetGameObjectQueryContext();
            var gameObject = ResolveSingleGameObject(context, args);
            var transform = gameObject.transform;
            var isWorld = ReadBool(args, "isWorld", false);

            return new
            {
                success = true,
                isWorld,
                transform = CreateTransformDto(transform, isWorld)
            };
        }

        public object UpdateTransform(JToken args)
        {
            var context = GetGameObjectQueryContext();
            var gameObject = ResolveSingleGameObject(context, args);
            var transform = gameObject.transform;
            var isWorld = ReadBool(args, "isWorld", false);

            var currentPosition = isWorld ? transform.position : transform.localPosition;
            var currentRotationEuler = isWorld ? transform.rotation.eulerAngles : transform.localEulerAngles;
            var currentScale = isWorld ? transform.lossyScale : transform.localScale;
            var hasPosition = TryReadFlexibleVector3(args, "position", currentPosition, out var position);
            var hasRotation = TryReadFlexibleVector3(args, "rotationEuler", currentRotationEuler, out var rotationEuler);
            var hasScale = TryReadFlexibleVector3(args, "scale", currentScale, out var scale);
            if (!hasPosition && !hasRotation && !hasScale)
            {
                throw new ArgumentException("Provide at least one transform field to update.");
            }

            Undo.RecordObject(transform, "ChievFX MCP Update Transform");

            if (hasPosition)
            {
                if (isWorld)
                {
                    transform.position = position;
                }
                else
                {
                    transform.localPosition = position;
                }
            }

            if (hasRotation)
            {
                if (isWorld)
                {
                    transform.rotation = Quaternion.Euler(rotationEuler);
                }
                else
                {
                    transform.localEulerAngles = rotationEuler;
                }
            }

            if (hasScale)
            {
                transform.localScale = isWorld ? WorldScaleToLocalScale(transform, scale) : scale;
            }

            MarkGameObjectMutationDirty(gameObject);
            RepaintEditorAfterMutation();

            return new
            {
                success = true
            };
        }

        public object SetParent(JToken args)
        {
            var context = GetGameObjectQueryContext();
            var gameObject = ResolveSingleGameObject(context, args);
            var parent = ResolveOptionalParentGameObject(context, args);
            ValidateParenting(gameObject, parent);

            var transform = gameObject.transform;
            var parentTransform = parent != null ? parent.transform : null;
            var worldPositionStays = ReadBool(args, "worldPositionStays", true);

            if (worldPositionStays)
            {
                Undo.SetTransformParent(transform, parentTransform, "ChievFX MCP Set Parent");
            }
            else
            {
                Undo.RecordObject(transform, "ChievFX MCP Set Parent");
                transform.SetParent(parentTransform, false);
            }

            MarkGameObjectMutationDirty(gameObject);
            if (parent != null)
            {
                MarkGameObjectMutationDirty(parent);
            }

            RepaintEditorAfterMutation();

            return new
            {
                success = true
            };
        }

        public object Duplicate(JToken args)
        {
            var context = GetGameObjectQueryContext();
            var source = ResolveSingleGameObject(context, args);
            var newName = ReadString(args, "newName");
            ValidateOptionalGameObjectName(newName, "newName");
            var includeChildren = ReadBool(args, "includeChildren", true);
            var count = ReadInt(args, "count", 1);
            if (count < 1 || count > 100)
            {
                throw new ArgumentException("count must be between 1 and 100.", nameof(count));
            }

            var parentSpecified = HasProperty(args, "parentPath") || HasProperty(args, "parentInstanceId");
            var parent = parentSpecified
                ? ResolveOptionalParentGameObject(context, args)
                : source.transform.parent != null
                    ? source.transform.parent.gameObject
                    : null;
            var parentTransform = parent != null ? parent.transform : null;
            ValidateExclusiveTransformOptions(args, "position", "positionOffset");
            ValidateExclusiveTransformOptions(args, "rotationEuler", "rotationEulerOffset", "euler", "eulerOffset");
            ValidateExclusiveTransformOptions(args, "scale", "scaleOffset");

            var clones = new List<GameObject>();
            for (var i = 0; i < count; i++)
            {
                var clone = Object.Instantiate(source);
                Undo.RegisterCreatedObjectUndo(clone, "ChievFX MCP Duplicate GameObject");
                clone.transform.SetParent(parentTransform, true);
                if (!includeChildren)
                {
                    RemoveClonedChildren(clone);
                }

                if (!string.IsNullOrWhiteSpace(newName))
                {
                    clone.name = count == 1 ? newName! : $"{newName} {i + 1}";
                }

                ApplyDuplicateTransformOptions(args, clone.transform, i + 1);
                MarkGameObjectMutationDirty(clone);
                clones.Add(clone);
            }
            if (parent != null)
            {
                MarkGameObjectMutationDirty(parent);
            }

            RepaintEditorAfterMutation();

            var afterContext = GetGameObjectQueryContext();
            var duplicates = clones
                .Select(clone => new Dictionary<string, object?>
                {
                    ["name"] = clone.name,
                    ["path"] = GetHierarchyPath(clone, afterContext),
                    ["instanceId"] = GetLegacyInstanceId(clone),
                    ["childCount"] = clone.transform.childCount
                })
                .ToArray();
            var first = duplicates[0];
            return new
            {
                success = true,
                path = first["path"],
                instanceId = first["instanceId"],
                duplicatedCount = duplicates.Length,
                includeChildren,
                parentPath = parent != null ? GetHierarchyPath(parent, afterContext) : null,
                duplicates
            };
        }

        private static void ValidateExclusiveTransformOptions(JToken args, params string[] names)
        {
            var provided = names.Count(name => HasProperty(args, name));
            if (provided > 1)
            {
                throw new ArgumentException($"Provide only one of {string.Join(", ", names)}.");
            }
        }

        private static void ApplyDuplicateTransformOptions(JToken args, Transform transform, int duplicateNumber)
        {
            var currentPosition = transform.localPosition;
            if (TryReadFlexibleVector3(args, "position", currentPosition, out var position))
            {
                transform.localPosition = position;
            }
            else if (TryReadFlexibleVector3(args, "positionOffset", Vector3.zero, out var positionOffset))
            {
                transform.localPosition = currentPosition + positionOffset * duplicateNumber;
            }

            var currentRotationEuler = transform.localEulerAngles;
            if (TryReadFlexibleVector3(args, "rotationEuler", currentRotationEuler, out var rotationEuler)
                || TryReadFlexibleVector3(args, "euler", currentRotationEuler, out rotationEuler))
            {
                transform.localEulerAngles = rotationEuler;
            }
            else if (TryReadFlexibleVector3(args, "rotationEulerOffset", Vector3.zero, out var rotationEulerOffset)
                || TryReadFlexibleVector3(args, "eulerOffset", Vector3.zero, out rotationEulerOffset))
            {
                transform.localEulerAngles = currentRotationEuler + rotationEulerOffset * duplicateNumber;
            }

            var currentScale = transform.localScale;
            if (TryReadFlexibleVector3(args, "scale", currentScale, out var scale))
            {
                transform.localScale = scale;
            }
            else if (TryReadFlexibleVector3(args, "scaleOffset", Vector3.zero, out var scaleOffset))
            {
                transform.localScale = currentScale + scaleOffset * duplicateNumber;
            }
        }

        private static void RemoveClonedChildren(GameObject clone)
        {
            for (var i = clone.transform.childCount - 1; i >= 0; i--)
            {
                Undo.DestroyObjectImmediate(clone.transform.GetChild(i).gameObject);
            }
        }

    }
}
