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
        private static void WriteClientConfig(McpClientInfo clientInfo, string transport, int port, int timeout)
        {
            // The config points at the stable launcher; make sure it exists before we reference it.
            ChievfxMcpServerLauncher.EnsureLauncherWritten();
            var content = BuildClientConfigPreview(clientInfo, transport, port, timeout);

            // Never rewrite a file whose content is already equivalent. Touching a config file makes
            // clients like Cursor re-register their MCP servers and drop a live connection (e.g. right as
            // a Play Mode domain reload runs). JSON is compared semantically so a formatting- or
            // key-order-only difference never triggers a write.
            if (WrittenContentMatchesExisting(clientInfo, content))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(clientInfo.ConfigPath)!);
            File.WriteAllText(clientInfo.ConfigPath, content, new UTF8Encoding(false));
        }

        private static bool WrittenContentMatchesExisting(McpClientInfo clientInfo, string newContent)
        {
            try
            {
                if (!File.Exists(clientInfo.ConfigPath))
                {
                    return false;
                }

                var existing = File.ReadAllText(clientInfo.ConfigPath);
                if (clientInfo.Format == McpClientConfigFormat.CodexToml)
                {
                    return string.Equals(NormalizeConfigText(existing), NormalizeConfigText(newContent), StringComparison.Ordinal);
                }

                return JToken.DeepEquals(JToken.Parse(existing), JToken.Parse(newContent));
            }
            catch
            {
                return false;
            }
        }

        private static string BuildClientConfigPreview(McpClientInfo clientInfo, string transport, int port, int timeout)
        {
            if (clientInfo.Format == McpClientConfigFormat.CodexToml)
            {
                return BuildCodexConfigPreview(transport, port, timeout);
            }

            var mcpServers = new JObject();
            foreach (var existingServer in ReadExistingServersForPreview(clientInfo.ConfigPath, port))
            {
                mcpServers[existingServer.Name] = existingServer.Value;
            }

            foreach (var expected in BuildExpectedManagedServerEntries(transport, port, timeout, clientInfo.IncludeTypeField))
            {
                mcpServers[expected.Key] = expected.Value;
            }

            var root = new JObject { ["mcpServers"] = mcpServers };
            return root.ToString(Formatting.Indented);
        }

        private static string BuildCodexConfigPreview(string transport, int port, int timeout)
        {
            var existing = File.Exists(CodexConfigPath)
                ? RemoveManagedCodexServerSections(File.ReadAllText(CodexConfigPath), port).TrimEnd()
                : string.Empty;
            if (string.IsNullOrWhiteSpace(existing))
            {
                existing = string.Empty;
            }

            var builder = new StringBuilder(existing);
            foreach (var block in BuildExpectedCodexServerBlocks(transport, port, timeout))
            {
                if (builder.Length > 0)
                {
                    builder.Append(Environment.NewLine).Append(Environment.NewLine);
                }

                builder.Append(block.Value);
            }

            return builder.ToString();
        }

        // Server name → section text: this project's first, then one per secondary project in the order
        // they were added.
        private static List<KeyValuePair<string, string>> BuildExpectedCodexServerBlocks(string transport, int port, int timeout)
        {
            var blocks = new List<KeyValuePair<string, string>>
            {
                new(ChievfxMcpToolPolicy.CursorServerName, BuildExpectedCodexServerBlock(transport, port, timeout))
            };

            foreach (var project in ChievfxMcpSecondaryProjects.Load())
            {
                if (ChievfxMcpSecondaryProjects.IsSamePath(project.ProjectRoot, ProjectRoot))
                {
                    continue;
                }

                ChievfxMcpSecondaryProjects.EnsureLauncherWritten(project);
                blocks.Add(new KeyValuePair<string, string>(project.ServerName, BuildSecondaryCodexServerBlock(project, timeout)));
            }

            return blocks;
        }

        // How many [mcp_servers.*] sections in the file are ones this package owns, by the same test the
        // rewrite uses to strip them.
        private static int CountManagedCodexSections(string text, int port)
        {
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var count = 0;
            var index = 0;
            while (index < lines.Length)
            {
                if (!IsTomlSectionHeader(lines[index]))
                {
                    index++;
                    continue;
                }

                var start = index;
                index++;
                while (index < lines.Length && !IsTomlSectionHeader(lines[index]))
                {
                    index++;
                }

                if (ShouldSkipCodexSection(string.Join("\n", lines, start, index - start), port))
                {
                    count++;
                }
            }

            return count;
        }

        private static string BuildExpectedCodexServerBlock(string transport, int port, int timeout)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"[mcp_servers.{TomlQuotedKey(ChievfxMcpToolPolicy.CursorServerName)}]");
            if (transport == TransportHttp)
            {
                builder.AppendLine($"url = {TomlString(HttpUrl(port))}");
            }
            else
            {
                var args = TomlArray(new[]
                {
                    ChievfxMcpToolPolicy.LauncherScriptPath,
                    "--transport",
                    TransportStdio,
                    "--project-root",
                    ProjectRoot,
                    "--bridge-dir",
                    ChievfxMcpToolPolicy.BridgeDirectory,
                    "--timeout",
                    timeout.ToString()
                });
                builder.AppendLine($"command = {TomlString(ChievfxMcpPythonLauncher.ExecutablePath)}");
                builder.AppendLine($"args = {args}");
            }

            builder.AppendLine($"tool_timeout_sec = {Mathf.CeilToInt(timeout / 1000f)}");
            return builder.ToString().TrimEnd();
        }

        private static string BuildSecondaryCodexServerBlock(ChievfxMcpSecondaryProject project, int timeout)
        {
            var args = TomlArray(new[]
            {
                project.LauncherScriptPath,
                "--transport",
                TransportStdio,
                "--project-root",
                project.ProjectRoot,
                "--bridge-dir",
                project.BridgeDirectory,
                "--timeout",
                timeout.ToString()
            });

            var builder = new StringBuilder();
            builder.AppendLine($"[mcp_servers.{TomlQuotedKey(project.ServerName)}]");
            builder.AppendLine($"command = {TomlString(ChievfxMcpPythonLauncher.ExecutablePath)}");
            builder.AppendLine($"args = {args}");

            // Env var names are bare TOML keys (letters, digits, underscore), so the key needs no quoting.
            builder.AppendLine(
                $"env = {{ {ChievfxMcpSecondaryProjects.ServerLabelEnvironmentVariable} = {TomlString(project.Label)} }}");
            builder.AppendLine($"tool_timeout_sec = {Mathf.CeilToInt(timeout / 1000f)}");
            return builder.ToString().TrimEnd();
        }

        private static string TomlArray(IEnumerable<string> values)
        {
            var builder = new StringBuilder("[");
            var first = true;
            foreach (var value in values)
            {
                if (!first)
                {
                    builder.Append(", ");
                }

                builder.Append(TomlString(value));
                first = false;
            }

            builder.Append(']');
            return builder.ToString().TrimEnd();
        }

        private static string TomlQuotedKey(string value)
        {
            return TomlString(value);
        }

        private static string TomlString(string value)
        {
            return "\"" + value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t") + "\"";
        }

        private static string RemoveManagedCodexServerSections(string text, int port)
        {
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var output = new StringBuilder();
            var index = 0;
            while (index < lines.Length)
            {
                var line = lines[index];
                if (!IsTomlSectionHeader(line))
                {
                    output.AppendLine(line);
                    index++;
                    continue;
                }

                var start = index;
                index++;
                while (index < lines.Length && !IsTomlSectionHeader(lines[index]))
                {
                    index++;
                }

                var block = string.Join("\n", lines, start, index - start);
                if (ShouldSkipCodexSection(block, port))
                {
                    continue;
                }

                output.AppendLine(block);
            }

            return output.ToString();
        }

        private static bool IsTomlSectionHeader(string line)
        {
            var trimmed = line.Trim();
            return trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal);
        }

        private static bool ShouldSkipCodexSection(string block, int port)
        {
            var firstLineEnd = block.IndexOf('\n');
            var header = firstLineEnd >= 0 ? block.Substring(0, firstLineEnd).Trim() : block.Trim();
            if (!header.StartsWith("[mcp_servers.", StringComparison.Ordinal))
            {
                return false;
            }

            if (header.Contains(ChievfxMcpToolPolicy.CursorServerName, StringComparison.Ordinal)
                || header.Contains(ChievfxMcpToolPolicy.ServerName, StringComparison.Ordinal)
                || ManagedCodexHeaderPattern.IsMatch(header))
            {
                return true;
            }

            return block.Contains(ServerScriptPath, StringComparison.Ordinal)
                || block.Contains(HttpUrl(port), StringComparison.Ordinal)
                || block.Contains("chievfx_mcp_server.py", StringComparison.Ordinal);
        }

        // Pulls this project's own [mcp_servers.<name>] section out of a Codex config, quoted
        // ([mcp_servers."unity-<hash>"]) or bare, ignoring everything else in the file.
        private static bool TryExtractCodexSection(string text, string serverName, out string section)
        {
            section = string.Empty;
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var quotedHeader = $"[mcp_servers.{TomlString(serverName)}]";
            var bareHeader = $"[mcp_servers.{serverName}]";
            var index = 0;
            while (index < lines.Length)
            {
                var trimmed = lines[index].Trim();
                if (IsTomlSectionHeader(lines[index])
                    && (string.Equals(trimmed, quotedHeader, StringComparison.Ordinal)
                        || string.Equals(trimmed, bareHeader, StringComparison.Ordinal)))
                {
                    var start = index;
                    index++;
                    while (index < lines.Length && !IsTomlSectionHeader(lines[index]))
                    {
                        index++;
                    }

                    section = string.Join("\n", lines, start, index - start);
                    return true;
                }

                index++;
            }

            return false;
        }

        // Order-independent equality of two Codex sections by their BODY lines (the header is excluded —
        // extraction already matched it, and Codex may rewrite our quoted [mcp_servers."unity-<hash>"]
        // to the equivalent bare [mcp_servers.unity-<hash>] since the hash is a valid bare key). Tolerates
        // key reordering and blank-line/whitespace differences so we only rewrite when content truly differs.
        private static bool CodexSectionMatches(string actual, string expected)
        {
            return CodexSectionBodyLineSet(actual).SetEquals(CodexSectionBodyLineSet(expected));
        }

        private static HashSet<string> CodexSectionBodyLineSet(string block)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in block.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                if (IsTomlSectionHeader(line))
                {
                    continue;
                }

                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    set.Add(trimmed);
                }
            }

            return set;
        }

        // Matches [mcp_servers."unity-<hash>"] / [mcp_servers.unity-<hash>] for any project copy
        // (any SHA) and the legacy unity-mcp-chievfx-<hash> form, so a stale copy's Codex section
        // is removed when writing config.
        private static readonly System.Text.RegularExpressions.Regex ManagedCodexHeaderPattern =
            new(
                @"^\[mcp_servers\.""?(?:unity-[0-9a-fA-F]{8}|unity-mcp-chievfx(?:-[0-9a-fA-F]{8})?)""?\]$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // Every entry this package owns in a client config, keyed by server name: this project's own,
        // then one per secondary project. Written together and compared together, so adding, editing, or
        // removing a secondary project is what makes a config stale.
        private static Dictionary<string, JObject> BuildExpectedManagedServerEntries(
            string transport,
            int port,
            int timeout,
            bool includeTypeField)
        {
            var entries = new Dictionary<string, JObject>(StringComparer.Ordinal)
            {
                [ChievfxMcpToolPolicy.CursorServerName] = BuildExpectedServerEntry(transport, port, timeout, includeTypeField)
            };

            foreach (var project in ChievfxMcpSecondaryProjects.Load())
            {
                if (ChievfxMcpSecondaryProjects.IsSamePath(project.ProjectRoot, ProjectRoot))
                {
                    continue;
                }

                ChievfxMcpSecondaryProjects.EnsureLauncherWritten(project);
                entries[project.ServerName] = BuildSecondaryServerEntry(project, timeout, includeTypeField);
            }

            return entries;
        }

        // Always stdio, whatever this project's transport is: the HTTP server binds one port and belongs to
        // this project's process, so a second project cannot share it. The client spawns the other
        // project's launcher instead, which runs that project's own package copy.
        private static JObject BuildSecondaryServerEntry(
            ChievfxMcpSecondaryProject project,
            int timeout,
            bool includeTypeField)
        {
            var server = new JObject();
            if (includeTypeField)
            {
                server["type"] = TransportStdio;
            }

            server["command"] = ChievfxMcpPythonLauncher.ExecutablePath;
            server["args"] = new JArray(
                project.LauncherScriptPath,
                "--transport",
                TransportStdio,
                "--project-root",
                project.ProjectRoot,
                "--bridge-dir",
                project.BridgeDirectory,
                "--timeout",
                timeout.ToString());
            server["env"] = new JObject
            {
                [ChievfxMcpSecondaryProjects.ServerLabelEnvironmentVariable] = project.Label
            };
            return server;
        }

        private static JObject BuildExpectedServerEntry(string transport, int port, int timeout, bool includeTypeField)
        {
            var server = new JObject();
            if (transport == TransportHttp)
            {
                if (includeTypeField)
                {
                    server["type"] = TransportHttp;
                }

                server["url"] = HttpUrl(port);
                // The HTTP handler demands the shared key once remote-cmd is armed or the port is bound
                // to a network address, so the entry carries it: without this the client's very first
                // request comes back 401 and looks like a dead server.
                if (ChievfxMcpToolPolicy.RemoteCmdRequiresClientKey)
                {
                    server["headers"] = new JObject
                    {
                        [ChievfxMcpToolPolicy.RemoteCmdSecurityKeyHeader] = ChievfxMcpToolPolicy.RemoteCmdSecurityKey
                    };
                }

                return server;
            }

            if (includeTypeField)
            {
                server["type"] = TransportStdio;
            }

            server["command"] = ChievfxMcpPythonLauncher.ExecutablePath;
            server["args"] = new JArray(
                ChievfxMcpToolPolicy.LauncherScriptPath,
                "--transport",
                TransportStdio,
                "--project-root",
                ProjectRoot,
                "--bridge-dir",
                ChievfxMcpToolPolicy.BridgeDirectory,
                "--timeout",
                timeout.ToString());
            return server;
        }

        private static List<CursorServerConfig> ReadExistingServersForPreview(string configPath, int port)
        {
            var servers = new List<CursorServerConfig>();
            if (!File.Exists(configPath))
            {
                return servers;
            }

            try
            {
                var root = JToken.Parse(File.ReadAllText(configPath));
                if (root is not JObject rootObj
                    || rootObj["mcpServers"] is not JObject mcpServers)
                {
                    return servers;
                }

                foreach (var server in mcpServers.Properties())
                {
                    if (ShouldSkipExistingServer(server.Name, server.Value, port))
                    {
                        continue;
                    }

                    servers.Add(new CursorServerConfig(server.Name, server.Value.DeepClone()));
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChievFX MCP could not parse existing MCP client config. Preview will replace it. {ex.Message}");
            }

            return servers;
        }

        private static bool ShouldSkipExistingServer(string name, JToken value, int port)
        {
            if (ChievfxMcpToolPolicy.IsChievfxManagedServerName(name))
            {
                // Skip the current project-unique entry (rewritten below), any legacy entry
                // this package wrote, and any stale entry left by another project copy (a
                // "unity-<hash>" key with a different SHA), so writing config migrates them
                // and leaves only the correct entry for this project.
                return true;
            }

            return IsSameProjectLocalMcpServer(value, port) || ReferencesChievfxServerScript(value);
        }

        // True when the entry points at a chievfx_mcp_server.py at any path (e.g. a copied
        // project whose script lives elsewhere), so it is recognised as our server and removed.
        private static bool ReferencesChievfxServerScript(JToken server)
        {
            if (server is not JObject serverObj)
            {
                return false;
            }

            if (serverObj["command"]?.Type == JTokenType.String
                && PathEndsWithServerScript(serverObj["command"]!.Value<string>()))
            {
                return true;
            }

            if (serverObj["args"] is JArray argsArray)
            {
                foreach (var item in argsArray)
                {
                    if (item.Type == JTokenType.String && PathEndsWithServerScript(item.Value<string>()))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool PathEndsWithServerScript(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var normalized = path!.Replace('\\', '/');
            return normalized.EndsWith("/chievfx_mcp_server.py", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "chievfx_mcp_server.py", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSameProjectLocalMcpServer(JToken server, int port)
        {
            if (server is not JObject serverObj)
            {
                return false;
            }

            var commandElement = serverObj["command"];
            if (commandElement?.Type == JTokenType.String
                && IsSamePath(commandElement.Value<string>(), ServerScriptPath))
            {
                return true;
            }

            if (serverObj["args"] is JToken argsElement
                && ArgsContainPath(argsElement, ServerScriptPath))
            {
                return true;
            }

            if (commandElement?.Type == JTokenType.String
                && IsSameProjectUpstreamUnityServer(commandElement.Value<string>()))
            {
                return true;
            }

            var urlElement = serverObj["url"];
            if (urlElement?.Type == JTokenType.String
                && IsSameLocalHttpEndpoint(urlElement.Value<string>(), port))
            {
                return true;
            }

            return false;
        }

        private static bool ArgsContainPath(JToken argsElement, string path)
        {
            if (argsElement is not JArray argsArray)
            {
                return false;
            }

            foreach (var item in argsArray)
            {
                if (item.Type == JTokenType.String && IsSamePath(item.Value<string>(), path))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsSameProjectUpstreamUnityServer(string? command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return false;
            }

            var normalizedCommand = command!.Replace('\\', '/');
            var normalizedProjectRoot = ProjectRoot.Replace('\\', '/');
            return normalizedCommand.StartsWith(normalizedProjectRoot, StringComparison.Ordinal)
                && normalizedCommand.Contains("/Library/mcp-server/", StringComparison.Ordinal)
                && normalizedCommand.EndsWith("/unity-mcp-server", StringComparison.Ordinal);
        }

        private static bool IsSamePath(string? first, string second)
        {
            if (string.IsNullOrWhiteSpace(first))
            {
                return false;
            }

            try
            {
                return string.Equals(
                    Path.GetFullPath(first),
                    Path.GetFullPath(second),
                    RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            catch
            {
                return string.Equals(first, second, StringComparison.Ordinal);
            }
        }

        private static bool IsClientConfigCurrent(McpClientInfo clientInfo, string transport, int port, int timeout)
        {
            if (!File.Exists(clientInfo.ConfigPath))
            {
                return false;
            }

            if (clientInfo.Format == McpClientConfigFormat.CodexToml)
            {
                // Compare ONLY the [mcp_servers.<name>] sections we own, order-independently. A full-file
                // compare treated any change elsewhere (other MCP servers, section ordering, Codex
                // reformatting them) as stale and rewrote on every check — an endless rewrite loop.
                var codexText = File.ReadAllText(clientInfo.ConfigPath);
                var expectedBlocks = BuildExpectedCodexServerBlocks(transport, port, timeout);
                foreach (var expectedBlock in expectedBlocks)
                {
                    if (!TryExtractCodexSection(codexText, expectedBlock.Key, out var actualSection)
                        || !CodexSectionMatches(actualSection, expectedBlock.Value))
                    {
                        return false;
                    }
                }

                // A section we own that is no longer expected (a secondary project that was removed)
                // makes the file stale too, so the rewrite drops it.
                return CountManagedCodexSections(codexText, port) == expectedBlocks.Count;
            }

            try
            {
                var root = JToken.Parse(File.ReadAllText(clientInfo.ConfigPath));
                if (root is not JObject rootObj
                    || rootObj["mcpServers"] is not JObject mcpServers)
                {
                    return false;
                }

                var expected = BuildExpectedManagedServerEntries(transport, port, timeout, clientInfo.IncludeTypeField);
                foreach (var entry in expected)
                {
                    if (mcpServers[entry.Key] is not JToken server || !JToken.DeepEquals(server, entry.Value))
                    {
                        return false;
                    }
                }

                foreach (var property in mcpServers.Properties())
                {
                    if (ChievfxMcpToolPolicy.IsChievfxManagedServerName(property.Name)
                        && !expected.ContainsKey(property.Name))
                    {
                        return false;
                    }
                }

                return true;
            }
            catch (Exception)
            {
                // The file exists but couldn't be read/parsed right now — almost always a concurrent
                // writer (another MCP-config tool, or a reload extension) caught mid-write, not a
                // genuinely stale entry. Report "current" so we do NOT clobber it: overwriting here
                // would change the file and make clients like Cursor drop their live MCP connection.
                // A genuinely broken file is still fixable via the window's "Write Config" button.
                return true;
            }
        }

        private static string NormalizeConfigText(string text)
        {
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        }

        private enum McpClientConfigFormat
        {
            JsonMcpServers,
            CodexToml
        }

        private readonly struct McpClientInfo
        {
            public McpClientInfo(
                string displayName,
                string configPath,
                string hint,
                McpClientConfigFormat format,
                bool requiresToolProbe,
                string? probeExecutableName,
                string availableLabel,
                string missingLabel,
                bool includeTypeField = true)
            {
                DisplayName = displayName;
                ConfigPath = configPath;
                Hint = hint;
                Format = format;
                RequiresToolProbe = requiresToolProbe;
                ProbeExecutableName = probeExecutableName;
                AvailableLabel = availableLabel;
                MissingLabel = missingLabel;
                IncludeTypeField = includeTypeField;
            }

            public string DisplayName { get; }

            public string ConfigPath { get; }

            public string Hint { get; }

            public McpClientConfigFormat Format { get; }

            public bool RequiresToolProbe { get; }

            public string? ProbeExecutableName { get; }

            public string AvailableLabel { get; }

            public string MissingLabel { get; }

            // Whether the generated JSON server entry carries an explicit "type" field.
            // Cursor/Claude Code accept it; Kimi Code's documented schema does not have it.
            public bool IncludeTypeField { get; }
        }

        private readonly struct CursorServerConfig
        {
            public CursorServerConfig(string name, JToken value)
            {
                Name = name;
                Value = value;
            }

            public string Name { get; }

            public JToken Value { get; }
        }
    }
}
