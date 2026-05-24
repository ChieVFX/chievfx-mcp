#nullable enable
using Newtonsoft.Json;

namespace Chievfx.Mcp.Editor
{
    internal static class McpJson
    {
        public static readonly JsonSerializerSettings SerializerSettings = new()
        {
            Formatting = Formatting.None
        };
    }
}
