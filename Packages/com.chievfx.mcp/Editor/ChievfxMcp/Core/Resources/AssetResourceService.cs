#nullable enable

namespace Chievfx.Mcp.Editor
{
    internal sealed class AssetResourceService
    {
        public object ReadFilteredAssetsResource(string uri, ResourceAssetFilter filter)
        {
            return BridgeResourcePayloadService.ReadFilteredAssetsResource(uri, filter);
        }

        public object ReadAssetDetailResource(string uri, string guid, string? localIdText)
        {
            return BridgeResourcePayloadService.ReadAssetDetailResource(uri, guid, localIdText);
        }
    }
}
