#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Chievfx.Mcp.Editor
{
    // A second Unity project whose MCP server is exposed to this project's agent alongside our own:
    // built-in vs URP side-by-side comparisons, or a server copy and a client copy the agent should be
    // able to drive both halves of. The injected entry runs the OTHER project's launcher, so that
    // project's own package, tool selection, and bridge stay in charge of it.
    internal readonly struct ChievfxMcpSecondaryProject
    {
        public ChievfxMcpSecondaryProject(string projectRoot, string note)
        {
            ProjectRoot = projectRoot;
            Note = note ?? string.Empty;
        }

        // Exactly the string handed to --project-root, so the SHA1-derived server name matches the one
        // that project's server reports at handshake.
        public string ProjectRoot { get; }

        // Optional one-liner from the user ("server copy", "URP"), folded into the label the agent reads.
        public string Note { get; }

        public string FolderName => ChievfxMcpSecondaryProjects.FolderNameOf(ProjectRoot);

        public string ServerName => ChievfxMcpToolPolicy.ServerNameForProjectRoot(ProjectRoot);

        public string BridgeDirectory => Path.Combine(ProjectRoot, "Library", "ChievfxMcpBridge");

        public string LauncherScriptPath => Path.Combine(BridgeDirectory, "launch_server.py");

        // What the agent sees at the top of that server's instructions. Names the project, its path, and
        // that it is the secondary one — enough to keep two editors apart, short enough to be free.
        public string Label
        {
            get
            {
                var note = string.IsNullOrWhiteSpace(Note) ? string.Empty : $" — {Note.Trim()}";
                return $"SECONDARY Unity project: {FolderName} at {ProjectRoot}{note}. "
                    + $"A second editor alongside primary {ChievfxMcpSecondaryProjects.FolderNameOf(ChievfxMcpToolPolicy.ProjectRoot)}; "
                    + $"every tool on this server acts on {FolderName} only.";
            }
        }
    }

    internal static class ChievfxMcpSecondaryProjects
    {
        // Passed to the secondary server as an environment variable rather than a CLI flag on purpose:
        // that project may run an older package build, and an unknown --flag makes argparse exit 2 while
        // an unknown env var is simply ignored.
        public const string ServerLabelEnvironmentVariable = "CHIEVFX_MCP_SERVER_LABEL";

        private const int SchemaVersion = 1;
        private const int MaxParentWalk = 8;

        private static readonly StringComparison PathComparison =
            Application.platform == RuntimePlatform.WindowsEditor
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        public static IReadOnlyList<ChievfxMcpSecondaryProject> Load()
        {
            var projects = new List<ChievfxMcpSecondaryProject>();
            try
            {
                var path = ChievfxMcpToolPolicy.SecondaryProjectsPath;
                if (!File.Exists(path))
                {
                    return projects;
                }

                if (JToken.Parse(File.ReadAllText(path)) is not JObject root
                    || root["projects"] is not JArray entries)
                {
                    return projects;
                }

                foreach (var entry in entries)
                {
                    if (entry is not JObject entryObj)
                    {
                        continue;
                    }

                    var projectRoot = entryObj["path"]?.Value<string>();
                    if (string.IsNullOrWhiteSpace(projectRoot))
                    {
                        continue;
                    }

                    projects.Add(new ChievfxMcpSecondaryProject(
                        projectRoot!,
                        entryObj["note"]?.Value<string>() ?? string.Empty));
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChievFX MCP could not read secondary projects. {ex.Message}");
            }

            return projects;
        }

        public static void Save(IReadOnlyList<ChievfxMcpSecondaryProject> projects)
        {
            try
            {
                var entries = new JArray();
                foreach (var project in projects)
                {
                    entries.Add(new JObject
                    {
                        ["path"] = project.ProjectRoot,
                        ["note"] = project.Note
                    });
                }

                var root = new JObject
                {
                    ["version"] = SchemaVersion,
                    ["projects"] = entries
                };

                var path = ChievfxMcpToolPolicy.SecondaryProjectsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, root.ToString(Formatting.Indented) + "\n", new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChievFX MCP could not save secondary projects. {ex.Message}");
            }
        }

        // Adds the project the picked path belongs to. error is set (and nothing is saved) when the path
        // is not a usable project; warning is set when it is usable but something is worth saying, e.g.
        // the package is declared but not yet resolved there.
        public static bool TryAdd(string pickedPath, out string error, out string warning)
        {
            warning = string.Empty;
            if (!TryResolveProjectRoot(pickedPath, out var projectRoot, out error))
            {
                return false;
            }

            if (IsSamePath(projectRoot, ChievfxMcpToolPolicy.ProjectRoot))
            {
                error = "That is this project. Pick a different Unity project.";
                return false;
            }

            var projects = new List<ChievfxMcpSecondaryProject>(Load());
            foreach (var existing in projects)
            {
                if (IsSamePath(existing.ProjectRoot, projectRoot))
                {
                    error = $"{FolderNameOf(projectRoot)} is already in the list.";
                    return false;
                }
            }

            if (!TryValidatePackage(projectRoot, out error, out warning))
            {
                return false;
            }

            projects.Add(new ChievfxMcpSecondaryProject(projectRoot, string.Empty));
            Save(projects);
            return true;
        }

        public static void Remove(string projectRoot)
        {
            var projects = new List<ChievfxMcpSecondaryProject>();
            foreach (var project in Load())
            {
                if (!IsSamePath(project.ProjectRoot, projectRoot))
                {
                    projects.Add(project);
                }
            }

            Save(projects);
        }

        public static void SetNote(string projectRoot, string note)
        {
            var projects = new List<ChievfxMcpSecondaryProject>();
            var changed = false;
            foreach (var project in Load())
            {
                if (IsSamePath(project.ProjectRoot, projectRoot)
                    && !string.Equals(project.Note, note, StringComparison.Ordinal))
                {
                    projects.Add(new ChievfxMcpSecondaryProject(project.ProjectRoot, note));
                    changed = true;
                    continue;
                }

                projects.Add(project);
            }

            if (changed)
            {
                Save(projects);
            }
        }

        // Accepts a project root, or anything inside one (the package folder, Assets/, a picked file), and
        // walks up to the Unity project root — the same forgiving behaviour as the Python installer's FROM.
        public static bool TryResolveProjectRoot(string pickedPath, out string projectRoot, out string error)
        {
            projectRoot = string.Empty;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(pickedPath))
            {
                error = "No folder selected.";
                return false;
            }

            string current;
            try
            {
                var trimmed = pickedPath.TrimEnd('/', '\\');
                if (trimmed.Length < 3)
                {
                    error = $"{pickedPath} is not inside a Unity project.";
                    return false;
                }

                current = Path.GetFullPath(File.Exists(trimmed) ? Path.GetDirectoryName(trimmed)! : trimmed);
            }
            catch (Exception ex)
            {
                error = $"Could not read {pickedPath}. {ex.Message}";
                return false;
            }

            for (var depth = 0; depth < MaxParentWalk && !string.IsNullOrEmpty(current); depth++)
            {
                if (IsUnityProjectRoot(current))
                {
                    // No trailing separator, matching ChievfxMcpToolPolicy.ProjectRoot, so both sides hash
                    // to the same server name.
                    projectRoot = current.TrimEnd('/', '\\');
                    return true;
                }

                var parent = Path.GetDirectoryName(current.TrimEnd('/', '\\'));
                if (string.IsNullOrEmpty(parent) || IsSamePath(parent!, current))
                {
                    break;
                }

                current = parent!;
            }

            error = $"{pickedPath} is not inside a Unity project (no Assets/ + ProjectSettings/ above it).";
            return false;
        }

        // Mirrors what the launcher looks for at run time, so "added successfully" means the entry will
        // actually start a server.
        public static bool TryValidatePackage(string projectRoot, out string error, out string warning)
        {
            error = string.Empty;
            warning = string.Empty;
            try
            {
                var relativeServer = Path.Combine("Tools~", "ChievfxMcp", "chievfx_mcp_server.py");
                if (File.Exists(Path.Combine(projectRoot, "Packages", ChievfxMcpToolPolicy.PackageName, relativeServer)))
                {
                    return true;
                }

                var packageCache = Path.Combine(projectRoot, "Library", "PackageCache");
                if (Directory.Exists(packageCache))
                {
                    foreach (var candidate in Directory.GetDirectories(packageCache, ChievfxMcpToolPolicy.PackageName + "@*"))
                    {
                        if (File.Exists(Path.Combine(candidate, relativeServer)))
                        {
                            return true;
                        }
                    }
                }

                foreach (var baseFolder in new[] { "PackagesSource", "Assets", "Packages" })
                {
                    var root = Path.Combine(projectRoot, baseFolder);
                    if (Directory.Exists(root)
                        && Directory.GetFiles(root, ChievfxMcpToolPolicy.PackageName + "-*.tgz", SearchOption.AllDirectories).Length > 0)
                    {
                        return true;
                    }
                }

                var manifest = Path.Combine(projectRoot, "Packages", "manifest.json");
                if (File.Exists(manifest)
                    && File.ReadAllText(manifest).Contains(ChievfxMcpToolPolicy.PackageName, StringComparison.Ordinal))
                {
                    warning = $"{FolderNameOf(projectRoot)} declares {ChievfxMcpToolPolicy.PackageName} but has not resolved it yet. "
                        + "Open that project in Unity once so the package imports and its bridge starts.";
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = $"Could not inspect {projectRoot}. {ex.Message}";
                return false;
            }

            error = $"{FolderNameOf(projectRoot)} does not have {ChievfxMcpToolPolicy.PackageName} installed. "
                + "Install ChievFX MCP there first (Package Manager git URL, or the Python installer).";
            return false;
        }

        // The launcher is self-locating — it derives its project root from its own path — so this project's
        // copy of the content works for any project. Only written when absent: if that project already has
        // one, its own (possibly different package version) copy stays authoritative.
        public static void EnsureLauncherWritten(ChievfxMcpSecondaryProject project)
        {
            try
            {
                if (File.Exists(project.LauncherScriptPath))
                {
                    return;
                }

                ChievfxMcpServerLauncher.WriteLauncherTo(project.LauncherScriptPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"ChievFX MCP could not write the server launcher for {project.FolderName}. {ex.Message}");
            }
        }

        public static string FolderNameOf(string projectRoot)
        {
            try
            {
                var name = Path.GetFileName(projectRoot.TrimEnd('/', '\\'));
                return string.IsNullOrEmpty(name) ? projectRoot : name;
            }
            catch
            {
                return projectRoot;
            }
        }

        public static bool IsSamePath(string first, string second)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(first).TrimEnd('/', '\\'),
                    Path.GetFullPath(second).TrimEnd('/', '\\'),
                    PathComparison);
            }
            catch
            {
                return string.Equals(first, second, PathComparison);
            }
        }

        private static bool IsUnityProjectRoot(string path)
        {
            return Directory.Exists(Path.Combine(path, "Assets"))
                && Directory.Exists(Path.Combine(path, "ProjectSettings"));
        }
    }
}
