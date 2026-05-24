#nullable enable

namespace Chievfx.Mcp.Editor
{
    internal sealed class MaterialProfileResourceService
    {
        public object ReadCurrentSceneMaterialProfileSummaryResource(string uri, GameObjectQueryContext context)
        {
            return BridgeResourcePayloadService.ReadCurrentSceneMaterialProfileSummaryResource(uri, context);
        }

        public object ReadCurrentSceneMaterialProfileShaderResource(string uri, GameObjectQueryContext context, string shaderKey)
        {
            return BridgeResourcePayloadService.ReadCurrentSceneMaterialProfileShaderResource(uri, context, shaderKey);
        }

        public object ReadCurrentSceneMaterialProfileMaterialResource(string uri, GameObjectQueryContext context, string materialKey)
        {
            return BridgeResourcePayloadService.ReadCurrentSceneMaterialProfileMaterialResource(uri, context, materialKey);
        }
    }
}
