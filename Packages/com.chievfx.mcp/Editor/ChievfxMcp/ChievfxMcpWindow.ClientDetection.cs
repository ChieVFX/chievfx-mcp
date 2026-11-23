#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using static Chievfx.Mcp.Editor.ChievfxMcpSelectionUi;

namespace Chievfx.Mcp.Editor
{
    internal sealed partial class ChievfxMcpWindow
    {
        private static McpClientInfo GetClientInfo(string client)
        {
            if (client == ClientClaudeCode)
            {
                return new McpClientInfo(
                    ClientClaudeCode,
                    ClaudeCodeConfigPath,
                    "Claude Code reads project MCP servers from .mcp.json on session start. Project-scoped servers may need one-time approval from /mcp.",
                    McpClientConfigFormat.JsonMcpServers,
                    true,
                    "claude",
                    "Claude CLI found",
                    "Claude CLI missing");
            }

            if (client == ClientCodex)
            {
                return new McpClientInfo(
                    ClientCodex,
                    CodexConfigPath,
                    "Codex reads project MCP servers from .codex/config.toml only while the project is trusted in your user-level Codex config. Restart Codex after writing config.",
                    McpClientConfigFormat.CodexToml,
                    true,
                    "codex",
                    "Codex CLI found",
                    "Codex CLI missing");
            }

            if (client == ClientKimiCode)
            {
                // Kimi Code's documented mcp.json schema has no "type" field; stdio is implied
                // by command/args and HTTP by url, so the entry is written without it.
                return new McpClientInfo(
                    ClientKimiCode,
                    KimiCodeConfigPath,
                    "Kimi Code reads project MCP servers from .kimi-code/mcp.json on session start. Run /mcp in a session to check server status.",
                    McpClientConfigFormat.JsonMcpServers,
                    true,
                    "kimi",
                    "Kimi CLI found",
                    "Kimi CLI missing",
                    includeTypeField: false);
            }

            if (client == ClientRider)
            {
                // Same {"mcpServers": {...}} shape as Cursor/Claude Code, with "type" accepted
                // (the plugin's bundled schema documents stdio/http/sse).
                return new McpClientInfo(
                    ClientRider,
                    RiderConfigPath,
                    "Rider AI Assistant and Junie read project MCP servers from .ai/mcp/mcp.json. A server added or changed on disk stays disabled until you accept the IDE's \"Enable Servers\" notification, or turn on Settings | Tools | AI Assistant | Model Context Protocol (MCP) → \"Automatically enable new and changed MCP servers\".",
                    McpClientConfigFormat.JsonMcpServers,
                    true,
                    "rider",
                    "Rider found",
                    "Rider missing");
            }

            return new McpClientInfo(
                ClientCursor,
                CursorConfigPath,
                "Write config after changing connection settings. Reload MCP tools or restart Cursor afterward.",
                McpClientConfigFormat.JsonMcpServers,
                false,
                null,
                "Cursor selected",
                "Cursor unavailable");
        }

        private static bool IsClientAvailable(McpClientInfo clientInfo)
        {
            if (!clientInfo.RequiresToolProbe || string.IsNullOrWhiteSpace(clientInfo.ProbeExecutableName))
            {
                return true;
            }

            if (IsExecutableAvailable(clientInfo.ProbeExecutableName!))
            {
                return true;
            }

            // Per-client fallbacks for installs an executable probe cannot see.
            if (string.Equals(clientInfo.ProbeExecutableName, "claude", StringComparison.OrdinalIgnoreCase))
            {
                // Claude Code's Store (MSIX) build installs under a package-redirected AppData
                // path that non-packaged processes like the Unity editor cannot see, so a plain
                // executable probe fails even when it is installed. Fall back to signals that are
                // not affected by package redirection.
                return IsClaudeCodeInstalled();
            }

            if (string.Equals(clientInfo.ProbeExecutableName, "rider", StringComparison.OrdinalIgnoreCase))
            {
                return IsRiderInstalled();
            }

            return false;
        }

