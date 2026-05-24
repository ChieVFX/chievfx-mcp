#nullable enable
using System;
using Newtonsoft.Json.Linq;

namespace Chievfx.Mcp.Editor
{
    internal sealed class EditorWindowToolService : IChievfxMcpToolHandler
    {
        private readonly Func<JToken, object> list;
        private readonly Func<JToken, object> open;
        private readonly Func<JToken, object> focus;

        public EditorWindowToolService(
            Func<JToken, object> list,
            Func<JToken, object> open,
            Func<JToken, object> focus)
        {
            this.list = list;
            this.open = open;
            this.focus = focus;
        }

        public bool TryRunTool(string toolName, JToken args, out object? result)
        {
            result = toolName switch
            {
                "editor-window-list" => list(args),
                "editor-window-open" => open(args),
                "editor-window-focus" => focus(args),
                _ => null
            };
            return result != null;
        }
    }
}
