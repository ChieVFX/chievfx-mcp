#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Chievfx.Mcp.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Chievfx.Mcp.Extensions.Control
{
    internal static partial class ChievfxMcpControlExtension
    {
        // Magenta (error/missing shader) and cyan (variant still compiling) are the two most common
        // rendering symptoms, and both previously needed a hand-written ShaderUtil probe to diagnose.
        private static Dictionary<string, object?> ShaderStatus(JToken args)
        {
            var path = ReadString(args, "path");
            var includeWarnings = ReadBool(args, "includeWarnings", false);
            var maxMessages = Math.Clamp(ReadInt(args, "maxMessages", 10), 1, 100);

            var result = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["compiling"] = UnityEditor.ShaderUtil.anythingCompiling,
            };

            if (UnityEditor.ShaderUtil.anythingCompiling)
            {
                result["hint"] = "Shader variants are still compiling — objects can render cyan/placeholder until they finish. Re-check before trusting a screenshot.";
            }

            var shaders = new List<(Shader Shader, string Origin)>();
            var missing = new List<Dictionary<string, object?>>();

            if (!string.IsNullOrWhiteSpace(path))
            {
                CollectShadersFromAssetPath(path!, shaders, result);
            }
            else
            {
                // A clean answer did not say whether anything was looked at: in a UI-only scene it covered nothing.
                var rendererCount = CollectShadersFromOpenScenes(shaders, missing);
                var shaderCount = shaders.Select(entry => entry.Shader).Where(shader => shader != null).Distinct().Count();
                result["scanned"] = $"{rendererCount} renderer(s) in the open scenes, {shaderCount} shader(s); UI Graphic materials are not scanned (pass path for one)";
            }

            var rows = new List<Dictionary<string, object?>>();
            foreach (var (shader, origin) in shaders
                         .GroupBy(entry => entry.Shader, entry => entry.Origin)
                         .Select(group => (Shader: group.Key, Origin: string.Join(", ", group.Distinct().Take(3)))))
            {
                if (shader == null)
                {
                    continue;
                }

                var messages = ReadShaderMessages(shader, includeWarnings, maxMessages, out var errorCount, out var warningCount);
                if (errorCount == 0 && messages.Count == 0)
                {
                    continue;
                }

                rows.Add(new Dictionary<string, object?>
                {
                    ["shader"] = shader.name,
                    ["assetPath"] = AssetDatabase.GetAssetPath(shader),
                    ["usedBy"] = origin,
                    ["errorCount"] = errorCount,
                    ["warningCount"] = warningCount,
                    ["messages"] = messages,
                });
            }

            if (rows.Count > 0)
            {
                result["shadersWithErrors"] = rows;
            }

            if (missing.Count > 0)
            {
                result["missingShaders"] = missing;
            }

            // A path names one shader or material: say which shader it is, the question that brought the agent here
            // ("what draws this?") was left without an answer when the shader compiled cleanly.
            if (!string.IsNullOrWhiteSpace(path) && shaders.Count > 0)
            {
                result["shaders"] = shaders
                    .Where(entry => entry.Shader != null)
                    .Select(entry => entry.Shader.name + " (" + AssetDatabase.GetAssetPath(entry.Shader) + ")")
                    .Distinct()
                    .ToArray();
            }

            if (rows.Count == 0 && missing.Count == 0)
            {
                result["summary"] = UnityEditor.ShaderUtil.anythingCompiling
                    ? "No shader errors found, but variants are still compiling."
                    : "No shader errors or missing shaders found.";
            }
            else
            {
                // Carry the next step in the result: a diagnostic that arrives when it is needed gets
                // read, unlike a catalogue the caller has to think to consult.
                result["summary"] = $"{rows.Count} shader(s) with errors, {missing.Count} missing/error material shader(s) — these render magenta.";
                result["nextStep"] = "Fix the reported file/line. If a pixel still looks wrong once shaders compile, use frame-debugger-pick-pixel to find the draw call that wrote it instead of toggling effects.";
            }

            return result;
        }

        private static void CollectShadersFromAssetPath(string path, List<(Shader, string)> shaders, Dictionary<string, object?> result)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader != null)
            {
                shaders.Add((shader, path));
                return;
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null)
            {
                if (material.shader != null)
                {
                    shaders.Add((material.shader, path));
                }
                else
                {
                    result["note"] = $"Material '{path}' has no shader assigned (renders magenta).";
                }

                return;
            }

            throw new ArgumentException($"No shader or material found at '{path}'.");
        }

        // Returns how many renderers it looked at.
        private static int CollectShadersFromOpenScenes(List<(Shader, string)> shaders, List<Dictionary<string, object?>> missing)
        {
            var rendererCount = 0;
#pragma warning disable CS0618
            foreach (var renderer in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
#pragma warning restore CS0618
            {
                rendererCount++;
                foreach (var material in renderer.sharedMaterials)
                {
                    var rendererPath = HierarchyPath(renderer.gameObject);
                    if (material == null)
                    {
                        missing.Add(new Dictionary<string, object?>
                        {
                            ["gameObject"] = rendererPath,
                            ["reason"] = "material slot is empty",
                        });
                        continue;
                    }

                    if (material.shader == null)
                    {
                        missing.Add(new Dictionary<string, object?>
                        {
                            ["gameObject"] = rendererPath,
                            ["material"] = material.name,
                            ["reason"] = "material has no shader",
                        });
                        continue;
                    }

                    shaders.Add((material.shader, rendererPath));
                }
            }

            return rendererCount;
        }

        private static string HierarchyPath(GameObject gameObject)
        {
            var path = gameObject.name;
            for (var parent = gameObject.transform.parent; parent != null; parent = parent.parent)
            {
                path = parent.name + "/" + path;
            }

            return "/" + path;
        }

        private static List<Dictionary<string, object?>> ReadShaderMessages(
            Shader shader,
            bool includeWarnings,
            int maxMessages,
            out int errorCount,
            out int warningCount)
        {
            errorCount = 0;
            warningCount = 0;
            var rows = new List<Dictionary<string, object?>>();
            UnityEditor.ShaderMessage[] messages;
            try
            {
                messages = UnityEditor.ShaderUtil.GetShaderMessages(shader);
            }
            catch (Exception)
            {
                return rows;
            }

            foreach (var message in messages)
            {
                var isError = message.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error;
                if (isError)
                {
                    errorCount++;
                }
                else
                {
                    warningCount++;
                }

                if ((!isError && !includeWarnings) || rows.Count >= maxMessages)
                {
                    continue;
                }

                var row = new Dictionary<string, object?>
                {
                    ["severity"] = isError ? "error" : "warning",
                    ["message"] = message.message,
                };
                if (!string.IsNullOrEmpty(message.file))
                {
                    row["file"] = message.file;
                }

                if (message.line > 0)
                {
                    row["line"] = message.line;
                }

                // messageDetails is "Compiling Subshader: N, Pass: X, <stage> program..." followed by a
                // multi-line dump of every platform define and disabled keyword. The first line says which
                // pass/variant failed (useful); the rest is hundreds of tokens of noise.
                var details = message.messageDetails;
                if (!string.IsNullOrEmpty(details))
                {
                    var firstLineEnd = details.IndexOf('\n');
                    row["variant"] = (firstLineEnd >= 0 ? details.Substring(0, firstLineEnd) : details).Trim();
                }

                rows.Add(row);
            }

            return rows;
        }

        private static JObject PlayModeSetSchema() => Schema(new JObject
        {
            // isPlaying is the sole advertised, canonical property. The play/playing aliases are kept
            // in the full schema (and enabled is read by the handler) for back-compat, but hidden from
            // the advertised surface via ADVERTISED_PROPERTY_OMISSIONS. They must stay declared here:
            // schemas use additionalProperties=false, so strict clients strip properties the full schema
            // omits. No required list - a client enforcing `required: isPlaying` would reject alias-only calls.
            ["isPlaying"] = Bool("Enter (true) or exit (false) Play Mode."),
            ["play"] = Bool("Alias for isPlaying."),
            ["playing"] = Bool("Alias for isPlaying."),
            // waitForReady/settleMs/timeoutMs are honored by the MCP server (it polls the heartbeat), not
            // the Unity handler, but must be declared here so additionalProperties=false clients keep them.
            ["waitForReady"] = Bool("Block until Play Mode actually reached the requested state (default true)."),
            ["settleMs"] = Int("Extra ms after the transition for frames to render (default 250)."),
            ["timeoutMs"] = Int("Max ms to wait for the transition (default 120000 entering, 30000 exiting — entering domain-reloads and can be slow on large projects)."),
        });

        private static JObject Schema(JObject properties, params string[] required)
        {
            var schema = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties };
            if (required.Length > 0) schema["required"] = new JArray(required);
            return schema;
        }

        private static JObject Str(string description) => new() { ["type"] = "string", ["description"] = description };
        private static JObject Bool(string description) => new() { ["type"] = "boolean", ["description"] = description };
        private static JObject Num(string description) => new() { ["type"] = "number", ["description"] = description };
        private static JObject Int(string description) => new() { ["type"] = "integer", ["description"] = description };
        private static JObject Enum(string description, params string[] values) => new() { ["type"] = "string", ["description"] = description, ["enum"] = new JArray(values) };
        private static JObject Vector(string description) => new()
        {
            ["type"] = "object",
            ["description"] = description,
            ["additionalProperties"] = false,
            // x/y need no description; the names are self-evident and repeat across every vector arg.
            ["properties"] = new JObject { ["x"] = new JObject { ["type"] = "number" }, ["y"] = new JObject { ["type"] = "number" } },
        };
    }
}
