#nullable enable
using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace Chievfx.Mcp.Editor
{
    // Cursor only re-reads initialize.instructions on a fresh MCP handshake (reconnect); a
    // live tool/resource/prompt availability edit never refreshes them on its own. The
    // reload-mcps extension can watch .cursor/reload-mcps.json and reconnect Cursor when the
    // file appears. Writing it on every availability change forces that reconnect, and the
    // server's post-initialized list_changed nudge then commits the up-to-date instructions
    // with no manual reload.
    internal static class ChievfxMcpReloadSignal
    {
        public static void RequestReload(string trigger)
        {
            if (!ChievfxMcpToolPolicy.AutoReloadCursorOnAvailabilityChange)
            {
                return;
            }

            // A reload signal makes the extension reconnect Cursor's MCP client. Doing that during a
            // Play Mode transition would drop a live tool call mid-play, so defer — the config/instructions
            // are re-read on the next reconnect anyway.
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            try
            {
                var path = ChievfxMcpToolPolicy.CursorReloadSignalPath;
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var payload = new JObject
                {
                    ["serverName"] = ChievfxMcpToolPolicy.CursorServerName
                };

                File.WriteAllText(path, payload.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChievFX MCP reload signal failed ({trigger}). {ex.Message}");
            }
        }
    }
}
