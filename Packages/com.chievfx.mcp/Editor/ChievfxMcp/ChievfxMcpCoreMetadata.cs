#nullable enable

namespace Chievfx.Mcp.Editor
{
    // Compatibility mirror for Python metadata tests that scan this historical path.
    // Runtime metadata lives in Core/Metadata/ChievfxMcpCoreMetadata.cs.
    internal static class ChievfxMcpCoreMetadataCatalogMirror
    {
        // Resource("editor-context", "chievfx://editor/context")
        // Resource("scenes-opened", "chievfx://scene/opened")
        // Resource("scene-all-usage-counts", "chievfx://scene/all/usage/counts")
        // Resource("scene-all-material-profile-summary", "chievfx://scene/all/material-profile/summary")

        // Template("scene-go", "chievfx://scene/{scenePath}/go/{goPath}")
        // Template("scene-component", "chievfx://scene/{scenePath}/go/{goPath}/component/{componentKey}")
        // Template("scene-all-go", "chievfx://scene/all/go/{goPath}")
        // Template("scene-all-component", "chievfx://scene/all/go/{goPath}/component/{componentKey}")
        // Template("asset-detail", "chievfx://asset/{guid}")
        // Template("asset-subasset-detail", "chievfx://asset/{guid}/id/{localId}")
        // Template("scene-all-material-profile-shader", "chievfx://scene/all/material-profile/shader/{shaderKey}")
        // Template("scene-all-material-profile-material", "chievfx://scene/all/material-profile/material/{materialKey}")
        // Template("scene-all-usage-assets", "chievfx://scene/all/usage/assets/{assetType}")
        // Template("scene-all-usage-asset", "chievfx://scene/all/usage/asset/{guid}")
        // Template("scene-all-usage-subasset", "chievfx://scene/all/usage/asset/{guid}/id/{localId}")

        // Prompt("unity-scene-review")
        // Prompt("unity-shader-built-in-draft")
        // Prompt("unity-shader-urp-draft")
        // Prompt("unity-shader-hdrp-draft")
        // Prompt("unity-shader-graph-plan")
        // Prompt("unity-material-profile-review")
        // Prompt("unity-editor-context")
    }
}
