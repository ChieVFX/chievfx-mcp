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
using static Chievfx.Mcp.Extensions.Particles.ParticlesSchemas;
using static Chievfx.Mcp.Extensions.Particles.ParticlesRows;
using static Chievfx.Mcp.Extensions.Particles.ParticlesShared;

namespace Chievfx.Mcp.Extensions.Particles
{
    internal static class ParticlesModules
    {
#if CHIEVFX_MCP_HAS_PARTICLESYSTEM
        internal static void ApplyBaseline(ParticleSystem system)
        {
            var main = system.main;
            main.duration = 1.5f;
            main.loop = false;
            main.startLifetime = 0.8f;
            main.startSpeed = 2.5f;
            main.startSize = 0.12f;
            main.maxParticles = 256;

            var emission = system.emission;
            emission.rateOverTime = 20f;

            var shape = system.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 18f;
            shape.radius = 0.08f;
        }

        internal static void ApplyNamedPreset(ParticleSystem system, string preset, List<string> warnings)
        {
            ApplyBaseline(system);
            var main = system.main;
            var emission = system.emission;
            var shape = system.shape;
            var renderer = system.GetComponent<ParticleSystemRenderer>();

            switch (preset.Trim().ToLowerInvariant())
            {
                case "spark-burst":
                case "sparks":
                    main.duration = 0.55f;
                    main.loop = false;
                    main.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 0.75f);
                    main.startSpeed = new ParticleSystem.MinMaxCurve(3.5f, 7.5f);
                    main.startSize = new ParticleSystem.MinMaxCurve(0.04f, 0.12f);
                    main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.75f, 0.25f), new Color(1f, 0.2f, 0.05f));
                    main.gravityModifier = 0.8f;
                    main.maxParticles = 128;
                    emission.rateOverTime = 0f;
                    emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 32) });
                    shape.shapeType = ParticleSystemShapeType.Cone;
                    shape.angle = 28f;
                    if (renderer != null)
                    {
                        renderer.renderMode = ParticleSystemRenderMode.Stretch;
                        renderer.lengthScale = 1.8f;
                    }

                    break;
                case "smoke-puff":
                case "smoke":
                    main.duration = 1.5f;
                    main.loop = false;
                    main.startLifetime = new ParticleSystem.MinMaxCurve(1.2f, 2.4f);
                    main.startSpeed = new ParticleSystem.MinMaxCurve(0.2f, 0.8f);
                    main.startSize = new ParticleSystem.MinMaxCurve(0.4f, 1.2f);
                    main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.45f, 0.45f, 0.45f, 0.45f), new Color(0.9f, 0.9f, 0.9f, 0.2f));
                    main.gravityModifier = -0.05f;
                    main.maxParticles = 96;
                    emission.rateOverTime = 0f;
                    emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 18) });
                    shape.shapeType = ParticleSystemShapeType.Sphere;
                    shape.radius = 0.25f;
                    break;
                case "magic-glow":
                case "magic":
                    main.duration = 2f;
                    main.loop = true;
                    main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.4f);
                    main.startSpeed = new ParticleSystem.MinMaxCurve(0.2f, 1.0f);
                    main.startSize = new ParticleSystem.MinMaxCurve(0.08f, 0.28f);
                    main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.4f, 0.7f, 1f, 0.85f), new Color(0.9f, 0.35f, 1f, 0.7f));
                    main.gravityModifier = 0f;
                    main.maxParticles = 192;
                    emission.rateOverTime = 36f;
                    emission.SetBursts(Array.Empty<ParticleSystem.Burst>());
                    shape.shapeType = ParticleSystemShapeType.Circle;
                    shape.radius = 0.35f;
                    break;
                default:
                    throw new ArgumentException($"Unknown preset '{preset}'. Allowed presets: spark-burst, smoke-puff, magic-glow.");
            }

            warnings.Add("Named preset changed common ParticleSystem modules; inspect detail resource before further patches.");
        }

        internal static void ApplyModulePatch(ParticleSystem system, string moduleName, JObject fields, List<string> warnings)
        {
            switch (moduleName.Trim().ToLowerInvariant())
            {
                case "main":
                    PatchMain(system.main, fields);
                    break;
                case "emission":
                    PatchEmission(system.emission, fields);
                    break;
                case "shape":
                    PatchShape(system.shape, fields);
                    break;
                case "coloroverlifetime":
                case "color-over-lifetime":
                    PatchColorOverLifetime(system.colorOverLifetime, fields);
                    break;
                case "sizeoverlifetime":
                case "size-over-lifetime":
                    PatchSizeOverLifetime(system.sizeOverLifetime, fields);
                    break;
                case "velocityoverlifetime":
                case "velocity-over-lifetime":
                    PatchVelocityOverLifetime(system.velocityOverLifetime, fields);
                    break;
                case "noise":
                    PatchNoise(system.noise, fields);
                    break;
                case "trails":
                    PatchTrails(system.trails, fields);
                    break;
                default:
                    throw new ArgumentException($"Unknown or unsupported module '{moduleName}'. Allowed modules: main, emission, shape, colorOverLifetime, sizeOverLifetime, velocityOverLifetime, noise, trails.");
            }

            if (fields.Properties().Count() > 8)
            {
                warnings.Add("Large module patch applied; prefer smaller patches for reviewability.");
            }
        }

        internal static void ValidateModulePatch(string moduleName, JObject fields, List<string> warnings)
        {
            switch (moduleName.Trim().ToLowerInvariant())
            {
                case "main":
                    ValidateMain(fields);
                    break;
                case "emission":
                    ValidateEmission(fields);
                    break;
                case "shape":
                    ValidateShape(fields);
                    break;
                case "coloroverlifetime":
                case "color-over-lifetime":
                    ValidateColorOverLifetime(fields);
                    break;
                case "sizeoverlifetime":
                case "size-over-lifetime":
                    ValidateSizeOverLifetime(fields);
                    break;
                case "velocityoverlifetime":
                case "velocity-over-lifetime":
                    ValidateVelocityOverLifetime(fields);
                    break;
                case "noise":
                    ValidateNoise(fields);
                    break;
                case "trails":
                    ValidateTrails(fields);
                    break;
                default:
                    throw new ArgumentException($"Unknown or unsupported module '{moduleName}'. Allowed modules: main, emission, shape, colorOverLifetime, sizeOverLifetime, velocityOverLifetime, noise, trails.");
            }

            if (fields.Properties().Count() > 8)
            {
                warnings.Add("Large module patch validated; prefer smaller patches for reviewability.");
            }
        }

        internal static void PatchMain(ParticleSystem.MainModule main, JObject fields)
        {
            foreach (var property in fields.Properties())
            {
                switch (property.Name)
                {
                    case "duration":
                        main.duration = PositiveFloat(property.Value, property.Name, 0.01f, 120f);
                        break;
                    case "loop":
                        main.loop = property.Value.Value<bool>();
                        break;
                    case "prewarm":
                        main.prewarm = property.Value.Value<bool>();
                        break;
                    case "startDelay":
                        main.startDelay = NonNegativeCurve(property.Value, property.Name, 60f);
                        break;
                    case "startLifetime":
                        main.startLifetime = NonNegativeCurve(property.Value, property.Name, 120f);
                        break;
                    case "startSpeed":
                        main.startSpeed = Curve(property.Value, property.Name, -100f, 100f);
                        break;
                    case "startSize":
                        main.startSize = NonNegativeCurve(property.Value, property.Name, 100f);
                        break;
                    case "startRotation":
                        main.startRotation = Curve(property.Value, property.Name, -360f, 360f);
                        break;
                    case "startColor":
                        main.startColor = Gradient(property.Value, property.Name);
                        break;
                    case "gravityModifier":
                        main.gravityModifier = Curve(property.Value, property.Name, -10f, 10f);
                        break;
                    case "simulationSpace":
                        main.simulationSpace = ParseEnum<ParticleSystemSimulationSpace>(property.Value.Value<string>() ?? string.Empty, property.Name);
                        break;
                    case "maxParticles":
                        main.maxParticles = IntRange(property.Value, property.Name, 1, 100000);
                        break;
                    default:
                        throw UnknownField("main", property.Name);
                }
            }
        }

        internal static void ValidateMain(JObject fields)
        {
            foreach (var property in fields.Properties())
            {
                switch (property.Name)
                {
                    case "duration":
                        _ = PositiveFloat(property.Value, property.Name, 0.01f, 120f);
                        break;
                    case "loop":
                    case "prewarm":
                        _ = property.Value.Value<bool>();
                        break;
                    case "startDelay":
                        _ = NonNegativeCurve(property.Value, property.Name, 60f);
                        break;
                    case "startLifetime":
                        _ = NonNegativeCurve(property.Value, property.Name, 120f);
                        break;
                    case "startSpeed":
                        _ = Curve(property.Value, property.Name, -100f, 100f);
                        break;
                    case "startSize":
                        _ = NonNegativeCurve(property.Value, property.Name, 100f);
                        break;
                    case "startRotation":
                        _ = Curve(property.Value, property.Name, -360f, 360f);
                        break;
                    case "startColor":
                        _ = Gradient(property.Value, property.Name);
                        break;
                    case "gravityModifier":
                        _ = Curve(property.Value, property.Name, -10f, 10f);
                        break;
                    case "simulationSpace":
                        _ = ParseEnum<ParticleSystemSimulationSpace>(property.Value.Value<string>() ?? string.Empty, property.Name);
                        break;
                    case "maxParticles":
                        _ = IntRange(property.Value, property.Name, 1, 100000);
                        break;
                    default:
                        throw UnknownField("main", property.Name);
                }
            }
        }

        internal static void PatchEmission(ParticleSystem.EmissionModule emission, JObject fields)
        {
            foreach (var property in fields.Properties())
            {
                switch (property.Name)
                {
                    case "enabled":
                        emission.enabled = property.Value.Value<bool>();
                        break;
                    case "rateOverTime":
                        emission.rateOverTime = NonNegativeCurve(property.Value, property.Name, 100000f);
                        break;
                    case "rateOverDistance":
                        emission.rateOverDistance = NonNegativeCurve(property.Value, property.Name, 100000f);
                        break;
                    case "bursts":
                        emission.SetBursts(ReadBursts(property.Value));
                        break;
                    default:
                        throw UnknownField("emission", property.Name);
                }
            }
        }

        internal static void ValidateEmission(JObject fields)
        {
            foreach (var property in fields.Properties())
            {
                switch (property.Name)
                {
                    case "enabled":
                        _ = property.Value.Value<bool>();
                        break;
                    case "rateOverTime":
                    case "rateOverDistance":
                        _ = NonNegativeCurve(property.Value, property.Name, 100000f);
                        break;
                    case "bursts":
                        _ = ReadBursts(property.Value);
                        break;
                    default:
                        throw UnknownField("emission", property.Name);
                }
            }
        }

        internal static void PatchShape(ParticleSystem.ShapeModule shape, JObject fields)
        {
            foreach (var property in fields.Properties())
            {
                switch (property.Name)
                {
                    case "enabled":
                        shape.enabled = property.Value.Value<bool>();
                        break;
                    case "shapeType":
                        shape.shapeType = ParseEnum<ParticleSystemShapeType>(property.Value.Value<string>() ?? string.Empty, property.Name);
                        break;
                    case "angle":
                        shape.angle = FloatRange(property.Value, property.Name, 0f, 90f);
                        break;
                    case "radius":
                        shape.radius = FloatRange(property.Value, property.Name, 0f, 1000f);
                        break;
                    case "radiusThickness":
                        shape.radiusThickness = FloatRange(property.Value, property.Name, 0f, 1f);
                        break;
                    case "arc":
                        shape.arc = FloatRange(property.Value, property.Name, 0f, 360f);
                        break;
                    case "scale":
                        shape.scale = ReadVector3(property.Value, Vector3.one);
                        break;
                    default:
                        throw UnknownField("shape", property.Name);
                }
            }
        }

        internal static void ValidateShape(JObject fields)
        {
            foreach (var property in fields.Properties())
            {
                switch (property.Name)
                {
                    case "enabled":
                        _ = property.Value.Value<bool>();
                        break;
                    case "shapeType":
                        _ = ParseEnum<ParticleSystemShapeType>(property.Value.Value<string>() ?? string.Empty, property.Name);
                        break;
                    case "angle":
                        _ = FloatRange(property.Value, property.Name, 0f, 90f);
                        break;
                    case "radius":
                        _ = FloatRange(property.Value, property.Name, 0f, 1000f);
                        break;
                    case "radiusThickness":
                        _ = FloatRange(property.Value, property.Name, 0f, 1f);
                        break;
                    case "arc":
                        _ = FloatRange(property.Value, property.Name, 0f, 360f);
                        break;
                    case "scale":
                        _ = ReadVector3(property.Value, Vector3.one);
                        break;
                    default:
                        throw UnknownField("shape", property.Name);
                }
            }
        }

        internal static void PatchColorOverLifetime(ParticleSystem.ColorOverLifetimeModule module, JObject fields)
        {
            foreach (var property in fields.Properties())
            {
                switch (property.Name)
                {
                    case "enabled":
                        module.enabled = property.Value.Value<bool>();
                        break;
                    case "color":
                        module.color = Gradient(property.Value, property.Name);
                        break;
                    default:
                        throw UnknownField("colorOverLifetime", property.Name);
                }
            }
        }

        internal static void ValidateColorOverLifetime(JObject fields)
        {
            foreach (var property in fields.Properties())
            {
                switch (property.Name)
                {
                    case "enabled":
                        _ = property.Value.Value<bool>();
                        break;
                    case "color":
                        _ = Gradient(property.Value, property.Name);
                        break;
                    default:
                        throw UnknownField("colorOverLifetime", property.Name);
                }
            }
        }

        internal static void PatchSizeOverLifetime(ParticleSystem.SizeOverLifetimeModule module, JObject fields)
        {
            foreach (var property in fields.Properties())
            {
                switch (property.Name)
                {
                    case "enabled":
                        module.enabled = property.Value.Value<bool>();
                        break;
                    case "size":
                        module.size = NonNegativeCurve(property.Value, property.Name, 100f);
                        break;
                    default:
                        throw UnknownField("sizeOverLifetime", property.Name);
                }
            }
        }

        internal static void ValidateSizeOverLifetime(JObject fields)
        {
            foreach (var property in fields.Properties())
            {
                switch (property.Name)
                {
                    case "enabled":
                        _ = property.Value.Value<bool>();
                        break;
                    case "size":
                        _ = NonNegativeCurve(property.Value, property.Name, 100f);
                        break;
                    default:
                        throw UnknownField("sizeOverLifetime", property.Name);
                }
            }
        }

        internal static void PatchVelocityOverLifetime(ParticleSystem.VelocityOverLifetimeModule module, JObject fields)
        {
            foreach (var property in fields.Properties())
            {
                switch (property.Name)
                {
                    case "enabled":
                        module.enabled = property.Value.Value<bool>();
                        break;
                    case "space":
                        module.space = ParseEnum<ParticleSystemSimulationSpace>(property.Value.Value<string>() ?? string.Empty, property.Name);
                        break;
                    case "x":
                        module.x = Curve(property.Value, property.Name, -100f, 100f);
                        break;
                    case "y":
                        module.y = Curve(property.Value, property.Name, -100f, 100f);
                        break;
                    case "z":
                        module.z = Curve(property.Value, property.Name, -100f, 100f);
                        break;
                    default:
                        throw UnknownField("velocityOverLifetime", property.Name);
                }
            }
        }

        internal static void ValidateVelocityOverLifetime(JObject fields)
        {
            foreach (var property in fields.Properties())
            {
                switch (property.Name)
                {
                    case "enabled":
                        _ = property.Value.Value<bool>();
                        break;
                    case "space":
                        _ = ParseEnum<ParticleSystemSimulationSpace>(property.Value.Value<string>() ?? string.Empty, property.Name);
                        break;
                    case "x":
                    case "y":
                    case "z":
                        _ = Curve(property.Value, property.Name, -100f, 100f);
                        break;
                    default:
                        throw UnknownField("velocityOverLifetime", property.Name);
                }
            }
        }

        internal static void PatchNoise(ParticleSystem.NoiseModule module, JObject fields)
        {
            foreach (var property in fields.Properties())
            {
                switch (property.Name)
                {
                    case "enabled":
                        module.enabled = property.Value.Value<bool>();
                        break;
                    case "strength":
                        module.strength = NonNegativeCurve(property.Value, property.Name, 100f);
                        break;
                    case "frequency":
                        module.frequency = FloatRange(property.Value, property.Name, 0.0001f, 100f);
                        break;
                    case "scrollSpeed":
                        module.scrollSpeed = Curve(property.Value, property.Name, -100f, 100f);
                        break;
                    case "damping":
                        module.damping = property.Value.Value<bool>();
                        break;
                    default:
                        throw UnknownField("noise", property.Name);
                }
            }
        }

        internal static void ValidateNoise(JObject fields)
        {
            foreach (var property in fields.Properties())
            {
                switch (property.Name)
                {
                    case "enabled":
                    case "damping":
                        _ = property.Value.Value<bool>();
                        break;
                    case "strength":
                        _ = NonNegativeCurve(property.Value, property.Name, 100f);
                        break;
                    case "frequency":
                        _ = FloatRange(property.Value, property.Name, 0.0001f, 100f);
                        break;
                    case "scrollSpeed":
                        _ = Curve(property.Value, property.Name, -100f, 100f);
                        break;
                    default:
                        throw UnknownField("noise", property.Name);
                }
            }
        }

        internal static void PatchTrails(ParticleSystem.TrailModule module, JObject fields)
        {
            foreach (var property in fields.Properties())
            {
                switch (property.Name)
                {
                    case "enabled":
                        module.enabled = property.Value.Value<bool>();
                        break;
                    case "ratio":
                        module.ratio = FloatRange(property.Value, property.Name, 0f, 1f);
                        break;
                    case "lifetime":
                        module.lifetime = NonNegativeCurve(property.Value, property.Name, 120f);
                        break;
                    case "minimumVertexDistance":
                        module.minVertexDistance = FloatRange(property.Value, property.Name, 0.001f, 100f);
                        break;
                    case "worldSpace":
                        module.worldSpace = property.Value.Value<bool>();
                        break;
                    case "dieWithParticles":
                        module.dieWithParticles = property.Value.Value<bool>();
                        break;
                    default:
                        throw UnknownField("trails", property.Name);
                }
            }
        }

        internal static void ValidateTrails(JObject fields)
        {
            foreach (var property in fields.Properties())
            {
                switch (property.Name)
                {
                    case "enabled":
                    case "worldSpace":
                    case "dieWithParticles":
                        _ = property.Value.Value<bool>();
                        break;
                    case "ratio":
                        _ = FloatRange(property.Value, property.Name, 0f, 1f);
                        break;
                    case "lifetime":
                        _ = NonNegativeCurve(property.Value, property.Name, 120f);
                        break;
                    case "minimumVertexDistance":
                        _ = FloatRange(property.Value, property.Name, 0.001f, 100f);
                        break;
                    default:
                        throw UnknownField("trails", property.Name);
                }
            }
        }

        internal static ParticleSystem.Burst[] ReadBursts(JToken token)
        {
            if (token.Type != JTokenType.Array)
            {
                throw new ArgumentException("emission.bursts must be an array.");
            }

            return token.Children<JObject>().Take(16).Select(item =>
            {
                var time = OptionalFloat(item, "time", 0f);
                var count = (short)IntRange(item["count"] ?? item["minCount"] ?? new JValue(1), "count", 0, short.MaxValue);
                if (item["maxCount"] != null)
                {
                    var maxCount = (short)IntRange(item["maxCount"]!, "maxCount", count, short.MaxValue);
                    return new ParticleSystem.Burst(time, count, maxCount);
                }

                return new ParticleSystem.Burst(time, count);
            }).ToArray();
        }

        internal static ParticleSystem.MinMaxCurve NonNegativeCurve(JToken token, string fieldName, float max)
        {
            return Curve(token, fieldName, 0f, max);
        }

        internal static ParticleSystem.MinMaxCurve Curve(JToken token, string fieldName, float min, float max)
        {
            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
            {
                return FloatRange(token, fieldName, min, max);
            }

            if (token is JObject obj)
            {
                if (obj["constant"] != null)
                {
                    return FloatRange(obj["constant"]!, fieldName + ".constant", min, max);
                }

                if (obj["min"] != null && obj["max"] != null)
                {
                    return new ParticleSystem.MinMaxCurve(
                        FloatRange(obj["min"]!, fieldName + ".min", min, max),
                        FloatRange(obj["max"]!, fieldName + ".max", min, max));
                }
            }

            throw new ArgumentException($"{fieldName} must be a number or {{constant}}/{{min,max}} object.");
        }

        internal static ParticleSystem.MinMaxGradient Gradient(JToken token, string fieldName)
        {
            if (token.Type == JTokenType.String)
            {
                return ParseColor(token.Value<string>()!, fieldName);
            }

            if (token is JObject obj)
            {
                if (obj["color"] != null)
                {
                    return ParseColor(obj["color"]!, fieldName + ".color");
                }

                if (obj["min"] != null && obj["max"] != null)
                {
                    return new ParticleSystem.MinMaxGradient(
                        ParseColor(obj["min"]!, fieldName + ".min"),
                        ParseColor(obj["max"]!, fieldName + ".max"));
                }
            }

            return ParseColor(token, fieldName);
        }

        internal static Color ParseColor(JToken token, string fieldName)
        {
            if (token.Type == JTokenType.String)
            {
                var value = token.Value<string>()!;
                if (ColorUtility.TryParseHtmlString(value, out var color))
                {
                    return color;
                }
            }

            if (token is JObject obj)
            {
                return new Color(
                    FloatRange(obj["r"] ?? new JValue(1f), fieldName + ".r", 0f, 1f),
                    FloatRange(obj["g"] ?? new JValue(1f), fieldName + ".g", 0f, 1f),
                    FloatRange(obj["b"] ?? new JValue(1f), fieldName + ".b", 0f, 1f),
                    FloatRange(obj["a"] ?? new JValue(1f), fieldName + ".a", 0f, 1f));
            }

            throw new ArgumentException($"{fieldName} must be a #RRGGBB/#RRGGBBAA string or r/g/b/a object.");
        }

        internal static ArgumentException UnknownField(string moduleName, string fieldName)
        {
            return new ArgumentException($"Unknown or unsupported {moduleName} field '{fieldName}'.");
        }
#endif
    }
}
