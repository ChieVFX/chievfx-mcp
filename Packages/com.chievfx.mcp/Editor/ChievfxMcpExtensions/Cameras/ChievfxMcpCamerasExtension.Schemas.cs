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
        private static JObject BrainEnsureSchema()
        {
            var schema = BaseSchema("Ensure CinemachineBrain on a Unity Camera.");
            var properties = (JObject)schema["properties"]!;
            properties["cameraPath"] = StringSchema("Transform path of Unity Camera.");
            properties["cameraInstanceId"] = IntSchema("Instance id of Camera or owning GameObject.");
            properties["cameraName"] = StringSchema("Name to use when creating a missing Camera.");
            properties["createCameraIfMissing"] = BoolSchema("Create a Camera when no existing Camera resolves. Default true.");
            properties["dryRun"] = BoolSchema("Validate target/create plan without mutating scene state.");
            return schema;
        }

        private static JObject CinemachineCreateSchema()
        {
            var schema = BaseSchema("Create a Cinemachine 3 camera.");
            var properties = (JObject)schema["properties"]!;
            properties["name"] = StringSchema("New Cinemachine camera GameObject name.");
            properties["parentPath"] = StringSchema("Optional parent transform path.");
            properties["targetPath"] = StringSchema("Optional subject transform path for target/follow/look-at and distance placement.");
            properties["position"] = Vector3Schema("World position for the camera.");
            properties["distance"] = NumberSchema("Distance behind target on negative Z when position is omitted.");
            properties["priority"] = IntSchema("Cinemachine priority.");
            properties["lens"] = LensSchema();
            properties["dryRun"] = BoolSchema("Validate create plan without mutating scene state.");
            return schema;
        }

        private static JObject CinemachineSetSchema()
        {
            var schema = TargetSchema("Patch an existing CinemachineCamera.");
            var properties = (JObject)schema["properties"]!;
            properties["targetPath"] = StringSchema("Transform path for target/follow/look-at.");
            properties["priority"] = IntSchema("Cinemachine priority.");
            properties["lens"] = LensSchema();
            properties["enabled"] = BoolSchema("Enable/disable camera component.");
            properties["dryRun"] = BoolSchema("Validate patch without mutating scene state.");
            return schema;
        }

        private static JObject SequencerCreateSchema()
        {
            var schema = BaseSchema("Create a CM3 CinemachineSequencerCamera plus child CinemachineCameras.");
            var properties = (JObject)schema["properties"]!;
            properties["name"] = StringSchema("New Sequencer Camera GameObject name.");
            properties["parentPath"] = StringSchema("Optional parent transform path.");
            properties["targetPath"] = StringSchema("Optional subject transform path for child camera targets and distance placement.");
            properties["position"] = Vector3Schema("World position for the Sequencer Camera parent.");
            properties["loop"] = BoolSchema("Set Sequencer Camera Loop. Default true.");
            properties["ensureBrain"] = BoolSchema("Ensure CinemachineBrain on a Unity Camera. Default false.");
            properties["cameraPath"] = StringSchema("Unity Camera transform path for optional Brain.");
            properties["cameraInstanceId"] = IntSchema("Camera or owning GameObject instance id for optional Brain.");
            properties["cameraName"] = StringSchema("Unity Camera name when createCameraIfMissing=true.");
            properties["createCameraIfMissing"] = BoolSchema("Create a Unity Camera only when ensureBrain=true and no camera resolves. Default false.");
            properties["shots"] = new JObject
            {
                ["type"] = "array",
                ["description"] = "Shot objects: name, hold/holdSeconds, fieldOfView/fov, distance, priority, blendStyle, blendTime. Creates child CinemachineCameras and Sequencer Instructions only; never creates Timeline assets.",
            };
            properties["dryRun"] = BoolSchema("Validate Sequencer authoring plan without mutating scene/assets.");
            return schema;
        }

        private static JObject SplineDollySetSchema()
        {
            var schema = TargetSchema("Add/update a CM3 CinemachineSplineDolly on an existing CinemachineCamera using an existing SplineContainer.");
            schema["required"] = new JArray("dryRun");
            var properties = (JObject)schema["properties"]!;
            properties["splinePath"] = StringSchema("Transform path containing an existing UnityEngine.Splines.SplineContainer. Spline knots/shape are never created or edited.");
            properties["splineInstanceId"] = IntSchema("Instance id of an existing SplineContainer or owning GameObject.");
            properties["position"] = NumberSchema("Optional dolly camera position value.");
            properties["cameraPosition"] = NumberSchema("Alias for position.");
            properties["positionUnits"] = StringSchema("Optional Cinemachine PositionUnits value such as Normalized, Distance, or Knot.");
            properties["units"] = StringSchema("Alias for positionUnits.");
            properties["autoDollyEnabled"] = BoolSchema("Optional AutoDolly.Enabled toggle when the CM3 type exposes it.");
            properties["dryRun"] = BoolSchema("Required. Validate existing camera/spline plan without mutating scene state.");
            return schema;
        }

        private static JObject BlenderSettingsSetSchema()
        {
            var schema = BaseSchema("Create or update a CM3 CinemachineBlenderSettings asset.");
            schema["required"] = new JArray("assetPath", "dryRun");
            var properties = (JObject)schema["properties"]!;
            properties["assetPath"] = StringSchema("Explicit Assets/*.asset path for the Blender Settings asset.");
            properties["assignToSelectedBrain"] = BoolSchema("Assign asset to the selected CinemachineBrain with Undo. Default false.");
            properties["blends"] = new JObject
            {
                ["type"] = "array",
                ["description"] = $"Custom blend entries capped at {MaxCustomBlendEntries}: from, to, style/blendStyle, time/blendTime. Use ANY CAMERA for wildcard endpoints.",
                ["maxItems"] = MaxCustomBlendEntries,
            };
            properties["dryRun"] = BoolSchema("Required. Validate asset/update/assignment plan without mutating assets or scene state.");
            return schema;
        }

        private static JObject ConfinerSetSchema()
        {
            var schema = TargetSchema("Add/update a CM3 Confiner2D or Confiner3D on an existing CinemachineCamera using an existing collider.");
            schema["required"] = new JArray("dimension", "dryRun");
            var properties = (JObject)schema["properties"]!;
            properties["dimension"] = StringSchema("'2d' for CinemachineConfiner2D with Collider2D, or '3d' for CinemachineConfiner3D with Collider.");
            properties["colliderPath"] = StringSchema("Transform path containing an existing Collider2D/Collider. Collider geometry is never created or edited.");
            properties["colliderInstanceId"] = IntSchema("Instance id of an existing Collider2D/Collider or owning GameObject.");
            properties["damping"] = NumberSchema("Optional Damping field when exposed by the confiner.");
            properties["slowingDistance"] = NumberSchema("Optional SlowingDistance field when exposed by the confiner.");
            properties["invalidateCache"] = BoolSchema("Explicitly invalidate bounding-shape/volume cache when the confiner exposes such a method. Default false.");
            properties["invalidateLensCache"] = BoolSchema("Explicitly invalidate lens cache when exposed after lens/FOV/orthographic-size changes. Default false.");
            properties["dryRun"] = BoolSchema("Required. Validate existing camera/collider plan without mutating scene state.");
            return schema;
        }

        private static JObject TimelineDirectorCreateSchema()
        {
            var schema = BaseSchema("Create a PlayableDirector and optional TimelineAsset.");
            var properties = (JObject)schema["properties"]!;
            properties["name"] = StringSchema("Director GameObject name.");
            properties["assetPath"] = StringSchema("Timeline asset path. Use .playable.");
            properties["createAsset"] = BoolSchema("Create missing TimelineAsset and assign it.");
            properties["time"] = NumberSchema("Initial director time.");
            properties["dryRun"] = BoolSchema("Validate create plan without mutating scene/assets.");
            return schema;
        }

        private static JObject ShotSequenceCreateSchema()
        {
            var schema = BaseSchema("Create a CinemachineTrack/CinemachineShot sequence.");
            var properties = (JObject)schema["properties"]!;
            properties["directorPath"] = StringSchema("Existing PlayableDirector path.");
            properties["directorInstanceId"] = IntSchema("Existing PlayableDirector instance id.");
            properties["directorName"] = StringSchema("New director name when directorPath is omitted.");
            properties["cameraPath"] = StringSchema("Unity Camera path for CinemachineBrain binding.");
            properties["cameraName"] = StringSchema("Unity Camera name when creating missing camera.");
            properties["targetPath"] = StringSchema("Subject transform path for shot camera target and distance placement.");
            properties["assetPath"] = StringSchema("Timeline asset path to create/assign.");
            properties["trackName"] = StringSchema("Cinemachine track name.");
            properties["shots"] = new JObject
            {
                ["type"] = "array",
                ["description"] = "Optional shot objects: name, start, duration, fieldOfView/fov, distance, priority. Overlap adjacent clip times for blends.",
            };
            properties["dryRun"] = BoolSchema("Validate sequence plan without mutating scene/assets.");
            return schema;
        }

        private static JObject TimelinePreviewSchema()
        {
            var schema = BaseSchema("Scrub/evaluate/play/stop a PlayableDirector.");
            var properties = (JObject)schema["properties"]!;
            properties["directorPath"] = StringSchema("PlayableDirector transform path.");
            properties["directorInstanceId"] = IntSchema("PlayableDirector instance id.");
            properties["time"] = NumberSchema("Director time before action.");
            properties["action"] = StringSchema("evaluate, scrub, play, or stop.");
            properties["dryRun"] = BoolSchema("Validate target/action without mutating preview state.");
            return schema;
        }

        private static JObject BaseSchema(string description)
        {
            return new JObject
            {
                ["type"] = "object",
                ["description"] = description,
                ["properties"] = new JObject(),
            };
        }

        private static JObject TargetSchema(string description)
        {
            var schema = BaseSchema(description);
            var properties = (JObject)schema["properties"]!;
            properties["targetPath"] = StringSchema("Component transform path.");
            properties["instanceId"] = IntSchema("Component instance id.");
            return schema;
        }

        private static JObject LensSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["description"] = "Safe lens patch fields.",
                ["properties"] = new JObject
                {
                    ["fieldOfView"] = NumberSchema("Perspective FOV."),
                    ["orthographicSize"] = NumberSchema("Orthographic size."),
                    ["nearClipPlane"] = NumberSchema("Near clip plane."),
                    ["farClipPlane"] = NumberSchema("Far clip plane."),
                    ["modeOverride"] = StringSchema("Cinemachine lens mode override when supported."),
                },
            };
        }

        private static JObject Vector3Schema(string description)
        {
            return new JObject
            {
                ["type"] = "object",
                ["description"] = description,
                ["properties"] = new JObject
                {
                    ["x"] = NumberSchema("X"),
                    ["y"] = NumberSchema("Y"),
                    ["z"] = NumberSchema("Z"),
                },
            };
        }

        private static JObject StringSchema(string description)
        {
            return new JObject { ["type"] = "string", ["description"] = description };
        }

        private static JObject BoolSchema(string description)
        {
            return new JObject { ["type"] = "boolean", ["description"] = description };
        }

        private static JObject NumberSchema(string description)
        {
            return new JObject { ["type"] = "number", ["description"] = description };
        }

        private static JObject IntSchema(string description)
        {
            return new JObject { ["type"] = "integer", ["description"] = description };
        }

        private static string? ReadString(JToken args, string name)
        {
            var token = args[name];
            return token == null || token.Type == JTokenType.Null ? null : token.Value<string>();
        }

        private static bool TryString(JToken args, string name, out string value)
        {
            value = string.Empty;
            var token = args[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }

            value = token.Value<string>() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        private static bool TryInt(JToken args, string name, out int value)
        {
            value = 0;
            var token = args[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }

            value = token.Value<int>();
            return true;
        }

        private static bool TryBool(JToken args, string name, out bool value)
        {
            value = false;
            var token = args[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }

            value = token.Value<bool>();
            return true;
        }

        private static bool TryDouble(JToken args, string name, out double value)
        {
            value = 0d;
            var token = args[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }

            value = token.Value<double>();
            return true;
        }

        private static bool OptionalBool(JToken args, string name, bool fallback)
        {
            var token = args[name];
            return token == null || token.Type == JTokenType.Null ? fallback : token.Value<bool>();
        }

        private static bool? OptionalBoolNullable(JToken args, string name)
        {
            var token = args[name];
            return token == null || token.Type == JTokenType.Null ? null : token.Value<bool>();
        }

        private static int? OptionalInt(JToken args, string name)
        {
            var token = args[name];
            return token == null || token.Type == JTokenType.Null ? null : token.Value<int>();
        }

        private static float? OptionalFloat(JToken args, string name)
        {
            var token = args[name];
            return token == null || token.Type == JTokenType.Null ? null : token.Value<float>();
        }

        private static double? OptionalDouble(JToken args, string name)
        {
            var token = args[name];
            return token == null || token.Type == JTokenType.Null ? null : token.Value<double>();
        }

        private static Vector3 ReadVector3(JToken token, string fieldName)
        {
            if (token is not JObject obj)
            {
                throw new ArgumentException($"{fieldName} must be an object with x/y/z numbers.");
            }

            return new Vector3(obj["x"]?.Value<float>() ?? 0f, obj["y"]?.Value<float>() ?? 0f, obj["z"]?.Value<float>() ?? 0f);
        }

        private static Dictionary<string, object?> Vector3Row(Vector3 value)
        {
            return new Dictionary<string, object?>
            {
                ["x"] = Round(value.x),
                ["y"] = Round(value.y),
                ["z"] = Round(value.z),
            };
        }

        private static double? Round(float? value)
        {
            return value.HasValue ? Math.Round(value.Value, 4) : null;
        }

        private static double Round(float value)
        {
            return Math.Round(value, 4);
        }
    }
}
