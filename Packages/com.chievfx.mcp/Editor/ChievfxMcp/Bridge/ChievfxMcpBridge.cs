#nullable enable
using System;
using UnityEditor;

namespace Chievfx.Mcp.Editor
{
    [InitializeOnLoad]
    internal static class ChievfxMcpBridge
    {
        private static readonly ChievfxMcpBridgeHost Host = new();

        static ChievfxMcpBridge()
        {
            // Asset import workers load the editor assemblies too and reload their domain after every
            // compile of the editor. A bridge there rewrote events.json from its own stale journal, so
            // the editor's newest events (a compile's compile-finish and errors) vanished from the file.
            if (IsImportWorker)
            {
                return;
            }

            Host.Attach();
        }

        internal static bool IsImportWorker => AssetDatabase.IsAssetImportWorkerProcess();

        public static bool IsRunning => Host.IsRunning;

        public static string Url => ChievfxMcpToolPolicy.BridgeDirectory;

        public static void EnsureStarted()
        {
            if (IsImportWorker)
            {
                return;
            }

            Host.EnsureStarted();
        }

        public static void Stop()
        {
            if (IsImportWorker)
            {
                return;
            }

            Host.Stop();
        }

        internal static object? RunTool(string toolName, Newtonsoft.Json.Linq.JToken args)
        {
            return Host.RunTool(toolName, args);
        }

        internal static object ReadResourceUri(string uri)
        {
            return Host.ReadResourceUri(uri);
        }

        internal static void CompleteTestRun(string id, object result)
        {
            Host.CompleteTestRun(id, result);
        }
    }
}
