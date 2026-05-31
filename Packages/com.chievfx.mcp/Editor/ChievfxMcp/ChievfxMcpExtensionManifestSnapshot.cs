#nullable enable

namespace Chievfx.Mcp.Editor
{
    internal static class ChievfxMcpExtensionManifestSnapshot
    {
        /// <summary>
        /// Writes the current in-editor extension registry for Python metadata CLIs.
        /// Unity selection windows spawn Python synchronously, so metadata must not
        /// round-trip through the bridge while the editor thread is blocked.
        /// </summary>
        public static void Refresh()
        {
            ChievfxMcpExtensionRegistry.ExportManifest(ChievfxMcpToolPolicy.ExtensionCapabilitySnapshotPath);
        }
    }
}
