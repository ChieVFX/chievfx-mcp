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
        private static Dictionary<string, object?> EnsureBrain(JToken args, DependencyStatus status)
        {
            var dryRun = OptionalBool(args, "dryRun", false);
            var createCameraIfMissing = OptionalBool(args, "createCameraIfMissing", true);
            var camera = ResolveCamera(args, allowCreate: createCameraIfMissing, dryRun: dryRun);
            if (camera == null)
            {
                if (dryRun && createCameraIfMissing)
                {
                    var preview = CreateCommandEnvelope(CinemachineBrainsUri, status, dryRun);
                    preview["wouldCreateCamera"] = ReadString(args, "cameraName") ?? "Main Camera";
                    preview["wouldCreateBrain"] = true;
                    return preview;
                }

                throw new ArgumentException("Expected targetPath or instanceId for a Camera, or createCameraIfMissing=true with name.");
            }

            var existing = camera.GetComponent(status.Cinemachine.BrainType!);
            var wouldCreate = existing == null;
            if (!dryRun && wouldCreate)
            {
                Undo.RegisterCompleteObjectUndo(camera.gameObject, "Ensure Cinemachine Brain");
                existing = Undo.AddComponent(camera.gameObject, status.Cinemachine.BrainType!);
                MarkChanged(camera);
            }

            var result = CreateCommandEnvelope(BrainDetailUri(existing as Component ?? camera), status, dryRun);
            result["target"] = DescribeUnityCamera(camera);
            result["brain"] = existing != null ? DescribeBrain(existing) : null;
            result["wouldCreate"] = wouldCreate;
            return result;
        }

        private static Dictionary<string, object?> CreateCinemachineCamera(JToken args, DependencyStatus status)
        {
            var dryRun = OptionalBool(args, "dryRun", false);
            var name = ReadString(args, "name") ?? "Cinemachine Camera";
            var warnings = new List<string>();
            var parent = TryString(args, "parentPath", out var parentPath) ? ResolveTransform(parentPath) : null;
            var target = TryString(args, "targetPath", out var targetPath) ? ResolveTransform(targetPath) : null;
            var position = args["position"] != null ? ReadVector3(args["position"]!, "position") : (Vector3?)null;
            var distance = OptionalFloat(args, "distance");
            var priority = OptionalInt(args, "priority");
            var lens = args["lens"] as JObject;

            if (dryRun)
            {
                var preview = CreateCommandEnvelope(CinemachineCamerasUri, status, dryRun);
                preview["wouldCreate"] = name;
                preview["parentPath"] = parent != null ? GetTransformPath(parent) : null;
                preview["targetPath"] = target != null ? GetTransformPath(target) : null;
                preview["warnings"] = warnings.ToArray();
                return preview;
            }

            var gameObject = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(gameObject, "Create Cinemachine Camera");
            if (parent != null)
            {
                Undo.SetTransformParent(gameObject.transform, parent, "Parent Cinemachine Camera");
            }

            if (position.HasValue)
            {
                Undo.RecordObject(gameObject.transform, "Position Cinemachine Camera");
                gameObject.transform.position = position.Value;
            }
            else if (target != null && distance.HasValue)
            {
                Undo.RecordObject(gameObject.transform, "Position Cinemachine Camera");
                gameObject.transform.position = target.position + new Vector3(0f, 0f, -Mathf.Max(0.01f, distance.Value));
                gameObject.transform.LookAt(target);
            }

            var camera = Undo.AddComponent(gameObject, status.Cinemachine.CameraType!);
            if (priority.HasValue)
            {
                TrySetMember(camera, "Priority", priority.Value, warnings);
            }

            if (target != null)
            {
                TrySetCameraTarget(camera, target, warnings);
            }

            if (lens != null)
            {
                ApplyLensPatch(camera, lens, warnings);
            }

            MarkChanged(camera);
            var result = CreateCommandEnvelope(CinemachineDetailUri(camera), status, dryRun);
            result["target"] = DescribeCinemachineCamera(camera, detail: true);
            result["warnings"] = warnings.ToArray();
            return result;
        }

        private static Dictionary<string, object?> SetCinemachineCamera(JToken args, DependencyStatus status)
        {
            var dryRun = OptionalBool(args, "dryRun", false);
            var camera = RequireComponent(args, status.Cinemachine.CameraType!, "CinemachineCamera");
            var warnings = new List<string>();
            var changes = new List<string>();

            if (TryInt(args, "priority", out var priority))
            {
                changes.Add("priority");
                if (!dryRun)
                {
                    Undo.RecordObject(camera, "Set Cinemachine Camera Priority");
                    TrySetMember(camera, "Priority", priority, warnings);
                }
            }

            if (TryString(args, "targetPath", out var targetPath))
            {
                var target = ResolveTransform(targetPath) ?? throw new ArgumentException($"Could not resolve targetPath '{targetPath}'.");
                changes.Add("target");
                if (!dryRun)
                {
                    Undo.RecordObject(camera, "Set Cinemachine Camera Target");
                    TrySetCameraTarget(camera, target, warnings);
                }
            }

            if (args["lens"] is JObject lens)
            {
                changes.Add("lens");
                if (!dryRun)
                {
                    Undo.RecordObject(camera, "Set Cinemachine Camera Lens");
                    ApplyLensPatch(camera, lens, warnings);
                }
            }

            if (TryBool(args, "enabled", out var enabled) && camera is Behaviour behaviour)
            {
                changes.Add("enabled");
                if (!dryRun)
                {
                    Undo.RecordObject(behaviour, "Set Cinemachine Camera Enabled");
                    behaviour.enabled = enabled;
                }
            }

            if (!dryRun)
            {
                MarkChanged(camera);
            }

            var result = CreateCommandEnvelope(CinemachineDetailUri(camera), status, dryRun);
            result["target"] = DescribeCinemachineCamera(camera, detail: true);
            result["changedFields"] = changes.ToArray();
            result["warnings"] = warnings.ToArray();
            result["blendSafeNotes"] = new[]
            {
                "Camera target/lens/priority changes do not edit Timeline clip overlap.",
                "Timeline shot blend feel is controlled by adjacent clip overlap; brain default/custom blends remain separate.",
            };
            return result;
        }

        private static Dictionary<string, object?> SetBlenderSettings(JToken args, DependencyStatus status)
        {
            if (args["dryRun"] == null || args["dryRun"]!.Type == JTokenType.Null)
            {
                throw new ArgumentException("dryRun is required for cinemachine-blender-settings-set.");
            }

            var dryRun = OptionalBool(args, "dryRun", false);
            var assetPath = ReadString(args, "assetPath");
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                throw new ArgumentException("assetPath is required and must be explicit.");
            }

            assetPath = assetPath!.Trim();
            if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal) || !assetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("assetPath must be an explicit Assets/*.asset path.");
            }

            var existingAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (existingAsset != null && !status.BlenderSettings.HelperType!.IsInstanceOfType(existingAsset))
            {
                throw new ArgumentException($"Asset '{assetPath}' exists but is not a CinemachineBlenderSettings asset.");
            }

            var blendSpecs = ReadBlenderBlendSpecs(args["blends"]);
            var warnings = new List<string>();
            AddBlendValidationWarnings(blendSpecs, CurrentCinemachineCameraNames(status), warnings);
            var assignToSelectedBrain = OptionalBool(args, "assignToSelectedBrain", false);
            var selectedBrain = assignToSelectedBrain ? ResolveSelectedBrain(status.Cinemachine.BrainType!) : null;
            if (assignToSelectedBrain && selectedBrain == null)
            {
                warnings.Add("assignToSelectedBrain=true, but Selection does not contain a CinemachineBrain.");
            }

            if (dryRun)
            {
                var preview = CreateCommandEnvelope(CinemachineBlenderSettingsUri, status, dryRun);
                preview["assetPath"] = assetPath;
                preview["wouldCreateAsset"] = existingAsset == null;
                preview["wouldUpdateAsset"] = existingAsset != null;
                preview["wouldAssignSelectedBrain"] = assignToSelectedBrain && selectedBrain != null;
                preview["selectedBrain"] = selectedBrain != null ? DescribeBrain(selectedBrain) : null;
                preview["blendCount"] = blendSpecs.Length;
                preview["maxBlendEntries"] = MaxCustomBlendEntries;
                preview["plannedEntries"] = blendSpecs.Select(DescribeBlendSpec).ToArray();
                preview["warnings"] = warnings.ToArray();
                preview["timelineNote"] = "Timeline Cinemachine shot overlaps ignore Brain default/custom blend settings; adjacent Timeline clip overlap controls Timeline blend feel.";
                return preview;
            }

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Set Cinemachine Blender Settings");

            var asset = existingAsset;
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance(status.BlenderSettings.HelperType!);
                asset.name = System.IO.Path.GetFileNameWithoutExtension(assetPath);
                EnsureAssetFolder(assetPath);
                AssetDatabase.CreateAsset(asset, assetPath);
            }

            Undo.RecordObject(asset, "Set Cinemachine Blender Settings");
            ApplyBlenderSettings(asset, blendSpecs, status, warnings);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssetIfDirty(asset);

            if (assignToSelectedBrain && selectedBrain != null)
            {
                Undo.RecordObject(selectedBrain, "Assign Cinemachine Blender Settings");
                if (!TrySetMember(selectedBrain, "CustomBlends", asset, warnings))
                {
                    warnings.Add("CinemachineBrain.CustomBlends field was not writable.");
                }

                MarkChanged(selectedBrain);
            }

            Undo.CollapseUndoOperations(undoGroup);

            var result = CreateCommandEnvelope(CinemachineBlenderSettingsUri, status, dryRun);
            result["asset"] = DescribeBlenderSettingsAsset(asset, CurrentCinemachineCameraNames(status));
            result["createdAsset"] = existingAsset == null;
            result["assignedBrain"] = selectedBrain != null ? DescribeBrain(selectedBrain) : null;
            result["assetCleanupPath"] = existingAsset == null ? assetPath : null;
            result["undoNotes"] = new[]
            {
                "Brain assignment and asset object edits are registered with Undo.",
                "New asset files are written through AssetDatabase; if Undo does not remove the file in this Unity version, delete assetCleanupPath.",
            };
            result["warnings"] = warnings.ToArray();
            result["timelineNote"] = "Timeline Cinemachine shot overlaps ignore Brain default/custom blend settings; adjacent Timeline clip overlap controls Timeline blend feel.";
            return result;
        }

        private static Dictionary<string, object?> SetSplineDolly(JToken args, DependencyStatus status)
        {
            if (args["dryRun"] == null || args["dryRun"]!.Type == JTokenType.Null)
            {
                throw new ArgumentException("dryRun is required for cinemachine-spline-dolly-set.");
            }

            var dryRun = OptionalBool(args, "dryRun", false);
            var camera = RequireComponent(args, status.Cinemachine.CameraType!, "CinemachineCamera");
            var splineContainer = RequireNamedComponent(args, status.SplinesDolly.OptionalType!, "SplineContainer", "splinePath", "splineInstanceId");
            var position = OptionalFloat(args, "position") ?? OptionalFloat(args, "cameraPosition");
            var positionUnits = ReadString(args, "positionUnits") ?? ReadString(args, "units");
            var autoDollyEnabled = OptionalBoolNullable(args, "autoDollyEnabled");
            var warnings = new List<string>();
            var changes = new List<string> { "splineContainer" };
            if (position.HasValue)
            {
                changes.Add("cameraPosition");
            }

            if (!string.IsNullOrWhiteSpace(positionUnits))
            {
                changes.Add("positionUnits");
            }

            if (autoDollyEnabled.HasValue)
            {
                changes.Add("autoDollyEnabled");
            }

            var existing = camera.gameObject.GetComponent(status.SplinesDolly.HelperType!);
            if (dryRun)
            {
                var preview = CreateCommandEnvelope(CinemachineSplinesDollyUri, status, dryRun);
                preview["camera"] = DescribeCinemachineCamera(camera, detail: false);
                preview["splineContainer"] = DescribeSplineContainer(splineContainer, status);
                preview["wouldAddSplineDolly"] = existing == null;
                preview["wouldAssignExistingSplineContainer"] = true;
                preview["plannedFields"] = changes.ToArray();
                preview["position"] = position.HasValue ? (object)Round(position.Value) : null;
                preview["positionUnits"] = positionUnits;
                preview["autoDollyEnabled"] = autoDollyEnabled;
                preview["geometryMutation"] = false;
                preview["warnings"] = warnings.ToArray();
                return preview;
            }

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Set Cinemachine Spline Dolly");

            var dolly = existing;
            if (dolly == null)
            {
                Undo.RegisterCompleteObjectUndo(camera.gameObject, "Add Cinemachine Spline Dolly");
                dolly = Undo.AddComponent(camera.gameObject, status.SplinesDolly.HelperType!);
            }

            Undo.RecordObject(dolly, "Set Cinemachine Spline Dolly");
            if (!TrySetFirstMember(dolly, new[] { "Spline", "SplineContainer", "Container" }, splineContainer, warnings))
            {
                warnings.Add("SplineContainer field was not writable; assign the spline manually.");
            }

            if (position.HasValue && !TrySetFirstMember(dolly, new[] { "CameraPosition", "Position" }, position.Value, warnings))
            {
                warnings.Add("CameraPosition/Position field was not writable.");
            }

            if (!string.IsNullOrWhiteSpace(positionUnits) && !TrySetFirstMember(dolly, new[] { "PositionUnits", "Units" }, positionUnits!, warnings))
            {
                warnings.Add("PositionUnits field was not writable.");
            }

            if (autoDollyEnabled.HasValue)
            {
                SetAutoDollyEnabled(dolly, autoDollyEnabled.Value, warnings);
            }

            MarkChanged(dolly);
            Undo.CollapseUndoOperations(undoGroup);

            var result = CreateCommandEnvelope(CinemachineSplinesDollyUri, status, dryRun);
            result["camera"] = DescribeSplineDollyCamera(camera, status);
            result["splineDolly"] = DescribeSplinesDolly(dolly, status);
            result["splineContainer"] = DescribeSplineContainer(splineContainer, status);
            result["addedSplineDolly"] = existing == null;
            result["changedFields"] = changes.ToArray();
            result["geometryMutation"] = false;
            result["warnings"] = warnings.ToArray();
            result["visualQaHint"] = "Use screenshot-camera at start/mid/end dolly positions on the Unity Camera with CinemachineBrain; verify target framing changes and no black frames.";
            return result;
        }

        private static Dictionary<string, object?> SetConfiner(JToken args, DependencyStatus status)
        {
            if (args["dryRun"] == null || args["dryRun"]!.Type == JTokenType.Null)
            {
                throw new ArgumentException("dryRun is required for cinemachine-confiner-set.");
            }

            var dimension = (ReadString(args, "dimension") ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.Equals(dimension, "2d", StringComparison.Ordinal) && !string.Equals(dimension, "3d", StringComparison.Ordinal))
            {
                throw new ArgumentException("dimension is required and must be '2d' or '3d'.");
            }

            var helper = dimension == "2d" ? status.Confiner2D : status.Confiner3D;
            if (!ConfinerAvailable(status, helper))
            {
                return CreateConfinerUnavailable(status, helper, "Tool 'cinemachine-confiner-set'");
            }

            var dryRun = OptionalBool(args, "dryRun", false);
            var camera = RequireComponent(args, status.Cinemachine.CameraType!, "CinemachineCamera");
            var collider = RequireCollider(args, helper.SecondaryHelperType!, dimension == "2d" ? "Collider2D" : "Collider");
            var damping = OptionalFloat(args, "damping");
            var slowingDistance = OptionalFloat(args, "slowingDistance");
            var invalidateCache = OptionalBool(args, "invalidateCache", false);
            var invalidateLensCache = OptionalBool(args, "invalidateLensCache", false);
            var warnings = new List<string>();
            var changes = new List<string> { dimension == "2d" ? "boundingShape2D" : "boundingVolume" };

            if (invalidateCache)
            {
                changes.Add("invalidateCache");
                warnings.Add("Explicit cache invalidation requested. Confiner2D cache rebuilds can be expensive for complex collider topology.");
            }

            if (invalidateLensCache)
            {
                changes.Add("invalidateLensCache");
                warnings.Add("Explicit lens cache invalidation requested. Use this after lens/FOV/orthographic-size changes when the confiner exposes lens caching.");
            }

            if (damping.HasValue)
            {
                changes.Add("damping");
            }

            if (slowingDistance.HasValue)
            {
                changes.Add("slowingDistance");
            }

            var existing = camera.gameObject.GetComponent(helper.HelperType!);
            if (dryRun)
            {
                var preview = CreateCommandEnvelope(dimension == "2d" ? CinemachineConfiner2DUri : CinemachineConfiner3DUri, status, dryRun);
                preview["camera"] = DescribeCinemachineCamera(camera, detail: false);
                preview["collider"] = DescribeObjectReference(collider);
                preview["wouldAddConfiner"] = existing == null;
                preview["wouldAssignExistingCollider"] = true;
                preview["wouldInvalidateCache"] = invalidateCache;
                preview["wouldInvalidateLensCache"] = invalidateLensCache;
                preview["plannedFields"] = changes.ToArray();
                preview["warnings"] = warnings.ToArray();
                preview["geometryMutation"] = false;
                return preview;
            }

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Set Cinemachine Confiner");

            var confiner = existing;
            if (confiner == null)
            {
                Undo.RegisterCompleteObjectUndo(camera.gameObject, "Add Cinemachine Confiner");
                confiner = Undo.AddComponent(camera.gameObject, helper.HelperType!);
            }

            Undo.RecordObject(confiner, "Set Cinemachine Confiner");
            var colliderMember = dimension == "2d" ? "BoundingShape2D" : "BoundingVolume";
            if (!TrySetMember(confiner, colliderMember, collider, warnings))
            {
                warnings.Add($"{colliderMember} field was not writable; assign the collider manually.");
            }

            if (damping.HasValue && !TrySetMember(confiner, "Damping", damping.Value, warnings))
            {
                warnings.Add("Damping field was not writable.");
            }

            if (slowingDistance.HasValue && !TrySetMember(confiner, "SlowingDistance", slowingDistance.Value, warnings))
            {
                warnings.Add("SlowingDistance field was not writable.");
            }

            if (invalidateCache)
            {
                InvokeCacheInvalidation(confiner, new[] { "InvalidateBoundingShapeCache", "InvalidateBoundingVolumeCache", "InvalidateCache" }, warnings);
            }

            if (invalidateLensCache)
            {
                InvokeCacheInvalidation(confiner, new[] { "InvalidateLensCache" }, warnings);
            }

            MarkChanged(confiner);
            Undo.CollapseUndoOperations(undoGroup);

            var result = CreateCommandEnvelope(dimension == "2d" ? CinemachineConfiner2DUri : CinemachineConfiner3DUri, status, dryRun);
            result["camera"] = DescribeCinemachineCamera(camera, detail: false);
            result["confiner"] = DescribeConfiner(confiner);
            result["collider"] = DescribeObjectReference(collider);
            result["addedConfiner"] = existing == null;
            result["changedFields"] = changes.ToArray();
            result["geometryMutation"] = false;
            result["warnings"] = warnings.ToArray();
            result["visualQaHint"] = "Use screenshot-camera on the gameplay camera inside bounds and near/over the edge; verify confinement and no black frames.";
            return result;
        }

        private static Dictionary<string, object?> CreateSequencerCamera(JToken args, DependencyStatus status)
        {
            var dryRun = OptionalBool(args, "dryRun", false);
            var name = ReadString(args, "name") ?? "Cinemachine Sequencer Camera";
            var parent = TryString(args, "parentPath", out var parentPath) ? ResolveTransform(parentPath) : null;
            var subject = TryString(args, "targetPath", out var targetPath) ? ResolveTransform(targetPath) : null;
            var position = args["position"] != null ? ReadVector3(args["position"]!, "position") : (Vector3?)null;
            var loop = OptionalBool(args, "loop", true);
            var ensureBrain = OptionalBool(args, "ensureBrain", false);
            var createCameraIfMissing = OptionalBool(args, "createCameraIfMissing", false);
            var gameCamera = ensureBrain ? ResolveCamera(args, allowCreate: createCameraIfMissing, dryRun: dryRun) : null;
            var shotSpecs = ReadSequencerShotSpecs(args);
            var warnings = new List<string>();

            if (shotSpecs.Length == 0)
            {
                shotSpecs = new[]
                {
                    new SequencerShotSpec("Sequencer Shot 1", 1d, 35f, 6f, 10, "Cut", 0f),
                    new SequencerShotSpec("Sequencer Shot 2", 1d, 24f, 3f, 20, "EaseInOut", 0.5f),
                };
            }

            if (dryRun)
            {
                var preview = CreateCommandEnvelope(CinemachineSequencersUri, status, dryRun);
                preview["wouldCreateSequencer"] = name;
                preview["parentPath"] = parent != null ? GetTransformPath(parent) : null;
                preview["targetPath"] = subject != null ? GetTransformPath(subject) : null;
                preview["loop"] = loop;
                preview["shotCount"] = shotSpecs.Length;
                preview["wouldEnsureBrain"] = ensureBrain;
                preview["wouldCreateCamera"] = ensureBrain && gameCamera == null && createCameraIfMissing ? ReadString(args, "cameraName") ?? "Main Camera" : null;
                preview["warnings"] = warnings.ToArray();
                return preview;
            }

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Create Cinemachine Sequencer Camera");

            var gameObject = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(gameObject, "Create Cinemachine Sequencer Camera");
            if (parent != null)
            {
                Undo.SetTransformParent(gameObject.transform, parent, "Parent Cinemachine Sequencer Camera");
            }

            if (position.HasValue)
            {
                Undo.RecordObject(gameObject.transform, "Position Cinemachine Sequencer Camera");
                gameObject.transform.position = position.Value;
            }
            else if (subject != null)
            {
                Undo.RecordObject(gameObject.transform, "Position Cinemachine Sequencer Camera");
                gameObject.transform.position = subject.position;
            }

            var sequencer = Undo.AddComponent(gameObject, status.Sequencer.SequencerCameraType!);
            TrySetMember(sequencer, "Loop", loop, warnings);

            Component? brain = null;
            if (ensureBrain)
            {
                if (gameCamera == null)
                {
                    throw new ArgumentException("ensureBrain requires cameraPath/cameraInstanceId, an existing MainCamera, or createCameraIfMissing=true.");
                }

                brain = gameCamera.GetComponent(status.Cinemachine.BrainType!) ?? Undo.AddComponent(gameCamera.gameObject, status.Cinemachine.BrainType!);
                MarkChanged(brain);
            }

            var childCameras = new List<Component>();
            foreach (var spec in shotSpecs)
            {
                childCameras.Add(CreateSequencerChildCamera(sequencer.transform, spec, subject, status, warnings));
            }

            SetSequencerInstructions(sequencer, childCameras, shotSpecs, status, warnings);
            MarkChanged(sequencer);
            Undo.CollapseUndoOperations(undoGroup);

            var result = CreateCommandEnvelope(SequencerDetailUri(sequencer), status, dryRun);
            result["target"] = DescribeSequencer(sequencer, detail: true);
            result["brain"] = brain != null ? DescribeBrain(brain) : null;
            result["warnings"] = warnings.ToArray();
            result["visualQaHint"] = "Activate or raise priority on the Sequencer Camera, then use screenshot-camera on the Unity Camera with CinemachineBrain at start, after first hold, and during a blend.";
            return result;
        }

        private static Dictionary<string, object?> CreateTimelineDirector(JToken args, DependencyStatus status)
        {
            var dryRun = OptionalBool(args, "dryRun", false);
            var name = ReadString(args, "name") ?? "Timeline Director";
            var assetPath = ReadString(args, "assetPath");
            var createAsset = OptionalBool(args, "createAsset", !string.IsNullOrWhiteSpace(assetPath));

            if (dryRun)
            {
                var preview = CreateCommandEnvelope(TimelineDirectorsUri, status, dryRun);
                preview["wouldCreateDirector"] = name;
                preview["wouldCreateAsset"] = createAsset ? assetPath : null;
                return preview;
            }

            var gameObject = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(gameObject, "Create Timeline Director");
            var director = Undo.AddComponent<PlayableDirector>(gameObject);

            if (createAsset)
            {
                assetPath = EnsureTimelineAssetPath(assetPath ?? $"Assets/{SanitizeAssetName(name)}.playable");
                var asset = CreateTimelineAsset(assetPath, status);
                Undo.RecordObject(director, "Assign Timeline Asset");
                director.playableAsset = asset as PlayableAsset;
            }
            else if (!string.IsNullOrWhiteSpace(assetPath))
            {
                var asset = AssetDatabase.LoadAssetAtPath<PlayableAsset>(assetPath);
                if (asset == null)
                {
                    throw new ArgumentException($"Timeline assetPath '{assetPath}' did not load as PlayableAsset.");
                }

                Undo.RecordObject(director, "Assign Timeline Asset");
                director.playableAsset = asset;
            }

            if (TryDouble(args, "time", out var time))
            {
                director.time = Math.Max(0d, time);
            }

            MarkChanged(director);
            var result = CreateCommandEnvelope(DirectorDetailUri(director), status, dryRun);
            result["target"] = DescribeDirector(director, detail: true);
            return result;
        }

        private static Dictionary<string, object?> CreateShotSequence(JToken args, DependencyStatus status)
        {
            var dryRun = OptionalBool(args, "dryRun", false);
            var warnings = new List<string>();
            var director = ResolveDirectorFromArgs(args);
            var assetPath = ReadString(args, "assetPath") ?? "Assets/Cinematics/EndingSessionSlowMoZoom.playable";
            var gameCamera = ResolveCamera(args, allowCreate: true, dryRun: dryRun);
            if (gameCamera == null && !dryRun)
            {
                throw new ArgumentException("Shot sequence requires or creates a Unity Camera for CinemachineBrain binding.");
            }

            var shotSpecs = ReadShotSpecs(args);
            if (shotSpecs.Length == 0)
            {
                shotSpecs = new[]
                {
                    new ShotSpec("Ending Wide Shot", 0d, 1.5d, 35f, 6f, 10),
                    new ShotSpec("Ending SlowMo Zoom", 1.25d, 2.25d, 22f, 2.5f, 20),
                };
            }

            if (dryRun)
            {
                var preview = CreateCommandEnvelope(TimelineDirectorsUri, status, dryRun);
                preview["wouldCreateOrUseDirector"] = director != null ? GetTransformPath(director.transform) : ReadString(args, "directorName") ?? "Ending Session Director";
                preview["wouldCreateAsset"] = director != null && director.playableAsset != null ? AssetDatabase.GetAssetPath(director.playableAsset) : assetPath;
                preview["wouldCreateOrUseCamera"] = gameCamera != null ? GetTransformPath(gameCamera.transform) : ReadString(args, "cameraName") ?? "Main Camera";
                preview["shotCount"] = shotSpecs.Length;
                preview["runtimeSlowMoNote"] = RuntimeSlowMoNote();
                return preview;
            }

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Create Cinemachine Timeline Shot Sequence");

            var brain = gameCamera!.GetComponent(status.Cinemachine.BrainType!) ?? Undo.AddComponent(gameCamera.gameObject, status.Cinemachine.BrainType!);
            director ??= CreateDirectorObject(ReadString(args, "directorName") ?? "Ending Session Director");
            var timelineAsset = director.playableAsset as UnityEngine.Object;
            if (timelineAsset == null || !status.Timeline.AssetType!.IsInstanceOfType(timelineAsset))
            {
                timelineAsset = (UnityEngine.Object)CreateTimelineAsset(EnsureTimelineAssetPath(assetPath), status);
                Undo.RecordObject(director, "Assign Timeline Asset");
                director.playableAsset = timelineAsset as PlayableAsset;
            }

            var track = CreateCinemachineTrack(timelineAsset, status, ReadString(args, "trackName") ?? "Cinemachine Shots");
            director.SetGenericBinding(track as UnityEngine.Object, brain as UnityEngine.Object);

            var subject = TryString(args, "targetPath", out var targetPath) ? ResolveTransform(targetPath) : null;
            var createdShots = new List<object?>();
            foreach (var spec in shotSpecs)
            {
                var camera = CreateShotCamera(spec, subject, status, warnings);
                var clip = CreateCinemachineShotClip(track, camera, spec, warnings);
                createdShots.Add(new Dictionary<string, object?>
                {
                    ["camera"] = DescribeCinemachineCamera(camera, detail: false),
                    ["clip"] = DescribeClip(clip),
                });
            }

            MarkChanged(director);
            if (timelineAsset != null)
            {
                EditorUtility.SetDirty(timelineAsset);
                AssetDatabase.SaveAssetIfDirty(timelineAsset);
            }

            Undo.CollapseUndoOperations(undoGroup);

            var result = CreateCommandEnvelope(DirectorDetailUri(director), status, dryRun);
            result["target"] = DescribeDirector(director, detail: true);
            result["brain"] = DescribeBrain(brain);
            result["shots"] = createdShots.ToArray();
            result["warnings"] = warnings.ToArray();
            result["runtimeSlowMoNote"] = RuntimeSlowMoNote();
            result["visualQaHint"] = "Use screenshot-camera on the bound Unity Camera after scrubbing the director.";
            return result;
        }

        private static Dictionary<string, object?> PreviewDirector(JToken args, DependencyStatus status)
        {
            var dryRun = OptionalBool(args, "dryRun", false);
            var director = RequireDirector(args);
            var action = (ReadString(args, "action") ?? "evaluate").ToLowerInvariant();
            var time = OptionalDouble(args, "time");
            var previousTime = director.time;
            var previousState = director.state.ToString();
            var restoredTime = false;

            if (!dryRun)
            {
                Undo.RecordObject(director, "Preview Timeline Director");
                if (time.HasValue)
                {
                    director.time = Math.Max(0d, time.Value);
                }

                switch (action)
                {
                    case "evaluate":
                    case "scrub":
                        director.Evaluate();
                        break;
                    case "play":
                        director.Play();
                        break;
                    case "stop":
                        director.Stop();
                        break;
                    default:
                        throw new ArgumentException("action must be evaluate, scrub, play, or stop.");
                }

                if ((string.Equals(action, "evaluate", StringComparison.Ordinal) || string.Equals(action, "scrub", StringComparison.Ordinal))
                    && Math.Abs(director.time - previousTime) > 0.0001d)
                {
                    director.time = previousTime;
                    director.Evaluate();
                    restoredTime = Math.Abs(director.time - previousTime) <= 0.0001d;
                }
            }

            var result = CreateCommandEnvelope(DirectorDetailUri(director), status, dryRun);
            result["action"] = action;
            result["previousTime"] = Math.Round(previousTime, 4);
            result["previousState"] = previousState;
            result["restoredTime"] = restoredTime;
            result["stateAfterPreview"] = director.state.ToString();
            result["target"] = DescribeDirector(director, detail: true);
            result["visualQaHint"] = "For camera-composed validation, use screenshot-camera on the bound Unity Camera rather than Game View screenshots.";
            return result;
        }
    }
}
