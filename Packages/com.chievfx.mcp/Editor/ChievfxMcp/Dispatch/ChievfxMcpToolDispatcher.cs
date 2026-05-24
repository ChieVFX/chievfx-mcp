#nullable enable
using System;
using Newtonsoft.Json.Linq;

namespace Chievfx.Mcp.Editor
{
    internal sealed class ChievfxMcpToolDispatcher
    {
        private readonly IChievfxMcpToolHandler[] handlers;
        private readonly TryRunToolDelegate extensionFallback;

        public ChievfxMcpToolDispatcher(
            IChievfxMcpToolHandler[] handlers,
            TryRunToolDelegate extensionFallback)
        {
            this.handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
            this.extensionFallback = extensionFallback ?? throw new ArgumentNullException(nameof(extensionFallback));
        }

        internal delegate bool TryRunToolDelegate(string toolName, JToken args, out object? result);

        public object? RunTool(string toolName, JToken args)
        {
            foreach (var handler in handlers)
            {
                if (handler.TryRunTool(toolName, args, out var result))
                {
                    return result;
                }
            }

            if (extensionFallback(toolName, args, out var extensionResult))
            {
                return extensionResult ?? new {};
            }

            throw new InvalidOperationException($"Unknown ChievFX MCP tool '{toolName}'.");
        }
    }
}
