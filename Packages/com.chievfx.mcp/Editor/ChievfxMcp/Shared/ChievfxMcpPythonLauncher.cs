#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using UnityEditor;
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
            if (!ChievfxMcpToolPolicy.UseSystemPython)
            {
                if (ChievfxMcpManagedPython.IsInstalledAndCurrent()
                    && ChievfxMcpManagedPython.TryGetExecutablePath(out var managed))
                {
                    var resolvedManaged = TryResolveRealExecutablePath(managed);
                    return resolvedManaged ?? managed;
                }

                // Managed mode ignores system interpreters entirely.
                return ChievfxMcpManagedPython.ExpectedExecutablePath();
            }

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

            // The installer is a PyQt6 GUI, but ExecutablePath is the MCP *server* interpreter — by
            // default the managed CPython under ~/.chievfx-mcp/env, which deliberately has no
            // third-party packages. Launching with it started a process that died instantly on
            // "ModuleNotFoundError: No module named 'PyQt6'": no Unity error, no window, nothing to go
            // on. Pick an interpreter that can actually import PyQt6, and say so plainly when none can.
            var python = ResolveInstallerPythonWithGui(installDirectory, out var checkedPythons);
            if (python == null && !TryProvisionInstallerVenv(installDirectory, out python, out var provisionError))
            {
                error =
                    "The Python installer is a PyQt6 GUI, no interpreter with PyQt6 was found, and creating "
                    + $"its virtual environment failed.\n\n{provisionError}\n\n"
                    + $"Manual fallback:  cd {installDirectory} && python3 -m venv .venv && "
                    + ".venv/bin/pip install -r requirements.txt\n"
                    + $"Interpreters checked for PyQt6: {string.Join(", ", checkedPythons)}";
                return false;
            }

            if (string.IsNullOrWhiteSpace(python))
            {
                error = "Could not resolve a Python interpreter for the installer.";
                return false;
            }

            var arguments =
                $"{QuoteArg(scriptPath)} --launcher-project {QuoteArg(ChievfxMcpToolPolicy.ProjectRoot)}";

            try
            {
                if (!TryStartInstallerProcess(python!, arguments, installDirectory, out var process, out error))
                {
                    return false;
                }

                // A process that starts and dies is indistinguishable from "nothing happened" unless we
                // look. Give it a moment and report a premature exit instead of claiming success.
                if (process.WaitForExit(2000) && process.ExitCode != 0)
                {
                    error =
                        $"The Python installer exited immediately (code {process.ExitCode}) using '{python}'.\n"
                        + "Run it in a terminal to see the reason:\n"
                        + $"  cd {installDirectory} && {python} chievfx_mcp_installer.py";
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

        /// <summary>
        /// Creates Install~/.venv and installs requirements.txt into it, so a machine with no PyQt6
        /// anywhere still gets a working installer from one button press. The venv is used instead of
        /// mutating a system Python (which may be externally managed) or the managed server runtime.
        /// </summary>
        private static bool TryProvisionInstallerVenv(string installDirectory, out string? venvPython, out string error)
        {
            venvPython = null;
            error = string.Empty;
            var venvDirectory = Path.Combine(installDirectory, ".venv");
            var candidateVenvPython = IsWindows()
                ? Path.Combine(venvDirectory, "Scripts", "python.exe")
                : Path.Combine(venvDirectory, "bin", "python3");

            var basePython = EnumerateInstallerPythonCandidates(installDirectory)
                .FirstOrDefault(candidate =>
                    !candidate.StartsWith(venvDirectory, StringComparison.Ordinal) && IsRunnablePython(candidate));
            if (string.IsNullOrWhiteSpace(basePython))
            {
                error = "No usable Python 3 interpreter was found to build the virtual environment.";
                return false;
            }

            try
            {
                EditorUtility.DisplayProgressBar("ChievFX MCP", "Creating the installer virtual environment...", 0.2f);
                if (!File.Exists(candidateVenvPython)
                    && !TryRunProcess(basePython!, $"-m venv {QuoteArg(venvDirectory)}", installDirectory, 180_000, out var venvOutput))
                {
                    error = $"'{basePython} -m venv .venv' failed. {venvOutput}";
                    return false;
                }

                if (!File.Exists(candidateVenvPython))
                {
                    error = $"The virtual environment was created but {candidateVenvPython} is missing.";
                    return false;
                }

                EditorUtility.DisplayProgressBar("ChievFX MCP", "Installing PyQt6 into the installer environment...", 0.6f);
                var requirements = Path.Combine(installDirectory, "requirements.txt");
                var installArguments = File.Exists(requirements)
                    ? $"-m pip install --disable-pip-version-check -r {QuoteArg(requirements)}"
                    : "-m pip install --disable-pip-version-check PyQt6";
                if (!TryRunProcess(candidateVenvPython, installArguments, installDirectory, 600_000, out var pipOutput))
                {
                    error = $"Installing the installer requirements failed. {pipOutput}";
                    return false;
                }

                if (!CanImportPyQt6(candidateVenvPython))
                {
                    error = $"Requirements installed but PyQt6 still cannot be imported by {candidateVenvPython}.";
                    return false;
                }

                Debug.Log($"ChievFX MCP created the installer environment at {venvDirectory} (base: {basePython}).");
                venvPython = candidateVenvPython;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private static bool TryRunProcess(string fileName, string arguments, string workingDirectory, int timeoutMs, out string output)
        {
            output = string.Empty;
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = arguments,
                        WorkingDirectory = workingDirectory,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    },
                };
                process.Start();
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(timeoutMs))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                        // Ignore kill failures.
                    }

                    output = $"Timed out after {timeoutMs / 1000}s.";
                    return false;
                }

                // Keep only the tail: pip is verbose and the failure reason is at the end.
                var combined = (stderr + "\n" + stdout).Trim();
                output = combined.Length > 600 ? combined.Substring(combined.Length - 600) : combined;
                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// First interpreter that can import PyQt6, preferring the installer's own venv. Returns null
        /// when none qualifies, with the list of interpreters tried for the error message.
        /// </summary>
        private static string? ResolveInstallerPythonWithGui(string installDirectory, out List<string> checkedPythons)
        {
            checkedPythons = new List<string>();
            foreach (var candidate in EnumerateInstallerPythonCandidates(installDirectory))
            {
                if (string.IsNullOrWhiteSpace(candidate) || checkedPythons.Contains(candidate))
                {
                    continue;
                }

                checkedPythons.Add(candidate);
                if (CanImportPyQt6(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static IEnumerable<string> EnumerateInstallerPythonCandidates(string installDirectory)
        {
            // The installer venv is the intended home for PyQt6, so try it first.
            var venvPython = ResolveInstallerPythonExecutable(installDirectory);
            if (venvPython != null)
            {
                yield return venvPython;
            }

            foreach (var candidate in EnumerateCandidates())
            {
                yield return candidate;
            }

            if (!IsWindows())
            {
                var probed = ProbeUnixWhich("python3");
                if (!string.IsNullOrWhiteSpace(probed))
                {
                    yield return probed!;
                }

                probed = ProbeUnixWhich("python");
                if (!string.IsNullOrWhiteSpace(probed))
                {
                    yield return probed!;
                }
            }
        }

        private static bool CanImportPyQt6(string python)
        {
            try
            {
                if (!File.Exists(python))
                {
                    return false;
                }

                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = python,
                        Arguments = "-c \"import PyQt6\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    },
                };
                process.Start();
                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                if (!process.WaitForExit(10000))
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
