#nullable enable
using System;
using System.Collections.Generic;
using Chievfx.Mcp.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Chievfx.Mcp.Samples.CustomExtension
{
    /// <summary>
    /// Reference extension showing how a project adds its own ChievFX MCP capabilities
    /// without editing the core package. Registers one executable tool, one dynamic
    /// resource, and one dynamic prompt, all under the "custom" category so they appear
    /// at the top of the optional categories in the ChievFX MCP window.
    /// </summary>
    [InitializeOnLoad]
    public static class ChievfxMcpCustomExtensionExample
    {
        private const string ExtensionId = "chievfx.example.custom";
        private const string Category = "custom";

        private const string EchoToolName = "custom-example-echo";
        private const string StatusUri = "chievfx://extensions/chievfx.example.custom/status";
        private const string PlanPromptName = "custom-example-plan";

        static ChievfxMcpCustomExtensionExample()
        {
            ChievfxMcpExtensionRegistry.RegisterExtension(CreateDescriptor());
        }

        private static ChievfxMcpExtensionDescriptor CreateDescriptor()
        {
            var descriptor = new ChievfxMcpExtensionDescriptor
            {
                Id = ExtensionId,
                DisplayName = "ChievFX MCP Custom Extension Example",
                Version = "0.1.0",
                Description = "Example tool, dynamic resource, and dynamic prompt registered under the Custom category.",
                ToolRunner = RunTool,
                ResourceReader = ReadResource,
                PromptRunner = RunPrompt,
            };

            descriptor.Tools.Add(new ChievfxMcpToolDescriptor
            {
                Name = EchoToolName,
                Description = "Echoes back the provided message. Demonstrates an executable extension tool.",
                Category = Category,
                InputSchema = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["message"] = new JObject
                        {
                            ["type"] = "string",
                            ["description"] = "Text to echo back.",
                        },
                    },
                    ["required"] = new JArray("message"),
                },
            });

            descriptor.Resources.Add(new ChievfxMcpResourceDescriptor
            {
                Id = "custom-example-status",
                Uri = StatusUri,
                Name = "Custom example status",
                Description = "Live editor snapshot returned by the example extension's ResourceReader.",
                MimeType = "application/json",
                Category = Category,
            });

            descriptor.Prompts.Add(new ChievfxMcpPromptDescriptor
            {
                Name = PlanPromptName,
                Title = "Custom example plan",
                Description = "Dynamic prompt that folds a live editor snapshot and an optional focus into a planning message.",
                Category = Category,
                Dynamic = true,
                Arguments = new JArray
                {
                    new JObject
                    {
                        ["name"] = "focus",
                        ["description"] = "Optional area to focus the generated plan on.",
                        ["required"] = false,
                    },
                },
            });

            return descriptor;
        }

        private static object? RunTool(string toolName, JToken args)
        {
            return toolName switch
            {
                EchoToolName => RunEcho(args),
                _ => throw new InvalidOperationException($"Unknown custom example tool '{toolName}'."),
            };
        }

        private static object RunEcho(JToken args)
        {
            var message = args?["message"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(message))
            {
                throw new ArgumentException("message is required.", nameof(args));
            }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["echo"] = message,
                ["length"] = message!.Length,
            };
        }

        private static object? ReadResource(string uri)
        {
            if (string.Equals(uri, StatusUri, StringComparison.Ordinal))
            {
                return BuildStatus();
            }

            return null;
        }

        private static Dictionary<string, object?> BuildStatus()
        {
            return new Dictionary<string, object?>
            {
                ["uri"] = StatusUri,
                ["extensionId"] = ExtensionId,
                ["isPlaying"] = EditorApplication.isPlaying,
                ["activeScene"] = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                ["selectionCount"] = Selection.objects?.Length ?? 0,
                ["unityVersion"] = Application.unityVersion,
            };
        }

        private static object RunPrompt(string promptName, JToken args)
        {
            if (!string.Equals(promptName, PlanPromptName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unknown custom example prompt '{promptName}'.");
            }

            var promptArgs = args?["arguments"] as JObject ?? new JObject();
            var focus = promptArgs["focus"]?.Value<string>();
            var focusLine = string.IsNullOrWhiteSpace(focus)
                ? "No specific focus provided."
                : $"Focus: {focus}";
            var status = BuildStatus();
            var text =
                "Use this live ChievFX MCP custom-extension snapshot to plan the next step.\n\n" +
                focusLine +
                $"\n\nPlay mode: {status["isPlaying"]}\nActive scene: {status["activeScene"]}\nSelected objects: {status["selectionCount"]}\n\n" +
                "Return concise next actions grounded in this editor state.";

            return new Dictionary<string, object?>
            {
                ["description"] = "Dynamic plan prompt generated by the custom example extension.",
                ["messages"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = new Dictionary<string, object?>
                        {
                            ["type"] = "text",
                            ["text"] = text,
                        },
                    },
                },
            };
        }
    }
}
