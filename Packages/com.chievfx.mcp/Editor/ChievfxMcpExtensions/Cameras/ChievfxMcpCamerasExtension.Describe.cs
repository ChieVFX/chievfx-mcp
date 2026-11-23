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
        private static Dictionary<string, object?> DescribeCinemachineCamera(Component camera, bool detail)
        {
            var result = new Dictionary<string, object?>
            {
                ["name"] = camera.name,
                ["path"] = GetTransformPath(camera.transform),
                ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(camera),
                ["activeInHierarchy"] = camera.gameObject.activeInHierarchy,
                ["enabled"] = camera is Behaviour behaviour ? behaviour.enabled : null,
                ["detailUri"] = CinemachineDetailUri(camera),
                ["priority"] = ReadMember(camera, "Priority"),
                ["lens"] = DescribeLens(ReadMember(camera, "Lens")),
                ["target"] = DescribeObjectReference(ReadMember(camera, "Target") ?? ReadMember(camera, "TrackingTarget") ?? ReadMember(camera, "Follow") ?? ReadMember(camera, "LookAt")),
            };

            if (detail)
            {
                result["unityTransform"] = DescribeTransform(camera.transform);
                result["safeSetFields"] = new[] { "targetPath", "priority", "lens.fieldOfView", "lens.orthographicSize", "lens.nearClipPlane", "lens.farClipPlane", "enabled" };
            }

            return result;
        }

        private static Dictionary<string, object?> DescribeSplineContainer(Component container, DependencyStatus status)
        {
            var result = DescribeAdvancedComponent(container);
            var spline = ReadMember(container, "Spline");
            var splines = ReadMember(container, "Splines");
            result["spline"] = DescribeMemberValue(spline);
            result["splines"] = DescribeMemberValue(splines);
            result["splineRoll"] = DescribeObjectReferenceOrNull(GetOptionalComponent(container.gameObject, status.SplinesDolly.SecondaryHelperType));
            result["hasSplineRoll"] = GetOptionalComponent(container.gameObject, status.SplinesDolly.SecondaryHelperType) != null;
            return result;
        }

        private static Dictionary<string, object?> DescribeSplineDollyCamera(Component camera, DependencyStatus status)
        {
            var result = DescribeCinemachineCamera(camera, detail: false);
            var dolly = camera.gameObject.GetComponent(status.SplinesDolly.HelperType!);
            result["splineDolly"] = dolly != null ? DescribeSplinesDolly(dolly, status) : null;
            result["splineRoll"] = DescribeObjectReferenceOrNull(GetOptionalComponent(camera.gameObject, status.SplinesDolly.SecondaryHelperType));
            result["hasSplineRoll"] = GetOptionalComponent(camera.gameObject, status.SplinesDolly.SecondaryHelperType) != null;
            return result;
        }

        private static Dictionary<string, object?> DescribeSplinesDolly(Component dolly, DependencyStatus status)
        {
            var result = DescribeAdvancedComponent(dolly);
            var splineContainer = ReadFirstMember(dolly, new[] { "Spline", "SplineContainer", "Container" }) as Component;
            var camera = status.Cinemachine.CameraType == null ? null : dolly.GetComponent(status.Cinemachine.CameraType);
            result["camera"] = camera != null ? DescribeCinemachineCamera(camera, detail: false) : null;
            result["splineContainer"] = splineContainer != null ? DescribeSplineContainer(splineContainer, status) : null;
            result["cameraPosition"] = new Dictionary<string, object?>
            {
                ["value"] = DescribeMemberValue(ReadFirstMember(dolly, new[] { "CameraPosition", "Position" })),
                ["units"] = DescribeMemberValue(ReadFirstMember(dolly, new[] { "PositionUnits", "Units" })),
            };
            result["autoDolly"] = DescribeMemberValue(ReadMember(dolly, "AutoDolly"));
            result["autoDollyMode"] = DescribeAutoDollyMode(ReadMember(dolly, "AutoDolly"));
            result["splineRoll"] = DescribeObjectReferenceOrNull(GetOptionalComponent(splineContainer?.gameObject, status.SplinesDolly.SecondaryHelperType) ?? GetOptionalComponent(dolly.gameObject, status.SplinesDolly.SecondaryHelperType));
            result["hasSplineRoll"] = GetOptionalComponent(splineContainer?.gameObject, status.SplinesDolly.SecondaryHelperType) != null || GetOptionalComponent(dolly.gameObject, status.SplinesDolly.SecondaryHelperType) != null;
            result["warnings"] = SplineDollyWarnings(dolly, splineContainer, status).ToArray();
            return result;
        }

        private static Dictionary<string, object?> DescribeInputAxisController(Component controller, DependencyStatus status)
        {
            var result = DescribeAdvancedComponent(controller);
            AddMemberIfPresent(result, controller, "PlayerIndex", "playerIndex");
            AddMemberIfPresent(result, controller, "ScanRecursively", "scanRecursively");
            AddMemberIfPresent(result, controller, "SuppressInputWhileDragging", "suppressInputWhileDragging");
            AddMemberIfPresent(result, controller, "Controllers", "controllers");
            result["inputSystem"] = status.InputSystem.ToDictionary();
            result["inputSystemReferences"] = status.InputSystem.Available
                ? DescribeInputSystemReferences(controller, status).Take(MaxAdvancedHelperRows).ToArray()
                : Array.Empty<Dictionary<string, object?>>();
            result["axisOwnerCandidate"] = status.InputAxisController.TertiaryHelperType == null
                ? null
                : DescribeNearestAxisOwner(controller, status.InputAxisController.TertiaryHelperType);
            return result;
        }

        private static Dictionary<string, object?> DescribeBlenderSettingsAsset(UnityEngine.Object asset, HashSet<string> cameraNames)
        {
            var result = DescribeAssetObject(asset);
            var specs = ReadCustomBlendSpecs(ReadMember(asset, "CustomBlends") ?? ReadMember(asset, "m_CustomBlends"));
            var warnings = new List<string>();
            AddBlendValidationWarnings(specs, cameraNames, warnings);
            result["blendCount"] = Math.Min(specs.Length, MaxCustomBlendEntries);
            result["blendEntriesCapped"] = specs.Length > MaxCustomBlendEntries;
            result["usesAnyCamera"] = specs.Any(UsesAnyCamera);
            result["blends"] = specs.Take(MaxCustomBlendEntries).Select(DescribeBlendSpec).ToArray();
            result["warnings"] = warnings.ToArray();
            return result;
        }

        private static Dictionary<string, object?> DescribeBrainCustomBlends(Component brain, HashSet<string> cameraNames)
        {
            var customBlends = ReadMember(brain, "CustomBlends");
            var result = DescribeBrain(brain);
            var specs = ReadCustomBlendSpecs(customBlends == null ? null : ReadMember(customBlends, "CustomBlends"));
            var warnings = new List<string>();
            AddBlendValidationWarnings(specs, cameraNames, warnings);
            result["customBlendsAsset"] = customBlends is UnityEngine.Object asset ? DescribeAssetObject(asset) : null;
            result["blendCount"] = Math.Min(specs.Length, MaxCustomBlendEntries);
            result["blendEntriesCapped"] = specs.Length > MaxCustomBlendEntries;
            result["usesAnyCamera"] = specs.Any(UsesAnyCamera);
            result["blends"] = specs.Take(MaxCustomBlendEntries).Select(DescribeBlendSpec).ToArray();
            result["warnings"] = warnings.ToArray();
            return result;
        }

        private static Dictionary<string, object?> DescribeImpulseComponent(Component impulse)
        {
            var result = DescribeAdvancedComponent(impulse);
            AddMemberIfPresent(result, impulse, "ImpulseDefinition", "impulseDefinition");
            AddMemberIfPresent(result, impulse, "Channel", "channel");
            AddMemberIfPresent(result, impulse, "ImpulseChannel", "impulseChannel");
            AddMemberIfPresent(result, impulse, "Gain", "gain");
            AddMemberIfPresent(result, impulse, "Use2DDistance", "use2DDistance");
            AddMemberIfPresent(result, impulse, "ReactionSettings", "reactionSettings");
            return result;
        }

        private static Dictionary<string, object?>? DescribeNearestAxisOwner(Component controller, Type axisOwnerType)
        {
            for (var current = controller.transform; current != null; current = current.parent)
            {
                foreach (var component in current.GetComponents<Component>())
                {
                    if (component != null && !ReferenceEquals(component, controller) && axisOwnerType.IsInstanceOfType(component))
                    {
                        return DescribeAdvancedComponent(component);
                    }
                }
            }

            return null;
        }

        private static IEnumerable<string> InputAxisControllerWarnings(Component[] controllers, Component[] owners, DependencyStatus status)
        {
            if (controllers.Length > 0 && owners.Length == 0)
            {
                yield return "CinemachineInputAxisController components exist, but no loaded IInputAxisOwner implementers were found in current stage.";
            }

            if (!status.InputSystem.Available)
            {
                yield return "Input System-specific inspection is gated off because com.unity.inputsystem and/or InputActionReference/PlayerInput types are unavailable.";
            }
        }

        private static IEnumerable<string> ImpulseWarnings(Component[] sources, Component[] listeners)
        {
            if (sources.Length > 0 && listeners.Length == 0)
            {
                yield return "Impulse sources exist, but no CinemachineImpulseListener was found; shake will not be received by Cinemachine cameras.";
            }

            if (listeners.Length > 0 && sources.Length == 0)
            {
                yield return "Impulse listeners exist, but no CinemachineImpulseSource was found; provide an explicit game-owned trigger source before QA.";
            }

            var sourceChannels = sources.Select(ImpulseChannelLabel).Where(value => value.Length > 0).ToHashSet(StringComparer.Ordinal);
            var listenerChannels = listeners.Select(ImpulseChannelLabel).Where(value => value.Length > 0).ToHashSet(StringComparer.Ordinal);
            if (sourceChannels.Count > 0 && listenerChannels.Count > 0 && !sourceChannels.Overlaps(listenerChannels))
            {
                yield return "Impulse source and listener channel labels do not visibly overlap; verify channel masks before tuning gain or amplitude.";
            }
        }

        private static string ImpulseChannelLabel(Component impulse)
        {
            var channel = ReadMember(impulse, "Channel") ?? ReadMember(impulse, "ImpulseChannel");
            return channel == null ? string.Empty : Convert.ToString(DescribeMemberValue(channel), CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static Dictionary<string, object?> DescribeConfiner(Component confiner)
        {
            var result = DescribeAdvancedComponent(confiner);
            var boundingShape2D = ReadMember(confiner, "BoundingShape2D");
            var boundingVolume = ReadMember(confiner, "BoundingVolume");
            result["boundingShape2D"] = DescribeMemberValue(boundingShape2D);
            result["boundingVolume"] = DescribeMemberValue(boundingVolume);
            AddMemberIfPresent(result, confiner, "ConfineMode", "confineMode");
            AddMemberIfPresent(result, confiner, "Damping", "damping");
            AddMemberIfPresent(result, confiner, "SlowingDistance", "slowingDistance");
            result["cacheStatus"] = DescribeConfinerCacheStatus(confiner);
            var warnings = new List<string>();
            if (boundingShape2D == null && confiner.GetType().Name.Contains("2D", StringComparison.Ordinal))
            {
                warnings.Add("Confiner2D has no BoundingShape2D assigned.");
            }

            if (boundingVolume == null && confiner.GetType().Name.Contains("3D", StringComparison.Ordinal))
            {
                warnings.Add("Confiner3D has no BoundingVolume assigned.");
            }

            result["warnings"] = warnings.ToArray();
            return result;
        }

        private static Dictionary<string, object?> DescribeConfinerCacheStatus(Component confiner)
        {
            var type = confiner.GetType();
            var cacheMembers = new Dictionary<string, object?>(StringComparer.Ordinal);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var property in type.GetProperties(flags))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0 || !property.Name.Contains("Cache", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    cacheMembers[property.Name] = DescribeMemberValue(property.GetValue(confiner));
                }
                catch
                {
                    cacheMembers[property.Name] = "unreadable";
                }
            }

            foreach (var field in type.GetFields(flags))
            {
                if (!field.Name.Contains("Cache", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    cacheMembers[field.Name] = DescribeMemberValue(field.GetValue(confiner));
                }
                catch
                {
                    cacheMembers[field.Name] = "unreadable";
                }
            }

            return new Dictionary<string, object?>
            {
                ["invalidateBoundingShapeCacheAvailable"] = type.GetMethod("InvalidateBoundingShapeCache", flags, null, Type.EmptyTypes, null) != null,
                ["invalidateBoundingVolumeCacheAvailable"] = type.GetMethod("InvalidateBoundingVolumeCache", flags, null, Type.EmptyTypes, null) != null,
                ["invalidateLensCacheAvailable"] = type.GetMethod("InvalidateLensCache", flags, null, Type.EmptyTypes, null) != null,
                ["cacheMembers"] = cacheMembers,
                ["notes"] = new[]
                {
                    "Cache status depends on members exposed by the loaded Cinemachine version.",
                    "Confiner2D bounding-shape cache invalidation can be expensive; run it only after explicit collider topology changes.",
                    "Lens cache invalidation is separate when exposed; run it explicitly after lens/FOV/orthographic-size changes.",
                },
            };
        }

        private static Dictionary<string, object?> DescribeAdvancedComponent(Component component)
        {
            return new Dictionary<string, object?>
            {
                ["name"] = component.name,
                ["path"] = GetTransformPath(component.transform),
                ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(component),
                ["type"] = component.GetType().FullName,
                ["activeInHierarchy"] = component.gameObject.activeInHierarchy,
                ["enabled"] = component is Behaviour behaviour ? behaviour.enabled : null,
            };
        }

        private static IEnumerable<Dictionary<string, object?>> DescribeCustomBlends(object? blends)
        {
            foreach (var blend in EnumerateObjects(blends))
            {
                yield return new Dictionary<string, object?>
                {
                    ["from"] = Convert.ToString(ReadMember(blend, "From"), CultureInfo.InvariantCulture),
                    ["to"] = Convert.ToString(ReadMember(blend, "To"), CultureInfo.InvariantCulture),
                    ["blend"] = DescribeBlend(ReadMember(blend, "Blend")),
                };
            }
        }

        private static IEnumerable<Dictionary<string, object?>> DescribeInputSystemReferences(Component component, DependencyStatus status)
        {
            foreach (var item in EnumerateMemberValues(component))
            {
                if (status.InputSystem.PrimaryType != null && status.InputSystem.PrimaryType.IsInstanceOfType(item.Value))
                {
                    yield return new Dictionary<string, object?>
                    {
                        ["member"] = item.Name,
                        ["kind"] = "InputActionReference",
                        ["value"] = DescribeMemberValue(item.Value),
                    };
                }
                else if (status.InputSystem.SecondaryType != null && status.InputSystem.SecondaryType.IsInstanceOfType(item.Value))
                {
                    yield return new Dictionary<string, object?>
                    {
                        ["member"] = item.Name,
                        ["kind"] = "PlayerInput",
                        ["value"] = DescribeMemberValue(item.Value),
                    };
                }
            }
        }

        private static Dictionary<string, object?> DescribeSequencer(Component sequencer, bool detail)
        {
            var warnings = new List<string>();
            var instructions = EnumerateInstructions(ReadMember(sequencer, "Instructions"))
                .Take(MaxSequencerInstructionRows + 1)
                .ToArray();
            var result = new Dictionary<string, object?>
            {
                ["name"] = sequencer.name,
                ["path"] = GetTransformPath(sequencer.transform),
                ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(sequencer),
                ["activeInHierarchy"] = sequencer.gameObject.activeInHierarchy,
                ["enabled"] = sequencer is Behaviour behaviour ? behaviour.enabled : null,
                ["detailUri"] = SequencerDetailUri(sequencer),
                ["parentCameraPath"] = GetTransformPath(sequencer.transform),
                ["parentCamera"] = DescribeObjectReference(sequencer),
                ["priority"] = ReadMember(sequencer, "Priority"),
                ["lens"] = DescribeLens(ReadMember(sequencer, "Lens")),
                ["target"] = DescribeObjectReference(ReadMember(sequencer, "Target") ?? ReadMember(sequencer, "TrackingTarget") ?? ReadMember(sequencer, "Follow") ?? ReadMember(sequencer, "LookAt")),
                ["loop"] = ReadMember(sequencer, "Loop"),
                ["instructionCount"] = Math.Min(instructions.Length, MaxSequencerInstructionRows),
                ["instructionCap"] = MaxSequencerInstructionRows,
                ["instructionsCapped"] = instructions.Length > MaxSequencerInstructionRows,
            };

            var describedInstructions = instructions
                .Take(MaxSequencerInstructionRows)
                .Select((instruction, index) => DescribeSequencerInstruction(sequencer, instruction, index, warnings))
                .ToArray();
            result["instructions"] = describedInstructions;
            result["warnings"] = warnings.ToArray();

            if (detail)
            {
                result["unityTransform"] = DescribeTransform(sequencer.transform);
                result["safeAuthoringTool"] = "cinemachine-sequencer-create";
                result["previewQaHint"] = "Use screenshot-camera on a Unity Camera with CinemachineBrain at start, after first hold, and during blend.";
            }

            return result;
        }

        private static IEnumerable<object> EnumerateInstructions(object? instructions)
        {
            if (instructions is not System.Collections.IEnumerable enumerable)
            {
                yield break;
            }

            foreach (var instruction in enumerable)
            {
                if (instruction != null)
                {
                    yield return instruction;
                }
            }
        }

        private static Dictionary<string, object?> DescribeSequencerInstruction(Component sequencer, object instruction, int index, List<string> warnings)
        {
            var camera = ReadMember(instruction, "Camera");
            var holdSeconds = Round(ReadFloatMember(instruction, "Hold") ?? 0f);
            var blend = DescribeBlend(ReadMember(instruction, "Blend"));
            var blendSummary = blend.TryGetValue("summary", out var summary)
                ? Convert.ToString(summary, CultureInfo.InvariantCulture)
                : blend.TryGetValue("style", out var style)
                    ? Convert.ToString(style, CultureInfo.InvariantCulture)
                    : "missing blend";
            if (camera == null)
            {
                warnings.Add($"Instruction {index} has no child camera assigned.");
            }
            else if (camera is Component component && component.transform.parent != sequencer.transform)
            {
                warnings.Add($"Instruction {index} camera '{GetTransformPath(component.transform)}' is not a direct child of sequencer '{GetTransformPath(sequencer.transform)}'.");
            }

            return new Dictionary<string, object?>
            {
                ["index"] = index,
                ["camera"] = DescribeObjectReference(camera),
                ["holdSeconds"] = holdSeconds,
                ["blend"] = blend,
                ["summary"] = $"Hold {holdSeconds}s, blend {blendSummary}",
            };
        }

        private static Dictionary<string, object?> DescribeBlend(object? blend)
        {
            if (blend == null)
            {
                return new Dictionary<string, object?> { ["missing"] = true };
            }

            return new Dictionary<string, object?>
            {
                ["style"] = Convert.ToString(ReadMember(blend, "Style"), CultureInfo.InvariantCulture),
                ["time"] = Round(ReadFloatMember(blend, "Time")),
                ["blendTime"] = Round(ReadFloatMember(blend, "BlendTime")),
                ["summary"] = Convert.ToString(blend, CultureInfo.InvariantCulture),
            };
        }

        private static Dictionary<string, object?> DescribeBrain(Component brain)
        {
            var customBlends = ReadMember(brain, "CustomBlends");
            return new Dictionary<string, object?>
            {
                ["name"] = brain.name,
                ["path"] = GetTransformPath(brain.transform),
                ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(brain),
                ["camera"] = DescribeUnityCamera(brain.GetComponent<Camera>()),
                ["enabled"] = brain is Behaviour behaviour ? behaviour.enabled : null,
                ["detailUri"] = BrainDetailUri(brain),
                ["defaultBlend"] = Convert.ToString(ReadMember(brain, "DefaultBlend"), CultureInfo.InvariantCulture),
                ["customBlendsAsset"] = customBlends is UnityEngine.Object asset ? DescribeAssetObject(asset) : null,
                ["fallbackBehavior"] = "Uses matching CustomBlends entry when available; otherwise falls back to DefaultBlend.",
            };
        }

        private static Dictionary<string, object?> DescribeUnityCamera(Camera? camera)
        {
            if (camera == null)
            {
                return new Dictionary<string, object?> { ["missing"] = true };
            }

            return new Dictionary<string, object?>
            {
                ["name"] = camera.name,
                ["path"] = GetTransformPath(camera.transform),
                ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(camera),
                ["fieldOfView"] = Round(camera.fieldOfView),
                ["orthographic"] = camera.orthographic,
                ["orthographicSize"] = Round(camera.orthographicSize),
                ["nearClipPlane"] = Round(camera.nearClipPlane),
                ["farClipPlane"] = Round(camera.farClipPlane),
            };
        }

        private static Dictionary<string, object?> DescribeDirector(PlayableDirector director, bool detail)
        {
            var asset = director.playableAsset as UnityEngine.Object;
            var result = new Dictionary<string, object?>
            {
                ["name"] = director.name,
                ["path"] = GetTransformPath(director.transform),
                ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(director),
                ["detailUri"] = DirectorDetailUri(director),
                ["time"] = Math.Round(director.time, 4),
                ["duration"] = Math.Round(director.duration, 4),
                ["state"] = director.state.ToString(),
                ["playOnAwake"] = director.playOnAwake,
                ["asset"] = asset != null ? DescribeAssetObject(asset) : null,
            };

            if (detail && asset != null)
            {
                result["clips"] = DescribeTimelineClips(asset).Take(MaxClipRows).ToArray();
                result["maxClipRows"] = MaxClipRows;
                result["tracks"] = DescribeTimelineTracks(asset).ToArray();
                result["signals"] = DescribeTimelineSignals(asset).ToArray();
                result["bindings"] = DescribeTimelineBindings(director, asset).ToArray();
            }

            return result;
        }

        private static Dictionary<string, object?> DescribeTimelineAsset(string path, DependencyStatus status, bool detail)
        {
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            var result = new Dictionary<string, object?>
            {
                ["name"] = asset != null ? asset.name : System.IO.Path.GetFileNameWithoutExtension(path),
                ["path"] = path,
                ["guid"] = AssetDatabase.AssetPathToGUID(path),
                ["detailUri"] = TimelineAssetDetailUri(path),
                ["type"] = asset != null ? asset.GetType().FullName : null,
            };
            if (detail && asset != null)
            {
                result["clips"] = DescribeTimelineClips(asset).Take(MaxClipRows).ToArray();
                result["maxClipRows"] = MaxClipRows;
                result["tracks"] = DescribeTimelineTracks(asset).ToArray();
                result["signals"] = DescribeTimelineSignals(asset).ToArray();
            }

            return result;
        }

        private static IEnumerable<Dictionary<string, object?>> DescribeTimelineTracks(UnityEngine.Object timelineAsset)
        {
            foreach (var track in InvokeEnumerable(timelineAsset, "GetOutputTracks"))
            {
                yield return DescribeTrack(track);
            }
        }

        private static Dictionary<string, object?> DescribeTrack(object track)
        {
            return new Dictionary<string, object?>
            {
                ["name"] = Convert.ToString(ReadMember(track, "name"), CultureInfo.InvariantCulture),
                ["type"] = track.GetType().FullName,
                ["muted"] = ReadMember(track, "muted"),
                ["clipCount"] = InvokeEnumerable(track, "GetClips").Count(),
                ["clips"] = InvokeEnumerable(track, "GetClips").Take(MaxClipRows).Select(DescribeClip).ToArray(),
            };
        }

        private static IEnumerable<Dictionary<string, object?>> DescribeTimelineSignals(UnityEngine.Object timelineAsset)
        {
            foreach (var track in InvokeEnumerable(timelineAsset, "GetOutputTracks"))
            {
                foreach (var marker in InvokeEnumerable(track, "GetMarkers"))
                {
                    yield return DescribeSignal(marker, track);
                }
            }
        }

        private static Dictionary<string, object?> DescribeSignal(object marker, object track)
        {
            return new Dictionary<string, object?>
            {
                ["name"] = Convert.ToString(ReadMember(marker, "name"), CultureInfo.InvariantCulture),
                ["type"] = marker.GetType().FullName,
                ["track"] = Convert.ToString(ReadMember(track, "name"), CultureInfo.InvariantCulture),
                ["time"] = ReadMember(marker, "time"),
                ["asset"] = DescribeMaybeAsset(ReadMember(marker, "asset") ?? ReadMember(marker, "signalAsset")),
            };
        }

        private static IEnumerable<Dictionary<string, object?>> DescribeTimelineBindings(PlayableDirector director, UnityEngine.Object timelineAsset)
        {
            foreach (var track in InvokeEnumerable(timelineAsset, "GetOutputTracks"))
            {
                var trackObject = track as UnityEngine.Object;
                var boundObject = trackObject != null ? director.GetGenericBinding(trackObject) : null;
                yield return new Dictionary<string, object?>
                {
                    ["trackName"] = Convert.ToString(ReadMember(track, "name"), CultureInfo.InvariantCulture),
                    ["trackType"] = track.GetType().FullName,
                    ["boundObject"] = DescribeObjectReference(boundObject),
                };
            }
        }

        private static IEnumerable<Dictionary<string, object?>> DescribeTimelineClips(UnityEngine.Object timelineAsset)
        {
            var tracks = InvokeEnumerable(timelineAsset, "GetOutputTracks");
            foreach (var track in tracks)
            {
                foreach (var clip in InvokeEnumerable(track, "GetClips"))
                {
                    yield return DescribeClip(clip);
                }
            }
        }

        private static Dictionary<string, object?> DescribeClip(object? clip)
        {
            if (clip == null)
            {
                return new Dictionary<string, object?> { ["missing"] = true };
            }

            return new Dictionary<string, object?>
            {
                ["displayName"] = ReadMember(clip, "displayName"),
                ["start"] = ReadMember(clip, "start"),
                ["duration"] = ReadMember(clip, "duration"),
                ["end"] = ReadMember(clip, "end"),
                ["assetType"] = ReadMember(clip, "asset")?.GetType().FullName,
            };
        }
    }
}
