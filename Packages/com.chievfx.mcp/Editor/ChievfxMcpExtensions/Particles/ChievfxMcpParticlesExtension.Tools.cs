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
using static Chievfx.Mcp.Extensions.Particles.ParticlesModules;
using static Chievfx.Mcp.Extensions.Particles.ParticlesSchemas;
using static Chievfx.Mcp.Extensions.Particles.ParticlesRows;
using static Chievfx.Mcp.Extensions.Particles.ParticlesShared;

namespace Chievfx.Mcp.Extensions.Particles
{
    internal static class ParticlesTools
    {
#if CHIEVFX_MCP_HAS_PARTICLESYSTEM
        internal static object CreateSystem(JToken args, DependencyStatus status)
        {
            var warnings = new List<string>();
            var name = OptionalString(args, "name", "Particle System");
            var parentPath = OptionalString(args, "parentPath", string.Empty);
            var preset = OptionalString(args, "preset", string.Empty);
            var position = ReadVector3(args["position"], Vector3.zero);
            var rotationEuler = ReadVector3(args["rotationEuler"], Vector3.zero);
            var parent = string.IsNullOrWhiteSpace(parentPath) ? null : ResolveTransform(parentPath);
            if (!string.IsNullOrWhiteSpace(parentPath) && parent == null)
            {
                throw new ArgumentException($"parentPath '{parentPath}' was not found in the current scene or prefab stage.");
            }

            var gameObject = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(gameObject, "Create ParticleSystem");
            if (parent != null)
            {
                Undo.SetTransformParent(gameObject.transform, parent, "Parent ParticleSystem");
            }

            gameObject.transform.localPosition = position;
            gameObject.transform.localEulerAngles = rotationEuler;
            var system = gameObject.AddComponent<ParticleSystem>();
            ApplyBaseline(system);
            if (!string.IsNullOrWhiteSpace(preset))
            {
                ApplyNamedPreset(system, preset, warnings);
            }

            MarkChanged(gameObject);
            return CreateOk(status, system, warnings, new Dictionary<string, object?>
            {
                ["created"] = true,
                ["preset"] = string.IsNullOrWhiteSpace(preset) ? null : preset,
            });
        }

        internal static object ApplyPreset(JToken args, DependencyStatus status)
        {
            var warnings = new List<string>();
            var system = RequireSystem(args);
            var preset = RequiredString(args, "preset");
            Undo.RecordObject(system, "Apply ParticleSystem preset");
            var renderer = system.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                Undo.RecordObject(renderer, "Apply ParticleSystem preset");
            }

            ApplyNamedPreset(system, preset, warnings);
            MarkChanged(system);
            return CreateOk(status, system, warnings, new Dictionary<string, object?>
            {
                ["preset"] = preset,
            });
        }

        internal static object PatchModule(JToken args, DependencyStatus status)
        {
            var warnings = new List<string>();
            var system = RequireSystem(args);
            var moduleName = RequiredString(args, "module");
            var dryRun = OptionalBool(args, "dryRun", false);
            var patch = args["fields"] as JObject ?? args["patch"] as JObject;
            if (patch == null)
            {
                throw new ArgumentException("Expected object field 'fields' with allowlisted module values.");
            }

            if (dryRun)
            {
                ValidateModulePatch(moduleName, patch, warnings);
                return CreateOk(status, system, warnings, new Dictionary<string, object?>
                {
                    ["module"] = moduleName,
                    ["patchedFields"] = patch.Properties().Select(property => property.Name).ToArray(),
                    ["dryRun"] = true,
                });
            }

            Undo.RegisterCompleteObjectUndo(system, "Patch ParticleSystem module");
            ApplyModulePatch(system, moduleName, patch, warnings);
            MarkChanged(system);
            return CreateOk(status, system, warnings, new Dictionary<string, object?>
            {
                ["module"] = moduleName,
                ["patchedFields"] = patch.Properties().Select(property => property.Name).ToArray(),
                ["dryRun"] = false,
            });
        }

        internal static object SetRenderer(JToken args, DependencyStatus status)
        {
            var warnings = new List<string>();
            var system = RequireSystem(args);
            var renderer = system.GetComponent<ParticleSystemRenderer>();
            if (renderer == null)
            {
                renderer = system.gameObject.AddComponent<ParticleSystemRenderer>();
                warnings.Add("ParticleSystemRenderer was missing and was added.");
            }

