#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Chievfx.Mcp.Editor
{
    /// <summary>
    /// Ensures Codex trusts this project, which is what makes it read the project-local
    /// <c>.codex/config.toml</c> we write.
    /// <para>
    /// Codex gates the whole project config layer behind a user-level trust record: without
    /// <c>[projects.'&lt;path&gt;'] trust_level = "trusted"</c> in <c>$CODEX_HOME/config.toml</c>
    /// (default <c>~/.codex/config.toml</c>) it silently ignores <c>.codex/config.toml</c>, hooks and
    /// rules — so our MCP server, which lives only in that file, never loads. Codex normally writes
    /// the record itself when the user answers its "do you trust this directory?" prompt, but an
    /// agent driven through another host (JetBrains AI's Codex agent) can start a session that never
    /// shows the prompt, leaving a correct config that is never read.
    /// </para>
    /// <para>
    /// Matching is exact-path, not hierarchical: Codex looks up the launch cwd, then the project root
    /// resolved from its root markers (default <c>.git</c>). Nothing else counts — a trusted
    /// grandparent that is not the repo root does not cover a nested project.
    /// </para>
    /// </summary>
    internal static class ChievfxMcpCodexTrust
    {
        private const string TrustedLevel = "trusted";

        /// <summary>Where Codex keeps its user-level config, honoring CODEX_HOME.</summary>
        public static string ConfigPath
        {
            get
            {
                var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
                if (!string.IsNullOrWhiteSpace(codexHome))
                {
                    return Path.Combine(codexHome!.Trim(), "config.toml");
                }

                return Path.Combine(UserHomeDirectory(), ".codex", "config.toml");
            }
        }

        /// <summary>
        /// True when a trust decision that covers this project is already recorded — either for the
        /// project root itself or for the git root Codex would resolve from it. Any decision counts,
        /// including an explicit "untrusted": that is the user's answer and we must not overturn it.
        /// </summary>
        public static bool HasTrustDecision()
        {
            return TryFindExistingDecision(out _, out var codexWillMatch) && codexWillMatch;
        }

        /// <summary>
        /// Adds the trust record for this project root when no decision covers it yet. Returns true
        /// only when the file was actually appended to; <paramref name="detail"/> describes what
        /// happened either way.
        /// </summary>
        public static bool TryEnsureProjectTrusted(out string detail)
        {
            detail = string.Empty;
            try
            {
                if (TryFindExistingDecision(out var existingKey, out var codexWillMatch))
                {
                    if (codexWillMatch)
                    {
                        detail = $"Codex already has a trust decision for {existingKey}.";
                        return false;
                    }

                    // Something in the file names this project but not in a form Codex's exact-string
                    // key lookup will match (a trailing separator, say). Editing an entry we did not
                    // write is the user's call, so report it instead of guessing — silently appending
                    // a second entry for the same directory would be worse.
                    detail =
                        $"{ConfigPath} mentions {existingKey} but not as a trust key Codex matches "
                        + "exactly, so Codex may still ignore .codex/config.toml. Fix that entry to read "
                        + $"[projects.{TomlKey(existingKey)}] with trust_level = \"trusted\", or remove it "
                        + "and let this write a fresh one.";
                    Debug.LogWarning($"ChievFX MCP {detail}");
                    return false;
                }

                var configPath = ConfigPath;
                var projectRoot = NormalizeDirectory(ChievfxMcpToolPolicy.ProjectRoot);

                // Append only. This is the user's global Codex config, shared with every other
                // project and hand-edited: parsing and re-emitting it would reformat or drop
                // comments and settings we do not model. A new table header at the end of the file
                // is valid TOML regardless of what precedes it.
                var addition = new StringBuilder();
                var existing = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
                if (existing.Length > 0 && !existing.EndsWith("\n", StringComparison.Ordinal))
                {
                    addition.Append('\n');
                }

                if (existing.Length > 0)
                {
                    addition.Append('\n');
                }

                addition.Append("# Added by ChievFX MCP: Codex only reads this project's .codex/config.toml\n");
                addition.Append("# (which carries the Unity MCP server) while the project is trusted.\n");
                addition.Append($"[projects.{TomlKey(projectRoot)}]\n");
                addition.Append($"trust_level = \"{TrustedLevel}\"\n");

                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                File.AppendAllText(configPath, addition.ToString(), new UTF8Encoding(false));
                detail = $"Trusted {projectRoot} in {configPath} so Codex loads .codex/config.toml.";
                return true;
            }
            catch (Exception ex)
            {
                detail = $"Could not record Codex project trust. {ex.Message}";
                Debug.LogWarning($"ChievFX MCP {detail}");
                return false;
            }
        }

        /// <summary>
        /// The paths a Codex trust record may live under for this project, in the order Codex checks
        /// them: the project root (its launch cwd) first, then the git root it resolves for trust
        /// when the project sits inside a larger repository.
        /// </summary>
        private static List<string> TrustLookupPaths()
        {
            var paths = new List<string>();
            var projectRoot = NormalizeDirectory(ChievfxMcpToolPolicy.ProjectRoot);
            paths.Add(projectRoot);

            var gitRoot = FindGitRoot(projectRoot);
            if (gitRoot != null && !PathsEqual(gitRoot, projectRoot))
            {
                paths.Add(gitRoot);
            }

            return paths;
        }

        /// <summary>
        /// Looks for a decision already covering this project. <paramref name="codexWillMatch"/>
        /// separates a key Codex's own lookup resolves — compared as an exact string, only lowercased
        /// on Windows — from a looser mention that leaves the project effectively untrusted.
        /// </summary>
        private static bool TryFindExistingDecision(out string matchedPath, out bool codexWillMatch)
        {
            matchedPath = string.Empty;
            codexWillMatch = false;
            var configPath = ConfigPath;
            if (!File.Exists(configPath))
            {
                return false;
            }

            var text = WithoutCommentLines(File.ReadAllText(configPath));
            var declaredKeys = ReadProjectKeys(text);
            var looseMatch = string.Empty;
            foreach (var candidate in TrustLookupPaths())
            {
                foreach (var declared in declaredKeys)
                {
                    if (string.Equals(declared, candidate, PathComparison))
                    {
                        matchedPath = candidate;
                        codexWillMatch = true;
                        return true;
                    }

                    if (looseMatch.Length == 0 && PathsEqual(declared, candidate))
                    {
                        looseMatch = candidate;
                    }
                }

                // Safety net for `projects` spellings the scan above does not model (a top-level
                // inline table, say). Seeing the path at all means a human or Codex already touched
                // this directory, so never edit the file on the strength of it.
                if (looseMatch.Length == 0 && text.IndexOf(candidate, PathComparison) >= 0)
                {
                    looseMatch = candidate;
                }
            }

            if (looseMatch.Length > 0)
            {
                matchedPath = looseMatch;
                return true;
            }

            return false;
        }

        // Matches [projects.<key>] headers, quoted either way, plus a bare key for completeness.
        private static readonly Regex ProjectTableHeaderPattern = new(
            @"^\s*\[\s*projects\s*\.\s*(?<key>'[^']*'|""(?:[^""\\]|\\.)*""|[A-Za-z0-9_\-]+)\s*\]\s*$",
            RegexOptions.Compiled);

        private static readonly Regex ProjectsTableHeaderPattern = new(
            @"^\s*\[\s*projects\s*\]\s*$",
            RegexOptions.Compiled);

        private static readonly Regex AnyTableHeaderPattern = new(
            @"^\s*\[.*\]\s*$",
            RegexOptions.Compiled);

        // Keys written directly under [projects], e.g. '/path' = { trust_level = "trusted" }.
        private static readonly Regex ProjectsTableEntryPattern = new(
            @"^\s*(?<key>'[^']*'|""(?:[^""\\]|\\.)*""|[A-Za-z0-9_\-]+)\s*=",
            RegexOptions.Compiled);

        // Drops whole comment lines so a commented-out entry never reads as a live decision — the
        // raw-text fallback below would otherwise see the path inside it. Only leading-# lines are
        // removed: a trailing "#" may sit inside a quoted path, and cutting there would corrupt it.
        private static string WithoutCommentLines(string text)
        {
            var kept = new StringBuilder();
            foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                if (line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                kept.Append(line).Append('\n');
            }

            return kept.ToString();
        }

        private static List<string> ReadProjectKeys(string text)
        {
            var keys = new List<string>();
            var insideProjectsTable = false;
            foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var line = rawLine.TrimStart();
                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                var header = ProjectTableHeaderPattern.Match(rawLine);
                if (header.Success)
                {
                    keys.Add(DecodeTomlKey(header.Groups["key"].Value));
                    insideProjectsTable = false;
                    continue;
                }

                if (ProjectsTableHeaderPattern.IsMatch(rawLine))
                {
                    insideProjectsTable = true;
                    continue;
                }

                if (AnyTableHeaderPattern.IsMatch(rawLine))
                {
                    insideProjectsTable = false;
                    continue;
                }

                if (insideProjectsTable)
                {
                    var entry = ProjectsTableEntryPattern.Match(rawLine);
                    if (entry.Success)
                    {
                        keys.Add(DecodeTomlKey(entry.Groups["key"].Value));
                    }
                }
            }

            return keys;
        }

        private static string DecodeTomlKey(string key)
        {
            if (key.Length >= 2 && key[0] == '\'' && key[key.Length - 1] == '\'')
            {
                // Literal string: no escape processing at all — the reason Windows paths are
                // written this way (a basic-string "D:\Unity..." would read \U as an escape).
                return key.Substring(1, key.Length - 2);
            }

            if (key.Length >= 2 && key[0] == '"' && key[key.Length - 1] == '"')
            {
                var body = key.Substring(1, key.Length - 2);
                return body
                    .Replace("\\\\", "\\")
                    .Replace("\\\"", "\"");
            }

            return key;
        }

        private static string TomlKey(string value)
        {
            // Prefer a literal key so path separators need no escaping, which is also what Codex
            // itself writes for Windows paths. Fall back to a basic string for the rare path
            // containing a single quote, which a literal string cannot express.
            if (!value.Contains("'"))
            {
                return $"'{value}'";
            }

            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        // Codex resolves the project root for trust with its root markers, default ".git" — a
        // directory in a normal clone, a file in a worktree or submodule.
        private static string? FindGitRoot(string startDirectory)
        {
            try
            {
                var current = new DirectoryInfo(startDirectory);
                while (current != null)
                {
                    var marker = Path.Combine(current.FullName, ".git");
                    if (Directory.Exists(marker) || File.Exists(marker))
                    {
                        return NormalizeDirectory(current.FullName);
                    }

                    current = current.Parent;
                }
            }
            catch
            {
                // Unreadable ancestor; the project-root key alone is still worth writing.
            }

            return null;
        }

        private static string NormalizeDirectory(string path)
        {
            try
            {
                var full = Path.GetFullPath(path);
                return full.Length > 3
                    ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    : full;
            }
            catch
            {
                return path;
            }
        }

        // Codex compares trust keys case-sensitively everywhere except Windows, where it lowercases
        // both sides. Mirror that so we neither miss an existing decision nor add a duplicate.
        private static StringComparison PathComparison =>
            Application.platform == RuntimePlatform.WindowsEditor
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        private static bool PathsEqual(string left, string right)
        {
            return string.Equals(NormalizeDirectory(left), NormalizeDirectory(right), PathComparison);
        }

        private static string UserHomeDirectory()
        {
            var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                return userProfile!;
            }

            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrWhiteSpace(home))
            {
                return home!;
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }
}
