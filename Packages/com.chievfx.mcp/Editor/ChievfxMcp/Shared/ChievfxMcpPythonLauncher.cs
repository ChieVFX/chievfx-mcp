#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Debug = UnityEngine.Debug;

namespace Chievfx.Mcp.Editor
{
    /// <summary>
    /// Resolves a concrete python.exe path for Cursor MCP stdio spawn and editor
    /// metadata subprocesses. Cursor's MCP host often lacks PATH entries that make
    /// bare "python3" work in an interactive shell (WindowsApps shims, etc.).
    /// </summary>
    internal static class ChievfxMcpPythonLauncher
    {
        private static string? cachedExecutablePath;

        public static string ExecutablePath => cachedExecutablePath ??= ResolveExecutablePath();

        public static string ResolveExecutablePath()
        {
            foreach (var candidate in EnumerateCandidates())
            {
                if (IsRunnablePython(candidate))
                {
                    return candidate;
                }
            }

            return "python3";
        }

        public static bool TryLaunchInstaller(out string error)
        {
            error = string.Empty;
            if (!ChievfxMcpToolPolicy.TryResolveInstallerScriptPath(out var scriptPath))
            {
                error =
                    "Python installer not found. Expected Packages/com.chievfx.mcp/Install/chievfx_mcp_installer.py in this project.";
                return false;
            }

            var installDirectory = Path.GetDirectoryName(scriptPath)!;
            var python = ResolveInstallerPythonExecutable(installDirectory) ?? ExecutablePath;
            var arguments =
                $"{QuoteArg(scriptPath)} --launcher-project {QuoteArg(ChievfxMcpToolPolicy.ProjectRoot)}";

            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = python,
                        Arguments = arguments,
                        WorkingDirectory = installDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    },
                };

                if (!process.Start())
                {
                    error = "Failed to start Python installer process.";
                    process.Dispose();
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = $"Could not launch Python installer. {ex.Message}";
                return false;
            }
        }

        private static string? ResolveInstallerPythonExecutable(string installDirectory)
        {
            if (IsWindows())
            {
                var venvPython = Path.Combine(installDirectory, ".venv", "Scripts", "python.exe");
                return File.Exists(venvPython) ? venvPython : null;
            }

            foreach (var name in new[] { "python3", "python" })
            {
                var venvPython = Path.Combine(installDirectory, ".venv", "bin", name);
                if (File.Exists(venvPython))
                {
                    return venvPython;
                }
            }

            return null;
        }

        private static string QuoteArg(string value)
        {
            return $"\"{value.Replace("\"", "\\\"")}\"";
        }

        private static IEnumerable<string> EnumerateCandidates()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void TryAdd(List<string> list, string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                var trimmed = path!.Trim().Trim('"');
                try
                {
                    trimmed = Path.GetFullPath(trimmed);
                }
                catch
                {
                    return;
                }

                if (seen.Add(trimmed))
                {
                    list.Add(trimmed);
                }
            }

            var candidates = new List<string>();

            foreach (var path in QueryWhereExecutable("python"))
            {
                TryAdd(candidates, path);
            }

            foreach (var path in QueryWhereExecutable("python3"))
            {
                TryAdd(candidates, path);
            }

            foreach (var path in QueryPyLauncherPaths())
            {
                TryAdd(candidates, path);
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            TryAdd(candidates, Path.Combine(localAppData, "Microsoft", "WindowsApps", "python.exe"));
            TryAdd(candidates, Path.Combine(localAppData, "Microsoft", "WindowsApps", "python3.exe"));

            var programsPython = Path.Combine(localAppData, "Programs", "Python");
            if (Directory.Exists(programsPython))
            {
                foreach (var directory in Directory.EnumerateDirectories(programsPython).OrderByDescending(Path.GetFileName))
                {
                    TryAdd(candidates, Path.Combine(directory, "python.exe"));
                }
            }

            return candidates;
        }

        private static List<string> QueryWhereExecutable(string command)
        {
            var results = new List<string>();
            if (!IsWindows())
            {
                return results;
            }

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "where.exe",
                        Arguments = command,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    },
                };
                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(2000);
                if (process.ExitCode != 0)
                {
                    return results;
                }

                foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    results.Add(line);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChievFX MCP could not resolve '{command}' via where.exe. {ex.Message}");
            }

            return results;
        }

        private static List<string> QueryPyLauncherPaths()
        {
            var results = new List<string>();
            if (!IsWindows())
            {
                return results;
            }

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "py",
                        Arguments = "-0p",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    },
                };
                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(2000);
                if (process.ExitCode != 0)
                {
                    return results;
                }

                var pattern = new Regex(@"^\s*-V:\d+\.\d+.*?\s+(.+\.exe)\s*$", RegexOptions.IgnoreCase);
                foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var match = pattern.Match(line);
                    if (match.Success)
                    {
                        results.Add(match.Groups[1].Value);
                    }
                }
            }
            catch
            {
                // py launcher is optional on Windows.
            }

            return results;
        }

        private static bool IsWindows()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }

        private static bool IsRunnablePython(string path)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = path,
                        Arguments = "--version",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    },
                };
                process.Start();
                if (!process.WaitForExit(3000))
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
    }
}
