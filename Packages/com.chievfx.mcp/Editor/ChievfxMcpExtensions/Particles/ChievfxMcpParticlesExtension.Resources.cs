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
using static Chievfx.Mcp.Extensions.Particles.ParticlesTools;
using static Chievfx.Mcp.Extensions.Particles.ParticlesModules;
using static Chievfx.Mcp.Extensions.Particles.ParticlesSchemas;
using static Chievfx.Mcp.Extensions.Particles.ParticlesRows;
using static Chievfx.Mcp.Extensions.Particles.ParticlesShared;

namespace Chievfx.Mcp.Extensions.Particles
{
    internal static class ParticlesResources
    {
#if CHIEVFX_MCP_HAS_PARTICLESYSTEM
        internal static Dictionary<string, object?> ReadSystemsResource(string uri, DependencyStatus status)
        {
            var systems = EnumerateParticleSystems()
                .Take(MaxSystemRows + 1)
                .ToArray();
            var capped = systems.Length > MaxSystemRows;

            return new Dictionary<string, object?>
            {
                ["uri"] = uri,
                ["status"] = status.ToDictionary(),
                ["stage"] = DescribeCurrentStage(),
                ["count"] = Math.Min(systems.Length, MaxSystemRows),
                ["capped"] = capped,
                ["maxRows"] = MaxSystemRows,
                ["systems"] = systems.Take(MaxSystemRows).Select(SummarizeSystem).ToArray(),
            };
        }

        internal static Dictionary<string, object?> ReadSystemDetailResource(string uri, DependencyStatus status)
        {
            var token = Uri.UnescapeDataString(uri.Substring(SystemDetailPrefix.Length));
            var system = ResolveSystem(token);
            if (system == null)
            {
                return new Dictionary<string, object?>
                {
                    ["uri"] = uri,
                    ["ok"] = false,
                    ["error"] = "not-found",
                    ["message"] = $"No ParticleSystem found for '{token}'. Use {SystemsUri} for valid paths and instance ids.",
                    ["stage"] = DescribeCurrentStage(),
                };
            }

            var childSystems = system.GetComponentsInChildren<ParticleSystem>(includeInactive: true)
                .Where(child => child != system)
                .Take(MaxDetailChildren + 1)
                .ToArray();
            return new Dictionary<string, object?>
            {
                ["uri"] = uri,
                ["status"] = status.ToDictionary(),
                ["stage"] = DescribeCurrentStage(),
                ["target"] = DescribeSystem(system, detail: true),
                ["children"] = childSystems.Take(MaxDetailChildren).Select(SummarizeSystem).ToArray(),
                ["childrenCapped"] = childSystems.Length > MaxDetailChildren,
                ["preview"] = new Dictionary<string, object?>
                {
                    ["isPlaying"] = system.isPlaying,
                    ["isPaused"] = system.isPaused,
                    ["isStopped"] = system.isStopped,
                    ["particleCount"] = system.particleCount,
                    ["time"] = Round(system.time),
                },
            };
        }
#endif
    }
}
