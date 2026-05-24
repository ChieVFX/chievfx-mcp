#nullable enable
using System;
using Newtonsoft.Json.Linq;

namespace Chievfx.Mcp.Editor
{
    internal sealed class PrefabToolService : IChievfxMcpToolHandler
    {
        private readonly Func<JToken, object> open;
        private readonly Func<JToken, object> close;
        private readonly Func<JToken, object> save;
        private readonly Func<JToken, object> create;
        private readonly Func<JToken, object> instantiate;

        public PrefabToolService(
            Func<JToken, object> open,
            Func<JToken, object> close,
            Func<JToken, object> save,
            Func<JToken, object> create,
            Func<JToken, object> instantiate)
        {
            this.open = open;
            this.close = close;
            this.save = save;
            this.create = create;
            this.instantiate = instantiate;
        }

        public bool TryRunTool(string toolName, JToken args, out object? result)
        {
            result = toolName switch
            {
                "prefab-open" => open(args),
                "prefab-close" => close(args),
                "prefab-save" => save(args),
                "prefab-create" => create(args),
                "prefab-instantiate" => instantiate(args),
                _ => null
            };
            return result != null;
        }
    }
}
