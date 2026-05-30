#nullable enable
using UnityEngine;

namespace Chievfx.Mcp.Editor
{
    internal sealed class SceneResourceService
    {
        public object ReadEditorContextResource(string uri)
        {
            return BridgeResourcePayloadService.ReadEditorContextResource(uri);
        }

        public object ReadOpenedScenesResource(string uri)
        {
            return BridgeResourcePayloadService.ReadOpenedScenesResource(uri);
        }

        public object ReadGameObjectResource(string uri, GameObjectQueryContext context, GameObject gameObject)
        {
            return BridgeResourcePayloadService.ReadGameObjectResource(uri, context, gameObject);
        }

        public object ReadComponentResource(
            string uri,
            GameObjectQueryContext context,
            GameObject gameObject,
            Component component,
            string componentKey)
        {
            return BridgeResourcePayloadService.ReadComponentResource(uri, context, gameObject, component, componentKey);
        }

        public object ReadFilteredGameObjectsResource(string uri, GameObjectQueryContext context, ResourceGameObjectFilter filter)
        {
            return BridgeResourcePayloadService.ReadFilteredGameObjectsResource(uri, context, filter);
        }

        public object ReadSceneUsageCountsResource(string uri, GameObjectQueryContext context)
        {
            return BridgeResourcePayloadService.ReadSceneUsageCountsResource(uri, context);
        }

        public object ReadSceneUsageAssetsResource(string uri, GameObjectQueryContext context, string assetType)
        {
            return BridgeResourcePayloadService.ReadSceneUsageAssetsResource(uri, context, assetType);
        }

        public object ReadSceneUsageAssetResource(string uri, GameObjectQueryContext context, string guid, string? localIdText)
        {
            return BridgeResourcePayloadService.ReadSceneUsageAssetResource(uri, context, guid, localIdText);
        }
    }
}
