#nullable enable
using static Chievfx.Mcp.Editor.McpLimits;
using System;
using Newtonsoft.Json.Linq;

namespace Chievfx.Mcp.Editor
{
    internal sealed class ChievfxMcpResourceRouter : BridgeDomainServiceBase
    {
        private readonly SceneResourceService sceneResources;
        private readonly AssetResourceService assetResources;
        private readonly MaterialProfileResourceService materialProfileResources;

        public ChievfxMcpResourceRouter(
            SceneResourceService sceneResources,
            AssetResourceService assetResources,
            MaterialProfileResourceService materialProfileResources)
        {
            this.sceneResources = sceneResources;
            this.assetResources = assetResources;
            this.materialProfileResources = materialProfileResources;
        }

        public object ReadResource(JToken args)
            {
                var uri = ReadString(args, "uri");
                if (string.IsNullOrWhiteSpace(uri))
                {
                    throw new ArgumentException("uri is required.", nameof(uri));
                }

                return ReadResourceUri(uri!);
            }

        internal object ReadResourceUri(string uri)
            {
                if (ChievfxMcpExtensionRegistry.TryReadResource(uri, out var extensionResource))
                {
                    return extensionResource ?? new System.Collections.Generic.Dictionary<string, object?>();
                }

                if (uri.Contains("?", StringComparison.Ordinal) || uri.Contains("#", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Unsupported ChievFX MCP resource URI '{uri}'.");
                }

                if (string.Equals(uri, "chievfx://editor/context", StringComparison.Ordinal))
                {
                    return sceneResources.ReadEditorContextResource(uri);
                }

                if (string.Equals(uri, "chievfx://scene/opened", StringComparison.Ordinal))
                {
                    return sceneResources.ReadOpenedScenesResource(uri);
                }

                if (uri.StartsWith("chievfx://asset/", StringComparison.Ordinal))
                {
                    var assetParts = uri.Substring("chievfx://asset/".Length).Split('/');
                    if (assetParts.Length == 1)
                    {
                        return assetResources.ReadAssetDetailResource(uri, BridgeResourcePayloadService.DecodeResourceFilterSegment(assetParts[0], "guid", MaxResourceFilterValueChars), null);
                    }

                    if (assetParts.Length == 3 && string.Equals(assetParts[1], "id", StringComparison.Ordinal))
                    {
                        return assetResources.ReadAssetDetailResource(
                            uri,
                            BridgeResourcePayloadService.DecodeResourceFilterSegment(assetParts[0], "guid", MaxResourceFilterValueChars),
                            BridgeResourcePayloadService.DecodeResourceFilterSegment(assetParts[2], "localId", MaxResourceFilterValueChars));
                    }
                }

                if (!uri.StartsWith("chievfx://scene/", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Unsupported ChievFX MCP resource URI '{uri}'.");
                }

                var parts = uri.Substring("chievfx://scene/".Length).Split('/');
                if (parts.Length >= 3
                    && string.Equals(parts[1], "usage", StringComparison.Ordinal))
                {
                    var context = BridgeResourcePayloadService.ResolveResourceSceneContext(parts[0]);
                    if (parts.Length == 3 && string.Equals(parts[2], "counts", StringComparison.Ordinal))
                    {
                        return sceneResources.ReadSceneUsageCountsResource(uri, context);
                    }

                    if (parts.Length == 4 && string.Equals(parts[2], "assets", StringComparison.Ordinal))
                    {
                        var assetType = BridgeResourcePayloadService.DecodeResourceFilterSegment(parts[3], "assetType", MaxResourceFilterValueChars);
                        return sceneResources.ReadSceneUsageAssetsResource(uri, context, assetType);
                    }

                    if (parts.Length == 4 && string.Equals(parts[2], "asset", StringComparison.Ordinal))
                    {
                        return sceneResources.ReadSceneUsageAssetResource(
                            uri,
                            context,
                            BridgeResourcePayloadService.DecodeResourceFilterSegment(parts[3], "guid", MaxResourceFilterValueChars),
                            null);
                    }

                    if (parts.Length == 6
                        && string.Equals(parts[2], "asset", StringComparison.Ordinal)
                        && string.Equals(parts[4], "id", StringComparison.Ordinal))
                    {
                        return sceneResources.ReadSceneUsageAssetResource(
                            uri,
                            context,
                            BridgeResourcePayloadService.DecodeResourceFilterSegment(parts[3], "guid", MaxResourceFilterValueChars),
                            BridgeResourcePayloadService.DecodeResourceFilterSegment(parts[5], "localId", MaxResourceFilterValueChars));
                    }
                }

                if (parts.Length >= 3
                    && string.Equals(parts[1], "material-profile", StringComparison.Ordinal))
                {
                    var context = BridgeResourcePayloadService.ResolveResourceSceneContext(parts[0]);
                    if (parts.Length == 3 && string.Equals(parts[2], "summary", StringComparison.Ordinal))
                    {
                        return materialProfileResources.ReadCurrentSceneMaterialProfileSummaryResource(uri, context);
                    }

                    if (parts.Length == 4 && string.Equals(parts[2], "shader", StringComparison.Ordinal))
                    {
                        return materialProfileResources.ReadCurrentSceneMaterialProfileShaderResource(
                            uri,
                            context,
                            BridgeResourcePayloadService.DecodeResourceSegment(parts[3], "shaderKey"));
                    }

                    if (parts.Length == 4 && string.Equals(parts[2], "material", StringComparison.Ordinal))
                    {
                        return materialProfileResources.ReadCurrentSceneMaterialProfileMaterialResource(
                            uri,
                            context,
                            BridgeResourcePayloadService.DecodeResourceSegment(parts[3], "materialKey"));
                    }
                }

                if (parts.Length == 3 && string.Equals(parts[1], "go", StringComparison.Ordinal))
                {
                    var context = BridgeResourcePayloadService.ResolveResourceSceneContext(parts[0]);
                    return sceneResources.ReadGameObjectPathResource(uri, context, BridgeResourcePayloadService.DecodeResourceSegment(parts[2], "goPath"));
                }

                if (parts.Length == 4
                    && string.Equals(parts[1], "go", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Unsupported ChievFX MCP resource URI '{uri}'.");
                }

                if (parts.Length == 5
                    && string.Equals(parts[1], "go", StringComparison.Ordinal)
                    && string.Equals(parts[3], "component", StringComparison.Ordinal))
                {
                    var context = BridgeResourcePayloadService.ResolveResourceSceneContext(parts[0]);
                    var gameObject = GameObjectBridgeService.ResolveGameObjectByPath(context, BridgeResourcePayloadService.DecodeResourceSegment(parts[2], "goPath"));
                    var componentKey = BridgeResourcePayloadService.DecodeResourceSegment(parts[4], "componentKey");
                    var component = BridgeResourcePayloadService.ResolveComponentByKey(gameObject, componentKey);
                    return sceneResources.ReadComponentResource(uri, context, gameObject, component.Component, component.Key);
                }

                throw new InvalidOperationException($"Unsupported ChievFX MCP resource URI '{uri}'.");
            }
    }
}
