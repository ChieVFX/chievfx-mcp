#nullable enable
using System;
using Newtonsoft.Json.Linq;

namespace Chievfx.Mcp.Editor
{
    internal sealed class ConsoleLogToolService : IChievfxMcpToolHandler
    {
        private readonly Func<object> clear;
        private readonly Func<JToken, object> get;
        private readonly Func<JToken, object> getSingle;

        public ConsoleLogToolService(Func<object> clear, Func<JToken, object> get, Func<JToken, object> getSingle)
        {
            this.clear = clear;
            this.get = get;
            this.getSingle = getSingle;
        }

        public bool TryRunTool(string toolName, JToken args, out object? result)
        {
            result = toolName switch
            {
                "console-clear-logs" => clear(),
                "console-get-logs" => get(args),
                "console-get-logs-single" => getSingle(args),
                _ => null
            };
            return result != null;
        }
    }
}
