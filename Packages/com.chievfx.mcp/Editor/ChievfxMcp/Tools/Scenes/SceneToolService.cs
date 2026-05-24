#nullable enable
using System;
using Newtonsoft.Json.Linq;

namespace Chievfx.Mcp.Editor
{
    internal sealed class SceneToolService : IChievfxMcpToolHandler
    {
        private readonly Func<object> listOpenedScenes;
        private readonly Func<JToken, object> listAvailableScenes;
        private readonly Func<JToken, object> createScene;
        private readonly Func<JToken, object> openScene;
        private readonly Func<JToken, object> saveScene;

        public SceneToolService(
            Func<object> listOpenedScenes,
            Func<JToken, object> listAvailableScenes,
            Func<JToken, object> createScene,
            Func<JToken, object> openScene,
            Func<JToken, object> saveScene)
        {
            this.listOpenedScenes = listOpenedScenes;
            this.listAvailableScenes = listAvailableScenes;
            this.createScene = createScene;
            this.openScene = openScene;
            this.saveScene = saveScene;
        }

        public bool TryRunTool(string toolName, JToken args, out object? result)
        {
            result = toolName switch
            {
                "scene-list-opened" => listOpenedScenes(),
                "scene-list-available" => listAvailableScenes(args),
                "scene-create" => createScene(args),
                "scene-open" => openScene(args),
                "scene-save" => saveScene(args),
                _ => null
            };
            return result != null;
        }
    }
}
