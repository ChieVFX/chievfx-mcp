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
        private static DependencyStatus GetDependencyStatus()
        {
            var packageInfo = PackageManagerPackageInfo.FindForPackageName(CinemachinePackageName);
            var cinemachineCamera = FindLoadedType("Unity.Cinemachine.CinemachineCamera");
            var cinemachineLegacyVirtualCamera = FindLoadedType("Unity.Cinemachine.CinemachineVirtualCamera");
            var cinemachine2VirtualCamera = FindLoadedType("Cinemachine.CinemachineVirtualCamera");
            var cinemachineBrain = FindLoadedType("Unity.Cinemachine.CinemachineBrain");
            var cinemachineTrack = FindLoadedType("Unity.Cinemachine.CinemachineTrack");
            var cinemachineShot = FindLoadedType("Unity.Cinemachine.CinemachineShot");
            var cinemachineSequencerCamera = FindLoadedType("Unity.Cinemachine.CinemachineSequencerCamera");
            var cinemachineSequencerInstruction = cinemachineSequencerCamera?.GetNestedType("Instruction", BindingFlags.Public | BindingFlags.NonPublic);
            var cinemachineVirtualCameraBase = FindLoadedType("Unity.Cinemachine.CinemachineVirtualCameraBase");
            var cinemachineBlendDefinition = FindLoadedType("Unity.Cinemachine.CinemachineBlendDefinition");
            var cinemachineSplineDolly = FindLoadedType("Unity.Cinemachine.CinemachineSplineDolly");
            var cinemachineSplineRoll = FindLoadedType("Unity.Cinemachine.CinemachineSplineRoll");
            var cinemachineInputAxisController = FindLoadedType("Unity.Cinemachine.CinemachineInputAxisController");
            var cinemachineInputAxis = FindLoadedType("Unity.Cinemachine.InputAxis");
            var cinemachineInputAxisOwner = FindLoadedType("Unity.Cinemachine.IInputAxisOwner");
            var cinemachineBlenderSettings = FindLoadedType("Unity.Cinemachine.CinemachineBlenderSettings");
            var cinemachineImpulseSource = FindLoadedType("Unity.Cinemachine.CinemachineImpulseSource");
            var cinemachineImpulseListener = FindLoadedType("Unity.Cinemachine.CinemachineImpulseListener");
            var cinemachineCollisionImpulseSource = FindLoadedType("Unity.Cinemachine.CinemachineCollisionImpulseSource");
            var cinemachineExternalImpulseListener = FindLoadedType("Unity.Cinemachine.CinemachineExternalImpulseListener");
            var cinemachineConfiner2D = FindLoadedType("Unity.Cinemachine.CinemachineConfiner2D");
            var cinemachineConfiner3D = FindLoadedType("Unity.Cinemachine.CinemachineConfiner3D");
            var cinemachineObsoleteConfiner = FindLoadedType("Unity.Cinemachine.CinemachineConfiner");
            var collider2D = FindLoadedType("UnityEngine.Collider2D");
            var collider3D = FindLoadedType("UnityEngine.Collider");
            var cinemachineApiFamily = DetermineCinemachineApiFamily(packageInfo != null, packageInfo?.version, cinemachineCamera, cinemachineLegacyVirtualCamera, cinemachine2VirtualCamera);
            var timelineAsset = FindLoadedType("UnityEngine.Timeline.TimelineAsset");
            var trackAsset = FindLoadedType("UnityEngine.Timeline.TrackAsset");
            var timelineClip = FindLoadedType("UnityEngine.Timeline.TimelineClip");
            var playableDirector = FindLoadedType("UnityEngine.Playables.PlayableDirector");
            var splinesPackageInfo = PackageManagerPackageInfo.FindForPackageName(SplinesPackageName);
            var splineContainer = FindLoadedType("UnityEngine.Splines.SplineContainer");
            var inputSystemPackageInfo = PackageManagerPackageInfo.FindForPackageName(InputSystemPackageName);
            var inputActionReference = FindLoadedType("UnityEngine.InputSystem.InputActionReference");
            var playerInput = FindLoadedType("UnityEngine.InputSystem.PlayerInput");

            var cinemachine = new PackageStatus(
                CinemachinePackageName,
                packageInfo != null,
                packageInfo?.version,
                cinemachineApiFamily,
                cinemachineApiFamily == CinemachineApiFamilyCm3 && cinemachineCamera != null && cinemachineBrain != null,
                CinemachineVersionDefineActive,
                cinemachineCamera,
                cinemachineBrain,
                cinemachineTrack,
                cinemachineShot);
            var sequencer = new SequencerCameraStatus(
                packageInfo != null,
                packageInfo?.version,
                cinemachineApiFamily,
                CinemachineVersionDefineActive,
                cinemachineSequencerCamera,
                cinemachineSequencerInstruction,
                cinemachineCamera,
                cinemachineVirtualCameraBase,
                cinemachineBlendDefinition,
                cinemachineBrain);
            var timelinePackageInfo = PackageManagerPackageInfo.FindForPackageName(TimelinePackageName);
            var timeline = new PackageStatus(
                TimelinePackageName,
                timelinePackageInfo != null,
                timelinePackageInfo?.version,
                null,
                timelineAsset != null && trackAsset != null && timelineClip != null && playableDirector != null,
                TimelineVersionDefineActive,
                timelineAsset,
                trackAsset,
                timelineClip,
                playableDirector);
            var splinesDolly = new AdvancedHelperStatus(
                "splinesDolly",
                "Splines Dolly",
                packageInfo != null,
                packageInfo?.version,
                cinemachineApiFamily,
                CinemachineVersionDefineActive,
                cinemachineSplineDolly,
                "Unity.Cinemachine.CinemachineSplineDolly",
                SplinesPackageName,
                splinesPackageInfo != null,
                splinesPackageInfo?.version,
                splineContainer,
                "UnityEngine.Splines.SplineContainer",
                cinemachineSplineRoll,
                "Unity.Cinemachine.CinemachineSplineRoll",
                optionalVersionDefineActive: SplinesVersionDefineActive);
            var inputSystem = new OptionalPackageStatus(
                InputSystemPackageName,
                inputSystemPackageInfo != null,
                inputSystemPackageInfo?.version,
                inputActionReference,
                "UnityEngine.InputSystem.InputActionReference",
                playerInput,
                "UnityEngine.InputSystem.PlayerInput");
            var inputAxisController = new AdvancedHelperStatus(
                "inputAxisController",
                "InputAxisController",
                packageInfo != null,
                packageInfo?.version,
                cinemachineApiFamily,
                CinemachineVersionDefineActive,
                cinemachineInputAxisController,
                "Unity.Cinemachine.CinemachineInputAxisController",
                null,
                false,
                null,
                null,
                null,
                cinemachineInputAxis,
                "Unity.Cinemachine.InputAxis",
                cinemachineInputAxisOwner,
                "Unity.Cinemachine.IInputAxisOwner");
            var blenderSettings = new AdvancedHelperStatus(
                "blenderSettings",
                "Blender Settings/custom blends",
                packageInfo != null,
                packageInfo?.version,
                cinemachineApiFamily,
                CinemachineVersionDefineActive,
                cinemachineBlenderSettings,
                "Unity.Cinemachine.CinemachineBlenderSettings",
                null,
                false,
                null,
                null,
                null);
            var impulse = new AdvancedHelperStatus(
                "impulse",
                "Impulse",
                packageInfo != null,
                packageInfo?.version,
                cinemachineApiFamily,
                CinemachineVersionDefineActive,
                cinemachineImpulseSource,
                "Unity.Cinemachine.CinemachineImpulseSource",
                null,
                false,
                null,
                null,
                null,
                cinemachineImpulseListener,
                "Unity.Cinemachine.CinemachineImpulseListener");
            var confiner2D = new AdvancedHelperStatus(
                "confiner2D",
                "Confiner2D",
                packageInfo != null,
                packageInfo?.version,
                cinemachineApiFamily,
                CinemachineVersionDefineActive,
                cinemachineConfiner2D,
                "Unity.Cinemachine.CinemachineConfiner2D",
                null,
                false,
                null,
                null,
                null,
                collider2D,
                "UnityEngine.Collider2D");
            var confiner3D = new AdvancedHelperStatus(
                "confiner3D",
                "Confiner3D",
                packageInfo != null,
                packageInfo?.version,
                cinemachineApiFamily,
                CinemachineVersionDefineActive,
                cinemachineConfiner3D,
                "Unity.Cinemachine.CinemachineConfiner3D",
                null,
                false,
                null,
                null,
                null,
                collider3D,
                "UnityEngine.Collider");
            return new DependencyStatus(cinemachine, sequencer, splinesDolly, inputAxisController, inputSystem, blenderSettings, impulse, cinemachineCollisionImpulseSource, cinemachineExternalImpulseListener, confiner2D, confiner3D, cinemachineObsoleteConfiner, timeline);
        }

        private static string DetermineCinemachineApiFamily(bool packageInstalled, string? packageVersion, Type? cm3CameraType, Type? cm3LegacyObsoleteCameraType, Type? cm2VirtualCameraType)
        {
            if (cm3CameraType != null)
            {
                return CinemachineApiFamilyCm3;
            }

            if (cm3LegacyObsoleteCameraType != null)
            {
                return CinemachineApiFamilyCm3LegacyObsolete;
            }

            if (cm2VirtualCameraType != null)
            {
                return CinemachineApiFamilyCm2;
            }

            if (packageInstalled)
            {
                if (IsPackageMajorVersion(packageVersion, 3))
                {
                    return CinemachineApiFamilyCm3;
                }

                if (IsPackageMajorVersion(packageVersion, 2))
                {
                    return CinemachineApiFamilyCm2;
                }
            }

            return CinemachineApiFamilyAbsent;
        }

        private static bool IsPackageMajorVersion(string? packageVersion, int major)
        {
            if (string.IsNullOrWhiteSpace(packageVersion))
            {
                return false;
            }

            var dotIndex = packageVersion!.IndexOf('.');
            var majorText = dotIndex >= 0 ? packageVersion.Substring(0, dotIndex) : packageVersion;
            return int.TryParse(majorText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMajor) && parsedMajor == major;
        }

        private static Type? FindLoadedType(string fullName)
        {
            foreach (var assembly in UnityAssemblies.GetLoaded())
            {
                Type? type;
                try
                {
                    type = assembly.GetType(fullName, throwOnError: false);
                }
                catch
                {
                    continue;
                }

                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static IEnumerable<Component> EnumerateComponents(Type componentType)
        {
            foreach (var root in GetCurrentScene().GetRootGameObjects())
            {
                foreach (var component in root.GetComponentsInChildren(componentType, includeInactive: true).OfType<Component>())
                {
                    yield return component;
                }
            }
        }

        private static IEnumerable<Component> EnumerateComponentsImplementing(Type interfaceType)
        {
            foreach (var root in GetCurrentScene().GetRootGameObjects())
            {
                foreach (var component in root.GetComponentsInChildren<Component>(includeInactive: true))
                {
                    if (component != null && interfaceType.IsInstanceOfType(component))
                    {
                        yield return component;
                    }
                }
            }
        }

        private static IEnumerable<PlayableDirector> EnumerateDirectors()
        {
            return GetCurrentScene().GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<PlayableDirector>(includeInactive: true))
                .OrderBy(director => GetTransformPath(director.transform), StringComparer.Ordinal);
        }

        private static IEnumerable<string> EnumerateTimelineAssetPaths(DependencyStatus status)
        {
            return AssetDatabase.FindAssets("t:TimelineAsset")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Where(path =>
                {
                    var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                    return asset != null && status.Timeline.AssetType!.IsInstanceOfType(asset);
                })
                .OrderBy(path => path, StringComparer.Ordinal);
        }

        private static IEnumerable<UnityEngine.Object> EnumerateAssetsOfType(Type assetType)
        {
            var paths = AssetDatabase.FindAssets("t:" + assetType.Name)
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .OrderBy(path => path, StringComparer.Ordinal);
            foreach (var path in paths)
            {
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                if (asset != null && assetType.IsInstanceOfType(asset))
                {
                    yield return asset;
                }
            }
        }
    }
}
