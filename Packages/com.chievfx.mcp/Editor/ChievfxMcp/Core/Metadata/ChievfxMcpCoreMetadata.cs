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
                Resource("resources-guide", "chievfx://resources/guide", "ChievFX MCP resource guide", "Static guide for ChievFX resource URIs, drill-down links, and encoding rules."),
                Resource("editor-context", "chievfx://editor/context", "Unity editor context", "Compact Unity editor, play mode, active scene, prefab stage, and selection context."),
                Resource("scene-opened", "chievfx://scene/opened", "Opened scenes", "Opened Unity scenes and their load/dirty/build state."),
                Resource("scene-current-hierarchy", "chievfx://scene/current/hierarchy", "Current hierarchy", "Compact hierarchy for the current prefab stage when open, otherwise active scene."),
                Resource("scene-current-usage-counts", "chievfx://scene/current/usage/counts", "Current scene asset usage counts", "Asset usage totals for the current prefab stage or active scene, grouped by common asset type."),
                Resource("scene-current-material-profile-summary", "chievfx://scene/current/material-profile/summary", "Current scene material profile", "Read-only material profile for the current prefab stage or active scene with exact shader/material counts and profiler memory estimates."),
            };

        public static IReadOnlyList<ChievfxMcpResourceTemplateDescriptor> ResourceTemplates { get; } =
            new[]
            {
                Template("scene-go", "chievfx://scene/{scenePath}/go/{goPath}", "GameObject summary by scene and path", "Compact GameObject summary. Percent-encode scene path and hierarchy path as full URI segments."),
                Template("scene-component", "chievfx://scene/{scenePath}/go/{goPath}/component/{componentKey}", "Component serialized values by scene, GameObject path, and component key", "Serialized values for one component. Percent-encode every dynamic value as a full URI segment."),
                Template("scene-current-go", "chievfx://scene/current/go/{goPath}", "Current GameObject summary", "Compact GameObject summary in the current prefab stage or active scene."),
                Template("scene-current-component", "chievfx://scene/current/go/{goPath}/component/{componentKey}", "Current component serialized values", "Serialized values for one component in the current prefab stage or active scene."),
                Template("scene-current-go-name-contains", "chievfx://scene/current/go/name-contains/{text}", "Current GameObjects by name substring", "Find current hierarchy GameObjects whose names contain literal percent-encoded text."),
                Template("scene-current-go-name-pattern", "chievfx://scene/current/go/name-pattern/{pattern}", "Current GameObjects by name wildcard", "Find current hierarchy GameObjects by anchored * and ? wildcard pattern in one encoded segment."),
                Template("scene-current-go-component", "chievfx://scene/current/go/component/{componentType}", "Current GameObjects by component type", "Find current hierarchy GameObjects by simple or full component type name."),
                Template("scene-current-go-filter", "chievfx://scene/current/go/filter/{filterSpec}", "Current GameObjects by compact filter", "Find current hierarchy GameObjects with encoded name/component/inactive/case/limit filter text."),
                Template("assets-name-contains", "chievfx://assets/name-contains/{text}", "Assets by name substring", "Find persisted project assets whose names match percent-encoded AssetDatabase name text. Defaults to area=assets."),
                Template("assets-type", "chievfx://assets/type/{assetType}", "Assets by type", "Find persisted project assets by AssetDatabase type or supported alias such as material, texture, prefab, scene, or mesh."),
                Template("assets-label", "chievfx://assets/label/{label}", "Assets by label", "Find persisted project assets by AssetDatabase label. Defaults to area=assets."),
                Template("assets-filter", "chievfx://assets/filter/{filterSpec}", "Assets by compact AssetDatabase filter", "Find persisted assets with encoded name/type/label/area/folder/limit/subassets filter text."),
                Template("asset-detail", "chievfx://asset/{guid}", "Asset detail by GUID", "Load one persisted main asset by GUID with AssetImporter metadata and subasset drill-down hints."),
                Template("asset-subasset-detail", "chievfx://asset/{guid}/id/{localId}", "Subasset detail by GUID and local file identifier", "Load one persisted subasset by GUID and long local file identifier."),
                Template("scene-current-material-profile-shader", "chievfx://scene/current/material-profile/shader/{shaderKey}", "Current material profile by shader", "Drill into materials using one shader key from the current material profile summary."),
                Template("scene-current-material-profile-material", "chievfx://scene/current/material-profile/material/{materialKey}", "Current material profile material detail", "Drill into one material key from the current material profile summary, including locations and texture links."),
                Template("scene-current-usage-assets", "chievfx://scene/current/usage/assets/{assetType}", "Current scene asset usage by type", "Summarize current scene or prefab-stage references for material, mesh, texture, renderTexture, or all assets."),
                Template("scene-current-usage-asset", "chievfx://scene/current/usage/asset/{guid}", "Current scene asset usage by GUID", "Drill into current scene or prefab-stage GameObject/component references to an asset GUID."),
                Template("scene-current-usage-subasset", "chievfx://scene/current/usage/asset/{guid}/id/{localId}", "Current scene subasset usage by GUID and local file identifier", "Drill into current scene or prefab-stage references to one subasset local file identifier."),
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
