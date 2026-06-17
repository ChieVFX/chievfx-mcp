#nullable enable
using System;
using Newtonsoft.Json.Linq;

namespace Chievfx.Mcp.Editor
{
    internal static class ChievfxMcpRuntimeUiInteractionInput
    {
        internal static bool HasScreenPositionInput(JToken args)
        {
            return args["normalized"] is JObject
                || args["screenPosition"] is JObject
                || args["x"] != null
                || args["y"] != null;
        }

        internal static bool HasExplicitTargetInput(JToken args)
        {
            if (args["instanceId"] != null)
            {
                return true;
            }

            foreach (var key in new[] { "path", "targetPath", "visualElementRef", "targetRef", "name", "targetName" })
            {
                if (!string.IsNullOrWhiteSpace(ReadString(args, key)))
                {
                    return true;
                }
            }

            return false;
        }

        internal static string DescribeExplicitTarget(JToken args)
        {
            var instanceId = ReadInt(args, "instanceId", 0);
            if (instanceId != 0)
            {
                return $"instanceId:{instanceId}";
            }

            var path = ReadString(args, "path")
                ?? ReadString(args, "targetPath")
                ?? ReadString(args, "visualElementRef")
                ?? ReadString(args, "targetRef");
            if (!string.IsNullOrWhiteSpace(path))
            {
                return $"path:'{path}'";
            }

            var name = ReadString(args, "name") ?? ReadString(args, "targetName");
            if (!string.IsNullOrWhiteSpace(name))
            {
                return $"name:'{name}'";
            }

            return "explicit target";
        }

        internal static void EnsureTargetOrScreenPosition(JToken args, string toolName)
        {
            if (HasExplicitTargetInput(args) || HasScreenPositionInput(args))
            {
                return;
            }

            throw new ArgumentException(
                $"{toolName} requires path, instanceId, visualElementRef, name, or x/y screen coordinates.");
        }

        internal static string FormatTargetNotFoundMessage(JToken args, string frameworkId)
        {
            return $"No {frameworkId} control found at {DescribeExplicitTarget(args)}.";
        }

        internal static string FormatCrossFrameworkTargetNotFoundMessage(JToken args)
        {
            return $"No runtime UI control found at {DescribeExplicitTarget(args)}. "
                + "Provide a valid uGUI path/instanceId, UI Toolkit path/visualElementRef/name, or x/y screen coordinates.";
        }

        private static string? ReadString(JToken token, string key)
        {
            return token[key]?.Type switch
            {
                JTokenType.String => token[key]!.Value<string>(),
                JTokenType.Integer => token[key]!.ToString(),
                JTokenType.Float => token[key]!.ToString(),
                _ => null,
            };
        }

        private static int ReadInt(JToken token, string key, int defaultValue)
        {
            return token[key]?.Type switch
            {
                JTokenType.Integer => token[key]!.Value<int>(),
                JTokenType.Float => (int)Math.Round(token[key]!.Value<double>()),
                JTokenType.String when int.TryParse(token[key]!.Value<string>(), out var parsed) => parsed,
                _ => defaultValue,
            };
        }
    }
}
