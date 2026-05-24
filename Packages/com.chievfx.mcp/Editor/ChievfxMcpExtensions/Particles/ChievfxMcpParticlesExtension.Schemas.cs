#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Chievfx.Mcp.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using PackageManagerPackageInfo = UnityEditor.PackageManager.PackageInfo;
using static Chievfx.Mcp.Extensions.Particles.ChievfxMcpParticlesExtension;
using static Chievfx.Mcp.Extensions.Particles.ParticlesResources;
using static Chievfx.Mcp.Extensions.Particles.ParticlesTools;
using static Chievfx.Mcp.Extensions.Particles.ParticlesModules;
using static Chievfx.Mcp.Extensions.Particles.ParticlesRows;
using static Chievfx.Mcp.Extensions.Particles.ParticlesShared;

namespace Chievfx.Mcp.Extensions.Particles
{
    internal static class ParticlesSchemas
    {
#if CHIEVFX_MCP_HAS_PARTICLESYSTEM
        internal static JObject CreateSystemSchema()
        {
            return ObjectSchema(new Dictionary<string, object?>
            {
                ["name"] = StringSchema("GameObject name for the new ParticleSystem."),
                ["parentPath"] = StringSchema("Optional parent transform path in the current scene or prefab stage."),
                ["preset"] = EnumSchema("Optional preset to apply.", "spark-burst", "smoke-puff", "magic-glow"),
                ["position"] = VectorSchema("Optional local position."),
                ["rotationEuler"] = VectorSchema("Optional local Euler rotation."),
            });
        }

        internal static JObject ApplyPresetSchema()
        {
            var schema = TargetSchema();
            schema["properties"]!["preset"] = EnumSchema("Named preset.", "spark-burst", "smoke-puff", "magic-glow");
            schema["required"] = new JArray("preset");
            return schema;
        }

        internal static JObject ModulePatchSchema()
        {
            var schema = TargetSchema();
            schema["properties"]!["module"] = EnumSchema("Allowlisted module.", "main", "emission", "shape", "colorOverLifetime", "sizeOverLifetime", "velocityOverLifetime", "noise", "trails");
            schema["properties"]!["fields"] = new JObject
            {
                ["type"] = "object",
                ["description"] = "Allowlisted fields for the selected module. Unknown fields are rejected by the tool.",
            };
            schema["properties"]!["dryRun"] = new JObject
            {
                ["type"] = "boolean",
                ["description"] = "Validate target/module/fields and return intended changes without mutating scene, dirty state, or Undo stack.",
            };
            schema["required"] = new JArray("module", "fields");
            return schema;
        }

        internal static JObject RendererSetSchema()
        {
            var schema = TargetSchema();
            var properties = (JObject)schema["properties"]!;
            properties["materialPath"] = StringSchema("Material asset path for ParticleSystemRenderer.sharedMaterial.");
            properties["meshPath"] = StringSchema("Mesh asset path; switches renderer to Mesh mode.");
            properties["trailMaterialPath"] = StringSchema("Optional trail material asset path when supported by Unity version.");
            properties["renderMode"] = StringSchema("ParticleSystemRenderMode enum name.");
            properties["sortMode"] = StringSchema("ParticleSystemSortMode enum name.");
            properties["sortingOrder"] = new JObject { ["type"] = "integer" };
            properties["sortingLayerName"] = StringSchema("Renderer sorting layer name.");
            properties["enableGPUInstancing"] = new JObject { ["type"] = "boolean" };
            return schema;
        }

        internal static JObject PreviewControlSchema()
        {
            var schema = TargetSchema();
            var properties = (JObject)schema["properties"]!;
            properties["action"] = EnumSchema("Preview action.", "simulate", "play", "stop", "clear");
            properties["seconds"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 30 };
            properties["restart"] = new JObject { ["type"] = "boolean" };
            properties["fixedTimeStep"] = new JObject { ["type"] = "boolean" };
            properties["withChildren"] = new JObject { ["type"] = "boolean" };
            properties["clear"] = new JObject { ["type"] = "boolean" };
            schema["required"] = new JArray("action");
            return schema;
        }

        internal static JObject TargetSchema()
        {
            return ObjectSchema(new Dictionary<string, object?>
            {
                ["targetPath"] = StringSchema("Transform path from current scene/prefab-stage root."),
                ["instanceId"] = StringSchema("ParticleSystem legacy instance identifier."),
            });
        }

        internal static JObject ObjectSchema(Dictionary<string, object?> properties)
        {
            var obj = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
            };
            var props = new JObject();
            foreach (var pair in properties)
            {
                props[pair.Key] = pair.Value is JToken token ? token : JToken.FromObject(pair.Value!);
            }

            obj["properties"] = props;
            return obj;
        }

        internal static JObject StringSchema(string description)
        {
            return new JObject
            {
                ["type"] = "string",
                ["description"] = description,
            };
        }

        internal static JObject VectorSchema(string description)
        {
            return new JObject
            {
                ["type"] = "object",
                ["description"] = description,
                ["properties"] = new JObject
                {
                    ["x"] = new JObject { ["type"] = "number" },
                    ["y"] = new JObject { ["type"] = "number" },
                    ["z"] = new JObject { ["type"] = "number" },
                },
                ["additionalProperties"] = false,
            };
        }

        internal static JObject EnumSchema(string description, params string[] values)
        {
            return new JObject
            {
                ["type"] = "string",
                ["description"] = description,
                ["enum"] = new JArray(values),
            };
        }
#endif
    }
}
