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
            var sceneFilter = ReadString(args, "scene");
            var context = GetGameObjectQueryContext(sceneFilter);
            var path = ReadString(args, "path");
            var createType = ReadGameObjectCreateType(args);
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

            var gameObject = CreateGameObjectByType(createType, name);
            Undo.RegisterCreatedObjectUndo(gameObject, "ChievFX MCP Create GameObject");
            if (parent != null)
            {
                gameObject.transform.SetParent(parent.transform, false);
            }
            else if (context.CreationScene.IsValid())
            {
                SceneManager.MoveGameObjectToScene(gameObject, context.CreationScene);
            }

            MarkGameObjectMutationDirty(gameObject);
            if (parent != null)
            {
                MarkGameObjectMutationDirty(parent);
            }

            RepaintEditorAfterMutation();

            var afterContext = GetGameObjectQueryContext(sceneFilter);
            return new
            {
                success = true,
                path = GetHierarchyPath(gameObject, afterContext),
                instanceId = GetLegacyInstanceId(gameObject),
                type = createType
            };
        }

        public object Hierarchy(JToken args)
        {
            var context = GetGameObjectQueryContext(ReadString(args, "scene"));
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
                sceneFilter = context.SceneFilter,
                sceneName = context.SceneName,
                scenePath = context.ScenePath,
                scenes = DescribeContextScenes(context),
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
            var context = GetGameObjectQueryContext(ReadString(args, "scene"));
            var path = ReadString(args, "path");
            var name = ReadString(args, "name");
            var namePattern = ReadString(args, "namePattern");
            var componentType = ReadString(args, "componentType");
            var instanceId = ReadNullableInt(args, "instanceId");
            var includeInactive = ReadBool(args, "includeInactive", false);
            var includeComponents = ReadBool(args, "includeComponents", false);
            // A path with no name/namePattern/componentType names one object, as in gameobject-transform-get; with a
            // filter it is the subtree to search. Listing the whole subtree for a bare path buried the object in hundreds of rows.
            var hasFilter = !string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(namePattern) || !string.IsNullOrWhiteSpace(componentType);
            var isBarePath = !instanceId.HasValue && !string.IsNullOrWhiteSpace(path) && !hasFilter;
            var includeDetails = ReadBool(args, "includeDetails", false) || includeComponents || instanceId.HasValue || isBarePath;
            var maxResults = ClampInt(ReadInt(args, "maxResults", DefaultGameObjectMaxResults), 1, HardGameObjectMaxResults);
            ValidateWildcardPattern(namePattern, "namePattern");
            ValidateComponentTypeText(componentType, required: false);

            // An exact target (instanceId, or a path without filters) is returned even when inactive: the caller named it. A path
            // with a filter stays the subtree to search, also with includeComponents (that once searched only the path itself: 0 matches).
            var isExactTarget = instanceId.HasValue || (!hasFilter && !string.IsNullOrWhiteSpace(path));
            var sourceObjects = isExactTarget
                ? new[] { ResolveSingleGameObject(context, args) }
                : string.IsNullOrWhiteSpace(path)
                    ? EnumerateContextGameObjects(context)
                    : EnumerateGameObjects(ResolveGameObjectByPath(context, path!));
            var searchedObjects = sourceObjects.ToList();
            var filterMatches = searchedObjects
                .Where(gameObject => string.IsNullOrWhiteSpace(name) || string.Equals(gameObject.name, name, StringComparison.Ordinal))
                .Where(gameObject => string.IsNullOrWhiteSpace(namePattern) || WildcardMatches(gameObject.name, namePattern!))
                .Where(gameObject => string.IsNullOrWhiteSpace(componentType) || HasMatchingComponent(gameObject, componentType!))
                .ToList();
            var matches = includeInactive || isExactTarget
                ? filterMatches
                : filterMatches.Where(gameObject => gameObject.activeInHierarchy).ToList();
            var inactiveHidden = filterMatches.Count - matches.Count;
            var selected = matches
                .Take(maxResults)
                .Select(gameObject => includeDetails
                    ? CreateGameObjectDetail(gameObject, context, includeComponents)
                    : CreateGameObjectRef(gameObject, context))
                .ToArray();
            var underPath = isBarePath && matches.Count == 1 ? EnumerateGameObjects(matches[0]).Count() - 1 : 0;

            return new
            {
                source = context.Source,
                sceneFilter = context.SceneFilter,
                sceneName = context.SceneName,
                scenePath = context.ScenePath,
                scenes = DescribeContextScenes(context),
                prefabAssetPath = context.PrefabAssetPath,
                count = selected.Length,
                totalMatches = matches.Count,
                inactiveHidden,
                maxResults,
                includeDetails,
                truncated = matches.Count > selected.Length,
                objects = selected,
                note = underPath > 0
                    ? $"path names this one object; {underPath} more are under it: add name, namePattern or componentType to search them, or use gameobject-hierarchy path:'{path}'."
                    : filterMatches.Count == 0 && !string.IsNullOrWhiteSpace(componentType)
                        ? DescribeUnknownComponentType(componentType!, searchedObjects)
                        : filterMatches.Count == 0 && !string.IsNullOrWhiteSpace(namePattern)
                            ? DescribeWholeNamePattern(namePattern!, searchedObjects)
                            : null
            };
        }

        // A pattern without * or ? matches the whole name: "Player" found nothing while 55 names contained it, and the reply
        // read as "no such objects".
        internal static string? DescribeWholeNamePattern(string namePattern, List<GameObject> searchedObjects)
        {
            // "(?i)fade|black" and "Fade|Black" found nothing without a word: the bar and brackets are matched as text.
            if (namePattern.IndexOfAny(RegexOnlyCharacters) >= 0)
            {
                var word = Regex.Match(namePattern.Replace("(?i)", string.Empty), "[A-Za-z0-9_]+").Value;
                return "namePattern is not a regex: it is a whole name with wildcards * and ? (case-insensitive), and | ( ) [ ] ^ $ + \\ are matched as text; "
                       + "one name per call" + (word.Length > 0 ? $", e.g. namePattern:'*{word}*'." : ".");
            }

            if (namePattern.IndexOfAny(WildcardCharacters) >= 0)
            {
                return null;
            }

            var containing = searchedObjects.Count(gameObject => gameObject.name.IndexOf(namePattern, StringComparison.OrdinalIgnoreCase) >= 0);
            return containing > 0
                ? $"namePattern matches the whole name (wildcards * and ?): {containing} names contain '{namePattern}' - try namePattern:'*{namePattern}*'."
                : null;
        }

        private static readonly char[] WildcardCharacters = { '*', '?' };
        private static readonly char[] RegexOnlyCharacters = { '|', '(', ')', '[', ']', '^', '$', '+', '\\' };

        // Zero matches for a type name nothing is called: a typo or a guessed name ("Car") read the same as "no such objects".
        // Types on the searched objects come first, the ones highest in the hierarchy first: the shortest loaded names
        // ("CarPh") and the most numerous ones (CarColorSlot on every paint slot) both hid the root's CarControl.
        private static string? DescribeUnknownComponentType(string componentType, List<GameObject> searchedObjects)
        {
            var types = UnityAssemblies.GetLoaded()
                .SelectMany(GetLoadableTypes)
                .Where(type => typeof(Component).IsAssignableFrom(type))
                .ToArray();
            if (types.Any(type => ComponentTypeMatches(type, componentType)))
            {
                return null;
            }

            var inUse = new Dictionary<string, (int Count, int Depth)>(StringComparer.Ordinal);
            foreach (var gameObject in searchedObjects)
            {
                var depth = -1;
                foreach (var component in gameObject.GetComponents<Component>())
                {
                    var typeName = component != null ? component.GetType().Name : null;
                    if (typeName == null || typeName.IndexOf(componentType, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    if (depth < 0)
                    {
                        depth = 0;
                        for (var parent = gameObject.transform.parent; parent != null; parent = parent.parent)
                        {
                            depth++;
                        }
                    }

                    inUse[typeName] = inUse.TryGetValue(typeName, out var seen) ? (seen.Count + 1, Math.Min(seen.Depth, depth)) : (1, depth);
                }
            }

            var similar = inUse
                .OrderBy(pair => pair.Value.Depth)
                .ThenByDescending(pair => pair.Value.Count)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key} (on {pair.Value.Count})")
                .Concat(types
                    .Where(type => !type.IsAbstract && !inUse.ContainsKey(type.Name) && type.Name.IndexOf(componentType, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(type => type.Name)
                    .Distinct()
                    .OrderBy(typeName => typeName.Length)
                    .ThenBy(typeName => typeName, StringComparer.Ordinal))
                .Take(8)
                .ToArray();
            return $"no loaded Component type is named '{componentType}'"
                   + (similar.Length > 0 ? $"; types containing it (those on the searched objects first, highest in the hierarchy first): {string.Join(", ", similar)}." : ": componentType is a class name (as in GetComponent<T>), check the spelling.");
        }

        public object GetComponent(JToken args)
        {
            var context = GetGameObjectQueryContext(ReadString(args, "scene"));
            var componentType = ReadString(args, "componentType");
            ValidateComponentTypeText(componentType, required: true);
            var componentIndex = ReadComponentIndex(args);

            var gameObject = ResolveSingleGameObject(context, args);
            var matches = GetMatchingComponents(gameObject, componentType!).ToArray();
            if (matches.Length == 0)
            {
                throw new InvalidOperationException($"GameObject '{GetHierarchyPath(gameObject, context)}' has no component matching '{componentType}'."
                                                    + DescribeComponentElsewhere(gameObject, componentType!, context));
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
                sceneFilter = context.SceneFilter,
                sceneName = context.SceneName,
                scenePath = context.ScenePath,
                prefabAssetPath = context.PrefabAssetPath,
                gameObject = CreateGameObjectRef(gameObject, context),
                componentIndex,
                matchCount = matches.Length,
                component = CreateComponentDetail(matches[componentIndex], includeSerializedData, isDebug, ref serializedTruncated),
                serializedDataTruncated = serializedTruncated
            };
        }

        // Where the asked type is instead: on a child (a car's Rigidbody sits on its Body), or which components the object has.
        private static string DescribeComponentElsewhere(GameObject gameObject, string componentType, GameObjectQueryContext context)
        {
            foreach (var child in gameObject.GetComponentsInChildren<Transform>(true))
            {
                if (child != gameObject.transform && GetMatchingComponents(child.gameObject, componentType).Any())
                {
                    return $" It is on '{GetHierarchyPath(child.gameObject, context)}' (instanceId {GetLegacyInstanceId(child.gameObject)}).";
                }
            }

            var names = gameObject.GetComponents<Component>().Where(component => component != null).Select(component => component.GetType().Name);
            return " Its components: " + string.Join(", ", names) + ".";
        }

        public object Update(JToken args)
        {
            var sceneFilter = ReadString(args, "scene");
            var context = GetGameObjectQueryContext(sceneFilter);
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

            var afterContext = GetGameObjectQueryContext(sceneFilter);
            return new
            {
                success = true,
                path = GetHierarchyPath(gameObject, afterContext),
                instanceId = GetLegacyInstanceId(gameObject)
            };
        }

        public object UpdateOrCreateComponent(JToken args)
        {
            var context = GetGameObjectQueryContext(ReadString(args, "scene"));
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
                // Play-mode edits apply to the live instance only and are discarded on exit; flag it so
                // callers know the change is not persisted to the scene/prefab.
                playModeLiveEdit = EditorApplication.isPlaying ? true : (bool?)null,
                // The reply shows the value as written; a camera rig or other game script writing the field every frame
                // put it back a frame later (FOV 80 read 45 again), and the success read as "changed".
                playModeNote = EditorApplication.isPlaying && updatedProperties.Length > 0
                    ? "Game scripts may set these fields every frame (camera rigs, animated or script-driven values): read them back with gameobject-component-get after a frame."
                    : null,
                component = CreateComponentMutationSummary(component, updatedProperties, created)
            };
        }

        public object GetTransform(JToken args)
        {
            var context = GetGameObjectQueryContext(ReadString(args, "scene"));
            var gameObject = ResolveSingleGameObject(context, args);
            var transform = gameObject.transform;
            var isWorld = ReadBool(args, "isWorld", false);

            return new
            {
                success = true,
                isWorld,
                transform = CreateTransformDto(transform, isWorld),
                note = DescribeMovingChildBody(gameObject)
            };
        }

        // A vehicle's root often stays at the origin while physics moves its child body: "where is the car" asked of the
        // object found by its controller read 0, 0, 0.
        private static string? DescribeMovingChildBody(GameObject gameObject)
        {
            if (!EditorApplication.isPlaying || gameObject.GetComponent<Rigidbody>() != null)
            {
                return null;
            }

            var body = gameObject.GetComponentsInChildren<Rigidbody>(false).FirstOrDefault(rigidbody => !rigidbody.isKinematic);
            if (body == null || Vector3.Distance(body.position, gameObject.transform.position) < 1f)
            {
                return null;
            }

            var position = body.position;
            return $"physics moves the child '{body.name}' (instanceId {body.gameObject.GetInstanceID()}, Rigidbody), not this object: it is at "
                   + $"{position.x.ToString("0.##", CultureInfo.InvariantCulture)}, {position.y.ToString("0.##", CultureInfo.InvariantCulture)}, {position.z.ToString("0.##", CultureInfo.InvariantCulture)} (world).";
        }

        public object UpdateTransform(JToken args)
        {
            var context = GetGameObjectQueryContext(ReadString(args, "scene"));
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
            var context = GetGameObjectQueryContext(ReadString(args, "scene"));
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
            var sceneFilter = ReadString(args, "scene");
            var context = GetGameObjectQueryContext(sceneFilter);
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

            var afterContext = GetGameObjectQueryContext(sceneFilter);
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