            Undo.RecordObject(renderer, "Set ParticleSystem renderer");
            if (TryString(args, "materialPath", out var materialPath))
            {
                renderer.sharedMaterial = LoadAsset<Material>(materialPath, "materialPath");
            }

            if (TryString(args, "meshPath", out var meshPath))
            {
                renderer.renderMode = ParticleSystemRenderMode.Mesh;
                renderer.mesh = LoadAsset<Mesh>(meshPath, "meshPath");
            }

            if (TryString(args, "renderMode", out var renderMode))
            {
                renderer.renderMode = ParseEnum<ParticleSystemRenderMode>(renderMode, "renderMode");
            }

            if (TryString(args, "sortMode", out var sortMode))
            {
                renderer.sortMode = ParseEnum<ParticleSystemSortMode>(sortMode, "sortMode");
            }

            if (TryInt(args, "sortingOrder", out var sortingOrder))
            {
                renderer.sortingOrder = sortingOrder;
            }

            if (TryString(args, "sortingLayerName", out var sortingLayerName))
            {
                renderer.sortingLayerName = sortingLayerName;
            }

            if (TryBool(args, "enableGPUInstancing", out var enableGpuInstancing))
            {
                renderer.enableGPUInstancing = enableGpuInstancing;
            }

            if (TryString(args, "trailMaterialPath", out var trailMaterialPath))
            {
                var trailMaterial = LoadAsset<Material>(trailMaterialPath, "trailMaterialPath");
                var property = typeof(ParticleSystemRenderer).GetProperty("trailMaterial");
                if (property != null && property.CanWrite)
                {
                    property.SetValue(renderer, trailMaterial);
                }
                else
                {
                    warnings.Add("ParticleSystemRenderer.trailMaterial is not writable in this Unity version.");
                }
            }

            MarkChanged(renderer);
            return CreateOk(status, system, warnings, new Dictionary<string, object?>
            {
                ["renderer"] = DescribeRenderer(renderer),
            });
        }

        internal static object ControlPreview(JToken args, DependencyStatus status)
        {
            var warnings = new List<string>();
            var system = RequireSystem(args);
            var action = RequiredString(args, "action").ToLowerInvariant();
            var withChildren = OptionalBool(args, "withChildren", true);
            Undo.RecordObject(system, "Preview ParticleSystem");

            switch (action)
            {
                case "simulate":
                    var seconds = OptionalFloat(args, "seconds", 1f);
                    if (seconds < 0f || seconds > 30f)
                    {
                        throw new ArgumentOutOfRangeException(nameof(args), "Preview simulate seconds must be between 0 and 30.");
                    }

                    system.Simulate(seconds, withChildren, OptionalBool(args, "restart", true), OptionalBool(args, "fixedTimeStep", true));
                    break;
                case "play":
                    system.Play(withChildren);
                    break;
                case "stop":
                    var clear = OptionalBool(args, "clear", false);
                    system.Stop(withChildren, clear ? ParticleSystemStopBehavior.StopEmittingAndClear : ParticleSystemStopBehavior.StopEmitting);
                    break;
                case "clear":
                    system.Clear(withChildren);
                    break;
                default:
                    throw new ArgumentException("action must be one of: simulate, play, stop, clear.");
            }

            SceneView.RepaintAll();
            MarkChanged(system);
            return CreateOk(status, system, warnings, new Dictionary<string, object?>
            {
                ["action"] = action,
                ["preview"] = new Dictionary<string, object?>
                {
                    ["isPlaying"] = system.isPlaying,
                    ["isPaused"] = system.isPaused,
                    ["isStopped"] = system.isStopped,
                    ["particleCount"] = system.particleCount,
                    ["time"] = Round(system.time),
                },
                ["screenshotReviewHints"] = new[]
                {
                    "Use screenshot-editor-window with Scene view after simulate/play for visual QA.",
                    "Use screenshot-game-view when effect camera composition matters.",
                    "Use screenshot-camera with a fixture camera when Game View does not render Edit Mode particles.",
                },
            });
        }

        internal static Dictionary<string, object?> CreateOk(
            DependencyStatus status,
            ParticleSystem system,
            List<string> warnings,
            Dictionary<string, object?> extra)
        {
            var result = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["status"] = status.ToDictionary(),
                ["target"] = DescribeSystem(system, detail: false),
                ["warnings"] = warnings.Take(MaxWarnings).ToArray(),
                ["detailUri"] = DetailUri(system),
            };
            foreach (var pair in extra)
            {
                result[pair.Key] = pair.Value;
            }

            return result;
        }
#endif
    }
}
