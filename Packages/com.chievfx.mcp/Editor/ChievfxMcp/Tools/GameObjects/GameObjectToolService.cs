#nullable enable
using System;
using Newtonsoft.Json.Linq;

namespace Chievfx.Mcp.Editor
{
    internal sealed class GameObjectToolService : IChievfxMcpToolHandler
    {
        private readonly Func<JToken, object> create;
        private readonly Func<JToken, object> getHierarchy;
        private readonly Func<JToken, object> find;
        private readonly Func<JToken, object> getComponent;
        private readonly Func<JToken, object> update;
        private readonly Func<JToken, object> updateOrCreateComponent;
        private readonly Func<JToken, object> getTransform;
        private readonly Func<JToken, object> updateTransform;
        private readonly Func<JToken, object> setParent;
        private readonly Func<JToken, object> duplicate;

        public GameObjectToolService(
            Func<JToken, object> create,
            Func<JToken, object> getHierarchy,
            Func<JToken, object> find,
            Func<JToken, object> getComponent,
            Func<JToken, object> update,
            Func<JToken, object> updateOrCreateComponent,
            Func<JToken, object> getTransform,
            Func<JToken, object> updateTransform,
            Func<JToken, object> setParent,
            Func<JToken, object> duplicate)
        {
            this.create = create;
            this.getHierarchy = getHierarchy;
            this.find = find;
            this.getComponent = getComponent;
            this.update = update;
            this.updateOrCreateComponent = updateOrCreateComponent;
            this.getTransform = getTransform;
            this.updateTransform = updateTransform;
            this.setParent = setParent;
            this.duplicate = duplicate;
        }

        public bool TryRunTool(string toolName, JToken args, out object? result)
        {
            result = toolName switch
            {
                "gameobject-create" => create(args),
                "gameobject-hierarchy" => getHierarchy(args),
                "gameobject-find" => find(args),
                "gameobject-component-get" => getComponent(args),
                "gameobject-update" => update(args),
                "gameobject-component-update-or-create" => updateOrCreateComponent(args),
                "gameobject-transform-get" => getTransform(args),
                "gameobject-transform-update" => updateTransform(args),
                "gameobject-set-parent" => setParent(args),
                "gameobject-duplicate" => duplicate(args),
                _ => null
            };
            return result != null;
        }
    }
}
