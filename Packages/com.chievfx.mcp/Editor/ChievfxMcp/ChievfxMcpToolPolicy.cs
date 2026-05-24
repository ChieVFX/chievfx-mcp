#nullable enable
using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Chievfx.Mcp.Editor
{
    [InitializeOnLoad]
    internal static class ChievfxMcpBootstrap
    {
        static ChievfxMcpBootstrap()
        {
            AssemblyReloadEvents.beforeAssemblyReload += PrepareForAssemblyReloadSafely;
            EditorApplication.quitting += StopLocalProcessesSafely;
            ChievfxMcpRuntimeUiAdapterRegistry.EnsureRegistered();
            ChievfxMcpFirstPartyExtensionLoader.EnsureLoaded();
            EditorApplication.delayCall += StartBridgeSafely;
        }

        private static void StartBridgeSafely()
        {
            try
            {
                ChievfxMcpBridge.EnsureStarted();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChievFX MCP bridge could not start on load. {ex.Message}");
            }
        }

        private static void PrepareForAssemblyReloadSafely()
        {
            try
            {
                ChievfxMcpBridge.Stop();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChievFX MCP bridge reload cleanup failed. {ex.Message}");
            }
        }

        private static void StopLocalProcessesSafely()
        {
            try
            {
                ChievfxMcpWindow.StopHttpServerProcess();
                ChievfxMcpBridge.Stop();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChievFX MCP local cleanup failed. {ex.Message}");
            }
        }
    }

    internal static class ChievfxMcpToolPolicy
    {
        public const string ServerName = "unity-mcp-chievfx";

        public const string PackageName = "com.chievfx.mcp";

        public static readonly string[] RequiredToolIds = LoadRequiredToolIds();

        public static readonly string[] DefaultEnabledToolIds =
        {
            "tools-list-categories",
            "tools-list-category",
            "tools-set-enabled-state",
            "tools-get-roles",
            "tools-get-role",
            "tools-set-role"
        };

        public static string RequiredToolsCsv => string.Join(",", RequiredToolIds);

        public static string RequiredToolsDisplay => string.Join(", ", RequiredToolIds);

        public const int DefaultMcpPort = 27247;

        public const int DefaultBridgePort = 27248;

        public const int DefaultTimeoutMs = 10000;

        public const string AutoReloadExternallyChangedScenesKey = ServerName + ".autoReloadExternallyChangedScenes";

        public static bool AutoReloadExternallyChangedScenes => EditorPrefs.GetBool(AutoReloadExternallyChangedScenesKey, true);

        public static string BridgeUrl => $"http://127.0.0.1:{DefaultBridgePort}";

        public static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        public static string PackageRoot => ResolvePackageRoot();

        public static string PackageToolsDirectory => Path.Combine(PackageRoot, "Tools", "ChievfxMcp");

        public static string BridgeDirectory => Path.Combine(ProjectRoot, "Library", "ChievfxMcpBridge");

        public static string BridgeRequestDirectory => Path.Combine(BridgeDirectory, "requests");

        public static string BridgeResponseDirectory => Path.Combine(BridgeDirectory, "responses");

        public static string BridgeOperationDirectory => Path.Combine(BridgeDirectory, "operations");

        public static string BridgeCancelDirectory => Path.Combine(BridgeDirectory, "cancel");

        public static string BridgeStatePath => Path.Combine(BridgeDirectory, "state.json");

        public static string BridgeEventPath => Path.Combine(BridgeDirectory, "events.json");

        public static string ExtensionCapabilityManifestPath => Path.Combine(BridgeDirectory, "extension-capabilities.json");

        public static string CursorConfigPath => Path.Combine(ProjectRoot, ".cursor", "mcp.json");

        public static string ServerScriptPath => Path.Combine(PackageToolsDirectory, "chievfx_mcp_server.py");

        public static string ToolPolicyPath => Path.Combine(PackageToolsDirectory, "chievfx_mcp_tool_policy.json");

        public static string ToolRolePresetsPath => Path.Combine(PackageToolsDirectory, "chievfx_mcp_role_presets.json");

        public static string ToolSelectionPath => Path.Combine(ProjectRoot, "UserSettings", "ChievfxMcpToolSelection.json");

        public static string ToolRoleAssetDefaultDirectory => "Assets/ChievfxMcp/Roles";

        public static string ResourceSelectionPath => Path.Combine(ProjectRoot, "UserSettings", "ChievfxMcpResourceSelection.json");

        public static string PromptSelectionPath => Path.Combine(ProjectRoot, "UserSettings", "ChievfxMcpPromptSelection.json");

        public static void EnsureBridgeStarted()
        {
            ChievfxMcpBridge.EnsureStarted();
        }

        private static string ResolvePackageRoot()
        {
            try
            {
                var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ChievfxMcpToolPolicy).Assembly);
                if (packageInfo != null && string.Equals(packageInfo.name, PackageName, StringComparison.Ordinal))
                {
                    return Path.GetFullPath(packageInfo.resolvedPath);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChievFX MCP could not resolve package path from package manager. {ex.Message}");
            }

            return Path.Combine(ProjectRoot, "Packages", PackageName);
        }

        private static string[] LoadRequiredToolIds()
        {
            try
            {
                if (File.Exists(ToolPolicyPath))
                {
                    var root = JToken.Parse(File.ReadAllText(ToolPolicyPath));
                    if (root is JObject rootObj
                        && rootObj["requiredToolIds"] is JArray ids)
                    {
                        var requiredIds = new System.Collections.Generic.List<string>();
                        foreach (var item in ids)
                        {
                            if (item.Type == JTokenType.String)
                            {
                                var id = item.Value<string>();
                                if (!string.IsNullOrWhiteSpace(id))
                                {
                                    requiredIds.Add(id!);
                                }
                            }
                        }

                        if (requiredIds.Count > 0)
                        {
                            return requiredIds.ToArray();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChievFX MCP could not read tool policy. Using built-in required tools. {ex.Message}");
            }

            return new[]
            {
                "screenshot-game-view",
                "screenshot-camera",
                "screenshot-editor-window",
                "tool-batch",
                "editor-playmode-set",
                "bridge-get-operation",
                "bridge-get-status",
                "events-check-since",
                "events-wait",
                "asset-create",
                "asset-delete",
                "assets-refresh",
                "folder-ensure",
                "recompile",
                "console-clear-logs",
                "console-get-logs",
                "console-get-logs-single",
                "reflection-method-find",
                "reflection-method-find-single",
                "reflection-method-call",
                "editor-window-list",
                "editor-window-open",
                "editor-window-focus"
            };
        }
    }
}
