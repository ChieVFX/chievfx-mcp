#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using Debug = UnityEngine.Debug;
using static Chievfx.Mcp.Editor.ChievfxMcpSelectionUi;

namespace Chievfx.Mcp.Editor
{
    internal static class ChievfxMcpDebugInstructionsDumper
    {

        public static string DebugInstructionsPath => Path.Combine(ChievfxMcpToolPolicy.ProjectRoot, ".temp", "debug_instructions.md");

        public static void TryDump(string trigger)
        {
            if (string.IsNullOrWhiteSpace(trigger))
            {
                trigger = "unity-selection-change";
            }

            // Every TryDump trigger is an availability change (tool/resource/prompt/category
            // edit). Signal Cursor to reconnect first so the on-disk dump and the agent's
            // instructions converge; this is independent of whether the debug dump succeeds.
            ChievfxMcpReloadSignal.RequestReload(trigger);

            try
            {
                if (!File.Exists(ChievfxMcpToolPolicy.ServerScriptPath))
                {
                    Debug.LogWarning("ChievFX MCP debug instructions dump skipped: server script not found.");
                    return;
                }

                ChievfxMcpExtensionManifestSnapshot.Refresh();

                var arguments = $"{QuoteArg(ChievfxMcpToolPolicy.ServerScriptPath)} --project-root {QuoteArg(ChievfxMcpToolPolicy.ProjectRoot)} --dump-debug-instructions --debug-trigger {QuoteArg(trigger)}";
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ChievfxMcpPythonLauncher.ExecutablePath,
                        WorkingDirectory = ChievfxMcpToolPolicy.ProjectRoot,
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    }
                };

                if (!process.Start())
                {
                    Debug.LogWarning("ChievFX MCP debug instructions dump failed: could not start python3.");
                    return;
                }

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(15000))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (InvalidOperationException)
                    {
                        // Process exited before timeout cleanup.
                    }

                    Debug.LogWarning("ChievFX MCP debug instructions dump timed out.");
                    return;
                }

                var stdout = stdoutTask.GetAwaiter().GetResult().Trim();
                var stderr = stderrTask.GetAwaiter().GetResult().Trim();
                if (process.ExitCode != 0)
                {
                    Debug.LogWarning($"ChievFX MCP debug instructions dump failed ({process.ExitCode}). {stderr}");
                    return;
                }

                var outputPath = string.IsNullOrWhiteSpace(stdout) ? DebugInstructionsPath : stdout;
                Debug.Log($"ChievFX MCP debug instructions written to {outputPath}. Trigger: {trigger}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChievFX MCP debug instructions dump failed. {ex.Message}");
            }
        }
    }
}
