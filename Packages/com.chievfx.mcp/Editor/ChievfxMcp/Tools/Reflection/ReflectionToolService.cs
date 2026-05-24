#nullable enable
using System;
using Newtonsoft.Json.Linq;

namespace Chievfx.Mcp.Editor
{
    internal sealed class ReflectionToolService : IChievfxMcpToolHandler
    {
        private readonly Func<JToken, object> findMethods;
        private readonly Func<JToken, object> findSingleMethod;
        private readonly Func<JToken, object> callMethod;

        public ReflectionToolService(
            Func<JToken, object> findMethods,
            Func<JToken, object> findSingleMethod,
            Func<JToken, object> callMethod)
        {
            this.findMethods = findMethods;
            this.findSingleMethod = findSingleMethod;
            this.callMethod = callMethod;
        }

        public bool TryRunTool(string toolName, JToken args, out object? result)
        {
            result = toolName switch
            {
                "reflection-method-find" => findMethods(args),
                "reflection-method-find-single" => findSingleMethod(args),
                "reflection-method-call" => callMethod(args),
                _ => null
            };
            return result != null;
        }
    }
}
