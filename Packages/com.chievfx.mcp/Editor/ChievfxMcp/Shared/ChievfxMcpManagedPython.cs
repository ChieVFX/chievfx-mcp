#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace Chievfx.Mcp.Editor
{
    /// <summary>
    /// Downloads and owns a portable CPython under ~/.chievfx-mcp/env/ so MCP does not
    /// depend on a system Python install. Source: astral-sh/python-build-standalone.
    /// </summary>
    internal static class ChievfxMcpManagedPython
    {
        // Pin deliberately; bump via plugin update when a newer standalone build is required.
        public const string PythonVersion = "3.12.13";
        public const string ReleaseTag = "20260718";
        public const string Variant = "install_only_stripped";

        private const string ProgressTitle = "ChievFX MCP — Python";
        private static readonly object InstallGate = new();
        private static bool installInProgress;
        private static string? lastError;

        public static string RootDirectory
        {
            get
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrWhiteSpace(home))
                {
                    home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
                }

                return Path.Combine(home, ".chievfx-mcp", "env");
            }
        }

        public static string ManifestPath => Path.Combine(RootDirectory, "manifest.json");

        public static bool IsInstallInProgress => installInProgress;

        public static string? LastError => lastError;

        public static bool TryGetExecutablePath(out string path)
        {
            path = ExpectedExecutablePath();
            return File.Exists(path);
        }

        public static string ExpectedExecutablePath()
        {
            return IsWindows()
                ? Path.Combine(RootDirectory, "python.exe")
                : Path.Combine(RootDirectory, "bin", "python3");
        }

        public static bool IsInstalledAndCurrent()
        {
            if (!TryGetExecutablePath(out var exe))
            {
                return false;
            }

            if (!TryReadManifest(out var manifest) || manifest == null)
            {
                return false;
            }

            return ManifestMatchesExpected(manifest) && IsRunnable(exe);
        }

        public static string DescribeStatus()
        {
            if (IsInstallInProgress)
            {
                return "Managed Python install in progress…";
            }

            if (IsInstalledAndCurrent())
            {
                TryReadManifest(out var manifest);
                return
                    $"Managed Python ready ({manifest?.PythonVersion ?? PythonVersion}, {manifest?.Os}/{manifest?.Arch}) at {RootDirectory}";
            }

            if (!string.IsNullOrWhiteSpace(lastError))
            {
                return $"Managed Python not ready. {lastError}";
            }

            if (TryReadManifest(out var existing) && existing != null)
            {
                return
                    $"Managed Python at {RootDirectory} does not match this package " +
                    $"(found {existing.Os}/{existing.Arch}/{existing.PythonVersion}+{existing.ReleaseTag}; " +
                    $"need {DetectOs()}/{DetectArch()}/{PythonVersion}+{ReleaseTag}). Reinstall Python.";
            }

            if (Directory.Exists(RootDirectory))
            {
                return $"Managed Python folder exists but is incomplete: {RootDirectory}. Reinstall Python.";
            }

            return $"Managed Python not installed. Will download CPython {PythonVersion} into {RootDirectory}.";
        }

        public static bool TryEnsureInstalled(bool forceReinstall, out string error)
        {
            error = string.Empty;
            if (!forceReinstall && IsInstalledAndCurrent())
            {
                lastError = null;
                return true;
            }

            lock (InstallGate)
            {
                if (installInProgress)
                {
                    error = "Managed Python install already in progress.";
                    return false;
                }

                installInProgress = true;
            }

            try
            {
                if (!TryResolveDownloadTarget(out var os, out var arch, out var url, out error))
                {
                    lastError = error;
                    return false;
                }

                var stagingRoot = RootDirectory + ".staging";
                var backupRoot = RootDirectory + ".bak";
                var downloadPath = Path.Combine(
                    Path.GetTempPath(),
                    $"chievfx-mcp-python-{ReleaseTag}-{os}-{arch}.tar.gz");

                try
                {
                    EditorUtility.DisplayProgressBar(ProgressTitle, "Downloading portable Python…", 0.05f);
                    if (!TryDownload(url, downloadPath, out error))
                    {
                        lastError = error;
                        return false;
                    }

                    EditorUtility.DisplayProgressBar(ProgressTitle, "Extracting…", 0.7f);
                    DeleteDirectoryIfExists(stagingRoot);
                    Directory.CreateDirectory(stagingRoot);
                    if (!TryExtractTarGz(downloadPath, stagingRoot, out error))
                    {
                        lastError = error;
                        return false;
                    }

                    var extractedPython = Path.Combine(stagingRoot, "python");
                    if (!Directory.Exists(extractedPython))
                    {
                        error = $"Archive did not contain a python/ folder after extract to {stagingRoot}.";
                        lastError = error;
                        return false;
                    }

                    WriteManifest(extractedPython, os, arch, url);

                    var stagedExe = IsWindows()
                        ? Path.Combine(extractedPython, "python.exe")
                        : Path.Combine(extractedPython, "bin", "python3");
                    if (!File.Exists(stagedExe))
                    {
                        error = $"Extracted Python executable missing: {stagedExe}";
                        lastError = error;
                        return false;
                    }

                    if (!IsWindows())
                    {
                        TryMakeExecutable(stagedExe);
                        TryMakeExecutable(Path.Combine(extractedPython, "bin", "python"));
                        var majorMinor = $"{PythonVersion.Split('.')[0]}.{PythonVersion.Split('.')[1]}";
                        TryMakeExecutable(Path.Combine(extractedPython, "bin", $"python{majorMinor}"));
                    }

                    EditorUtility.DisplayProgressBar(ProgressTitle, "Activating…", 0.9f);
                    DeleteDirectoryIfExists(backupRoot);
                    if (Directory.Exists(RootDirectory))
                    {
                        Directory.Move(RootDirectory, backupRoot);
                    }

                    Directory.Move(extractedPython, RootDirectory);
                    DeleteDirectoryIfExists(stagingRoot);
                    DeleteDirectoryIfExists(backupRoot);

                    if (!IsInstalledAndCurrent())
                    {
                        error =
                            $"Install finished but managed Python is still not runnable at {ExpectedExecutablePath()}.";
                        lastError = error;
                        return false;
                    }

                    lastError = null;
                    ChievfxMcpPythonLauncher.InvalidateCache();
                    ChievfxMcpPythonEnvironment.GetStatus(forceRefresh: true);
                    Debug.Log(
                        $"ChievFX MCP installed managed Python {PythonVersion} ({os}/{arch}) at {RootDirectory}");
                    return true;
                }
                finally
                {
                    try
                    {
                        if (File.Exists(downloadPath))
                        {
                            File.Delete(downloadPath);
                        }
                    }
                    catch
                    {
                        // Best-effort temp cleanup.
                    }

                    DeleteDirectoryIfExists(stagingRoot);
                    EditorUtility.ClearProgressBar();
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                lastError = error;
                return false;
            }
            finally
            {
                lock (InstallGate)
                {
                    installInProgress = false;
                }
            }
        }

        public static bool TryReinstall(out string error)
        {
            return TryEnsureInstalled(forceReinstall: true, out error);
        }

        private static bool TryResolveDownloadTarget(
            out string os,
            out string arch,
            out string url,
            out string error)
        {
            os = DetectOs();
            arch = DetectArch();
            url = string.Empty;
            error = string.Empty;

            if (string.Equals(os, "unsupported", StringComparison.Ordinal)
                || string.Equals(arch, "unsupported", StringComparison.Ordinal))
            {
                error =
                    $"Unsupported platform for managed Python ({RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}).";
                return false;
            }

            var tripleArch = arch switch
            {
                "arm64" => "aarch64",
                "x64" => "x86_64",
                _ => arch,
            };
            var tripleOs = os switch
            {
                "macos" => "apple-darwin",
                "windows" => "pc-windows-msvc",
                "linux" => "unknown-linux-gnu",
                _ => os,
            };

            url =
                $"https://github.com/astral-sh/python-build-standalone/releases/download/{ReleaseTag}/" +
                $"cpython-{PythonVersion}+{ReleaseTag}-{tripleArch}-{tripleOs}-{Variant}.tar.gz";
            return true;
        }

        private static bool TryDownload(string url, string destinationPath, out string error)
        {
            error = string.Empty;
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromMinutes(10);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "ChievFX-MCP-Unity/" + ChievfxMcpToolPolicy.PackageName);

                using var response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    error = $"Download failed HTTP {(int)response.StatusCode} for {url}";
                    return false;
                }

                var total = response.Content.Headers.ContentLength ?? -1L;
                using var remote = response.Content.ReadAsStreamAsync()
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();
                using var local = new FileStream(
                    destinationPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None);
                var buffer = new byte[81920];
                long readTotal = 0;
                int read;
                while ((read = remote.Read(buffer, 0, buffer.Length)) > 0)
                {
                    local.Write(buffer, 0, read);
                    readTotal += read;
                    if (total > 0)
                    {
                        var fraction = 0.05f + (0.6f * (float)readTotal / total);
                        EditorUtility.DisplayProgressBar(
                            ProgressTitle,
                            $"Downloading portable Python… ({readTotal / (1024 * 1024)} / {total / (1024 * 1024)} MB)",
                            Math.Min(0.69f, fraction));
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                error = $"Download failed: {ex.Message}";
                return false;
            }
        }

        private static bool TryExtractTarGz(string archivePath, string destinationDirectory, out string error)
        {
            error = string.Empty;
            try
            {
                var tar = IsWindows() ? "tar.exe" : "tar";
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = tar,
                        Arguments = $"-xzf {QuoteArg(archivePath)} -C {QuoteArg(destinationDirectory)}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    },
                };
                process.Start();
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(600000))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                        // Ignore.
                    }

                    error = "Timed out extracting portable Python archive.";
                    return false;
                }

                if (process.ExitCode != 0)
                {
                    error = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                    if (string.IsNullOrWhiteSpace(error))
                    {
                        error = $"tar exited with code {process.ExitCode}";
                    }

                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = $"Extract failed: {ex.Message}";
                return false;
            }
        }

        private static void WriteManifest(string pythonRoot, string os, string arch, string url)
        {
            var manifest = new ManagedPythonManifest
            {
                Os = os,
                Arch = arch,
                PythonVersion = PythonVersion,
                ReleaseTag = ReleaseTag,
                Variant = Variant,
                SourceUrl = url,
                InstalledAtUtc = DateTime.UtcNow.ToString("o"),
            };
            var json = JsonConvert.SerializeObject(manifest, Formatting.Indented);
            File.WriteAllText(Path.Combine(pythonRoot, "manifest.json"), json, Encoding.UTF8);
        }

        private static bool TryReadManifest(out ManagedPythonManifest? manifest)
        {
            manifest = null;
            try
            {
                if (!File.Exists(ManifestPath))
                {
                    return false;
                }

                manifest = JsonConvert.DeserializeObject<ManagedPythonManifest>(File.ReadAllText(ManifestPath));
                return manifest != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool ManifestMatchesExpected(ManagedPythonManifest manifest)
        {
            return string.Equals(manifest.Os, DetectOs(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(manifest.Arch, DetectArch(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(manifest.PythonVersion, PythonVersion, StringComparison.Ordinal)
                && string.Equals(manifest.ReleaseTag, ReleaseTag, StringComparison.Ordinal)
                && string.Equals(manifest.Variant, Variant, StringComparison.Ordinal);
        }

        public static string DetectOs()
        {
            if (IsWindows())
            {
                return "windows";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return "macos";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return "linux";
            }

            return "unsupported";
        }

        public static string DetectArch()
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X64 => "x64",
                _ => "unsupported",
            };
        }

        private static bool IsRunnable(string path)
        {
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
                if (!process.WaitForExit(5000))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                        // Ignore.
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

        private static void TryMakeExecutable(string path)
        {
            if (!File.Exists(path) || IsWindows())
            {
                return;
            }

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/bin/chmod",
                        Arguments = $"+x {QuoteArg(path)}",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    },
                };
                process.Start();
                process.WaitForExit(3000);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChievFX MCP could not chmod +x {path}. {ex.Message}");
            }
        }

        private static void DeleteDirectoryIfExists(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChievFX MCP could not delete {path}. {ex.Message}");
            }
        }

        private static string QuoteArg(string value)
        {
            return $"\"{value.Replace("\"", "\\\"")}\"";
        }

        private static bool IsWindows()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }

        [Serializable]
        private sealed class ManagedPythonManifest
        {
            [JsonProperty("os")]
            public string Os { get; set; } = string.Empty;

            [JsonProperty("arch")]
            public string Arch { get; set; } = string.Empty;

            [JsonProperty("pythonVersion")]
            public string PythonVersion { get; set; } = string.Empty;

            [JsonProperty("releaseTag")]
            public string ReleaseTag { get; set; } = string.Empty;

            [JsonProperty("variant")]
            public string Variant { get; set; } = string.Empty;

            [JsonProperty("sourceUrl")]
            public string SourceUrl { get; set; } = string.Empty;

            [JsonProperty("installedAtUtc")]
            public string InstalledAtUtc { get; set; } = string.Empty;
        }
    }
}
