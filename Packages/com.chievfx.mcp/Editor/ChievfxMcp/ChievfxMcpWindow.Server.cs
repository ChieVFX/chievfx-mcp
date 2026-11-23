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
        private void UpdatePythonStatusUi(ChievfxMcpPythonEnvironmentStatus pythonStatus)
        {
            if (pythonStatus.PythonFound && pythonStatus.VersionSupported && !pythonStatus.IsWindowsStoreShim)
            {
                UpdateStateChip(pythonChip, "Python OK", StatusChipState.Good);
            }
            else if (pythonStatus.PythonFound)
            {
                var label = pythonStatus.IsWindowsStoreShim
                    ? "Python shim"
                    : "Python unsupported";
                UpdateStateChip(pythonChip, label, StatusChipState.Warning);
            }
            else
            {
                UpdateStateChip(pythonChip, "Python missing", StatusChipState.Warning);
            }

            if (!pythonStatus.PythonFound || !pythonStatus.VersionSupported || pythonStatus.IsWindowsStoreShim)
            {
                UpdateStateChip(pythonPackagesChip, "Packages unknown", StatusChipState.Neutral);
            }
            else if (!pythonStatus.HasRequiredPackages)
            {
                UpdateStateChip(pythonPackagesChip, "Packages none", StatusChipState.Good);
            }
            else if (pythonStatus.PackagesSatisfied)
            {
                UpdateStateChip(pythonPackagesChip, "Packages OK", StatusChipState.Good);
            }
            else
            {
                UpdateStateChip(pythonPackagesChip, "Packages missing", StatusChipState.Warning);
            }

            if (pythonDetailLabel != null)
            {
                if (pythonStatus.PythonFound)
                {
                    pythonDetailLabel.text =
                        $"Python: {pythonStatus.ExecutablePath} — {pythonStatus.VersionDisplay}";
                }
                else
                {
                    pythonDetailLabel.text = pythonStatus.Guidance;
                }
            }

            if (installPythonPackagesButton != null)
            {
                installPythonPackagesButton.style.display =
                    pythonStatus.HasRequiredPackages
                    && pythonStatus.PythonFound
                    && pythonStatus.VersionSupported
                    && !pythonStatus.IsWindowsStoreShim
                    && !pythonStatus.PackagesSatisfied
                        ? DisplayStyle.Flex
                        : DisplayStyle.None;
            }

            if (reinstallPythonButton != null)
            {
                reinstallPythonButton.style.display = ChievfxMcpToolPolicy.UseSystemPython
                    ? DisplayStyle.None
                    : DisplayStyle.Flex;
            }
        }

        private static string BuildSetupGuidance(
            ChievfxMcpPythonEnvironmentStatus pythonStatus,
            string transport,
            bool serverScriptExists,
            bool bridgeRunning,
            bool httpRunning,
            bool configured,
            McpClientInfo clientInfo,
            bool clientAvailable)
        {
            if (!pythonStatus.IsReady)
            {
                return pythonStatus.Guidance;
            }

            if (!serverScriptExists)
            {
                return $"Server script missing. Confirm project install before writing {clientInfo.DisplayName} config.";
            }

            if (!clientAvailable)
            {
                return $"{clientInfo.DisplayName} CLI not found. Install it or pick another MCP client.";
            }

            if (!configured)
            {
                return $"Write {clientInfo.DisplayName} Config, then reload MCP tools or restart {clientInfo.DisplayName}.";
            }

            if (!bridgeRunning)
            {
                return "Start Bridge before using Unity-backed tools.";
            }

            if (transport == TransportHttp && !httpRunning)
            {
                return "HTTP transport selected. Start HTTP before Cursor connects over HTTP.";
            }

            return $"Ready. Reload MCP tools or restart {clientInfo.DisplayName} after config or selection changes.";
        }

        private static void LaunchPythonInstaller()
        {
            ChievfxMcpPythonLauncher.InvalidateCache();
            if (!ChievfxMcpPythonLauncher.TryLaunchInstaller(out var error))
            {
                EditorUtility.DisplayDialog("ChievFX MCP", error, "OK");
            }
        }

        private void InstallPythonPackages()
        {
            if (ChievfxMcpPythonEnvironment.TryInstallRequirements(out var error, out var output))
            {
                EditorUtility.DisplayDialog("ChievFX MCP", output, "OK");
            }
            else
            {
                EditorUtility.DisplayDialog("ChievFX MCP", error, "OK");
            }

            RefreshUi(forcePythonRefresh: true);
        }

        private void ReinstallManagedPython()
        {
            if (ChievfxMcpManagedPython.TryReinstall(out var error))
            {
                var written = EnsureAllClientConfigs();
                var message =
                    $"Managed Python reinstalled at {ChievfxMcpManagedPython.RootDirectory}.";
                if (written.Count > 0)
                {
                    message += $"\n\nRewrote MCP client configs:\n• {string.Join("\n• ", written)}";
                }

                EditorUtility.DisplayDialog("ChievFX MCP", message, "OK");
            }
            else
            {
                EditorUtility.DisplayDialog("ChievFX MCP", error, "OK");
            }

            RefreshUi(forcePythonRefresh: true);
        }

        private void StartHttpServer()
        {
            SavePreferences();

            if (GetTransport() != TransportHttp)
            {
                EditorUtility.DisplayDialog("ChievFX MCP", "Switch transport to HTTP before starting the HTTP server.", "OK");
                return;
            }

            if (!File.Exists(ServerScriptPath))
            {
                EditorUtility.DisplayDialog("ChievFX MCP", $"ChievFX MCP server script not found:\n{ServerScriptPath}", "OK");
                return;
            }

            if (IsHttpServerRunning())
            {
                RefreshUi();
                return;
            }

            ChievfxMcpToolPolicy.EnsureBridgeStarted();
            if (!TryStartHttpServerProcess(GetPort(), GetTimeout(), out var error))
            {
                EditorUtility.DisplayDialog("ChievFX MCP", error, "OK");
                return;
            }

            RefreshUi();
        }

        private void StopHttpServer()
        {
            StopHttpServerProcess();
            RefreshUi();
        }

        internal static void StopHttpServerProcess()
        {
            var process = httpProcess;
            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
                catch (InvalidOperationException)
                {
                    // Process exited between HasExited and Kill.
                }
                finally
                {
                    process.Dispose();
                    httpProcess = null;
                }
            }
            else
            {
                var pid = EditorPrefs.GetInt(PrefKey("httpPid"), 0);
                if (pid > 0 && TryGetProcess(pid, out var storedProcess))
                {
                    try
                    {
                        storedProcess.Kill();
                    }
                    catch (InvalidOperationException)
                    {
                        // Process exited before cleanup.
                    }
                    finally
                    {
                        storedProcess.Dispose();
                    }
                }
            }

            EditorPrefs.DeleteKey(PrefKey("httpPid"));
        }

        private void WriteSelectedClientConfig()
        {
            SavePreferences();
            ChievfxMcpToolPolicy.EnsureBridgeStarted();
            var clientInfo = GetClientInfo(GetClient());
            WriteClientConfig(clientInfo, GetTransport(), GetPort(), GetTimeout());

            // A Codex config nobody is allowed to read is not a written config, so the trust record
            // is part of the same action.
            var trustNote = string.Empty;
            if (string.Equals(clientInfo.DisplayName, ClientCodex, StringComparison.Ordinal)
                && ChievfxMcpToolPolicy.EnsureCodexProjectTrust
                && ChievfxMcpCodexTrust.TryEnsureProjectTrusted(out var trustDetail))
            {
                trustNote = $"\n\n{trustDetail}";
                Debug.Log($"[ChievFX MCP] {trustDetail}");
            }

            RefreshUi();
            EditorUtility.DisplayDialog(
                "ChievFX MCP",
                $"{clientInfo.DisplayName} config written. Reload MCP tools or restart {clientInfo.DisplayName} before {ChievfxMcpToolPolicy.CursorServerName} appears in the current session.{trustNote}",
                "OK");
        }

        private void StartBridge()
        {
            ChievfxMcpToolPolicy.EnsureBridgeStarted();
            RefreshUi();
            EditorUtility.DisplayDialog("ChievFX MCP", $"Unity bridge IPC is active at:\n{ChievfxMcpToolPolicy.BridgeDirectory}", "OK");
        }

        private void CopyPreview()
        {
            EditorGUIUtility.systemCopyBuffer = BuildClientConfigPreview(GetClientInfo(GetClient()), GetTransport(), GetPort(), GetTimeout());
        }

        private static bool IsHttpServerRunning()
        {
            if (httpProcess != null && !httpProcess.HasExited)
            {
                return true;
            }

            var pid = EditorPrefs.GetInt(PrefKey("httpPid"), 0);
            return pid > 0 && TryGetProcess(pid, out _);
        }

        private static bool TryStartHttpServerProcess(int port, int timeout, out string error)
        {
            error = string.Empty;
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ChievfxMcpPythonLauncher.ExecutablePath,
                    WorkingDirectory = ProjectRoot,
                    Arguments = BuildHttpServerArguments(port, timeout),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = false,
                    RedirectStandardOutput = false
                },
                EnableRaisingEvents = true
            };

            process.Exited += (_, _) => EditorApplication.delayCall += RefreshOpenWindows;

            if (!process.Start())
            {
                error = "Failed to start HTTP server process.";
                process.Dispose();
                return false;
            }

            httpProcess = process;
            EditorPrefs.SetInt(PrefKey("httpPid"), process.Id);
            return true;
        }

        private static void RefreshOpenWindows()
        {
            foreach (var window in UnityEngine.Resources.FindObjectsOfTypeAll<ChievfxMcpWindow>())
            {
                window.RefreshUi();
            }
        }

        private static bool TryGetProcess(int pid, out Process process)
        {
            try
            {
                process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch
            {
                process = null!;
                return false;
            }
        }

        private static string BuildHttpServerArguments(int port, int timeout)
        {
            return $"{QuoteArg(ServerScriptPath)} --transport {TransportHttp} --port {port} --host {QuoteArg(ChievfxMcpToolPolicy.RemoteCmdBindHost)} --project-root {QuoteArg(ProjectRoot)} --bridge-dir {QuoteArg(ChievfxMcpToolPolicy.BridgeDirectory)} --timeout {timeout}";
        }

        private static string QuoteArg(string value)
        {
            return $"\"{value.Replace("\"", "\\\"")}\"";
        }

        private static void LogServerLine(string? line)
        {
            if (!string.IsNullOrEmpty(line))
            {
                Debug.Log($"[ChievFX MCP] {line}");
            }
        }
    }
}
