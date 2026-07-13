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

        public static void InvalidateCache()
        {
            cachedExecutablePath = null;
        }

        public static string ExecutablePath => cachedExecutablePath ??= ResolveExecutablePath();

        public static string ResolveExecutablePath()
        {
            foreach (var candidate in EnumerateCandidates())
            {
                if (!IsRunnablePython(candidate))
                {
                    continue;
                }

                var resolved = TryResolveRealExecutablePath(candidate);
                return resolved ?? candidate;
            }

            // Prefer python3 on macOS/Linux; bare "python" often does not exist.
            return IsWindows() ? "python" : "python3";
        }

        public static bool TryLaunchInstaller(out string error)
        {
            error = string.Empty;
            if (!ChievfxMcpToolPolicy.TryResolveInstallerScriptPath(out var scriptPath))
            {
                error =
                    "Python installer not found. Expected Packages/com.chievfx.mcp/Install~/chievfx_mcp_installer.py in this project.";
                return false;
            }

            var installDirectory = Path.GetDirectoryName(scriptPath)!;
            var python = ResolveInstallerPythonExecutable(installDirectory) ?? ExecutablePath;
            if (!LooksLikeAbsoluteExecutable(python))
            {
                // Unity's PATH on macOS is often too thin for bare "python3".
                // Re-resolve against Unix known locations before giving up.
                InvalidateCache();
                python = ResolveInstallerPythonExecutable(installDirectory) ?? ResolveExecutablePath();
            }

            if (!LooksLikeAbsoluteExecutable(python) && !IsWindows())
            {
                var probed = ProbeUnixWhich("python3") ?? ProbeUnixWhich("python");
                if (!string.IsNullOrWhiteSpace(probed))
                {
                    python = probed!;
                }
            }

            var arguments =
                $"{QuoteArg(scriptPath)} --launcher-project {QuoteArg(ChievfxMcpToolPolicy.ProjectRoot)}";

            try
            {
                if (!TryStartInstallerProcess(python, arguments, installDirectory, out var process, out error))
                {
                    return false;
                }

                Debug.Log($"ChievFX MCP launched installer via '{python}' ({scriptPath}).");
                TryActivateInstallerProcessOnMac(process.Id);
                return true;
            }
            catch (Exception ex)
            {
                error =
                    $"Could not launch Python installer with '{python}'. {ex.Message}\n\n" +
                    "On macOS install Python 3 and ensure python3 is on PATH, or create " +
                    "Packages/com.chievfx.mcp/Install~/.venv with PyQt6.";
                return false;
            }
        }

        private static bool TryStartInstallerProcess(
            string python,
            string arguments,
            string workingDirectory,
            out Process process,
            out string error)
        {
            error = string.Empty;
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = python,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            // Last-resort macOS/Linux: login shell so Homebrew / pyenv PATH applies.
            if (!LooksLikeAbsoluteExecutable(python) && !IsWindows())
            {
                process.StartInfo.FileName = "/bin/bash";
                process.StartInfo.Arguments =
                    $"-lc {QuoteArg($"{QuoteShellArg(python)} {arguments}")}";
            }

            if (!process.Start())
            {
                error = $"Failed to start Python installer process ('{python}').";
                process.Dispose();
                process = null!;
                return false;
            }

            return true;
        }

        private static bool LooksLikeAbsoluteExecutable(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                return Path.IsPathRooted(path) && File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        private static string QuoteShellArg(string value)
        {
            return "'" + value.Replace("'", "'\"'\"'") + "'";
        }

        private static void TryActivateInstallerProcessOnMac(int processId)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || processId <= 0)
            {
                return;
            }

            try
            {
                // Bare python GUIs launched from Unity often open behind the editor and
                // may not receive mouse presses until made frontmost via System Events.
                using var activate = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/usr/bin/osascript",
                        Arguments =
                            $"-e \"delay 0.4\" -e \"tell application \\\"System Events\\\" to set frontmost of (first process whose unix id is {processId}) to true\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    },
                };
                activate.Start();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChievFX MCP could not frontmost installer on macOS. {ex.Message}");
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

            if (IsWindows())
            {
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
                var windowsApps = Path.Combine(localAppData, "Microsoft", "WindowsApps");
                if (Directory.Exists(windowsApps))
                {
                    foreach (var directory in Directory.EnumerateDirectories(windowsApps)
                                 .Where(static path =>
                                     Path.GetFileName(path)
                                         .StartsWith("PythonSoftwareFoundation.", StringComparison.OrdinalIgnoreCase))
                                 .OrderByDescending(Path.GetFileName))
                    {
                        TryAdd(candidates, Path.Combine(directory, "python.exe"));
                        TryAdd(candidates, Path.Combine(directory, "python3.exe"));
                    }
                }

                TryAdd(candidates, Path.Combine(windowsApps, "python.exe"));
                TryAdd(candidates, Path.Combine(windowsApps, "python3.exe"));

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

            // macOS / Linux: Unity's PATH is often empty of Homebrew / python.org installs.
            // Prefer python3 absolute paths; bare "python" is commonly missing on Mac.
            TryAdd(candidates, ProbeUnixWhich("python3"));
            TryAdd(candidates, ProbeUnixWhich("python"));
            foreach (var path in EnumerateUnixPythonCandidates())
            {
                TryAdd(candidates, path);
            }

            return candidates;
        }

        private static IEnumerable<string> EnumerateUnixPythonCandidates()
        {
            yield return "/opt/homebrew/bin/python3";
            yield return "/usr/local/bin/python3";
            yield return "/usr/bin/python3";
            yield return "/Library/Frameworks/Python.framework/Versions/Current/bin/python3";

            var frameworks = "/Library/Frameworks/Python.framework/Versions";
            if (Directory.Exists(frameworks))
            {
                foreach (var directory in Directory.EnumerateDirectories(frameworks)
                             .Where(static path => !string.Equals(Path.GetFileName(path), "Current", StringComparison.Ordinal))
                             .OrderByDescending(Path.GetFileName))
                {
                    yield return Path.Combine(directory, "bin", "python3");
                }
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                yield return Path.Combine(home, ".local", "bin", "python3");
                yield return Path.Combine(home, ".pyenv", "shims", "python3");

                var pyenvVersions = Path.Combine(home, ".pyenv", "versions");
                if (Directory.Exists(pyenvVersions))
                {
                    foreach (var directory in Directory.EnumerateDirectories(pyenvVersions).OrderByDescending(Path.GetFileName))
                    {
                        yield return Path.Combine(directory, "bin", "python3");
                    }
                }
            }

            // Last: rare systems that only ship "python".
            yield return "/opt/homebrew/bin/python";
            yield return "/usr/local/bin/python";
            yield return "/usr/bin/python";
        }

        private static string? ProbeUnixWhich(string command)
        {
            if (IsWindows() || string.IsNullOrWhiteSpace(command))
            {
                return null;
            }

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        // Login shell picks up Homebrew / python.org PATH that Unity lacks.
                        FileName = "/bin/bash",
                        Arguments = $"-lc {QuoteArg("command -v " + command)}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    },
                };
                process.Start();
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.StandardError.ReadToEnd();
                if (!process.WaitForExit(3000) || process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                        // Ignore kill failures on a probe process.
                    }

                    return null;
                }

                var firstLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(static line => line.Trim())
                    .FirstOrDefault(static line => !string.IsNullOrWhiteSpace(line));
                return string.IsNullOrWhiteSpace(firstLine) ? null : firstLine;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChievFX MCP could not resolve '{command}' via bash command -v. {ex.Message}");
                return null;
            }
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

        private static string? TryResolveRealExecutablePath(string candidate)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = candidate,
                        Arguments = "-c \"import sys; print(sys.executable)\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    },
                };
                process.Start();
                var stdout = process.StandardOutput.ReadToEnd().Trim();
                process.StandardError.ReadToEnd();
                if (!process.WaitForExit(5000) || process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                        // Ignore kill failures on a probe process.
                    }

                    return null;
                }

                if (!File.Exists(stdout))
                {
                    return null;
                }

                try
                {
                    return Path.GetFullPath(stdout);
                }
                catch
                {
                    return stdout;
                }
            }
            catch
            {
                return null;
            }
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
