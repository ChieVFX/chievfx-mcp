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
        private static object? ReadResource(string uri)
        {
            var status = GetDependencyStatus();
            if (string.Equals(uri, StatusUri, StringComparison.Ordinal))
            {
                return ReadStatusResource(uri, status);
            }

            if (string.Equals(uri, CinemachineCamerasUri, StringComparison.Ordinal))
            {
                return status.Cinemachine.Available ? ReadCinemachineCamerasResource(uri, status) : CreateCinemachineUnavailable(status, $"Resource '{uri}'");
            }

            if (uri.StartsWith(CinemachineCameraDetailPrefix, StringComparison.Ordinal))
            {
                return status.Cinemachine.Available ? ReadCinemachineCameraDetailResource(uri, DecodeSegment(uri.Substring(CinemachineCameraDetailPrefix.Length)), status) : CreateCinemachineUnavailable(status, $"Resource '{uri}'");
            }

            if (string.Equals(uri, CinemachineBrainsUri, StringComparison.Ordinal))
            {
                return status.Cinemachine.Available ? ReadCinemachineBrainsResource(uri, status) : CreateCinemachineUnavailable(status, $"Resource '{uri}'");
            }

            if (uri.StartsWith(CinemachineBrainDetailPrefix, StringComparison.Ordinal))
            {
                return status.Cinemachine.Available ? ReadCinemachineBrainDetailResource(uri, DecodeSegment(uri.Substring(CinemachineBrainDetailPrefix.Length)), status) : CreateCinemachineUnavailable(status, $"Resource '{uri}'");
            }

            if (string.Equals(uri, CinemachineSequencersUri, StringComparison.Ordinal))
            {
                return status.Sequencer.Available ? ReadSequencersResource(uri, status) : CreateSequencerUnavailable(status, $"Resource '{uri}'");
            }

            if (uri.StartsWith(CinemachineSequencerDetailPrefix, StringComparison.Ordinal))
            {
                return status.Sequencer.Available ? ReadSequencerDetailResource(uri, DecodeSegment(uri.Substring(CinemachineSequencerDetailPrefix.Length)), status) : CreateSequencerUnavailable(status, $"Resource '{uri}'");
            }

            if (string.Equals(uri, CinemachineSplinesDollyUri, StringComparison.Ordinal))
            {
                return SplinesDollyAvailable(status) ? ReadSplinesDollyResource(uri, status) : CreateAdvancedHelperUnavailable(status, status.SplinesDolly, $"Resource '{uri}'");
            }

            if (string.Equals(uri, CinemachineInputAxisControllersUri, StringComparison.Ordinal))
            {
                return status.InputAxisController.Available ? ReadInputAxisControllersResource(uri, status) : CreateAdvancedHelperUnavailable(status, status.InputAxisController, $"Resource '{uri}'");
            }

            if (string.Equals(uri, CinemachineBlenderSettingsUri, StringComparison.Ordinal))
            {
                return BlenderSettingsAvailable(status) ? ReadBlenderSettingsResource(uri, status) : CreateBlenderSettingsUnavailable(status, $"Resource '{uri}'");
            }

            if (string.Equals(uri, CinemachineImpulseUri, StringComparison.Ordinal))
            {
                return status.Impulse.Available ? ReadImpulseResource(uri, status) : CreateAdvancedHelperUnavailable(status, status.Impulse, $"Resource '{uri}'");
            }

            if (string.Equals(uri, CinemachineConfiner2DUri, StringComparison.Ordinal))
            {
                return ConfinerAvailable(status, status.Confiner2D) ? ReadConfinerResource(uri, status, status.Confiner2D) : CreateConfinerUnavailable(status, status.Confiner2D, $"Resource '{uri}'");
            }

            if (string.Equals(uri, CinemachineConfiner3DUri, StringComparison.Ordinal))
            {
                return ConfinerAvailable(status, status.Confiner3D) ? ReadConfinerResource(uri, status, status.Confiner3D) : CreateConfinerUnavailable(status, status.Confiner3D, $"Resource '{uri}'");
            }

            if (string.Equals(uri, TimelineDirectorsUri, StringComparison.Ordinal))
            {
                return status.Timeline.Available ? ReadTimelineDirectorsResource(uri, status) : CreateUnavailable(StatusUri, status, $"Resource '{uri}' requires {TimelinePackageName}, loaded UnityEngine.Timeline types, and active CHIEVFX_MCP_HAS_TIMELINE.");
            }

            if (uri.StartsWith(TimelineDirectorDetailPrefix, StringComparison.Ordinal))
            {
                return status.Timeline.Available ? ReadTimelineDirectorDetailResource(uri, DecodeSegment(uri.Substring(TimelineDirectorDetailPrefix.Length)), status) : CreateUnavailable(StatusUri, status, $"Resource '{uri}' requires {TimelinePackageName}, loaded UnityEngine.Timeline types, and active CHIEVFX_MCP_HAS_TIMELINE.");
            }

            if (string.Equals(uri, TimelineAssetsUri, StringComparison.Ordinal))
            {
                return status.Timeline.Available ? ReadTimelineAssetsResource(uri, status) : CreateUnavailable(StatusUri, status, $"Resource '{uri}' requires {TimelinePackageName}, loaded UnityEngine.Timeline types, and active CHIEVFX_MCP_HAS_TIMELINE.");
            }

            if (uri.StartsWith(TimelineAssetDetailPrefix, StringComparison.Ordinal))
            {
                return status.Timeline.Available ? ReadTimelineAssetDetailResource(uri, DecodeSegment(uri.Substring(TimelineAssetDetailPrefix.Length)), status) : CreateUnavailable(StatusUri, status, $"Resource '{uri}' requires {TimelinePackageName}, loaded UnityEngine.Timeline types, and active CHIEVFX_MCP_HAS_TIMELINE.");
            }

            throw new InvalidOperationException($"Unknown camera/cutscene extension resource '{uri}'.");
        }

        private static object? RunTool(string toolName, JToken args)
        {
            var status = GetDependencyStatus();
            return toolName switch
            {
                "brain-ensure" => status.Cinemachine.Available ? EnsureBrain(args, status) : CreateCinemachineUnavailable(status, $"Tool '{toolName}'"),
                "cinemachine-create" => status.Cinemachine.Available ? CreateCinemachineCamera(args, status) : CreateCinemachineUnavailable(status, $"Tool '{toolName}'"),
                "cinemachine-set" => status.Cinemachine.Available ? SetCinemachineCamera(args, status) : CreateCinemachineUnavailable(status, $"Tool '{toolName}'"),
                "cinemachine-sequencer-create" => status.Sequencer.Available ? CreateSequencerCamera(args, status) : CreateSequencerUnavailable(status, $"Tool '{toolName}'"),
                "cinemachine-spline-dolly-set" => SplinesDollyAvailable(status) ? SetSplineDolly(args, status) : CreateAdvancedHelperUnavailable(status, status.SplinesDolly, $"Tool '{toolName}'"),
                "cinemachine-blender-settings-set" => BlenderSettingsAvailable(status) ? SetBlenderSettings(args, status) : CreateBlenderSettingsUnavailable(status, $"Tool '{toolName}'"),
                "cinemachine-confiner-set" => SetConfiner(args, status),
                "timeline-director-create" => status.Timeline.Available ? CreateTimelineDirector(args, status) : CreateUnavailable(StatusUri, status, $"Tool '{toolName}' requires {TimelinePackageName}, loaded UnityEngine.Timeline types, and active CHIEVFX_MCP_HAS_TIMELINE."),
                "timeline-shot-sequence-create" => status.Cinemachine.Available && status.Timeline.Available ? CreateShotSequence(args, status) : CreateShotSequenceUnavailable(status, $"Tool '{toolName}'"),
                "timeline-director-preview" => status.Timeline.Available ? PreviewDirector(args, status) : CreateUnavailable(StatusUri, status, $"Tool '{toolName}' requires {TimelinePackageName}, loaded UnityEngine.Timeline types, and active CHIEVFX_MCP_HAS_TIMELINE."),
                _ => throw new InvalidOperationException($"Unknown camera/cutscene extension tool '{toolName}'."),
            };
        }

        private static Dictionary<string, object?> ReadStatusResource(string uri, DependencyStatus status)
        {
            var result = CreateEnvelope(uri, status);
            result["available"] = status.AnyAvailable;
            result["dependencyReason"] = status.Reason;
            result["cinemachineApiFamily"] = status.Cinemachine.ApiFamily;
            result["sequencerCameraAvailable"] = status.Sequencer.Available;
            result["sequencerCameraTypeLoaded"] = status.Sequencer.SequencerCameraTypeLoaded;
            result["sequencerCameraUnavailableReason"] = status.Sequencer.Reason;
            result["cinemachine"] = status.Cinemachine.ToDictionary();
            result["sequencerCamera"] = status.Sequencer.ToDictionary();
            result["splinesDolly"] = status.SplinesDolly.ToDictionary();
            result["inputAxisController"] = status.InputAxisController.ToDictionary();
            result["inputSystem"] = status.InputSystem.ToDictionary();
            result["blenderSettings"] = status.BlenderSettings.ToDictionary();
            result["impulse"] = status.Impulse.ToDictionary();
            result["collisionImpulseSourceTypeLoaded"] = status.CollisionImpulseSourceType != null;
            result["externalImpulseListenerTypeLoaded"] = status.ExternalImpulseListenerType != null;
            result["confiner2D"] = status.Confiner2D.ToDictionary();
            result["confiner3D"] = status.Confiner3D.ToDictionary();
            result["obsoleteConfinerTypeLoaded"] = status.ObsoleteConfinerType != null;
            result["obsoleteConfinerWarning"] = status.ObsoleteConfinerType == null
                ? null
                : "Deprecated Unity.Cinemachine.CinemachineConfiner is loaded; helper will warn only and will not author obsolete confiners.";
            result["timeline"] = status.Timeline.ToDictionary();
            result["resources"] = new[]
            {
                StatusUri,
                CinemachineCamerasUri,
                CinemachineBrainsUri,
                CinemachineSequencersUri,
                CinemachineSplinesDollyUri,
                CinemachineInputAxisControllersUri,
                CinemachineBlenderSettingsUri,
                CinemachineImpulseUri,
                CinemachineConfiner2DUri,
                CinemachineConfiner3DUri,
                TimelineDirectorsUri,
                TimelineAssetsUri,
            };
            result["resourceTemplates"] = new[]
            {
                CinemachineCameraDetailPrefix + "{pathOrInstanceId}",
                CinemachineBrainDetailPrefix + "{pathOrInstanceId}",
                CinemachineSequencerDetailPrefix + "{pathOrInstanceId}",
                TimelineDirectorDetailPrefix + "{pathOrInstanceId}",
                TimelineAssetDetailPrefix + "{guidOrPath}",
            };
            result["tools"] = new[]
            {
                "brain-ensure",
                "cinemachine-create",
                "cinemachine-set",
                "cinemachine-sequencer-create",
                "cinemachine-spline-dolly-set",
                "cinemachine-blender-settings-set",
                "cinemachine-confiner-set",
                "timeline-director-create",
                "timeline-shot-sequence-create",
                "timeline-director-preview",
            };
            result["prompts"] = new[] { "cameras-ending-session-slowmo-zoom", "gamefeel-ending-session-slowmo", "cameras-cinemachine-sequencer-camera", "cameras-cinemachine-splines-dolly", "cameras-cinemachine-input-axis-controller", "cameras-cinemachine-impulse-shake" };
            result["workflowNotes"] = new[]
            {
                "Ending-session slow-mo should be integrated in runtime game code with Time.timeScale plus Time.fixedDeltaTime restore; MCP tools only author Camera/Timeline assets.",
                "gamefeel-ending-session-slowmo is prompt-only guidance; MCP does not create hidden runtime owners or mutate Time globals.",
                "Read status and read-only inventory resources before using mutating camera/cutscene tools.",
                "Splines dolly helper binds existing CinemachineCamera and existing SplineContainer only; author spline knots/shape manually in Unity first.",
                "InputAxisController guidance is prompt/read-only only; MCP does not invent input assets or hidden input forwarding scripts.",
                "Impulse guidance is prompt/read-only only; MCP does not add hidden impulse trigger scripts or invent gameplay trigger ownership.",
                "Use screenshot-camera against authored Cinemachine/Unity camera fixtures for visual QA.",
                "Timeline shot overlaps ignore CinemachineBrain default/custom blend settings; use Timeline clip overlap for Timeline blend feel.",
                "Confiner2D cache invalidation can be expensive; invalidate bounding-shape/lens caches only when explicitly requested after collider or lens changes.",
            };
            return result;
        }

        private static Dictionary<string, object?> ReadCinemachineCamerasResource(string uri, DependencyStatus status)
        {
            var cameras = EnumerateComponents(status.Cinemachine.CameraType!)
                .Take(MaxCameraRows + 1)
                .ToArray();
            var result = CreateEnvelope(uri, status);
            result["stage"] = DescribeCurrentStage();
            result["count"] = Math.Min(cameras.Length, MaxCameraRows);
            result["capped"] = cameras.Length > MaxCameraRows;
            result["maxRows"] = MaxCameraRows;
            result["cameras"] = cameras.Take(MaxCameraRows).Select(camera => DescribeCinemachineCamera(camera, detail: false)).ToArray();
            return result;
        }

        private static Dictionary<string, object?> ReadCinemachineCameraDetailResource(string uri, string pathOrInstanceId, DependencyStatus status)
        {
            var camera = ResolveComponent(status.Cinemachine.CameraType!, pathOrInstanceId);
            if (camera == null)
            {
                return CreateNotFound(uri, status, "cinemachine-camera", pathOrInstanceId);
            }

            var result = CreateEnvelope(uri, status);
            result["stage"] = DescribeCurrentStage();
            result["target"] = DescribeCinemachineCamera(camera, detail: true);
            return result;
        }

        private static Dictionary<string, object?> ReadCinemachineBrainsResource(string uri, DependencyStatus status)
        {
            var brains = EnumerateComponents(status.Cinemachine.BrainType!)
                .Take(MaxBrainRows + 1)
                .ToArray();
            var result = CreateEnvelope(uri, status);
            result["stage"] = DescribeCurrentStage();
            result["count"] = Math.Min(brains.Length, MaxBrainRows);
            result["capped"] = brains.Length > MaxBrainRows;
            result["maxRows"] = MaxBrainRows;
            result["brains"] = brains.Take(MaxBrainRows).Select(DescribeBrain).ToArray();
            return result;
        }

        private static Dictionary<string, object?> ReadCinemachineBrainDetailResource(string uri, string pathOrInstanceId, DependencyStatus status)
        {
            var brain = ResolveComponent(status.Cinemachine.BrainType!, pathOrInstanceId);
            if (brain == null)
            {
                return CreateNotFound(uri, status, "cinemachine-brain", pathOrInstanceId);
            }

            var result = CreateEnvelope(uri, status);
            result["stage"] = DescribeCurrentStage();
            result["target"] = DescribeBrain(brain);
            return result;
        }

        private static Dictionary<string, object?> ReadSequencersResource(string uri, DependencyStatus status)
        {
            var sequencers = EnumerateComponents(status.Sequencer.SequencerCameraType!)
                .Take(MaxSequencerRows + 1)
                .ToArray();
            var result = CreateEnvelope(uri, status);
            result["stage"] = DescribeCurrentStage();
            result["count"] = Math.Min(sequencers.Length, MaxSequencerRows);
            result["capped"] = sequencers.Length > MaxSequencerRows;
            result["maxRows"] = MaxSequencerRows;
            result["sequencers"] = sequencers.Take(MaxSequencerRows).Select(sequencer => DescribeSequencer(sequencer, detail: false)).ToArray();
            return result;
        }

        private static Dictionary<string, object?> ReadSequencerDetailResource(string uri, string pathOrInstanceId, DependencyStatus status)
        {
            var sequencer = ResolveComponent(status.Sequencer.SequencerCameraType!, pathOrInstanceId);
            if (sequencer == null)
            {
                return CreateNotFound(uri, status, "cinemachine-sequencer-camera", pathOrInstanceId);
            }

            var result = CreateEnvelope(uri, status);
            result["stage"] = DescribeCurrentStage();
            result["target"] = DescribeSequencer(sequencer, detail: true);
            return result;
        }

        private static Dictionary<string, object?> ReadSplinesDollyResource(string uri, DependencyStatus status)
        {
            var dollies = EnumerateComponents(status.SplinesDolly.HelperType!)
                .Take(MaxAdvancedHelperRows + 1)
                .ToArray();
            var splineContainers = EnumerateComponents(status.SplinesDolly.OptionalType!)
                .Take(MaxAdvancedHelperRows + 1)
                .ToArray();
            var camerasWithDolly = status.Cinemachine.CameraType == null
                ? Array.Empty<Component>()
                : EnumerateComponents(status.Cinemachine.CameraType)
                    .Where(camera => camera.GetComponent(status.SplinesDolly.HelperType!) != null)
                    .Take(MaxAdvancedHelperRows + 1)
                    .ToArray();

            var result = CreateEnvelope(uri, status);
            result["helper"] = status.SplinesDolly.ToDictionary();
            result["stage"] = DescribeCurrentStage();
            result["count"] = Math.Min(dollies.Length, MaxAdvancedHelperRows);
            result["capped"] = dollies.Length > MaxAdvancedHelperRows;
            result["maxRows"] = MaxAdvancedHelperRows;
            result["splineContainerCount"] = Math.Min(splineContainers.Length, MaxAdvancedHelperRows);
            result["splineContainersCapped"] = splineContainers.Length > MaxAdvancedHelperRows;
            result["splineContainers"] = splineContainers.Take(MaxAdvancedHelperRows).Select(container => DescribeSplineContainer(container, status)).ToArray();
            result["cameraCount"] = Math.Min(camerasWithDolly.Length, MaxAdvancedHelperRows);
            result["cameras"] = camerasWithDolly.Take(MaxAdvancedHelperRows).Select(camera => DescribeSplineDollyCamera(camera, status)).ToArray();
            result["splinesDollies"] = dollies.Take(MaxAdvancedHelperRows).Select(dolly => DescribeSplinesDolly(dolly, status)).ToArray();
            result["authoringPolicy"] = "Read-only inventory plus cinemachine-spline-dolly-set. Author SplineContainer knots and shape manually in Unity first; MCP does not create or reshape spline geometry.";
            result["visualQaHint"] = "Preview/evaluate start, mid, and end camera positions, then capture screenshot-camera from the Unity Camera with CinemachineBrain; verify target framing changes and no black frames.";
            return result;
        }

        private static Dictionary<string, object?> ReadAdvancedComponentInventoryResource(string uri, DependencyStatus status, AdvancedHelperStatus helper, string rowsKey, Func<Component, Dictionary<string, object?>> describe)
        {
            var components = EnumerateComponents(helper.HelperType!)
                .Take(MaxAdvancedHelperRows + 1)
                .ToArray();
            var result = CreateEnvelope(uri, status);
            result["helper"] = helper.ToDictionary();
            result["stage"] = DescribeCurrentStage();
            result["count"] = Math.Min(components.Length, MaxAdvancedHelperRows);
            result["capped"] = components.Length > MaxAdvancedHelperRows;
            result["maxRows"] = MaxAdvancedHelperRows;
            result[rowsKey] = components.Take(MaxAdvancedHelperRows).Select(describe).ToArray();
            return result;
        }

        private static Dictionary<string, object?> ReadInputAxisControllersResource(string uri, DependencyStatus status)
        {
            var controllers = EnumerateComponents(status.InputAxisController.HelperType!)
                .Take(MaxAdvancedHelperRows + 1)
                .ToArray();
            var owners = status.InputAxisController.TertiaryHelperType == null
                ? Array.Empty<Component>()
                : EnumerateComponentsImplementing(status.InputAxisController.TertiaryHelperType)
                    .Take(MaxAdvancedHelperRows + 1)
                    .ToArray();
            var result = CreateEnvelope(uri, status);
            result["helper"] = status.InputAxisController.ToDictionary();
            result["inputSystem"] = status.InputSystem.ToDictionary();
            result["stage"] = DescribeCurrentStage();
            result["count"] = Math.Min(controllers.Length, MaxAdvancedHelperRows);
            result["capped"] = controllers.Length > MaxAdvancedHelperRows;
            result["maxRows"] = MaxAdvancedHelperRows;
            result["inputAxisControllers"] = controllers.Take(MaxAdvancedHelperRows).Select(component => DescribeInputAxisController(component, status)).ToArray();
            result["axisOwnerCount"] = Math.Min(owners.Length, MaxAdvancedHelperRows);
            result["axisOwnersCapped"] = owners.Length > MaxAdvancedHelperRows;
            result["axisOwners"] = owners.Take(MaxAdvancedHelperRows).Select(DescribeAdvancedComponent).ToArray();
            result["authoringPolicy"] = "Prompt/read-only guidance only. MCP should not invent input assets, add hidden input forwarding scripts, edit Packages/manifest.json, or silently switch project input handling.";
            result["axisOwnerGuidance"] = "Detect existing IInputAxisOwner implementers before suggesting controller wiring; owners expose Cinemachine InputAxis values and should not be inferred from object names alone.";
            result["inputSystemGuidance"] = status.InputSystem.Available
                ? "Input System-specific guidance is enabled because InputActionReference and PlayerInput types are loaded; reference existing actions only, and remember PlayerInput may clone action assets per player at runtime."
                : "Input System-specific guidance is unavailable; discuss legacy input/manual value driving without fabricating InputAction assets.";
            result["visualQaHint"] = "screenshot-camera is secondary for input setup; manually exercise actions or legacy axes in a controlled fixture and verify camera orientation/FOV.";
            result["warnings"] = InputAxisControllerWarnings(controllers, owners, status).ToArray();
            return result;
        }

        private static Dictionary<string, object?> ReadBlenderSettingsResource(string uri, DependencyStatus status)
        {
            var assets = EnumerateAssetsOfType(status.BlenderSettings.HelperType!)
                .Take(MaxAdvancedHelperRows + 1)
                .ToArray();
            var customBlendBrains = status.Cinemachine.BrainType == null
                ? Array.Empty<Component>()
                : EnumerateComponents(status.Cinemachine.BrainType)
                    .Where(brain => ReadMember(brain, "CustomBlends") != null)
                    .Take(MaxAdvancedHelperRows + 1)
                    .ToArray();
            var cameraNames = CurrentCinemachineCameraNames(status);
            var result = CreateEnvelope(uri, status);
            result["helper"] = status.BlenderSettings.ToDictionary();
            result["stage"] = DescribeCurrentStage();
            result["count"] = Math.Min(assets.Length + customBlendBrains.Length, MaxAdvancedHelperRows);
            result["capped"] = assets.Length + customBlendBrains.Length > MaxAdvancedHelperRows;
            result["maxRows"] = MaxAdvancedHelperRows;
            result["maxBlendEntries"] = MaxCustomBlendEntries;
            result["assets"] = assets.Take(MaxAdvancedHelperRows).Select(asset => DescribeBlenderSettingsAsset(asset, cameraNames)).ToArray();
            result["brainCustomBlends"] = customBlendBrains.Take(MaxAdvancedHelperRows).Select(brain => DescribeBrainCustomBlends(brain, cameraNames)).ToArray();
            result["fallbackBehavior"] = "CinemachineBrain uses a matching custom blend when found, otherwise DefaultBlend. Entries using ANY CAMERA are wildcard fallbacks.";
            result["timelineNote"] = "Timeline Cinemachine shot overlaps ignore Brain default/custom blend settings; adjacent Timeline clip overlap controls Timeline blend feel.";
            return result;
        }

        private static Dictionary<string, object?> ReadImpulseResource(string uri, DependencyStatus status)
        {
            var sources = EnumerateComponents(status.Impulse.HelperType!)
                .Take(MaxAdvancedHelperRows + 1)
                .ToArray();
            var listeners = status.Impulse.SecondaryHelperType == null
                ? Array.Empty<Component>()
                : EnumerateComponents(status.Impulse.SecondaryHelperType)
                    .Take(MaxAdvancedHelperRows + 1)
                    .ToArray();
            var collisionSources = status.CollisionImpulseSourceType == null
                ? Array.Empty<Component>()
                : EnumerateComponents(status.CollisionImpulseSourceType)
                    .Take(MaxAdvancedHelperRows + 1)
                    .ToArray();
            var externalListeners = status.ExternalImpulseListenerType == null
                ? Array.Empty<Component>()
                : EnumerateComponents(status.ExternalImpulseListenerType)
                    .Take(MaxAdvancedHelperRows + 1)
                    .ToArray();
            var result = CreateEnvelope(uri, status);
            result["helper"] = status.Impulse.ToDictionary();
            result["stage"] = DescribeCurrentStage();
            result["count"] = Math.Min(sources.Length + listeners.Length, MaxAdvancedHelperRows);
            result["capped"] = sources.Length + listeners.Length > MaxAdvancedHelperRows;
            result["maxRows"] = MaxAdvancedHelperRows;
            result["sources"] = sources.Take(MaxAdvancedHelperRows).Select(DescribeImpulseComponent).ToArray();
            result["listeners"] = listeners.Take(MaxAdvancedHelperRows).Select(DescribeImpulseComponent).ToArray();
            result["optionalTypes"] = new Dictionary<string, object?>
            {
                ["collisionImpulseSourceTypeName"] = "Unity.Cinemachine.CinemachineCollisionImpulseSource",
                ["collisionImpulseSourceTypeLoaded"] = status.CollisionImpulseSourceType != null,
                ["externalImpulseListenerTypeName"] = "Unity.Cinemachine.CinemachineExternalImpulseListener",
                ["externalImpulseListenerTypeLoaded"] = status.ExternalImpulseListenerType != null,
            };
            result["collisionSources"] = collisionSources.Take(MaxAdvancedHelperRows).Select(DescribeImpulseComponent).ToArray();
            result["externalListeners"] = externalListeners.Take(MaxAdvancedHelperRows).Select(DescribeImpulseComponent).ToArray();
            result["sourceChannels"] = sources.Select(ImpulseChannelLabel).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            result["listenerChannels"] = listeners.Select(ImpulseChannelLabel).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            result["authoringPolicy"] = "Prompt/read-only guidance only. MCP should not add hidden impulse trigger scripts or invent gameplay events; triggers belong in explicit game-owned code or user-authored QA fixtures.";
            result["pairingGuidance"] = "Verify at least one source and one listener exist, then compare channel masks before tuning amplitude, gain, distance, or damping.";
            result["visualQaHint"] = "In Play Mode, trigger an explicit source and capture screenshot-camera before/during/after shake on the Unity Camera with CinemachineBrain; visual diff is best-effort because impulse timing is transient.";
            result["warnings"] = ImpulseWarnings(sources, listeners).ToArray();
            return result;
        }

        private static Dictionary<string, object?> ReadConfinerResource(string uri, DependencyStatus status, AdvancedHelperStatus helper)
        {
            var confiners = EnumerateComponents(helper.HelperType!)
                .Take(MaxAdvancedHelperRows + 1)
                .ToArray();
            var obsoleteConfiners = status.ObsoleteConfinerType == null
                ? Array.Empty<Component>()
                : EnumerateComponents(status.ObsoleteConfinerType)
                    .Take(MaxAdvancedHelperRows + 1)
                    .ToArray();
            var warnings = new List<string>
            {
                "Confiner2D cache invalidation can be expensive for complex polygon/composite colliders; use invalidateCache only after explicit collider topology changes.",
                "If the camera lens changes and the confiner exposes a lens cache, use invalidateLensCache explicitly before visual QA.",
            };
            if (obsoleteConfiners.Length > 0)
            {
                warnings.Add("Deprecated Unity.Cinemachine.CinemachineConfiner components detected. This helper reports them only and will not author or migrate obsolete confiners.");
            }

            var result = CreateEnvelope(uri, status);
            result["helper"] = helper.ToDictionary();
            result["stage"] = DescribeCurrentStage();
            result["count"] = Math.Min(confiners.Length, MaxAdvancedHelperRows);
            result["capped"] = confiners.Length > MaxAdvancedHelperRows;
            result["maxRows"] = MaxAdvancedHelperRows;
            result["confiners"] = confiners.Take(MaxAdvancedHelperRows).Select(DescribeConfiner).ToArray();
            result["obsoleteConfinerTypeLoaded"] = status.ObsoleteConfinerType != null;
            result["obsoleteConfinerCount"] = Math.Min(obsoleteConfiners.Length, MaxAdvancedHelperRows);
            result["obsoleteConfiners"] = obsoleteConfiners.Take(MaxAdvancedHelperRows).Select(DescribeAdvancedComponent).ToArray();
            result["warnings"] = warnings.ToArray();
            return result;
        }

        private static Dictionary<string, object?> ReadTimelineDirectorsResource(string uri, DependencyStatus status)
        {
            var directors = EnumerateDirectors()
                .Take(MaxDirectorRows + 1)
                .ToArray();
            var result = CreateEnvelope(uri, status);
            result["stage"] = DescribeCurrentStage();
            result["count"] = Math.Min(directors.Length, MaxDirectorRows);
            result["capped"] = directors.Length > MaxDirectorRows;
            result["maxRows"] = MaxDirectorRows;
            result["directors"] = directors.Take(MaxDirectorRows).Select(director => DescribeDirector(director, detail: false)).ToArray();
            return result;
        }

        private static Dictionary<string, object?> ReadTimelineDirectorDetailResource(string uri, string pathOrInstanceId, DependencyStatus status)
        {
            var director = ResolveDirector(pathOrInstanceId);
            if (director == null)
            {
                return CreateNotFound(uri, status, "timeline-director", pathOrInstanceId);
            }

            var result = CreateEnvelope(uri, status);
            result["stage"] = DescribeCurrentStage();
            result["target"] = DescribeDirector(director, detail: true);
            return result;
        }

        private static Dictionary<string, object?> ReadTimelineAssetsResource(string uri, DependencyStatus status)
        {
            var assets = EnumerateTimelineAssetPaths(status)
                .Take(MaxAssetRows + 1)
                .ToArray();
            var result = CreateEnvelope(uri, status);
            result["count"] = Math.Min(assets.Length, MaxAssetRows);
            result["capped"] = assets.Length > MaxAssetRows;
            result["maxRows"] = MaxAssetRows;
            result["assets"] = assets.Take(MaxAssetRows).Select(path => DescribeTimelineAsset(path, status, detail: false)).ToArray();
            return result;
        }

        private static Dictionary<string, object?> ReadTimelineAssetDetailResource(string uri, string guidOrPath, DependencyStatus status)
        {
            var path = ResolveAssetPath(guidOrPath);
            var asset = string.IsNullOrWhiteSpace(path) ? null : AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (asset == null || !status.Timeline.AssetType!.IsInstanceOfType(asset))
            {
                return CreateNotFound(uri, status, "timeline-asset", guidOrPath);
            }

            var result = CreateEnvelope(uri, status);
            result["asset"] = DescribeTimelineAsset(path!, status, detail: true);
            return result;
        }
    }
}
