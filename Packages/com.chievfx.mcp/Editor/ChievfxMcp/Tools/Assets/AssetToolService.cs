#nullable enable
using System;
using Newtonsoft.Json.Linq;

namespace Chievfx.Mcp.Editor
{
    internal sealed class AssetToolService : IChievfxMcpToolHandler
    {
        private readonly Func<JToken, object> refresh;
        private readonly Func<JToken, object> find;
        private readonly Func<JToken, object> delete;
        private readonly Func<JToken, object> create;
        private readonly Func<JToken, object> ensureFolder;
        private readonly Func<JToken, object> recompile;

        public AssetToolService(Func<JToken, object> refresh, Func<JToken, object> find, Func<JToken, object> delete, Func<JToken, object> create, Func<JToken, object> ensureFolder, Func<JToken, object> recompile)
        {
            this.refresh = refresh;
            this.find = find;
            this.delete = delete;
            this.create = create;
            this.ensureFolder = ensureFolder;
            this.recompile = recompile;
        }

        public bool TryRunTool(string toolName, JToken args, out object? result)
        {
            result = toolName switch
            {
                "assets-refresh" => refresh(args),
                "asset-find" => find(args),
                "asset-delete" => delete(args),
                "asset-create" => create(args),
                "folder-ensure" => ensureFolder(args),
                "recompile" => recompile(args),
                _ => null
            };
            return result != null;
        }
    }
}
