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
using static Chievfx.Mcp.Extensions.Particles.ParticlesResources;
using static Chievfx.Mcp.Extensions.Particles.ParticlesTools;
using static Chievfx.Mcp.Extensions.Particles.ParticlesModules;
using static Chievfx.Mcp.Extensions.Particles.ParticlesSchemas;
using static Chievfx.Mcp.Extensions.Particles.ParticlesRows;
using static Chievfx.Mcp.Extensions.Particles.ParticlesShared;

namespace Chievfx.Mcp.Extensions.Particles
{
    [InitializeOnLoad]
    internal static class ChievfxMcpParticlesExtension
    {
        internal const string ExtensionId = "chievfx.particles";
        internal const string Category = "particles";
        internal const string UriPrefix = "chievfx://extensions/chievfx.particles/";
        internal const string SystemsUri = UriPrefix + "systems";
        internal const string SystemDetailPrefix = UriPrefix + "system/";
        internal const int MaxSystemRows = 96;
        internal const int MaxDetailChildren = 24;
        internal const int MaxWarnings = 16;

#if CHIEVFX_MCP_HAS_PARTICLESYSTEM
        internal const bool ParticleSystemVersionDefineActive = true;
#else
        internal const bool ParticleSystemVersionDefineActive = false;
#endif

        static ChievfxMcpParticlesExtension()
        {
            ChievfxMcpExtensionRegistry.RegisterExtension(CreateDescriptor());
        }

        public static object? RunToolForTests(string toolName, string argsJson)
        {
            return RunTool(toolName, string.IsNullOrWhiteSpace(argsJson) ? new JObject() : JObject.Parse(argsJson));
        }

        internal static object? RunToolForTests(string toolName, JToken args)
        {
            return RunTool(toolName, args);
        }

        public static object? ReadResourceForTests(string uri)
        {
            return ReadResource(uri);
        }

        private static ChievfxMcpExtensionDescriptor CreateDescriptor()
        {
            EnsureParticleSystemTypesLoaded();
            var status = GetDependencyStatus();
            var descriptor = new ChievfxMcpExtensionDescriptor
            {
                Id = ExtensionId,
                DisplayName = "ChievFX MCP ParticleSystem",
                Version = "0.1.0",
                Description = status.Available
                    ? "First-party editor helpers for built-in Unity ParticleSystem authoring and preview."
                    : "First-party ParticleSystem helpers unavailable until com.unity.modules.particlesystem and UnityEngine.ParticleSystem types are loaded.",
                ToolRunner = RunTool,
                ResourceReader = ReadResource,
            };

            descriptor.Prompts.Add(new ChievfxMcpPromptDescriptor
            {
                Name = "particles-effect-recipe",
                Title = "Draft ParticleSystem effect recipe",
                Description = "Guidance for drafting a built-in ParticleSystem effect plan before mutation.",
                Category = Category,
                Arguments = new JArray
                {
                    new JObject
                    {
                        ["name"] = "goal",
                        ["description"] = "Effect goal, style, timing, and constraints.",
                        ["required"] = false,
                    },
                },
                StaticText = "Draft a compact built-in ParticleSystem recipe. Start from chievfx://extensions/chievfx.particles/systems for context. Prefer named presets, then narrow module patches only for allowlisted fields. Goal: {goal}",
            });
            descriptor.Prompts.Add(new ChievfxMcpPromptDescriptor
            {
                Name = "particles-tool-call-plan",
                Title = "Plan ParticleSystem MCP tool calls",
                Description = "Guidance for safe tool-call ordering with current scene resources.",
                Category = Category,
                Arguments = new JArray
                {
                    new JObject
                    {
                        ["name"] = "goal",
                        ["description"] = "Authoring or preview workflow goal.",
                        ["required"] = false,
                    },
                },
                StaticText = "Use ParticleSystem MCP tools in small steps: read /systems, create or target one system, apply a named preset, patch only known modules/fields, set renderer assets if needed, then preview simulate/play/stop/clear. Goal: {goal}",
            });
            descriptor.Prompts.Add(new ChievfxMcpPromptDescriptor
            {
                Name = "particles-authoring-review",
                Title = "Review ParticleSystem authoring",
                Description = "Checklist for reviewing authored particle systems and preview-readiness.",
                Category = Category,
                Arguments = new JArray
                {
                    new JObject
                    {
                        ["name"] = "focus",
                        ["description"] = "Optional review focus such as readability, performance, or timing.",
                        ["required"] = false,
                    },
                },
                StaticText = "Review built-in ParticleSystem changes using chievfx://extensions/chievfx.particles/systems and per-system details. Check maxParticles, emission rates, looping, renderer material, simulation space, and preview state. Capture Scene/Game view screenshots after preview when visual validation is required. Focus: {focus}",
            });

            if (!status.Available)
            {
                return descriptor;
            }

            descriptor.Resources.Add(new ChievfxMcpResourceDescriptor
            {
                Id = "particles-systems-summary",
                Uri = SystemsUri,
                Name = "Current ParticleSystem summary",
                Description = "Compact summary of built-in ParticleSystem components in the active scene or prefab stage.",
                MimeType = "application/json",
                Category = Category,
            });
            descriptor.ResourceTemplates.Add(new ChievfxMcpResourceTemplateDescriptor
            {
                Id = "particles-system-detail",
                UriTemplate = SystemDetailPrefix + "{pathOrInstanceId}",
                Name = "ParticleSystem detail",
                Description = "Compact detail for one ParticleSystem by instance id or URL-encoded transform path.",
                MimeType = "application/json",
                Category = Category,
            });

            descriptor.Tools.Add(CreateTool("particles-system-create", "Create a built-in ParticleSystem in the active scene or prefab stage.", CreateSystemSchema()));
            descriptor.Tools.Add(CreateTool("particles-preset-apply", "Apply a named built-in ParticleSystem preset to one system.", ApplyPresetSchema()));
            descriptor.Tools.Add(CreateTool("particles-module-patch", "Patch allowlisted ParticleSystem module fields on one system.", ModulePatchSchema()));
            descriptor.Tools.Add(CreateTool("particles-renderer-set", "Set safe ParticleSystemRenderer material, mesh render mode, trail material, or sorting fields.", RendererSetSchema()));
            descriptor.Tools.Add(CreateTool("particles-preview-control", "Preview a ParticleSystem in the editor with simulate, play, stop, or clear.", PreviewControlSchema()));
            return descriptor;
        }

