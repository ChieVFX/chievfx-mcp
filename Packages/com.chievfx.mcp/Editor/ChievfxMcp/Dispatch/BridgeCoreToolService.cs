#nullable enable
using System;
using Newtonsoft.Json.Linq;

namespace Chievfx.Mcp.Editor
{
    internal sealed class BridgeCoreToolService : IChievfxMcpToolHandler
    {
        private readonly Func<JToken, object> readResource;
        private readonly Func<JToken, object> getPrompt;

        public BridgeCoreToolService(
            Func<JToken, object> readResource,
            Func<JToken, object> getPrompt)
        {
            this.readResource = readResource;
            this.getPrompt = getPrompt;
        }

        public bool TryRunTool(string toolName, JToken args, out object? result)
        {
            result = toolName switch
            {
                "resource-read" => readResource(args),
                "prompt-get" => getPrompt(args),
                "extension-capabilities-get" => ChievfxMcpExtensionRegistry.GetManifest(),
                _ => null
            };
            return result != null;
        }
    }
}
