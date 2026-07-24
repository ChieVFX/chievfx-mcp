#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
            EditorApplication.delayCall += EnforceExperimentalVisibilitySafely;
            EditorApplication.delayCall += ChievfxMcpToolPolicy.WriteAvailabilitySettings;
            EditorApplication.delayCall += EnsureClientConfigsSafely;
        }

        private static void EnsureClientConfigsSafely()
        {
            try
            {
                if (Application.isBatchMode)
                {
                    return;
                }

                // Never (re)write client configs while entering or in Play Mode. This runs from the
                // InitializeOnLoad bootstrap after EVERY domain reload — including the one that entering
                // Play Mode triggers — and touching a config file makes clients like Cursor reconnect
                // their MCP server, dropping a live tool call mid-play (the "server vanishes on Play"
                // report). Config setup belongs to edit mode / project open, so defer while playing.
                if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    return;
                }

                var writtenConfigs = ChievfxMcpToolPolicy.AutoWriteClientConfigs
                    ? ChievfxMcpWindow.EnsureAllClientConfigs()
                    : new System.Collections.Generic.List<string>();

                // Welcome only surfaces on initial setup: when configs were just (re)written —
                // signalling the MCP is now available to Cursor/Claude/Codex/Kimi — or when something
                // is wrong and needs the user's attention. A healthy, already-configured project
                // opens silently.
                ChievfxMcpWelcomeWindow.ShowOnStartupIfNeeded(writtenConfigs);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChievFX MCP could not auto-write MCP client configs. {ex.Message}");
            }
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

        private static void EnforceExperimentalVisibilitySafely()
        {
            try
            {
                ChievfxMcpWindow.EnforceExperimentalVisibility();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChievFX MCP experimental visibility enforcement failed. {ex.Message}");
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

        // Cursor shares MCP snapshot state (including initialize.instructions) by
        // server name across every open project/window. When multiple projects use
        // the same bare name, one project's instructions overwrite the others'
        // cached INSTRUCTIONS.md. A per-project suffix keeps each project's MCP
        // server distinct so Cursor cannot collide them. Kept short ("unity-<hash>")
        // because Cursor prefixes tool names with the server name, and overly long
        // fully-qualified tool names get filtered out. Used only as the mcp.json
        // key; EditorPrefs keys stay on the stable bare ServerName.
        public static string CursorServerName => $"unity-{ProjectKeySuffix()}";

        // True for any Cursor mcp.json key this package has ever written for the
        // current project (current short form, the legacy bare name, and the prior
        // "unity-mcp-chievfx-<hash>" form) so writing config migrates old entries.
        public static bool IsManagedCursorServerName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            return string.Equals(name, CursorServerName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, ServerName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, $"{ServerName}-{ProjectKeySuffix()}", StringComparison.OrdinalIgnoreCase);
        }

        // Matches the current "unity-<hash>" form for the SHA of ANY project copy plus the
        // legacy "unity-mcp-chievfx-<hash>" form, in addition to this project's managed names.
        // Used when writing config so a stale copy from another project path (wrong SHA) is
        // removed, leaving only the correct entry for the current project.
        private static readonly Regex ProjectServerNamePattern =
            new(@"^unity-[0-9a-fA-F]{8}$", RegexOptions.Compiled);

        private static readonly Regex LegacyProjectServerNamePattern =
            new(@"^unity-mcp-chievfx-[0-9a-fA-F]{8}$", RegexOptions.Compiled);

        public static bool IsChievfxManagedServerName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            return IsManagedCursorServerName(name)
                || ProjectServerNamePattern.IsMatch(name)
                || LegacyProjectServerNamePattern.IsMatch(name);
        }

        public const string ShowWelcomeOnStartupKey = ServerName + ".showWelcomeOnStartup";

        // Default ON: allow the Welcome window to surface automatically — only when MCP client
        // configs were just (re)written (initial setup of the plugin) or when setup is unhealthy.
        // A healthy, already-configured project never pops it.
        public static bool ShowWelcomeOnStartup => EditorPrefs.GetBool(ShowWelcomeOnStartupKey, true);

        public const string AutoWriteClientConfigsKey = ServerName + ".autoWriteClientConfigs";

        // Default ON: on each Unity open, write the MCP config for every supported client
        // (Cursor, Claude Code, Codex, Kimi Code) for this project copy when it is missing,
        // stale, or points at a different copy, so the project is always ready without a manual
        // "Write Config".
        public static bool AutoWriteClientConfigs => EditorPrefs.GetBool(AutoWriteClientConfigsKey, true);

        public const string ManualToolResourceSelectionKey = ServerName + ".manualToolResourceSelection";

        // Default OFF: expose every non-hidden tool and resource; the Tools/Resources tabs are read-only
        // and the Presets tab is hidden. ON: the user hand-picks tools, resources, and categories (Presets
        // tab + per-item toggles), and the saved selection is honored. Mirrored to
        // UserSettings/ChievfxMcpAvailability.json for the Python server, which can't read EditorPrefs.
        public static bool ManualToolResourceSelection => EditorPrefs.GetBool(ManualToolResourceSelectionKey, false);

        public const string PackageName = "com.chievfx.mcp";

        public static readonly string[] RequiredToolIds = LoadRequiredToolIds();

        public static readonly string[] DefaultEnabledToolIds =
        {
        };

        public static readonly string[] AutonomousToolIds =
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

        public const int DefaultTimeoutMs = 120000;

        public const string AutoReloadExternallyChangedScenesKey = ServerName + ".autoReloadExternallyChangedScenes";

        public static bool AutoReloadExternallyChangedScenes => EditorPrefs.GetBool(AutoReloadExternallyChangedScenesKey, false);

        public const string AutoReloadCursorOnAvailabilityChangeKey = ServerName + ".autoReloadCursorOnAvailabilityChange";

        // Default ON: when tool/resource/prompt availability changes, drop a reload signal
        // file so the reload-mcps extension reconnects Cursor's MCP client. Cursor only
        // re-reads initialize.instructions on a handshake, so without a reconnect the live
        // edit never reaches the agent's instructions.
        public static bool AutoReloadCursorOnAvailabilityChange => EditorPrefs.GetBool(AutoReloadCursorOnAvailabilityChangeKey, true);

        public const string StripStyleTagsFromConsoleLogsKey = ServerName + ".stripStyleTagsFromConsoleLogs";

        // Default ON: strip rich-text <b>/<color> style tags from console-get-logs and
        // console-get-logs-single messages so the agent sees clean text instead of Unity's
        // markup. Only tags with both opening and closing present are removed.
        public static bool StripStyleTagsFromConsoleLogs => EditorPrefs.GetBool(StripStyleTagsFromConsoleLogsKey, true);

        public const string UseSystemPythonKey = ServerName + ".useSystemPython";

        // Default OFF: use the managed portable CPython under ~/.chievfx-mcp/env/.
        // Dev-only escape hatch to prefer a system interpreter instead.
        public static bool UseSystemPython => EditorPrefs.GetBool(UseSystemPythonKey, false);

        public static string BridgeUrl => $"http://127.0.0.1:{DefaultBridgePort}";

        public static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ProjectKeySuffix()
        {
            using var sha1 = SHA1.Create();
            var hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(ProjectRoot));
            var builder = new StringBuilder(8);
            for (var i = 0; i < 4; i++)
            {
                builder.Append(hash[i].ToString("x2"));
            }

            return builder.ToString();
        }

        public static string PackageRoot => ResolvePackageRoot();

        // "Tools~" (trailing tilde) keeps the Python server tree out of Unity's asset
        // importer, so runtime-generated __pycache__/*.pyc files never trip a Player build
        // ("has no meta file, but it's in an immutable folder"). It is still a real folder
        // on disk, so File.Combine paths below resolve normally.
        public static string PackageToolsDirectory => Path.Combine(PackageRoot, "Tools~", "ChievfxMcp");

        public static string BridgeDirectory => Path.Combine(ProjectRoot, "Library", "ChievfxMcpBridge");

        public static string BridgeRequestDirectory => Path.Combine(BridgeDirectory, "requests");

        public static string BridgeResponseDirectory => Path.Combine(BridgeDirectory, "responses");

        public static string BridgeOperationDirectory => Path.Combine(BridgeDirectory, "operations");

        public static string BridgeCancelDirectory => Path.Combine(BridgeDirectory, "cancel");

        public static string BridgeStatePath => Path.Combine(BridgeDirectory, "state.json");

        public static string BridgeEventPath => Path.Combine(BridgeDirectory, "events.json");

        public static string ExtensionCapabilitySnapshotPath => Path.Combine(BridgeDirectory, "extension-capabilities.snapshot.json");

        // Mirror of the ManualToolResourceSelection toggle for the Python server (it can't read
        // EditorPrefs). Written idempotently on load and whenever the toggle changes.
        public static string AvailabilitySettingsPath => Path.Combine(ProjectRoot, "UserSettings", "ChievfxMcpAvailability.json");

        public static void WriteAvailabilitySettings()
        {
            try
            {
                var path = AvailabilitySettingsPath;
                var content = "{\n  \"manualSelection\": " + (ManualToolResourceSelection ? "true" : "false") + "\n}\n";
                if (File.Exists(path) && string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
                {
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChievFX MCP could not write availability settings. {ex.Message}");
            }
        }

        public static string CursorConfigPath => Path.Combine(ProjectRoot, ".cursor", "mcp.json");

        public static string ClaudeCodeConfigPath => Path.Combine(ProjectRoot, ".mcp.json");

        public static string CodexConfigPath => Path.Combine(ProjectRoot, ".codex", "config.toml");

        public static string KimiCodeConfigPath => Path.Combine(ProjectRoot, ".kimi-code", "mcp.json");

        // Watched by the reload-mcps extension (when its file-reload setting is on). Writing
        // {"serverName": CursorServerName} here asks Cursor to reload just this project's MCP.
        public static string CursorReloadSignalPath => Path.Combine(ProjectRoot, ".cursor", "reload-mcps.json");

        public static string ServerScriptPath => Path.Combine(PackageToolsDirectory, "chievfx_mcp_server.py");

        // Stable, hash-independent entry point that MCP client configs point at. The real server lives
        // under Library/PackageCache/com.chievfx.mcp@<hash>/ whose hash changes on every package
        // re-resolution; baking that path into a config breaks the client. This launcher (written to the
        // bridge dir, a fixed project path) resolves the current server at launch time instead.
        public static string LauncherScriptPath => Path.Combine(BridgeDirectory, "launch_server.py");

        // "Install~" is hidden from Unity's importer for the same reason as Tools~ above.
        public static string InstallerDirectory => Path.Combine(PackageRoot, "Install~");

        public static string InstallerScriptPath => Path.Combine(InstallerDirectory, "chievfx_mcp_installer.py");

        public static string RequirementsPath => Path.Combine(PackageRoot, "requirements.txt");

        public static bool TryResolveInstallerScriptPath(out string path)
        {
            path = InstallerScriptPath;
            return File.Exists(path);
        }

        public static string ToolPolicyPath => Path.Combine(PackageToolsDirectory, "chievfx_mcp_tool_policy.json");

        public static string ToolRolePresetsPath => Path.Combine(PackageToolsDirectory, "chievfx_mcp_role_presets.json");

        public static string ToolSelectionPath => Path.Combine(ProjectRoot, "UserSettings", "ChievfxMcpToolSelection.json");

        public static string ToolRoleAssetDefaultDirectory => "Assets/ChievfxMcp/Roles";

        public static string ResourceSelectionPath => Path.Combine(ProjectRoot, "UserSettings", "ChievfxMcpResourceSelection.json");

        public static string PromptSelectionPath => Path.Combine(ProjectRoot, "UserSettings", "ChievfxMcpPromptSelection.json");

        public static string CategorySelectionPath => Path.Combine(ProjectRoot, "UserSettings", "ChievfxMcpCategorySelection.json");

        public static string DebugSettingsPath => Path.Combine(ProjectRoot, "UserSettings", "ChievfxMcpDebugSettings.json");

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
                "asset-find",
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
