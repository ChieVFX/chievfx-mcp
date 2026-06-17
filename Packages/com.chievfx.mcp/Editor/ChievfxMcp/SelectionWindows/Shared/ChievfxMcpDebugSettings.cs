#nullable enable
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Chievfx.Mcp.Editor
{
    /// <summary>
    /// Shared debug-mode flag (UserSettings/ChievfxMcpDebugSettings.json).
    /// Python MCP server reads the same file before writing .temp debug artifacts.
    /// </summary>
    internal static class ChievfxMcpDebugSettings
    {
        private const int SchemaVersion = 1;

        public static bool DebugMode => LoadDebugMode();

        public static bool LoadDebugMode()
        {
            try
            {
                if (!File.Exists(ChievfxMcpToolPolicy.DebugSettingsPath))
                {
                    return false;
                }

                var root = JToken.Parse(File.ReadAllText(ChievfxMcpToolPolicy.DebugSettingsPath));
                return root is JObject obj
                    && obj["debugMode"] is JValue value
                    && value.Type == JTokenType.Boolean
                    && value.Value<bool>();
            }
            catch (IOException ex)
            {
                Debug.LogWarning($"ChievFX MCP debug settings read failed. {ex.Message}");
                return false;
            }
        }

        public static void SetDebugMode(bool enabled)
        {
            var directory = Path.GetDirectoryName(ChievfxMcpToolPolicy.DebugSettingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var payload = new JObject
            {
                ["schemaVersion"] = SchemaVersion,
                ["debugMode"] = enabled,
            };
            File.WriteAllText(ChievfxMcpToolPolicy.DebugSettingsPath, payload.ToString(Newtonsoft.Json.Formatting.Indented));

            if (!enabled)
            {
                ClearDebugArtifacts();
            }
        }

        public static void ClearDebugArtifacts()
        {
            TryDeletePath(DebugInstructionsPath);
            TryDeleteDirectory(DebugDescriptorsDirectory);
        }

        private static void TryDeletePath(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException ex)
            {
                Debug.LogWarning($"ChievFX MCP debug artifact delete failed ({path}). {ex.Message}");
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException ex)
            {
                Debug.LogWarning($"ChievFX MCP debug artifact delete failed ({path}). {ex.Message}");
            }
        }

        private static string DebugInstructionsPath =>
            Path.Combine(ChievfxMcpToolPolicy.ProjectRoot, ".temp", "debug_instructions.md");

        private static string DebugDescriptorsDirectory =>
            Path.Combine(ChievfxMcpToolPolicy.ProjectRoot, ".temp", "descriptors");
    }
}
