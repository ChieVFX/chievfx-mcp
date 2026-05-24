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
using static Chievfx.Mcp.Extensions.Particles.ParticlesSchemas;
using static Chievfx.Mcp.Extensions.Particles.ParticlesShared;

namespace Chievfx.Mcp.Extensions.Particles
{
    internal static class ParticlesRows
    {
#if CHIEVFX_MCP_HAS_PARTICLESYSTEM
        internal static IEnumerable<ParticleSystem> EnumerateParticleSystems()
        {
            var scene = GetCurrentScene();
            if (!scene.IsValid())
            {
                return Array.Empty<ParticleSystem>();
            }

            return scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<ParticleSystem>(includeInactive: true))
                .OrderBy(system => GetTransformPath(system.transform), StringComparer.Ordinal);
        }

        internal static Scene GetCurrentScene()
        {
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            return prefabStage != null ? prefabStage.scene : SceneManager.GetActiveScene();
        }

        internal static Dictionary<string, object?> DescribeCurrentStage()
        {
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            var scene = GetCurrentScene();
            return new Dictionary<string, object?>
            {
                ["kind"] = prefabStage != null ? "prefab-stage" : "scene",
                ["scenePath"] = scene.path,
                ["sceneName"] = scene.name,
                ["prefabAssetPath"] = prefabStage?.assetPath,
            };
        }

        internal static Dictionary<string, object?> SummarizeSystem(ParticleSystem system)
        {
            return DescribeSystem(system, detail: false);
        }

        internal static Dictionary<string, object?> DescribeSystem(ParticleSystem system, bool detail)
        {
            var main = system.main;
            var emission = system.emission;
            var shape = system.shape;
            var renderer = system.GetComponent<ParticleSystemRenderer>();
            var result = new Dictionary<string, object?>
            {
                ["name"] = system.name,
                ["path"] = GetTransformPath(system.transform),
                ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(system),
                ["activeInHierarchy"] = system.gameObject.activeInHierarchy,
                ["detailUri"] = DetailUri(system),
                ["main"] = new Dictionary<string, object?>
                {
                    ["duration"] = Round(main.duration),
                    ["loop"] = main.loop,
                    ["prewarm"] = main.prewarm,
                    ["startLifetime"] = DescribeCurve(main.startLifetime),
                    ["startSpeed"] = DescribeCurve(main.startSpeed),
                    ["startSize"] = DescribeCurve(main.startSize),
                    ["startColor"] = DescribeGradient(main.startColor),
                    ["gravityModifier"] = DescribeCurve(main.gravityModifier),
                    ["simulationSpace"] = main.simulationSpace.ToString(),
                    ["maxParticles"] = main.maxParticles,
                },
                ["emission"] = new Dictionary<string, object?>
                {
                    ["enabled"] = emission.enabled,
                    ["rateOverTime"] = DescribeCurve(emission.rateOverTime),
                    ["rateOverDistance"] = DescribeCurve(emission.rateOverDistance),
                    ["burstCount"] = emission.burstCount,
                },
                ["shape"] = new Dictionary<string, object?>
                {
                    ["enabled"] = shape.enabled,
                    ["shapeType"] = shape.shapeType.ToString(),
                    ["angle"] = Round(shape.angle),
                    ["radius"] = Round(shape.radius),
                    ["arc"] = Round(shape.arc),
                },
                ["renderer"] = renderer != null ? DescribeRenderer(renderer) : null,
            };

            if (detail)
            {
                result["modules"] = new Dictionary<string, object?>
                {
                    ["colorOverLifetime"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = system.colorOverLifetime.enabled,
                        ["color"] = DescribeGradient(system.colorOverLifetime.color),
                    },
                    ["sizeOverLifetime"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = system.sizeOverLifetime.enabled,
                        ["size"] = DescribeCurve(system.sizeOverLifetime.size),
                    },
                    ["velocityOverLifetime"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = system.velocityOverLifetime.enabled,
                        ["space"] = system.velocityOverLifetime.space.ToString(),
                        ["x"] = DescribeCurve(system.velocityOverLifetime.x),
                        ["y"] = DescribeCurve(system.velocityOverLifetime.y),
                        ["z"] = DescribeCurve(system.velocityOverLifetime.z),
                    },
                    ["noise"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = system.noise.enabled,
                        ["strength"] = DescribeCurve(system.noise.strength),
                        ["frequency"] = Round(system.noise.frequency),
                        ["damping"] = system.noise.damping,
                    },
                    ["trails"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = system.trails.enabled,
                        ["ratio"] = Round(system.trails.ratio),
                        ["lifetime"] = DescribeCurve(system.trails.lifetime),
                        ["worldSpace"] = system.trails.worldSpace,
                        ["dieWithParticles"] = system.trails.dieWithParticles,
                    },
                };
            }

            return result;
        }

        internal static Dictionary<string, object?> DescribeRenderer(ParticleSystemRenderer renderer)
        {
            return new Dictionary<string, object?>
            {
                ["renderMode"] = renderer.renderMode.ToString(),
                ["sortMode"] = renderer.sortMode.ToString(),
                ["sortingLayerName"] = renderer.sortingLayerName,
                ["sortingOrder"] = renderer.sortingOrder,
                ["materialPath"] = renderer.sharedMaterial != null ? AssetDatabase.GetAssetPath(renderer.sharedMaterial) : null,
                ["meshPath"] = renderer.mesh != null ? AssetDatabase.GetAssetPath(renderer.mesh) : null,
                ["enableGPUInstancing"] = renderer.enableGPUInstancing,
            };
        }

        internal static object DescribeCurve(ParticleSystem.MinMaxCurve curve)
        {
            return curve.mode switch
            {
                ParticleSystemCurveMode.Constant => Round(curve.constant),
                ParticleSystemCurveMode.TwoConstants => new Dictionary<string, object?>
                {
                    ["min"] = Round(curve.constantMin),
                    ["max"] = Round(curve.constantMax),
                },
                _ => new Dictionary<string, object?>
                {
                    ["mode"] = curve.mode.ToString(),
                    ["constant"] = Round(curve.constant),
                    ["multiplier"] = Round(curve.curveMultiplier),
                },
            };
        }

        internal static object DescribeGradient(ParticleSystem.MinMaxGradient gradient)
        {
            return gradient.mode switch
            {
                ParticleSystemGradientMode.Color => "#" + ColorUtility.ToHtmlStringRGBA(gradient.color),
                ParticleSystemGradientMode.TwoColors => new Dictionary<string, object?>
                {
                    ["min"] = "#" + ColorUtility.ToHtmlStringRGBA(gradient.colorMin),
                    ["max"] = "#" + ColorUtility.ToHtmlStringRGBA(gradient.colorMax),
                },
                _ => new Dictionary<string, object?>
                {
                    ["mode"] = gradient.mode.ToString(),
                    ["color"] = "#" + ColorUtility.ToHtmlStringRGBA(gradient.color),
                },
            };
        }
#endif
    }
}
