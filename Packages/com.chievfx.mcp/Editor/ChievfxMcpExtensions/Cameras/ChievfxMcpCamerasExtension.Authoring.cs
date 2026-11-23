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
        private static object CreateTimelineAsset(string assetPath, DependencyStatus status)
        {
            var existing = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (existing != null)
            {
                if (!status.Timeline.AssetType!.IsInstanceOfType(existing))
                {
                    throw new ArgumentException($"Asset '{assetPath}' exists but is not a TimelineAsset.");
                }

                return existing;
            }

            var asset = ScriptableObject.CreateInstance(status.Timeline.AssetType!);
            asset.name = System.IO.Path.GetFileNameWithoutExtension(assetPath);
            EnsureAssetFolder(assetPath);
            AssetDatabase.CreateAsset(asset, assetPath);
            AssetDatabase.SaveAssetIfDirty(asset);
            return asset;
        }

        private static object CreateCinemachineTrack(UnityEngine.Object timelineAsset, DependencyStatus status, string trackName)
        {
            if (status.Cinemachine.TrackType == null)
            {
                throw new InvalidOperationException("CinemachineTrack type is not loaded.");
            }

            var method = timelineAsset.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(item => item.Name == "CreateTrack" && item.GetParameters().Length >= 1 && item.GetParameters()[0].ParameterType == typeof(Type));
            if (method == null)
            {
                throw new InvalidOperationException("TimelineAsset.CreateTrack(Type, ...) method is unavailable.");
            }

            var parameters = method.GetParameters();
            object? track = parameters.Length switch
            {
                1 => method.Invoke(timelineAsset, new object?[] { status.Cinemachine.TrackType }),
                2 => method.Invoke(timelineAsset, new object?[] { status.Cinemachine.TrackType, null }),
                _ => method.Invoke(timelineAsset, new object?[] { status.Cinemachine.TrackType, null, trackName }),
            };
            if (track == null)
            {
                throw new InvalidOperationException("TimelineAsset.CreateTrack returned null.");
            }

            TrySetMember(track, "name", trackName, new List<string>());
            return track;
        }

        private static Component CreateShotCamera(ShotSpec spec, Transform? subject, DependencyStatus status, List<string> warnings)
        {
            var gameObject = new GameObject(spec.Name);
            Undo.RegisterCreatedObjectUndo(gameObject, "Create Cinemachine Shot Camera");
            if (subject != null)
            {
                gameObject.transform.position = subject.position + new Vector3(0f, 0f, -Mathf.Max(0.01f, spec.Distance));
                gameObject.transform.LookAt(subject);
            }

            var camera = Undo.AddComponent(gameObject, status.Cinemachine.CameraType!);
            TrySetMember(camera, "Priority", spec.Priority, warnings);
            if (subject != null)
            {
                TrySetCameraTarget(camera, subject, warnings);
            }

            ApplyLensPatch(camera, new JObject { ["fieldOfView"] = spec.FieldOfView }, warnings);
            MarkChanged(camera);
            return camera;
        }

        private static Component CreateSequencerChildCamera(Transform parent, SequencerShotSpec spec, Transform? subject, DependencyStatus status, List<string> warnings)
        {
            var gameObject = new GameObject(spec.Name);
            Undo.RegisterCreatedObjectUndo(gameObject, "Create Sequencer Child Camera");
            Undo.SetTransformParent(gameObject.transform, parent, "Parent Sequencer Child Camera");
            if (subject != null)
            {
                gameObject.transform.position = subject.position + new Vector3(0f, 0f, -Mathf.Max(0.01f, spec.Distance));
                gameObject.transform.LookAt(subject);
            }

            var camera = Undo.AddComponent(gameObject, status.Cinemachine.CameraType!);
            TrySetMember(camera, "Priority", spec.Priority, warnings);
            if (subject != null)
            {
                TrySetCameraTarget(camera, subject, warnings);
            }

            ApplyLensPatch(camera, new JObject { ["fieldOfView"] = spec.FieldOfView }, warnings);
            MarkChanged(camera);
            return camera;
        }

        private static void SetSequencerInstructions(Component sequencer, IReadOnlyList<Component> cameras, IReadOnlyList<SequencerShotSpec> specs, DependencyStatus status, List<string> warnings)
        {
            var instructionType = status.Sequencer.InstructionType!;
            var listType = typeof(List<>).MakeGenericType(instructionType);
            var instructions = (System.Collections.IList)Activator.CreateInstance(listType)!;
            for (var i = 0; i < cameras.Count; i++)
            {
                var instruction = Activator.CreateInstance(instructionType);
                if (instruction == null)
                {
                    warnings.Add("Could not create CinemachineSequencerCamera.Instruction.");
                    continue;
                }

                TrySetMember(instruction, "Camera", cameras[i], warnings);
                TrySetMember(instruction, "Hold", Math.Max(0f, (float)specs[i].Hold), warnings);
                TrySetMember(instruction, "Blend", CreateBlendDefinition(status, specs[i].BlendStyle, specs[i].BlendTime, warnings), warnings);
                instructions.Add(instruction);
            }

            Undo.RecordObject(sequencer, "Set Sequencer Instructions");
            if (!TrySetMember(sequencer, "Instructions", instructions, warnings))
            {
                warnings.Add("CinemachineSequencerCamera.Instructions field was not writable.");
            }
        }

        private static object? CreateBlendDefinition(DependencyStatus status, string style, float time, List<string> warnings)
        {
            var blend = Activator.CreateInstance(status.Sequencer.BlendDefinitionType!);
            if (blend == null)
            {
                warnings.Add("Could not create CinemachineBlendDefinition.");
                return null;
            }

            TrySetMember(blend, "Style", style, warnings);
            TrySetMember(blend, "Time", Math.Max(0f, time), warnings);
            return blend;
        }

        private static void ApplyBlenderSettings(UnityEngine.Object asset, BlenderBlendSpec[] specs, DependencyStatus status, List<string> warnings)
        {
            var customBlendType = BlenderCustomBlendType(status);
            if (customBlendType == null)
            {
                throw new InvalidOperationException("CinemachineBlenderSettings.CustomBlend type is not loaded.");
            }

            var array = Array.CreateInstance(customBlendType, specs.Length);
            for (var i = 0; i < specs.Length; i++)
            {
                var customBlend = Activator.CreateInstance(customBlendType);
                if (customBlend == null)
                {
                    warnings.Add("Could not create CinemachineBlenderSettings.CustomBlend.");
                    continue;
                }

                TrySetMember(customBlend, "From", specs[i].From, warnings);
                TrySetMember(customBlend, "To", specs[i].To, warnings);
                TrySetMember(customBlend, "Blend", CreateBlendDefinition(status, specs[i].Style, specs[i].Time, warnings), warnings);
                array.SetValue(customBlend, i);
            }

            if (!TrySetMember(asset, "CustomBlends", array, warnings)
                && !TrySetMember(asset, "m_CustomBlends", array, warnings))
            {
                warnings.Add("CinemachineBlenderSettings.CustomBlends field was not writable.");
            }
        }

        private static BlenderBlendSpec[] ReadBlenderBlendSpecs(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return Array.Empty<BlenderBlendSpec>();
            }

            if (token is not JArray array)
            {
                throw new ArgumentException("blends must be an array.");
            }

            if (array.Count > MaxCustomBlendEntries)
            {
                throw new ArgumentException($"blends is capped at {MaxCustomBlendEntries} entries.");
            }

            return array
                .OfType<JObject>()
                .Select(item =>
                {
                    var from = NormalizeBlendCameraName(ReadString(item, "from") ?? "ANY CAMERA");
                    var to = NormalizeBlendCameraName(ReadString(item, "to") ?? "ANY CAMERA");
                    var style = ReadString(item, "style") ?? ReadString(item, "blendStyle") ?? "EaseInOut";
                    var time = OptionalFloat(item, "time") ?? OptionalFloat(item, "blendTime") ?? 0.5f;
                    return new BlenderBlendSpec(from, to, style, Math.Max(0f, time));
                })
                .ToArray();
        }

        private static BlenderBlendSpec[] ReadCustomBlendSpecs(object? blends)
        {
            return EnumerateObjects(blends)
                .Select(blend =>
                {
                    var from = NormalizeBlendCameraName(Convert.ToString(ReadMember(blend, "From"), CultureInfo.InvariantCulture) ?? "ANY CAMERA");
                    var to = NormalizeBlendCameraName(Convert.ToString(ReadMember(blend, "To"), CultureInfo.InvariantCulture) ?? "ANY CAMERA");
                    var blendDefinition = ReadMember(blend, "Blend");
                    var style = blendDefinition == null ? "missing" : Convert.ToString(ReadMember(blendDefinition, "Style"), CultureInfo.InvariantCulture) ?? "missing";
                    var time = blendDefinition == null ? 0f : ReadFloatMember(blendDefinition, "Time") ?? ReadFloatMember(blendDefinition, "BlendTime") ?? 0f;
                    return new BlenderBlendSpec(from, to, style, Math.Max(0f, time));
                })
                .ToArray();
        }

        private static string NormalizeBlendCameraName(string value)
        {
            return string.Equals(value.Trim(), "any camera", StringComparison.OrdinalIgnoreCase)
                ? "ANY CAMERA"
                : value.Trim();
        }

        private static void AddBlendValidationWarnings(BlenderBlendSpec[] specs, HashSet<string> cameraNames, List<string> warnings)
        {
            for (var i = 0; i < specs.Length; i++)
            {
                AddCameraNameWarning(specs[i].From, i, "from", cameraNames, warnings);
                AddCameraNameWarning(specs[i].To, i, "to", cameraNames, warnings);
            }

            for (var i = 0; i < specs.Length; i++)
            {
                for (var j = i + 1; j < specs.Length; j++)
                {
                    if (!BlendsOverlap(specs[i], specs[j]) || BlendSpecificity(specs[i]) != BlendSpecificity(specs[j]))
                    {
                        continue;
                    }

                    if (string.Equals(specs[i].From, specs[j].From, StringComparison.Ordinal)
                        && string.Equals(specs[i].To, specs[j].To, StringComparison.Ordinal))
                    {
                        warnings.Add($"Custom blend entries {i} and {j} duplicate '{specs[i].From}' -> '{specs[i].To}'; earlier entry wins.");
                    }
                    else
                    {
                        warnings.Add($"Custom blend entries {i} and {j} can match the same camera switch with equal specificity; ordering decides which blend wins.");
                    }
                }
            }
        }

        private static void AddCameraNameWarning(string cameraName, int index, string fieldName, HashSet<string> cameraNames, List<string> warnings)
        {
            if (string.Equals(cameraName, "ANY CAMERA", StringComparison.Ordinal) || cameraNames.Contains(cameraName))
            {
                return;
            }

            warnings.Add($"Custom blend entry {index} {fieldName} camera '{cameraName}' does not match any loaded CinemachineCamera name; this is allowed but may never match.");
        }

        private static bool BlendsOverlap(BlenderBlendSpec left, BlenderBlendSpec right)
        {
            return BlendEndpointOverlaps(left.From, right.From) && BlendEndpointOverlaps(left.To, right.To);
        }

        private static bool BlendEndpointOverlaps(string left, string right)
        {
            return string.Equals(left, "ANY CAMERA", StringComparison.Ordinal)
                || string.Equals(right, "ANY CAMERA", StringComparison.Ordinal)
                || string.Equals(left, right, StringComparison.Ordinal);
        }

        private static int BlendSpecificity(BlenderBlendSpec spec)
        {
            var specificity = 0;
            if (!string.Equals(spec.From, "ANY CAMERA", StringComparison.Ordinal))
            {
                specificity++;
            }

            if (!string.Equals(spec.To, "ANY CAMERA", StringComparison.Ordinal))
            {
                specificity++;
            }

            return specificity;
        }

        private static bool UsesAnyCamera(BlenderBlendSpec spec)
        {
            return string.Equals(spec.From, "ANY CAMERA", StringComparison.Ordinal)
                || string.Equals(spec.To, "ANY CAMERA", StringComparison.Ordinal);
        }

        private static Dictionary<string, object?> DescribeBlendSpec(BlenderBlendSpec spec)
        {
            return new Dictionary<string, object?>
            {
                ["from"] = spec.From,
                ["to"] = spec.To,
                ["blend"] = new Dictionary<string, object?>
                {
                    ["style"] = spec.Style,
                    ["time"] = Round(spec.Time),
                    ["blendTime"] = Round(spec.Time),
                    ["summary"] = $"{spec.Style} {Round(spec.Time)}s",
                },
            };
        }

        private static object? CreateCinemachineShotClip(object track, Component camera, ShotSpec spec, List<string> warnings)
        {
            var createClip = track.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(method => method.Name == "CreateClip" && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(Type));
            if (createClip == null)
            {
                warnings.Add("CinemachineTrack.CreateClip(Type) unavailable; shot camera was created without Timeline clip.");
                return null;
            }

            var shotType = FindLoadedType("Unity.Cinemachine.CinemachineShot");
            if (shotType == null)
            {
                warnings.Add("CinemachineShot type unavailable; shot camera was created without Timeline clip.");
                return null;
            }

            var clip = createClip.Invoke(track, new object?[] { shotType });
            if (clip == null)
            {
                warnings.Add("CinemachineTrack.CreateClip returned null.");
                return null;
            }

            TrySetMember(clip, "displayName", spec.Name, warnings);
            TrySetMember(clip, "start", spec.Start, warnings);
            TrySetMember(clip, "duration", spec.Duration, warnings);
            var clipAsset = ReadMember(clip, "asset");
            if (clipAsset != null)
            {
                SetCinemachineShotCamera(clipAsset, camera, warnings);
            }

            return clip;
        }

        private static void SetCinemachineShotCamera(object shotAsset, Component camera, List<string> warnings)
        {
            var property = shotAsset.GetType().GetProperty("VirtualCamera", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null || !property.CanWrite)
            {
                warnings.Add("CinemachineShot.VirtualCamera property unavailable.");
                return;
            }

            var value = property.GetValue(shotAsset);
            if (value != null && value.GetType().IsValueType)
            {
                var field = value.GetType().GetField("defaultValue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                {
                    field.SetValue(value, camera);
                    property.SetValue(shotAsset, value);
                    return;
                }
            }

            if (property.PropertyType.IsInstanceOfType(camera))
            {
                property.SetValue(shotAsset, camera);
                return;
            }

            warnings.Add("Could not assign CinemachineShot.VirtualCamera; assign camera manually if clip is empty.");
        }

        private static void ApplyLensPatch(Component camera, JObject lensPatch, List<string> warnings)
        {
            var lens = ReadMember(camera, "Lens");
            if (lens == null)
            {
                warnings.Add("CinemachineCamera Lens property unavailable.");
                return;
            }

            var lensType = lens.GetType();
            var mutableLens = lens;
            foreach (var property in lensPatch.Properties())
            {
                var memberName = property.Name switch
                {
                    "fieldOfView" => "FieldOfView",
                    "orthographicSize" => "OrthographicSize",
                    "nearClipPlane" => "NearClipPlane",
                    "farClipPlane" => "FarClipPlane",
                    "modeOverride" => "ModeOverride",
                    _ => property.Name,
                };
                if (!TrySetMember(mutableLens, memberName, ConvertJToken(property.Value), warnings))
                {
                    warnings.Add($"Lens field '{property.Name}' was not applied.");
                }
            }

            // The lens follows the Unity Camera it drives: under an orthographic camera fieldOfView is stored and does nothing.
            var isOrthographic = ReadMember(mutableLens, "Orthographic") as bool?;
            if (isOrthographic == true && lensPatch.ContainsKey("fieldOfView"))
            {
                warnings.Add("The lens is orthographic (it follows its Unity Camera): fieldOfView changes nothing on screen; set orthographicSize.");
            }
            else if (isOrthographic == false && lensPatch.ContainsKey("orthographicSize"))
            {
                warnings.Add("The lens is perspective (it follows its Unity Camera): orthographicSize changes nothing on screen; set fieldOfView.");
            }

            TrySetMember(camera, "Lens", mutableLens, warnings);
        }

        // A failed attempt is reported only when no way worked: "Could not set Target" next to a target set through Follow read
        // as a camera without a target.
        private static void TrySetCameraTarget(Component camera, Transform target, List<string> warnings)
        {
            var attempts = new List<string>();
            if (TrySetStructMemberField(camera, "Target", "TrackingTarget", target, attempts)
                || TrySetMember(camera, "Target", target, attempts)
                || TrySetMember(camera, "TrackingTarget", target, attempts)
                || TrySetMember(camera, "Follow", target, attempts)
                || TrySetMember(camera, "LookAt", target, attempts))
            {
                return;
            }

            warnings.AddRange(attempts);
        }
    }
}
