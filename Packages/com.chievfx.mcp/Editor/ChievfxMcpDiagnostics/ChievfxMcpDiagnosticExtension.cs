#nullable enable
using Chievfx.Mcp.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Chievfx.Mcp.Diagnostics
{
    [InitializeOnLoad]
    internal static class ChievfxMcpDiagnosticExtension
    {
        static ChievfxMcpDiagnosticExtension()
        {
            ChievfxMcpExtensionRegistry.RegisterExtension(
                new ChievfxMcpExtensionDescriptor
                {
                    Id = "chievfx.diagnostics",
                    DisplayName = "ChievFX MCP Diagnostics",
                    Version = "1.0.0",
                    Description = "Built-in diagnostic extension that proves external assemblies can register MCP capabilities.",
                    Resources =
                    {
                        new ChievfxMcpResourceDescriptor
                        {
                            Id = "chievfx-diagnostics-capabilities",
                            Uri = "chievfx://extensions/chievfx.diagnostics/capabilities",
                            Name = "ChievFX MCP extension capabilities",
                            Description = "Read-only diagnostic resource exposed through the extension registry manifest path.",
                            MimeType = "text/plain",
                            Category = "Diagnostics",
                            Required = true,
                            StaticText = "ChievFX MCP extension registry active.\nManifest: Library/ChievfxMcpBridge/extension-capabilities.json",
                        },
                    },
                    Prompts =
                    {
                        new ChievfxMcpPromptDescriptor
                        {
                            Name = "chievfx-diagnostics-summary",
                            Title = "Summarize ChievFX MCP diagnostics",
                            Description = "Static diagnostic prompt exposed by a Unity extension assembly.",
                            Category = "Diagnostics",
                            Required = true,
                            StaticText = "Summarize the ChievFX MCP extension registry state and mention that diagnostics extension registration is active.",
                            Arguments = new JArray(),
                        },
                    },
                });
        }
    }
}
