#nullable enable
using System;
using UnityEditor;
using UnityEngine;

namespace Chievfx.Mcp.Editor
{
    internal static class ChievfxMcpExtensionManifestSnapshot
    {
        private static readonly object SyncRoot = new();

        /// <summary>
        /// Writes the current in-editor extension registry for Python metadata CLIs.
        /// Unity selection windows spawn Python synchronously, so metadata must not
        /// round-trip through the bridge while the editor thread is blocked.
        /// </summary>
        public static void Refresh()
        {
            lock (SyncRoot)
            {
                ChievfxMcpExtensionRegistry.ExportManifest(ChievfxMcpToolPolicy.ExtensionCapabilitySnapshotPath);
            }
        }

        // After a domain reload a recompile may have changed an extension's tools or arguments. The MCP server watches
        // this file: when it changes, the server rereads extension schemas and sends tools/list_changed to its clients.
        // The first editor tick, not delayCall: every [InitializeOnLoad] extension has registered by then.
        public static void RefreshAfterDomainReload()
        {
            EditorApplication.update += RefreshOnFirstUpdate;
        }

        private static void RefreshOnFirstUpdate()
        {
            EditorApplication.update -= RefreshOnFirstUpdate;
            try
            {
                Refresh();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChievFX MCP could not refresh the extension capability snapshot. {ex.Message}");
            }
        }
    }
}
