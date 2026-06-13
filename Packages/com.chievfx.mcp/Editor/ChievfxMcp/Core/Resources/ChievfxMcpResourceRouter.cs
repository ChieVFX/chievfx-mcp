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

                if (uri.StartsWith("chievfx://assets/", StringComparison.Ordinal))
                {
                    var assetParts = uri.Substring("chievfx://assets/".Length).Split('/');
                    if (assetParts.Length == 2 && string.Equals(assetParts[0], "name-contains", StringComparison.Ordinal))
                    {
                        var text = BridgeResourcePayloadService.DecodeResourceFilterSegment(assetParts[1], "text", MaxResourceFilterValueChars);
                        return assetResources.ReadFilteredAssetsResource(uri, BridgeResourcePayloadService.CreateAssetNameContainsResourceFilter(text));
                    }

                    if (assetParts.Length == 2 && string.Equals(assetParts[0], "type", StringComparison.Ordinal))
                    {
                        var assetType = BridgeResourcePayloadService.DecodeResourceFilterSegment(assetParts[1], "assetType", MaxResourceFilterValueChars);
                        return assetResources.ReadFilteredAssetsResource(uri, BridgeResourcePayloadService.CreateAssetTypeResourceFilter(assetType));
                    }

                    if (assetParts.Length == 2 && string.Equals(assetParts[0], "label", StringComparison.Ordinal))
                    {
                        var label = BridgeResourcePayloadService.DecodeResourceFilterSegment(assetParts[1], "label", MaxResourceFilterValueChars);
                        return assetResources.ReadFilteredAssetsResource(uri, BridgeResourcePayloadService.CreateAssetLabelResourceFilter(label));
                    }

                    if (assetParts.Length == 2 && string.Equals(assetParts[0], "filter", StringComparison.Ordinal))
                    {
                        var filterSpec = BridgeResourcePayloadService.DecodeResourceFilterSegment(assetParts[1], "filterSpec", MaxResourceFilterSegmentChars);
                        return assetResources.ReadFilteredAssetsResource(uri, BridgeResourcePayloadService.ParseAssetResourceFilterSpec(filterSpec));
                    }
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
                    && string.Equals(parts[0], "current", StringComparison.Ordinal)
                    && string.Equals(parts[1], "usage", StringComparison.Ordinal))
                {
                    if (parts.Length == 3 && string.Equals(parts[2], "counts", StringComparison.Ordinal))
                    {
                        return sceneResources.ReadSceneUsageCountsResource(uri, GameObjectBridgeService.GetGameObjectQueryContext());
                    }

                    if (parts.Length == 4 && string.Equals(parts[2], "assets", StringComparison.Ordinal))
                    {
                        var assetType = BridgeResourcePayloadService.DecodeResourceFilterSegment(parts[3], "assetType", MaxResourceFilterValueChars);
                        return sceneResources.ReadSceneUsageAssetsResource(uri, GameObjectBridgeService.GetGameObjectQueryContext(), assetType);
                    }

                    if (parts.Length == 4 && string.Equals(parts[2], "asset", StringComparison.Ordinal))
                    {
                        return sceneResources.ReadSceneUsageAssetResource(
                            uri,
                            GameObjectBridgeService.GetGameObjectQueryContext(),
                            BridgeResourcePayloadService.DecodeResourceFilterSegment(parts[3], "guid", MaxResourceFilterValueChars),
                            null);
                    }

                    if (parts.Length == 6
                        && string.Equals(parts[2], "asset", StringComparison.Ordinal)
                        && string.Equals(parts[4], "id", StringComparison.Ordinal))
                    {
                        return sceneResources.ReadSceneUsageAssetResource(
                            uri,
                            GameObjectBridgeService.GetGameObjectQueryContext(),
                            BridgeResourcePayloadService.DecodeResourceFilterSegment(parts[3], "guid", MaxResourceFilterValueChars),
                            BridgeResourcePayloadService.DecodeResourceFilterSegment(parts[5], "localId", MaxResourceFilterValueChars));
                    }
                }

                if (parts.Length >= 3
                    && string.Equals(parts[0], "current", StringComparison.Ordinal)
                    && string.Equals(parts[1], "material-profile", StringComparison.Ordinal))
                {
                    if (parts.Length == 3 && string.Equals(parts[2], "summary", StringComparison.Ordinal))
                    {
                        return materialProfileResources.ReadCurrentSceneMaterialProfileSummaryResource(uri, GameObjectBridgeService.GetGameObjectQueryContext());
                    }

                    if (parts.Length == 4 && string.Equals(parts[2], "shader", StringComparison.Ordinal))
                    {
                        return materialProfileResources.ReadCurrentSceneMaterialProfileShaderResource(
                            uri,
                            GameObjectBridgeService.GetGameObjectQueryContext(),
                            BridgeResourcePayloadService.DecodeResourceSegment(parts[3], "shaderKey"));
                    }

                    if (parts.Length == 4 && string.Equals(parts[2], "material", StringComparison.Ordinal))
                    {
                        return materialProfileResources.ReadCurrentSceneMaterialProfileMaterialResource(
                            uri,
                            GameObjectBridgeService.GetGameObjectQueryContext(),
                            BridgeResourcePayloadService.DecodeResourceSegment(parts[3], "materialKey"));
                    }
                }

                if (parts.Length == 3 && string.Equals(parts[1], "go", StringComparison.Ordinal))
                {
                    var context = BridgeResourcePayloadService.ResolveResourceSceneContext(parts[0]);
                    var gameObject = GameObjectBridgeService.ResolveGameObjectByPath(context, BridgeResourcePayloadService.DecodeResourceSegment(parts[2], "goPath"));
                    return sceneResources.ReadGameObjectResource(uri, context, gameObject);
                }

                if (parts.Length == 4
                    && string.Equals(parts[1], "go", StringComparison.Ordinal))
                {
                    var context = BridgeResourcePayloadService.ResolveResourceSceneContext(parts[0]);
                    if (string.Equals(parts[2], "name-contains", StringComparison.Ordinal))
                    {
                        var text = BridgeResourcePayloadService.DecodeResourceFilterSegment(parts[3], "text", MaxResourceFilterValueChars);
                        return sceneResources.ReadFilteredGameObjectsResource(uri, context, BridgeResourcePayloadService.CreateNameContainsResourceFilter(text));
                    }

                    if (string.Equals(parts[2], "name-pattern", StringComparison.Ordinal))
                    {
                        var pattern = BridgeResourcePayloadService.DecodeResourceFilterSegment(parts[3], "pattern", MaxResourceFilterValueChars);
                        GameObjectBridgeService.ValidateWildcardPattern(pattern, "pattern");
                        return sceneResources.ReadFilteredGameObjectsResource(uri, context, BridgeResourcePayloadService.CreateNamePatternResourceFilter(pattern));
                    }

                    if (string.Equals(parts[2], "component", StringComparison.Ordinal))
                    {
                        var componentType = BridgeResourcePayloadService.DecodeResourceFilterSegment(parts[3], "componentType", MaxResourceFilterValueChars);
                        GameObjectBridgeService.ValidateComponentTypeText(componentType, required: true);
                        return sceneResources.ReadFilteredGameObjectsResource(uri, context, BridgeResourcePayloadService.CreateComponentResourceFilter(componentType));
                    }

                    if (string.Equals(parts[2], "filter", StringComparison.Ordinal))
                    {
                        var filterSpec = BridgeResourcePayloadService.DecodeResourceFilterSegment(parts[3], "filterSpec", MaxResourceFilterSegmentChars);
                        return sceneResources.ReadFilteredGameObjectsResource(uri, context, BridgeResourcePayloadService.ParseResourceFilterSpec(filterSpec));
                    }
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
