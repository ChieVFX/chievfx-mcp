#nullable enable
using System;
using Newtonsoft.Json.Linq;

namespace Chievfx.Mcp.Editor
{
    internal sealed class RuntimeUiToolService : IChievfxMcpToolHandler
    {
        public bool TryRunTool(string toolName, JToken args, out object? result)
        {
            if (string.Equals(toolName, "ui-control-find", StringComparison.Ordinal))
            {
                result = ChievfxMcpRuntimeUiAdapterRegistry.ControlFind(args);
                return true;
            }

            if (string.Equals(toolName, ChievfxMcpRuntimeUiAdapterRegistry.ClickToolName, StringComparison.Ordinal))
            {
                result = ChievfxMcpRuntimeUiAdapterRegistry.RuntimeClick(args);
                return true;
            }

            result = null;
            return false;
        }
    }
}
