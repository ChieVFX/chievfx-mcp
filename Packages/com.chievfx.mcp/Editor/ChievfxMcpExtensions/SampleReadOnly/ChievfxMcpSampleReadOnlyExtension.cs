#nullable enable
using System;
using System.Collections.Generic;
using Chievfx.Mcp.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine.SceneManagement;

namespace Chievfx.Mcp.Extensions.SampleReadOnly
{
    [InitializeOnLoad]
    internal static class ChievfxMcpSampleReadOnlyExtension
    {
        private const string ExtensionId = "chievfx.sample-readonly";
        private const string Category = "Samples";
        private const string UriPrefix = "chievfx://extensions/chievfx.sample-readonly/";
        private const string StatusUri = UriPrefix + "status";

        static ChievfxMcpSampleReadOnlyExtension()
        {
            ChievfxMcpExtensionRegistry.RegisterExtension(CreateDescriptor());
        }

        private static ChievfxMcpExtensionDescriptor CreateDescriptor()
        {
            var descriptor = new ChievfxMcpExtensionDescriptor
            {
                Id = ExtensionId,
                DisplayName = "ChievFX MCP Sample Read-Only Extension",
                Version = "0.1.0",
                Description = "Minimal sample extension with one read-only resource and one prompt.",
                ResourceReader = ReadResource,
            };

            descriptor.Resources.Add(
                new ChievfxMcpResourceDescriptor
                {
                    Id = "sample-readonly-status",
                    Uri = StatusUri,
                    Name = "Sample read-only status",
                    Description = "Small JSON status payload from a separate ChievFX MCP extension assembly.",
                    MimeType = "application/json",
                    Category = Category,
                });

            descriptor.Prompts.Add(
                new ChievfxMcpPromptDescriptor
                {
                    Name = "sample-readonly-review",
                    Title = "Review a read-only extension",
                    Description = "Guidance for inspecting the sample extension without changing project state.",
                    Category = Category,
                    Arguments = new JArray
                    {
                        new JObject
                        {
                            ["name"] = "focus",
                            ["description"] = "Optional area to inspect, such as registration, resource output, or descriptor metadata.",
                            ["required"] = false,
                        },
                    },
                    StaticText = "Inspect the sample extension read-only. Start with chievfx://extensions/chievfx.sample-readonly/status, then check descriptor metadata if needed. Focus: {focus}",
                });

            return descriptor;
        }

        private static object? ReadResource(string uri)
        {
            if (!string.Equals(uri, StatusUri, StringComparison.Ordinal))
            {
                return null;
            }

            var activeScene = SceneManager.GetActiveScene();
            return new Dictionary<string, object?>
            {
                ["uri"] = uri,
                ["extensionId"] = ExtensionId,
                ["readOnly"] = true,
                ["readAtUtc"] = DateTime.UtcNow.ToString("o"),
                ["activeScene"] = new Dictionary<string, object?>
                {
                    ["name"] = activeScene.name,
                    ["path"] = activeScene.path,
                    ["isLoaded"] = activeScene.isLoaded,
                    ["rootCount"] = activeScene.IsValid() && activeScene.isLoaded ? activeScene.rootCount : 0,
                },
                ["maxResults"] = 1,
                ["truncated"] = false,
            };
        }
    }
}
