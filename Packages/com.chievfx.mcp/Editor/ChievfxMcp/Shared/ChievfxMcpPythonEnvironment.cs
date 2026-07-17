#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Chievfx.Mcp.Editor
{
    internal readonly struct ChievfxMcpPythonEnvironmentStatus
    {
        public bool PythonFound { get; init; }
        public bool VersionSupported { get; init; }
        public bool IsWindowsStoreShim { get; init; }
        public bool PipAvailable { get; init; }
        public bool RequirementsFileExists { get; init; }
        public bool HasRequiredPackages { get; init; }
        public bool PackagesSatisfied { get; init; }
        public string ExecutablePath { get; init; }
        public string VersionDisplay { get; init; }
        public IReadOnlyList<string> MissingPackageLines { get; init; }
        public string Guidance { get; init; }

        public bool IsReady =>
            PythonFound
            && VersionSupported
            && !IsWindowsStoreShim
            && PackagesSatisfied;

        public static ChievfxMcpPythonEnvironmentStatus Unknown { get; } = new()
        {
            ExecutablePath = string.Empty,
            VersionDisplay = string.Empty,
            MissingPackageLines = Array.Empty<string>(),
            Guidance = "Python environment not checked yet.",
        };
    }

    internal static class ChievfxMcpPythonEnvironment
    {
        public const int MinimumMajor = 3;

        // The server tree targets Python 3.9: every part file uses `from __future__ import
        // annotations`, so PEP 604 "X | None" hints stay unevaluated strings, and it otherwise
        // relies only on 3.9-era stdlib (PEP 585 builtin generics). Keep this at 9 so a working
        // 3.9 interpreter is not rejected as unsupported.
        public const int MinimumMinor = 9;

        private static readonly Regex VersionPattern = new(
            @"Python\s+(?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static ChievfxMcpPythonEnvironmentStatus cachedStatus = ChievfxMcpPythonEnvironmentStatus.Unknown;
        private static DateTime cachedAtUtc = DateTime.MinValue;
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

        public static ChievfxMcpPythonEnvironmentStatus GetStatus(bool forceRefresh = false)
        {
            if (!forceRefresh && cachedAtUtc != DateTime.MinValue && DateTime.UtcNow - cachedAtUtc < CacheLifetime)
            {
                return cachedStatus;
            }

            cachedStatus = Probe();
            cachedAtUtc = DateTime.UtcNow;
            return cachedStatus;
        }

        public static bool TryInstallRequirements(out string error, out string output)
        {
            error = string.Empty;
            output = string.Empty;
            var status = GetStatus(forceRefresh: true);
            if (!status.PythonFound || !status.VersionSupported || status.IsWindowsStoreShim)
            {
                error = status.Guidance;
                return false;
            }

            if (!status.RequirementsFileExists)
            {
                error = $"Requirements file not found:\n{RequirementsPath}";
                return false;
            }

            if (!status.HasRequiredPackages)
            {
                output = "No third-party Python packages are required for the MCP server.";
                return true;
            }

            if (!status.PipAvailable)
            {
                error = BuildPipMissingGuidance(status.ExecutablePath);
                return false;
            }

            return TryRunPythonCommand(
                status.ExecutablePath,
                $"-m pip install -r {QuoteArg(RequirementsPath)} --disable-pip-version-check",
                120000,
                out output,
                out error);
        }

        public static string RequirementsPath => ChievfxMcpToolPolicy.RequirementsPath;

        public static string BuildInstallCommand()
        {
            var status = GetStatus();
            if (!status.PythonFound)
            {
                return $"python3 -m pip install -r \"{RequirementsPath}\"";
            }

            return $"{QuoteArg(status.ExecutablePath)} -m pip install -r {QuoteArg(RequirementsPath)}";
        }

        private static ChievfxMcpPythonEnvironmentStatus Probe()
        {
            var requirementsPath = RequirementsPath;
            var requirementsFileExists = File.Exists(requirementsPath);
            var requiredPackages = requirementsFileExists
                ? ParseRequirementLines(File.ReadAllText(requirementsPath))
                : new List<string>();
            var hasRequiredPackages = requiredPackages.Count > 0;

            var executablePath = ChievfxMcpPythonLauncher.ExecutablePath;
            if (!TryProbePythonVersion(executablePath, out var versionText, out var version))
            {
                return new ChievfxMcpPythonEnvironmentStatus
                {
                    PythonFound = false,
                    ExecutablePath = executablePath,
                    VersionDisplay = string.IsNullOrWhiteSpace(versionText) ? "not found" : versionText.Trim(),
                    RequirementsFileExists = requirementsFileExists,
                    HasRequiredPackages = hasRequiredPackages,
                    MissingPackageLines = requiredPackages,
                    Guidance = BuildMissingPythonGuidance(executablePath, versionText),
                };
            }

            var isWindowsStoreShim = IsWindowsStoreShimPath(executablePath);
            var versionSupported = IsSupportedVersion(version);
            var pipAvailable = TryProbePip(executablePath, out var pipError);
            string[] missingPackages = Array.Empty<string>();
            var packagesSatisfied = true;
            if (hasRequiredPackages)
            {
                if (!pipAvailable)
                {
                    packagesSatisfied = false;
                }
                else
                {
                    packagesSatisfied = TryProbeRequirementsSatisfied(executablePath, requirementsPath, out missingPackages);
                }
            }

            return new ChievfxMcpPythonEnvironmentStatus
            {
                PythonFound = true,
                VersionSupported = versionSupported,
                IsWindowsStoreShim = isWindowsStoreShim,
                PipAvailable = pipAvailable,
                RequirementsFileExists = requirementsFileExists,
                HasRequiredPackages = hasRequiredPackages,
                PackagesSatisfied = packagesSatisfied && !isWindowsStoreShim && versionSupported,
                ExecutablePath = executablePath,
                VersionDisplay = versionText.Trim(),
                MissingPackageLines = missingPackages,
                Guidance = BuildGuidance(
                    executablePath,
                    versionText,
                    versionSupported,
                    isWindowsStoreShim,
                    pipAvailable,
                    pipError,
                    requirementsFileExists,
                    hasRequiredPackages,
                    packagesSatisfied,
                    missingPackages),
            };
        }

        private static string BuildGuidance(
            string executablePath,
            string versionText,
            bool versionSupported,
            bool isWindowsStoreShim,
            bool pipAvailable,
            string pipError,
            bool requirementsFileExists,
            bool hasRequiredPackages,
            bool packagesSatisfied,
            IReadOnlyList<string> missingPackages)
        {
            if (isWindowsStoreShim)
            {
                return BuildWindowsStoreShimGuidance(executablePath);
            }

            if (!versionSupported)
            {
                return
                    $"Python {MinimumMajor}.{MinimumMinor}+ required; found {versionText.Trim()}. Install a newer Python and ensure Cursor MCP config points at it.";
            }

            if (hasRequiredPackages && !pipAvailable)
            {
                return BuildPipMissingGuidance(executablePath, pipError);
            }

            if (hasRequiredPackages && !packagesSatisfied)
            {
                var missing = missingPackages.Count > 0
                    ? string.Join(", ", missingPackages)
                    : "one or more packages from requirements.txt";
                return
                    $"Missing Python packages: {missing}. Run Install Python Packages or: {BuildInstallCommand()}";
            }

            if (!requirementsFileExists)
            {
                return $"requirements.txt not found at {RequirementsPath}. Reinstall the package or restore the file.";
            }

            return hasRequiredPackages
                ? $"Python ready ({versionText.Trim()}). Required packages from requirements.txt are installed."
                : $"Python ready ({versionText.Trim()}). MCP server uses stdlib only; no pip packages required.";
        }

        private static string BuildMissingPythonGuidance(string executablePath, string versionText)
        {
            var builder = new StringBuilder();
            builder.Append($"Python {MinimumMajor}.{MinimumMinor}+ not found or not runnable");
            if (!string.IsNullOrWhiteSpace(versionText))
            {
                builder.Append($" ({versionText.Trim()})");
            }

            builder.Append('.');
            builder.AppendLine();
            builder.AppendLine(BuildInstallPythonGuidance());

            if (IsWindows())
            {
                builder.AppendLine();
                builder.Append(
                    "Also disable Windows App execution aliases for python.exe and python3.exe (Settings > Apps > Advanced app settings > App execution aliases) so the real install is used instead of the Microsoft Store stub.");
            }

            if (!string.Equals(executablePath, "python3", StringComparison.Ordinal))
            {
                builder.AppendLine();
                builder.Append($"Last resolved path: {executablePath}");
            }

            return builder.ToString().Trim();
        }

        private static string BuildInstallPythonGuidance()
        {
            if (IsWindows())
            {
                return
                    "Install from https://www.python.org/downloads/windows/ (check \"Add python.exe to PATH\"), or run: winget install Python.Python.3.12";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return
                    "Install from https://www.python.org/downloads/macos/ or run: brew install python@3.12";
            }

            return
                $"Install Python {MinimumMajor}.{MinimumMinor}+ with your package manager, e.g. sudo apt install python3 python3-pip (Debian/Ubuntu) or sudo dnf install python3 python3-pip (Fedora).";
        }

        private static string BuildWindowsStoreShimGuidance(string executablePath)
        {
            return
                $"Detected Windows Store python stub at {executablePath}. Install Python {MinimumMajor}.{MinimumMinor}+ from python.org or winget, disable App execution aliases for python.exe/python3.exe, then Write Config so MCP uses the real interpreter.";
        }

        private static string BuildPipMissingGuidance(string executablePath, string? pipError = null)
        {
            var builder = new StringBuilder();
            builder.Append("pip is not available for the selected Python interpreter.");
            if (!string.IsNullOrWhiteSpace(pipError))
            {
                builder.Append(' ');
                builder.Append(pipError!.Trim());
            }

            builder.AppendLine();
            builder.Append(
                $"Install it with: {QuoteArg(executablePath)} -m ensurepip --upgrade && {QuoteArg(executablePath)} -m pip install --upgrade pip");
            return builder.ToString().Trim();
        }

        private static bool TryProbePythonVersion(string executablePath, out string versionText, out Version? version)
        {
            versionText = string.Empty;
            version = null;
            if (!TryRunPythonCommand(executablePath, "--version", 5000, out var stdout, out var stderr))
            {
                versionText = CombineOutput(stdout, stderr);
                return false;
            }

            versionText = CombineOutput(stdout, stderr);
            var match = VersionPattern.Match(versionText);
            if (!match.Success)
            {
                return false;
            }

            var major = int.Parse(match.Groups["major"].Value);
            var minor = int.Parse(match.Groups["minor"].Value);
            var patch = match.Groups["patch"].Success ? int.Parse(match.Groups["patch"].Value) : 0;
            version = new Version(major, minor, patch);
            return true;
        }

        private static bool TryProbePip(string executablePath, out string error)
        {
            return TryRunPythonCommand(
                executablePath,
                "-m pip --version",
                10000,
                out _,
                out error);
        }

        private static bool TryProbeRequirementsSatisfied(
            string executablePath,
            string requirementsPath,
            out string[] missingLines)
        {
            missingLines = Array.Empty<string>();
            if (!TryRunPythonCommand(
                    executablePath,
                    $"-m pip install -r {QuoteArg(requirementsPath)} --dry-run --disable-pip-version-check",
                    120000,
                    out var stdout,
                    out var stderr))
            {
                missingLines = ExtractMissingPackages(stdout, stderr, requirementsPath);
                return false;
            }

            return true;
        }

        private static string[] ExtractMissingPackages(string stdout, string stderr, string requirementsPath)
        {
            var parsed = ParseRequirementLines(File.Exists(requirementsPath) ? File.ReadAllText(requirementsPath) : string.Empty);
            if (parsed.Count == 0)
            {
                return Array.Empty<string>();
            }

            var combined = CombineOutput(stdout, stderr);
            if (string.IsNullOrWhiteSpace(combined))
            {
                return parsed.ToArray();
            }

            var hits = new List<string>();
            foreach (var line in parsed)
            {
                var packageName = GetRequirementPackageName(line);
                if (combined.IndexOf(packageName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hits.Add(line);
                }
            }

            return hits.Count > 0 ? hits.ToArray() : parsed.ToArray();
        }

        private static List<string> ParseRequirementLines(string contents)
        {
            var results = new List<string>();
            foreach (var rawLine in contents.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith("-r ", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                results.Add(line);
            }

            return results;
        }

        private static string GetRequirementPackageName(string requirementLine)
        {
            var trimmed = requirementLine.Trim();
            var index = trimmed.IndexOfAny(new[] { '=', '<', '>', '[', ';', ' ' });
            return index >= 0 ? trimmed.Substring(0, index) : trimmed;
        }

        private static bool IsSupportedVersion(Version? version)
        {
            if (version == null)
            {
                return false;
            }

            if (version.Major > MinimumMajor)
            {
                return true;
            }

            return version.Major == MinimumMajor && version.Minor >= MinimumMinor;
        }

        private static bool IsWindowsStoreShimPath(string executablePath)
        {
            if (!IsWindows())
            {
                return false;
            }

            try
            {
                var normalized = Path.GetFullPath(executablePath);
                if (normalized.IndexOf(@"\PythonSoftwareFoundation.", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }

                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var aliasDirectory = Path.Combine(localAppData, "Microsoft", "WindowsApps");
                if (normalized.StartsWith(aliasDirectory, StringComparison.OrdinalIgnoreCase)
                    && normalized.IndexOf("python", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                if (File.Exists(normalized))
                {
                    var info = new FileInfo(normalized);
                    if (info.Length > 0 && info.Length <= 8192)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                return executablePath.IndexOf(@"Microsoft\WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0
                    && executablePath.IndexOf("PythonSoftwareFoundation", StringComparison.OrdinalIgnoreCase) < 0;
            }

            return false;
        }

        private static bool TryRunPythonCommand(
            string executablePath,
            string arguments,
            int timeoutMs,
            out string stdout,
            out string stderr)
        {
            stdout = string.Empty;
            stderr = string.Empty;

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = executablePath,
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    },
                };

                if (!process.Start())
                {
                    stderr = "Failed to start Python process.";
                    return false;
                }

                stdout = process.StandardOutput.ReadToEnd();
                stderr = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(timeoutMs))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                        // Ignore kill failures on a probe process.
                    }

                    stderr = CombineOutput(stdout, stderr, "Timed out waiting for Python command.");
                    stdout = string.Empty;
                    return false;
                }

                if (process.ExitCode != 0)
                {
                    stderr = CombineOutput(stdout, stderr);
                    stdout = string.Empty;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                stderr = ex.Message;
                return false;
            }
        }

        private static string CombineOutput(string stdout, string stderr, string? suffix = null)
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(stdout))
            {
                builder.Append(stdout.Trim());
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(stderr.Trim());
            }

            if (!string.IsNullOrWhiteSpace(suffix))
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(suffix);
            }

            return builder.ToString().Trim();
        }

        private static string QuoteArg(string value)
        {
            return $"\"{value.Replace("\"", "\\\"")}\"";
        }

        private static bool IsWindows()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }
    }
}
