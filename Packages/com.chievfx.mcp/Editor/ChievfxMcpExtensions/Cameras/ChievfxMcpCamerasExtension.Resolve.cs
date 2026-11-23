#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Chievfx.Mcp.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using PackageManagerPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Chievfx.Mcp.Extensions.Cameras
{
    internal static partial class ChievfxMcpCamerasExtension
    {
        private static Camera? ResolveCamera(JToken args, bool allowCreate, bool dryRun)
        {
            if (TryInt(args, "cameraInstanceId", out var cameraInstanceId) || TryInt(args, "instanceId", out cameraInstanceId))
            {
                var obj = UnityObjectIdentity.LegacyInstanceIdToObject(cameraInstanceId);
                if (obj is Camera camera)
                {
                    return camera;
                }

                if (obj is GameObject targetGameObject)
                {
                    return targetGameObject.GetComponent<Camera>();
                }
            }

            if (TryString(args, "cameraPath", out var cameraPath) || TryString(args, "targetPath", out cameraPath))
            {
                var transform = ResolveTransform(cameraPath);
                var camera = transform != null ? transform.GetComponent<Camera>() : null;
                if (camera != null)
                {
                    return camera;
                }
            }

            var mainCamera = Camera.main;
            if (mainCamera != null)
            {
                return mainCamera;
            }

            if (!allowCreate || dryRun)
            {
                return null;
            }

            var gameObject = new GameObject(ReadString(args, "cameraName") ?? "Main Camera");
            Undo.RegisterCreatedObjectUndo(gameObject, "Create Unity Camera");
            gameObject.tag = "MainCamera";
            return Undo.AddComponent<Camera>(gameObject);
        }

        private static Component? ResolveSelectedBrain(Type brainType)
        {
            foreach (var selected in Selection.objects)
            {
                if (selected is Component component && brainType.IsInstanceOfType(component))
                {
                    return component;
                }

                if (selected is GameObject gameObject)
                {
                    var brain = gameObject.GetComponent(brainType);
                    if (brain != null)
                    {
                        return brain;
                    }
                }
            }

            return null;
        }

        private static HashSet<string> CurrentCinemachineCameraNames(DependencyStatus status)
        {
            return status.Cinemachine.CameraType == null
                ? new HashSet<string>(StringComparer.Ordinal)
                : EnumerateComponents(status.Cinemachine.CameraType)
                    .Select(component => component.name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToHashSet(StringComparer.Ordinal);
        }

        private static PlayableDirector? ResolveDirectorFromArgs(JToken args)
        {
            if (TryInt(args, "directorInstanceId", out var instanceId) || TryInt(args, "instanceId", out instanceId))
            {
                return UnityObjectIdentity.LegacyInstanceIdToObject(instanceId) as PlayableDirector;
            }

            if (TryString(args, "directorPath", out var path) || TryString(args, "targetPath", out path))
            {
                return ResolveDirector(path);
            }

            return null;
        }

        private static PlayableDirector RequireDirector(JToken args)
        {
            return ResolveDirectorFromArgs(args) ?? throw new ArgumentException("Expected directorPath or directorInstanceId for a PlayableDirector.");
        }

        private static PlayableDirector? ResolveDirector(string pathOrInstanceId)
        {
            if (int.TryParse(pathOrInstanceId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var instanceId))
            {
                return UnityObjectIdentity.LegacyInstanceIdToObject(instanceId) as PlayableDirector;
            }

            var normalizedPath = pathOrInstanceId.Trim('/');
            return EnumerateDirectors().FirstOrDefault(director => string.Equals(GetTransformPath(director.transform), normalizedPath, StringComparison.Ordinal));
        }

        private static Component RequireComponent(JToken args, Type componentType, string displayName)
        {
            Component? component = null;
            if (TryInt(args, "instanceId", out var instanceId))
            {
                component = UnityObjectIdentity.LegacyInstanceIdToObject(instanceId) as Component;
            }

            if (component == null && TryString(args, "targetPath", out var targetPath))
            {
                component = ResolveComponent(componentType, targetPath);
            }

            if (component == null || !componentType.IsInstanceOfType(component))
            {
                throw new ArgumentException($"Expected targetPath or instanceId for an existing {displayName}.");
            }

            return component;
        }

        private static Component RequireNamedComponent(JToken args, Type componentType, string displayName, string pathFieldName, string instanceIdFieldName)
        {
            Component? component = null;
            if (TryInt(args, instanceIdFieldName, out var instanceId))
            {
                var obj = UnityObjectIdentity.LegacyInstanceIdToObject(instanceId);
                component = obj switch
                {
                    Component resolved when componentType.IsInstanceOfType(resolved) => resolved,
                    GameObject gameObject => gameObject.GetComponent(componentType),
                    _ => null,
                };
            }

            if (component == null && TryString(args, pathFieldName, out var path))
            {
                component = ResolveComponent(componentType, path);
            }

            if (component == null || !componentType.IsInstanceOfType(component))
            {
                throw new ArgumentException($"Expected {pathFieldName} or {instanceIdFieldName} for an existing {displayName}. Existing spline geometry is not created or edited.");
            }

            return component;
        }

        private static Component RequireCollider(JToken args, Type colliderType, string displayName)
        {
            Component? collider = null;
            if (TryInt(args, "colliderInstanceId", out var instanceId))
            {
                var obj = UnityObjectIdentity.LegacyInstanceIdToObject(instanceId);
                collider = obj switch
                {
                    Component component when colliderType.IsInstanceOfType(component) => component,
                    GameObject gameObject => gameObject.GetComponent(colliderType),
                    _ => null,
                };
            }

            if (collider == null && TryString(args, "colliderPath", out var colliderPath))
            {
                var transform = ResolveTransform(colliderPath);
                collider = transform == null ? null : transform.GetComponent(colliderType);
            }

            if (collider == null || !colliderType.IsInstanceOfType(collider))
            {
                throw new ArgumentException($"Expected colliderPath or colliderInstanceId for an existing {displayName}. Collider geometry is not created or edited.");
            }

            return collider;
        }

        private static Component? ResolveComponent(Type componentType, string pathOrInstanceId)
        {
            if (int.TryParse(pathOrInstanceId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var instanceId))
            {
                var obj = UnityObjectIdentity.LegacyInstanceIdToObject(instanceId);
                return obj is Component component && componentType.IsInstanceOfType(component) ? component : null;
            }

            var normalizedPath = pathOrInstanceId.Trim('/');
            return EnumerateComponents(componentType)
                .FirstOrDefault(component => string.Equals(GetTransformPath(component.transform), normalizedPath, StringComparison.Ordinal));
        }

        private static Transform? ResolveTransform(string pathOrInstanceId)
        {
            if (int.TryParse(pathOrInstanceId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var instanceId))
            {
                var obj = UnityObjectIdentity.LegacyInstanceIdToObject(instanceId);
                return obj switch
                {
                    Transform transform => transform,
                    GameObject gameObject => gameObject.transform,
                    Component component => component.transform,
                    _ => null,
                };
            }

            var normalizedPath = pathOrInstanceId.Trim('/');
            foreach (var root in GetCurrentScene().GetRootGameObjects())
            {
                if (string.Equals(root.name, normalizedPath, StringComparison.Ordinal))
                {
                    return root.transform;
                }

                if (normalizedPath.StartsWith(root.name + "/", StringComparison.Ordinal))
                {
                    var child = root.transform.Find(normalizedPath.Substring(root.name.Length + 1));
                    if (child != null)
                    {
                        return child;
                    }
                }
            }

            return null;
        }

        private static PlayableDirector CreateDirectorObject(string name)
        {
            var gameObject = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(gameObject, "Create Timeline Director");
            return Undo.AddComponent<PlayableDirector>(gameObject);
        }

        private static ShotSpec[] ReadShotSpecs(JToken args)
        {
            if (args["shots"] is not JArray shots)
            {
                return Array.Empty<ShotSpec>();
            }

            return shots.OfType<JObject>()
                .Select((shot, index) => new ShotSpec(
                    ReadString(shot, "name") ?? $"Shot {index + 1}",
                    OptionalDouble(shot, "start") ?? index,
                    OptionalDouble(shot, "duration") ?? 1d,
                    OptionalFloat(shot, "fieldOfView") ?? OptionalFloat(shot, "fov") ?? 35f,
                    OptionalFloat(shot, "distance") ?? 4f,
                    OptionalInt(shot, "priority") ?? (10 + index)))
                .ToArray();
        }

        private static SequencerShotSpec[] ReadSequencerShotSpecs(JToken args)
        {
            if (args["shots"] is not JArray shots)
            {
                return Array.Empty<SequencerShotSpec>();
            }

            return shots.OfType<JObject>()
                .Select((shot, index) => new SequencerShotSpec(
                    ReadString(shot, "name") ?? $"Sequencer Shot {index + 1}",
                    OptionalDouble(shot, "holdSeconds") ?? OptionalDouble(shot, "hold") ?? 1d,
                    OptionalFloat(shot, "fieldOfView") ?? OptionalFloat(shot, "fov") ?? 35f,
                    OptionalFloat(shot, "distance") ?? 4f,
                    OptionalInt(shot, "priority") ?? (10 + index),
                    ReadString(shot, "blendStyle") ?? "Cut",
                    OptionalFloat(shot, "blendTime") ?? 0f))
                .ToArray();
        }

        private static Dictionary<string, object?> DescribeLens(object? lens)
        {
            if (lens == null)
            {
                return new Dictionary<string, object?> { ["missing"] = true };
            }

            return new Dictionary<string, object?>
            {
                ["fieldOfView"] = Round(ReadFloatMember(lens, "FieldOfView")),
                ["orthographicSize"] = Round(ReadFloatMember(lens, "OrthographicSize")),
                ["nearClipPlane"] = Round(ReadFloatMember(lens, "NearClipPlane")),
                ["farClipPlane"] = Round(ReadFloatMember(lens, "FarClipPlane")),
                ["modeOverride"] = Convert.ToString(ReadMember(lens, "ModeOverride"), CultureInfo.InvariantCulture),
                ["orthographic"] = ReadMember(lens, "Orthographic") as bool?,
            };
        }

        private static Dictionary<string, object?> DescribeTransform(Transform transform)
        {
            return new Dictionary<string, object?>
            {
                ["position"] = Vector3Row(transform.position),
                ["rotationEuler"] = Vector3Row(transform.rotation.eulerAngles),
                ["localScale"] = Vector3Row(transform.localScale),
            };
        }

        private static Dictionary<string, object?> DescribeObjectReference(object? value)
        {
            if (value is Transform transform)
            {
                return new Dictionary<string, object?>
                {
                    ["name"] = transform.name,
                    ["path"] = GetTransformPath(transform),
                    ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(transform),
                };
            }

            if (value is GameObject gameObject)
            {
                return new Dictionary<string, object?>
                {
                    ["name"] = gameObject.name,
                    ["path"] = GetTransformPath(gameObject.transform),
                    ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(gameObject),
                };
            }

            if (value is Component component)
            {
                return new Dictionary<string, object?>
                {
                    ["name"] = component.name,
                    ["path"] = GetTransformPath(component.transform),
                    ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(component),
                    ["type"] = component.GetType().FullName,
                };
            }

            return new Dictionary<string, object?> { ["value"] = Convert.ToString(value, CultureInfo.InvariantCulture) };
        }

        private static object? DescribeObjectReferenceOrNull(object? value)
        {
            return value == null ? null : DescribeObjectReference(value);
        }

        private static Dictionary<string, object?> DescribeAssetObject(UnityEngine.Object asset)
        {
            var path = AssetDatabase.GetAssetPath(asset);
            return new Dictionary<string, object?>
            {
                ["name"] = asset.name,
                ["path"] = path,
                ["guid"] = string.IsNullOrWhiteSpace(path) ? string.Empty : AssetDatabase.AssetPathToGUID(path),
                ["type"] = asset.GetType().FullName,
            };
        }

        private static object? DescribeMaybeAsset(object? value)
        {
            return value is UnityEngine.Object asset ? DescribeAssetObject(asset) : null;
        }

        private static Component? GetOptionalComponent(GameObject? gameObject, Type? componentType)
        {
            return gameObject == null || componentType == null ? null : gameObject.GetComponent(componentType);
        }

        private static object? ReadFirstMember(object target, string[] memberNames)
        {
            foreach (var memberName in memberNames)
            {
                var value = ReadMember(target, memberName);
                if (value != null)
                {
                    return value;
                }
            }

            return null;
        }

        private static bool TrySetFirstMember(object target, string[] memberNames, object? value, List<string> warnings)
        {
            foreach (var memberName in memberNames)
            {
                if (TrySetMember(target, memberName, value, warnings))
                {
                    return true;
                }
            }

            return false;
        }

        private static void SetAutoDollyEnabled(Component dolly, bool enabled, List<string> warnings)
        {
            var autoDolly = ReadMember(dolly, "AutoDolly");
            if (autoDolly == null)
            {
                warnings.Add("AutoDolly field was not found.");
                return;
            }

            if (!TrySetMember(autoDolly, "Enabled", enabled, warnings))
            {
                warnings.Add("AutoDolly.Enabled field was not writable.");
                return;
            }

            if (!TrySetMember(dolly, "AutoDolly", autoDolly, warnings))
            {
                warnings.Add("AutoDolly field was not writable after changing Enabled.");
            }
        }

        private static string DescribeAutoDollyMode(object? autoDolly)
        {
            if (autoDolly == null)
            {
                return "missing";
            }

            var enabled = ReadMember(autoDolly, "Enabled");
            return enabled is bool isEnabled ? (isEnabled ? "enabled" : "disabled") : Convert.ToString(autoDolly, CultureInfo.InvariantCulture) ?? "unknown";
        }

        private static IEnumerable<string> SplineDollyWarnings(Component dolly, Component? splineContainer, DependencyStatus status)
        {
            if (splineContainer == null)
            {
                yield return "CinemachineSplineDolly has no SplineContainer assigned.";
            }

            var camera = status.Cinemachine.CameraType == null ? null : dolly.GetComponent(status.Cinemachine.CameraType);
            var target = camera == null ? null : ReadMember(camera, "Target") ?? ReadMember(camera, "TrackingTarget") ?? ReadMember(camera, "Follow") ?? ReadMember(camera, "LookAt");
            if (target == null)
            {
                yield return "CinemachineCamera has no target/follow/look-at assigned; framing changes may be hard to validate.";
            }
        }
    }
}
