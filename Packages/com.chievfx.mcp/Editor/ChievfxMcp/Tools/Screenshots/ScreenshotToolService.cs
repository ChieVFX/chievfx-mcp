#nullable enable
using System;
using Newtonsoft.Json.Linq;

namespace Chievfx.Mcp.Editor
{
    internal sealed class ScreenshotToolService : IChievfxMcpToolHandler
    {
        private readonly Func<JToken, object> captureGameView;
        private readonly Func<JToken, object> captureCamera;

        public ScreenshotToolService(
            Func<JToken, object> captureGameView,
            Func<JToken, object> captureCamera)
        {
            this.captureGameView = captureGameView;
            this.captureCamera = captureCamera;
        }

        public bool TryRunTool(string toolName, JToken args, out object? result)
        {
            result = toolName switch
            {
                "screenshot-game-view" => captureGameView(args),
                "screenshot-camera" => captureCamera(args),
                _ => null
            };
            return result != null;
        }
    }
}
