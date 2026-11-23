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
        private static string CinemachineDetailUri(Component camera)
        {
            return CinemachineCameraDetailPrefix + EncodeSegment(UnityObjectIdentity.GetEntityIdText(camera));
        }

        private static string BrainDetailUri(Component brain)
        {
            return CinemachineBrainDetailPrefix + EncodeSegment(UnityObjectIdentity.GetEntityIdText(brain));
        }

        private static string SequencerDetailUri(Component sequencer)
        {
            return CinemachineSequencerDetailPrefix + EncodeSegment(UnityObjectIdentity.GetEntityIdText(sequencer));
        }

        private static string DirectorDetailUri(PlayableDirector director)
        {
            return TimelineDirectorDetailPrefix + EncodeSegment(UnityObjectIdentity.GetEntityIdText(director));
        }

        private static string TimelineAssetDetailUri(string path)
        {
            var guid = AssetDatabase.AssetPathToGUID(path);
            return TimelineAssetDetailPrefix + EncodeSegment(string.IsNullOrWhiteSpace(guid) ? path : guid);
        }

        private static Dictionary<string, object?> CreateCommandEnvelope(string uri, DependencyStatus status, bool dryRun)
        {
            var result = CreateEnvelope(uri, status);
            result["ok"] = true;
            result["dryRun"] = dryRun;
            return result;
        }

        private static Dictionary<string, object?> CreateEnvelope(string uri, DependencyStatus status)
        {
            return new Dictionary<string, object?>();
        }

        private static Dictionary<string, object?> CreateUnavailable(string statusUri, DependencyStatus status, string message)
        {
            return new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["unavailable"] = true,
                ["message"] = message,
                ["statusUri"] = statusUri,
                ["reason"] = status.Reason,
            };
        }

        private static Dictionary<string, object?> CreateCinemachineUnavailable(DependencyStatus status, string subject)
        {
            return CreateUnavailable(
                StatusUri,
                status,
                $"{subject} requires Cinemachine 3.x Unity.Cinemachine.CinemachineCamera authoring API. {status.Cinemachine.Reason}");
        }

        private static Dictionary<string, object?> CreateSequencerUnavailable(DependencyStatus status, string subject)
        {
            return CreateUnavailable(
                StatusUri,
                status,
                $"{subject} requires CM3 Sequencer Camera: {CinemachinePackageName} installed, CHIEVFX_MCP_HAS_CINEMACHINE active, apiFamily=cm3, and loaded Unity.Cinemachine.CinemachineSequencerCamera, Instruction, CinemachineCamera, CinemachineVirtualCameraBase, CinemachineBlendDefinition, and CinemachineBrain. {status.Sequencer.Reason}");
        }

        private static Dictionary<string, object?> CreateAdvancedHelperUnavailable(DependencyStatus status, AdvancedHelperStatus helper, string subject)
        {
            var optional = helper.OptionalGateRequired
                ? $", plus {helper.OptionalPackageName} installed, CHIEVFX_MCP_HAS_SPLINES active, and loaded {helper.OptionalTypeName}"
                : string.Empty;
            var secondary = helper.SecondaryHelperTypeName == null ? string.Empty : $", and loaded {helper.SecondaryHelperTypeName}";
            return CreateUnavailable(
                StatusUri,
                status,
                $"{subject} requires CM3 {helper.DisplayName}: {CinemachinePackageName} installed, CHIEVFX_MCP_HAS_CINEMACHINE active, apiFamily=cm3, loaded {helper.HelperTypeName}{secondary}{optional}. {helper.Reason}");
        }

        private static bool SplinesDollyAvailable(DependencyStatus status)
        {
            return status.Cinemachine.Available && status.SplinesDolly.Available;
        }

        private static bool ConfinerAvailable(DependencyStatus status, AdvancedHelperStatus helper)
        {
            return status.Cinemachine.Available && helper.Available;
        }

        private static Dictionary<string, object?> CreateConfinerUnavailable(DependencyStatus status, AdvancedHelperStatus helper, string subject)
        {
            var colliderType = helper.SecondaryHelperTypeName ?? "UnityEngine.Collider/Collider2D";
            var reason = !status.Cinemachine.Available ? status.Cinemachine.Reason : helper.Reason;
            return CreateUnavailable(
                StatusUri,
                status,
                $"{subject} requires CM3 {helper.DisplayName}: {CinemachinePackageName} installed, CHIEVFX_MCP_HAS_CINEMACHINE active, apiFamily=cm3, loaded Unity.Cinemachine.CinemachineCamera, Unity.Cinemachine.CinemachineBrain, {helper.HelperTypeName}, and {colliderType}. Deprecated Unity.Cinemachine.CinemachineConfiner is warning-only and will not be authored. {reason}");
        }

        private static Dictionary<string, object?> CreateBlenderSettingsUnavailable(DependencyStatus status, string subject)
        {
            var result = CreateUnavailable(
                StatusUri,
                status,
                $"{subject} requires CM3 Blender Settings: {CinemachinePackageName} installed, CHIEVFX_MCP_HAS_CINEMACHINE active, apiFamily=cm3, and loaded Unity.Cinemachine.CinemachineBlenderSettings, CustomBlend, CinemachineBlendDefinition, CinemachineBrain, and CinemachineCamera. {BlenderSettingsUnavailableReason(status)}");
            result["requiredTypes"] = BlenderSettingsRequiredTypes(status);
            return result;
        }

        private static bool BlenderSettingsAvailable(DependencyStatus status)
        {
            return status.Cinemachine.Available
                && status.BlenderSettings.Available
                && BlenderCustomBlendType(status) != null
                && status.Sequencer.BlendDefinitionType != null;
        }

        private static Type? BlenderCustomBlendType(DependencyStatus status)
        {
            return status.BlenderSettings.HelperType?.GetNestedType("CustomBlend", BindingFlags.Public | BindingFlags.NonPublic);
        }

        private static string BlenderSettingsUnavailableReason(DependencyStatus status)
        {
            if (!status.Cinemachine.Available)
            {
                return status.Cinemachine.Reason;
            }

            if (!status.BlenderSettings.Available)
            {
                return status.BlenderSettings.Reason;
            }

            if (BlenderCustomBlendType(status) == null)
            {
                return "CM3 package detected, but CinemachineBlenderSettings.CustomBlend type is not loaded.";
            }

            if (status.Sequencer.BlendDefinitionType == null)
            {
                return "CM3 package detected, but CinemachineBlendDefinition type is not loaded.";
            }

            return "available";
        }

        private static Dictionary<string, object?> BlenderSettingsRequiredTypes(DependencyStatus status)
        {
            return new Dictionary<string, object?>
            {
                ["cinemachineBlenderSettingsTypeLoaded"] = status.BlenderSettings.HelperType != null,
                ["customBlendTypeLoaded"] = BlenderCustomBlendType(status) != null,
                ["blendDefinitionTypeLoaded"] = status.Sequencer.BlendDefinitionType != null,
                ["brainTypeLoaded"] = status.Cinemachine.BrainType != null,
                ["cinemachineCameraTypeLoaded"] = status.Cinemachine.CameraType != null,
            };
        }

        private static Dictionary<string, object?> CreateShotSequenceUnavailable(DependencyStatus status, string subject)
        {
            if (!status.Cinemachine.Available)
            {
                return CreateCinemachineUnavailable(status, subject);
            }

            return CreateUnavailable(StatusUri, status, $"{subject} requires {TimelinePackageName}, loaded UnityEngine.Timeline types, and active CHIEVFX_MCP_HAS_TIMELINE. {status.Timeline.Reason}");
        }

        private static Dictionary<string, object?> CreateNotFound(string uri, DependencyStatus status, string kind, string key)
        {
            var result = CreateEnvelope(uri, status);
            result["ok"] = false;
            result["error"] = "not-found";
            result["kind"] = kind;
            result["key"] = key;
            return result;
        }

        private static string EncodeSegment(string value)
        {
            return Uri.EscapeDataString(value);
        }

        private static string DecodeSegment(string value)
        {
            return Uri.UnescapeDataString(value);
        }

        private static string? ResolveAssetPath(string guidOrPath)
        {
            var guidPath = AssetDatabase.GUIDToAssetPath(guidOrPath);
            return string.IsNullOrEmpty(guidPath) ? guidOrPath : guidPath;
        }

        private static string EnsureTimelineAssetPath(string path)
        {
            var normalized = string.IsNullOrWhiteSpace(path) ? "Assets/Cinematics/Timeline.playable" : path;
            if (!normalized.EndsWith(".playable", StringComparison.OrdinalIgnoreCase))
            {
                normalized += ".playable";
            }

            return normalized;
        }

#pragma warning disable CS8602 // Roslyn does not narrow folder after the Unity AssetDatabase guard below.
        private static void EnsureAssetFolder(string assetPath)
        {
            var folder = (System.IO.Path.GetDirectoryName(assetPath) ?? string.Empty).Replace("\\", "/");
            if (string.IsNullOrWhiteSpace(folder) || AssetDatabase.IsValidFolder(folder))
            {
                return;
            }

            var parts = folder.Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }

                current = next;
            }
        }
#pragma warning restore CS8602

        private static string SanitizeAssetName(string name)
        {
            foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '_');
            }

            return string.IsNullOrWhiteSpace(name) ? "Timeline" : name;
        }

        private static string RuntimeSlowMoNote()
        {
            return "Runtime slow-mo is intentionally not hidden in editor authoring tools. During the ending session, game code should tween Time.timeScale (for example 0.2-0.4), set Time.fixedDeltaTime = baseFixedDeltaTime * Time.timeScale while slowed, then restore both values after the Timeline beat.";
        }
    }
}
