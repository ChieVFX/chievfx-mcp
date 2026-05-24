#nullable enable
using Newtonsoft.Json.Linq;

namespace Chievfx.Mcp.Editor
{
    internal interface IChievfxMcpToolHandler
    {
        bool TryRunTool(string toolName, JToken args, out object? result);
    }
}