        // Rider is normally installed through JetBrains Toolbox or as a plain app bundle and puts
        // nothing on PATH, so the executable probe above rarely fires. Every build writes a
        // per-version settings directory (JetBrains/Rider<version>) on first run — the same place
        // the MCP plugin keeps its global server list — so that is the reliable signal.
        private static bool IsRiderInstalled()
        {
            foreach (var root in JetBrainsSettingsRoots())
            {
                try
                {
                    if (Directory.Exists(root) && Directory.GetDirectories(root, "Rider*").Length > 0)
                    {
                        return true;
                    }
                }
                catch
                {
                    // Unreadable candidate root; try the next one.
                }
            }

            return false;
        }

        private static List<string> JetBrainsSettingsRoots()
        {
            var roots = new List<string>();
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var appData = Environment.GetEnvironmentVariable("APPDATA");
                    if (!string.IsNullOrWhiteSpace(appData))
                    {
                        roots.Add(Path.Combine(appData!, "JetBrains"));
                    }

                    return roots;
                }

                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrWhiteSpace(home))
                {
                    return roots;
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    roots.Add(Path.Combine(home, "Library", "Application Support", "JetBrains"));
                    return roots;
                }

                var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                roots.Add(string.IsNullOrWhiteSpace(xdgConfig)
                    ? Path.Combine(home, ".config", "JetBrains")
                    : Path.Combine(xdgConfig!, "JetBrains"));
            }
            catch (PlatformNotSupportedException)
            {
                // No user profile available; treat as "cannot tell".
            }

            return roots;
        }

        private static bool IsClaudeCodeInstalled()
        {
            // Claude Code creates a ~/.claude home directory on first run. It is a plain
            // user-profile directory, unaffected by MSIX package redirection.
            var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
            if (string.IsNullOrWhiteSpace(userProfile))
            {
                try
                {
                    userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                }
                catch (PlatformNotSupportedException)
                {
                    userProfile = null;
                }
            }

            if (!string.IsNullOrWhiteSpace(userProfile) && Directory.Exists(Path.Combine(userProfile!, ".claude")))
            {
                return true;
            }

            // Definitive when Claude Code (or the desktop app's bundled CLI) is running.
            try
            {
                var processes = Process.GetProcessesByName("claude");
                try
                {
                    if (processes.Length > 0)
                    {
                        return true;
                    }
                }
                finally
                {
                    foreach (var process in processes)
                    {
                        process.Dispose();
                    }
                }
            }
            catch
            {
                // Process enumeration can fail under restricted environments; ignore.
            }

            return false;
        }

        private static bool IsExecutableAvailable(string executableName)
        {
            foreach (var candidate in EnumerateExecutableCandidates(executableName))
            {
                if (File.Exists(candidate))
                {
                    return true;
                }
            }

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = executableName,
                        Arguments = "--version",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    },
                };
                process.Start();
                if (!process.WaitForExit(2000))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                        // Ignore kill failures on a probe process.
                    }

                    return false;
                }

                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<string> EnumerateExecutableCandidates(string executableName)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void YieldPath(List<string> results, string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                var trimmed = path!.Trim().Trim('"');
                if (seen.Add(trimmed))
                {
                    results.Add(trimmed);
                }
            }

            var results = new List<string>();
            var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var directory in pathVariable.Split(Path.PathSeparator))
            {
                foreach (var candidate in ExecutablePathVariants(Path.Combine(directory, executableName)))
                {
                    YieldPath(results, candidate);
                }
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Resolve well-known roots from environment variables first because Unity's
                // Mono returns inconsistent results from Environment.GetFolderPath; fall back
                // to GetFolderPath so detection still works if a variable is unset.
                var appDataRoots = ResolveWindowsRoots("APPDATA", Environment.SpecialFolder.ApplicationData, Path.Combine("AppData", "Roaming"));
                var localAppDataRoots = ResolveWindowsRoots("LOCALAPPDATA", Environment.SpecialFolder.LocalApplicationData, Path.Combine("AppData", "Local"));

                foreach (var appData in appDataRoots)
                {
                    foreach (var candidate in ExecutablePathVariants(Path.Combine(appData, "npm", executableName)))
                    {
                        YieldPath(results, candidate);
                    }
                }

                // The native Claude Code installer (and the desktop-bundled CLI) places the
                // executable under a versioned directory that is not added to PATH, e.g.
                // %APPDATA%\Claude\claude-code\<version>\claude.exe. Scan those roots so the
                // client is detected even without a PATH shim.
                if (string.Equals(executableName, "claude", StringComparison.OrdinalIgnoreCase))
                {
                    var installRoots = new List<string>();
                    foreach (var appData in appDataRoots)
                    {
                        installRoots.Add(Path.Combine(appData, "Claude", "claude-code"));
                        installRoots.Add(Path.Combine(appData, "Claude", "claude-code-vm"));
                    }

                    foreach (var localAppData in localAppDataRoots)
                    {
                        installRoots.Add(Path.Combine(localAppData, "Claude", "claude-code"));
                        installRoots.Add(Path.Combine(localAppData, "Claude", "claude-code-vm"));
                    }

                    foreach (var root in installRoots)
                    {
                        foreach (var candidate in EnumerateVersionedExecutables(root, executableName))
                        {
                            YieldPath(results, candidate);
                        }
                    }
                }
            }
            else
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                YieldPath(results, $"/opt/homebrew/bin/{executableName}");
                YieldPath(results, $"/usr/local/bin/{executableName}");
                YieldPath(results, $"/usr/bin/{executableName}");
                YieldPath(results, Path.Combine(home, ".npm-global", "bin", executableName));
                YieldPath(results, Path.Combine(home, ".local", "bin", executableName));
            }

            return results;
        }

        private static IEnumerable<string> ExecutablePathVariants(string basePath)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                yield return basePath;
                yield break;
            }

            yield return basePath;
            yield return basePath + ".cmd";
            yield return basePath + ".exe";
            yield return basePath + ".bat";
        }

        private static IReadOnlyList<string> ResolveWindowsRoots(string environmentVariable, Environment.SpecialFolder specialFolder, string userProfileRelative)
        {
            var roots = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string? value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                var trimmed = value!.Trim();
                if (seen.Add(trimmed))
                {
                    roots.Add(trimmed);
                }
            }

            Add(Environment.GetEnvironmentVariable(environmentVariable));
            try
            {
                Add(Environment.GetFolderPath(specialFolder));
            }
            catch (PlatformNotSupportedException)
            {
                // GetFolderPath can throw under some Mono configurations; other sources cover us.
            }

            // Unity's Mono runtime frequently leaves APPDATA/LOCALAPPDATA unset and returns
            // empty strings from GetFolderPath, so derive the folder from USERPROFILE (which is
            // reliably present) as a final fallback.
            var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
            if (string.IsNullOrWhiteSpace(userProfile))
            {
                try
                {
                    userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                }
                catch (PlatformNotSupportedException)
                {
                    userProfile = null;
                }
            }

            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                Add(Path.Combine(userProfile!, userProfileRelative));
            }

            return roots;
        }

        private static IEnumerable<string> EnumerateVersionedExecutables(string root, string executableName)
        {
            string[] versionDirectories;
            try
            {
                versionDirectories = Directory.Exists(root)
                    ? Directory.GetDirectories(root)
                    : Array.Empty<string>();
            }
            catch (IOException)
            {
                versionDirectories = Array.Empty<string>();
            }
            catch (UnauthorizedAccessException)
            {
                versionDirectories = Array.Empty<string>();
            }

            foreach (var versionDirectory in versionDirectories)
            {
                foreach (var candidate in ExecutablePathVariants(Path.Combine(versionDirectory, executableName)))
                {
                    yield return candidate;
                }
            }
        }

        private static bool IsSameLocalHttpEndpoint(string? url, int port)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            return (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) || uri.Host == "127.0.0.1")
                && uri.Port == port;
        }
    }
}