        private static ChievfxMcpToolDescriptor CreateTool(string name, string description, JObject schema)
        {
            return new ChievfxMcpToolDescriptor
            {
                Name = name,
                Description = description,
                Category = Category,
                InputSchema = schema,
            };
        }

        private static object? ReadResource(string uri)
        {
            var status = GetDependencyStatus();
            if (!status.Available)
            {
                return CreateUnavailableResource(uri, status);
            }

#if CHIEVFX_MCP_HAS_PARTICLESYSTEM
            if (string.Equals(uri, SystemsUri, StringComparison.Ordinal))
            {
                return ReadSystemsResource(uri, status);
            }

            if (uri.StartsWith(SystemDetailPrefix, StringComparison.Ordinal))
            {
                return ReadSystemDetailResource(uri, status);
            }
#endif

            throw new InvalidOperationException($"Unknown ParticleSystem extension resource '{uri}'.");
        }

        private static object? RunTool(string toolName, JToken args)
        {
            var status = GetDependencyStatus();
            if (!status.Available)
            {
                return CreateUnavailable(status, $"Tool '{toolName}' requires com.unity.modules.particlesystem and loaded UnityEngine.ParticleSystem types.");
            }

#if CHIEVFX_MCP_HAS_PARTICLESYSTEM
            return toolName switch
            {
                "particles-system-create" => CreateSystem(args, status),
                "particles-preset-apply" => ApplyPreset(args, status),
                "particles-module-patch" => PatchModule(args, status),
                "particles-renderer-set" => SetRenderer(args, status),
                "particles-preview-control" => ControlPreview(args, status),
                _ => throw new InvalidOperationException($"Unknown ParticleSystem extension tool '{toolName}'."),
            };
#else
            return CreateUnavailable(status, $"Tool '{toolName}' requires com.unity.modules.particlesystem and loaded UnityEngine.ParticleSystem types.");
#endif
        }

        internal static void EnsureParticleSystemTypesLoaded()
        {
#if CHIEVFX_MCP_HAS_PARTICLESYSTEM
            _ = typeof(ParticleSystem);
            _ = typeof(ParticleSystemRenderer);
#endif
        }

        internal static DependencyStatus GetDependencyStatus()
        {
            var packageInstalled = PackageManagerPackageInfo.FindForPackageName("com.unity.modules.particlesystem") != null;
            var typeLoaded = FindLoadedType("UnityEngine.ParticleSystem") != null
                && FindLoadedType("UnityEngine.ParticleSystemRenderer") != null;
            return new DependencyStatus(
                packageInstalled && typeLoaded && ParticleSystemVersionDefineActive,
                packageInstalled,
                typeLoaded,
                ParticleSystemVersionDefineActive);
        }

        internal static Type? FindLoadedType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? type;
                try
                {
                    type = assembly.GetType(fullName, throwOnError: false);
                }
                catch
                {
                    continue;
                }

                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        internal static Dictionary<string, object?> CreateUnavailableResource(string uri, DependencyStatus status)
        {
            return CreateUnavailable(status, $"Resource '{uri}' requires com.unity.modules.particlesystem and loaded UnityEngine.ParticleSystem types.");
        }

        internal static Dictionary<string, object?> CreateUnavailable(DependencyStatus status, string message)
        {
            return new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["unavailable"] = true,
                ["message"] = message,
                ["extensionId"] = ExtensionId,
                ["status"] = status.ToDictionary(),
            };
        }
    }
}
