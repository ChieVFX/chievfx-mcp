#nullable enable
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Chievfx.Mcp.Editor
{
    internal static class ChievfxMcpCoreMetadata
    {
        public const string ResourceMimeType = "text/plain";

        public static IReadOnlyList<ChievfxMcpResourceDescriptor> Resources { get; } =
            new[]
            {
                Resource("editor-context", "chievfx://editor/context", "Unity editor context", "Compact Unity editor, play mode, active scene, prefab stage, and selection context."),
                Resource("instructions-core-descriptors", "chievfx://instructions/core-descriptors", "Core MCP descriptors", "Full compact tool/resource/prompt descriptor lines from initialize.instructions (Tools: through Extra API capabilities) when startup instructions are truncated."),
                Resource("scenes-opened", "chievfx://scene/opened", "Opened scenes", "Opened Unity scenes and their load/dirty/build state."),
                Resource("scene-all-usage-counts", "chievfx://scene/all/usage/counts", "Asset usage counts across loaded scenes", "Asset usage totals across all loaded scenes, grouped by common asset type."),
                Resource("scene-all-material-profile-summary", "chievfx://scene/all/material-profile/summary", "Material profile across loaded scenes", "Read-only material profile across all loaded scenes with exact shader/material counts and profiler memory estimates."),
            };

        public static IReadOnlyList<ChievfxMcpResourceTemplateDescriptor> ResourceTemplates { get; } =
            new[]
            {
                Template("scene-go", "chievfx://scene/{scenePath}/go/{goPath}", "GameObject summary by scene and path", "Compact GameObject summary. Percent-encode scene path and hierarchy path as full URI segments."),
                Template("scene-component", "chievfx://scene/{scenePath}/go/{goPath}/component/{componentKey}", "Component serialized values by scene, GameObject path, and component key", "Serialized values for one component. Percent-encode every dynamic value as a full URI segment."),
                Template("scene-all-go", "chievfx://scene/all/go/{goPath}", "GameObject summary across loaded scenes", "Compact GameObject summary across all loaded scenes. Prefer this default when scene scope is unknown."),
                Template("scene-all-component", "chievfx://scene/all/go/{goPath}/component/{componentKey}", "Component serialized values across loaded scenes", "Serialized values for one component across all loaded scenes. Prefer this default when scene scope is unknown."),
                Template("asset-detail", "chievfx://asset/{guid}", "Asset detail by GUID", "Load one persisted main asset by GUID with AssetImporter metadata and subasset drill-down hints."),
                Template("asset-subasset-detail", "chievfx://asset/{guid}/id/{localId}", "Subasset detail by GUID and local file identifier", "Load one persisted subasset by GUID and long local file identifier."),
                Template("scene-all-material-profile-shader", "chievfx://scene/all/material-profile/shader/{shaderKey}", "Material profile by shader across loaded scenes", "Drill into materials using one shader key from the material profile summary across loaded scenes."),
                Template("scene-all-material-profile-material", "chievfx://scene/all/material-profile/material/{materialKey}", "Material profile material detail across loaded scenes", "Drill into one material key from the material profile summary across loaded scenes, including locations and texture links."),
                Template("scene-all-usage-assets", "chievfx://scene/all/usage/assets/{assetType}", "Asset usage by type across loaded scenes", "Summarize loaded-scene references for material, mesh, texture, renderTexture, or all assets."),
                Template("scene-all-usage-asset", "chievfx://scene/all/usage/asset/{guid}", "Asset usage by GUID across loaded scenes", "Drill into loaded-scene GameObject/component references to an asset GUID."),
                Template("scene-all-usage-subasset", "chievfx://scene/all/usage/asset/{guid}/id/{localId}", "Subasset usage by GUID and local file identifier across loaded scenes", "Drill into loaded-scene references to one subasset local file identifier."),
            };

        public static IReadOnlyList<ChievfxMcpPromptDescriptor> Prompts { get; } =
            new[]
            {
                Prompt("unity-scene-review", "Review current Unity scene work", "Static prompt for reviewing a Unity scene or prefab against a requested goal.", "Scene", Arg("goal", "Specific scene, prefab, or gameplay goal to review against.", true), Arg("focus", "Optional focus area such as lighting, UI, gameplay wiring, or performance.", false)),
                Prompt("unity-shader-built-in-draft", "Draft Built-in Render Pipeline shader code", "Static prompt for drafting Unity Built-in Render Pipeline shader code from project context.", "Shader", Arg("goal", "Visual effect, material behavior, or shader change to implement.", true), Arg("shaderName", "Optional shader name or target asset name.", false), Arg("context", "Optional material, texture, scene object, platform, or performance constraints.", false)),
                Prompt("unity-shader-urp-draft", "Draft URP shader code", "Static prompt for drafting Unity Universal Render Pipeline shader code from project context.", "Shader", Arg("goal", "Visual effect, material behavior, or shader change to implement.", true), Arg("shaderName", "Optional shader name or target asset name.", false), Arg("context", "Optional material, renderer feature, platform, or performance constraints.", false)),
                Prompt("unity-shader-hdrp-draft", "Draft HDRP shader code", "Static prompt for drafting Unity High Definition Render Pipeline shader code from project context.", "Shader", Arg("goal", "Visual effect, material behavior, or shader change to implement.", true), Arg("shaderName", "Optional shader name or target asset name.", false), Arg("context", "Optional material, volume/profile, platform, or performance constraints.", false)),
                Prompt("unity-shader-graph-plan", "Plan Unity Shader Graph", "Static prompt for planning Shader Graph properties, nodes, targets, and validation.", "Shader", Arg("goal", "Visual effect or material behavior the Shader Graph should implement.", true), Arg("pipeline", "Optional target render pipeline such as URP or HDRP.", false), Arg("context", "Optional material, texture, platform, graph asset, or performance constraints.", false)),
                Prompt("unity-material-profile-review", "Review Unity material and render profile setup", "Static prompt for reviewing shader, material, render pipeline asset, and profile configuration.", "Shader", Arg("goal", "Material, shader, or render profile outcome to review.", true), Arg("assetHint", "Optional material, shader, renderer asset, volume profile, or scene object hint.", false), Arg("focus", "Optional focus such as visuals, batching, lighting, mobile performance, or migration.", false)),
                Prompt("unity-editor-context", "Summarize live Unity editor context", "Dynamic prompt backed by Unity bridge context from current scene, selection, and editor state.", "Editor", Arg("focus", "Optional question or work area to prioritize in the generated context prompt.", false)),
            };

        private static ChievfxMcpResourceDescriptor Resource(string id, string uri, string name, string description)
        {
            return new ChievfxMcpResourceDescriptor
            {
                Id = id,
                Uri = uri,
                Name = name,
                Description = description,
                MimeType = ResourceMimeType,
            };
        }

        private static ChievfxMcpResourceTemplateDescriptor Template(string id, string uriTemplate, string name, string description)
        {
            return new ChievfxMcpResourceTemplateDescriptor
            {
                Id = id,
                UriTemplate = uriTemplate,
                Name = name,
                Description = description,
                MimeType = ResourceMimeType,
            };
        }

        private static ChievfxMcpPromptDescriptor Prompt(string name, string title, string description, string category, params JObject[] arguments)
        {
            return new ChievfxMcpPromptDescriptor
            {
                Name = name,
                Title = title,
                Description = description,
                Category = category,
                Arguments = new JArray(arguments),
            };
        }

        private static JObject Arg(string name, string description, bool required)
        {
            return new JObject
            {
                ["name"] = name,
                ["description"] = description,
                ["required"] = required,
            };
        }
    }
}
