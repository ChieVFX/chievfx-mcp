#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Chievfx.Mcp.Editor
{
    /// <summary>
    /// Writes a per-client skill holding the full ChievFX tool/resource reference.
    /// <para>
    /// initialize.instructions is truncated by most clients and agents do not reliably follow a pointer
    /// to a resource, so the same content is also placed where each client loads skills from — a channel
    /// the agent reads as configuration rather than as optional context.
    /// </para>
    /// </summary>
    internal static class ChievfxMcpClientSkillWriter
    {
        public const string SkillName = "mcp-unity-chievfx";

        // Project-local skill folders per client. Cursor and Claude Code are documented; Codex and Kimi
        // follow the same <client-dir>/skills/<name>/SKILL.md convention, so writing them is harmless if
        // the client ignores it.
        private static readonly (string Client, string Directory)[] SkillDirectories =
        {
            ("Cursor", ".cursor"),
            ("Claude Code", ".claude"),
            ("Codex", ".codex"),
            ("Kimi Code", ".kimi-code"),
        };

        /// <summary>Writes the skill for every client. Returns the project-relative paths actually written.</summary>
        public static List<string> WriteAll()
        {
            var written = new List<string>();
            if (!TryReadCoreDescriptors(out var body, out var error))
            {
                Debug.LogWarning($"ChievFX MCP could not generate the {SkillName} skill. {error}");
                return written;
            }

            foreach (var (client, directory) in SkillDirectories)
            {
                var content = BuildSkillMarkdown(body, client);
                var path = Path.Combine(ChievfxMcpToolPolicy.ProjectRoot, directory, "skills", SkillName, "SKILL.md");
                try
                {
                    // Never rewrite identical content: touching a file under a client's config directory
                    // can make that client reload, which drops a live session.
                    if (File.Exists(path) && string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, content, new UTF8Encoding(false));
                    written.Add($"{client} — {directory}/skills/{SkillName}/SKILL.md");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"ChievFX MCP could not write the {client} {SkillName} skill. {ex.Message}");
                }
            }

            return written;
        }

        // Claude Code only. Past a few dozen tools it stops loading MCP schemas up front and defers them
        // to name-only entries, so knowing the tool and its arguments from this file is not enough to call
        // it — the schema has to be fetched first. This project surfaces well over a hundred tools, so
        // deferral is the normal case. The failure it prevents is a per-tool ToolSearch round-trip: the
        // agent already knows every name it needs from the list below, so one batched call covers the run.
        private const string ClaudeCodeClient = "Claude Code";

        private static void AppendDeferredToolSchemaNote(StringBuilder builder)
        {
            builder.AppendLine("## Loading these tools in Claude Code");
            builder.AppendLine();
            builder.AppendLine(
                "Claude Code defers MCP tool schemas on a surface this large: the tools below appear as "
                + "names only, and calling one before its schema is fetched fails with "
                + "`InputValidationError`. This file gives you the name and the arguments; it cannot make "
                + "the tool callable.");
            builder.AppendLine();
            builder.AppendLine(
                "Pick every tool the task needs from the list below, then load them in **one** batched "
                + "`ToolSearch` call — never one call per tool, and never a keyword search, since you "
                + "already have the exact names:");
            builder.AppendLine();
            builder.AppendLine("```");
            builder.AppendLine(
                $"ToolSearch({{ query: \"select:{ToolSearchName("bridge-get-status")},"
                + $"{ToolSearchName("screenshot-game-view")},{ToolSearchName("recompile")}\" }})");
            builder.AppendLine("```");
            builder.AppendLine();
            builder.AppendLine(
                $"Prefix every name with `mcp__{ChievfxMcpToolPolicy.CursorServerName}__`. Reading a "
                + "resource (`chievfx://...`) needs `ReadMcpResourceTool` loaded the same way.");
            builder.AppendLine();
        }

        private static string ToolSearchName(string toolId) =>
            $"mcp__{ChievfxMcpToolPolicy.CursorServerName}__{toolId}";

        private static string BuildSkillMarkdown(string body, string client)
        {
            var builder = new StringBuilder();
            builder.AppendLine("---");
            builder.AppendLine($"name: {SkillName}");
            builder.AppendLine(
                "description: Complete ChievFX Unity MCP tool and resource reference with argument signatures. "
                + "Read before calling ChievFX MCP tools, or before hand-writing C# for anything Unity exposes, "
                + "so arguments are not guessed and existing tools are not reimplemented.");
            builder.AppendLine("---");
            builder.AppendLine();
            builder.AppendLine("# ChievFX Unity MCP capabilities");
            builder.AppendLine();
            builder.AppendLine(
                "Generated by Unity from `chievfx://instructions/core-descriptors` for this project copy. "
                + "The MCP startup instructions are truncated by most clients; this file is the complete list.");
            builder.AppendLine();
            builder.AppendLine($"MCP server name: `{ChievfxMcpToolPolicy.CursorServerName}`");
            builder.AppendLine();
            if (string.Equals(client, ClaudeCodeClient, StringComparison.Ordinal))
            {
                AppendDeferredToolSchemaNote(builder);
            }

            builder.AppendLine(body.TrimEnd());
            builder.AppendLine();
            return builder.ToString();
        }

        private static bool TryReadCoreDescriptors(out string body, out string error)
        {
            body = string.Empty;
            error = string.Empty;
            var scriptPath = ChievfxMcpToolPolicy.ServerScriptPath;
            if (!File.Exists(scriptPath))
            {
                error = $"Server script not found at {scriptPath}.";
                return false;
            }

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ChievfxMcpPythonLauncher.ExecutablePath,
                        Arguments =
                            $"{QuoteArg(scriptPath)} --project-root {QuoteArg(ChievfxMcpToolPolicy.ProjectRoot)} --core-descriptors",
                        WorkingDirectory = ChievfxMcpToolPolicy.ProjectRoot,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    },
                };
                process.Start();
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(60000))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                        // Ignore kill failures on a metadata probe.
                    }

                    error = "Timed out reading core descriptors.";
                    return false;
                }

                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                {
                    error = $"'--core-descriptors' exited {process.ExitCode}. {stderr.Trim()}";
                    return false;
                }

                body = stdout;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string QuoteArg(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
