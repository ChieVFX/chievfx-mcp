#nullable enable
using System;
using Newtonsoft.Json.Linq;

namespace Chievfx.Mcp.Editor
{
    internal sealed class RuntimeUiToolService : IChievfxMcpToolHandler
    {
        public bool TryRunTool(string toolName, JToken args, out object? result)
        {
            if (!string.Equals(toolName, "ui-control-find", StringComparison.Ordinal))
            {
                result = null;
                return false;
            }

            result = ChievfxMcpRuntimeUiAdapterRegistry.ControlFind(args);
            return true;
        }
    }
}
