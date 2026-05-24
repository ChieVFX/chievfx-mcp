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
            Host.Attach();
        }

        public static bool IsRunning => Host.IsRunning;

        public static string Url => ChievfxMcpToolPolicy.BridgeDirectory;

        public static void EnsureStarted()
        {
            Host.EnsureStarted();
        }

        public static void Stop()
        {
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
