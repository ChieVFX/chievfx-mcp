#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Chievfx.Mcp.Editor
{
    internal sealed class BridgeRuntimeState
    {
        public const string DefaultScriptClassName = "Script";
        public const string DefaultScriptMethodName = "Main";
        public const string RoslynAssemblyName = "Microsoft.CodeAnalysis";
        public const string RoslynCSharpAssemblyName = "Microsoft.CodeAnalysis.CSharp";
        public const string PendingPackageOperationsSessionKey = "ChievfxMcpBridge.PendingPackageOperations.v1";
        public const string LogMarkerPrefix = "MCPEventReachedLocation(";
        public const string LogMarkerSuffix = ")";

        private const double HeartbeatCadenceSeconds = 0.5;

        public static readonly JsonSerializerSettings JsonOptions = McpJson.SerializerSettings;

        private bool isRunning;
        private double lastHeartbeatWriteTime;
        private long editorUpdateTick;

        public List<LogEntryDto> LogEntries { get; } = new();

        public HashSet<string> DirtyPrefabStageAssetPaths { get; } = new(StringComparer.Ordinal);

        public PackageAsyncOperationService PackageOperations { get; } = new();

        public TestAsyncOperationService TestOperations { get; } = new();

        public ScriptAsyncOperationService ScriptOperations { get; } = new();

        public EditorWindowScreenshotAsyncOperationService EditorWindowScreenshotOperations { get; } = new();

        public Regex RegistryPackageIdPattern { get; } = new(@"^[a-z0-9]+(\.[a-z0-9][a-z0-9-]*)+$", RegexOptions.Compiled);

        public object LogLock { get; } = new();

        public bool IsRunning => isRunning;

        public long EditorUpdateTick => Interlocked.Read(ref editorUpdateTick);

        public void Start()
        {
            isRunning = true;
        }

        public void Stop()
        {
            isRunning = false;
        }

        public void IncrementEditorUpdateTick()
        {
            Interlocked.Increment(ref editorUpdateTick);
        }

        public void EnsureInitializedPaths()
        {
            Directory.CreateDirectory(ChievfxMcpToolPolicy.BridgeDirectory);
            Directory.CreateDirectory(ChievfxMcpToolPolicy.BridgeRequestDirectory);
            Directory.CreateDirectory(ChievfxMcpToolPolicy.BridgeResponseDirectory);
            Directory.CreateDirectory(ChievfxMcpToolPolicy.BridgeOperationDirectory);
            Directory.CreateDirectory(ChievfxMcpToolPolicy.BridgeCancelDirectory);
            // Sweep stragglers from prior crash or domain reload so the file
            // bridge does not look permanently busy to the MCP server.
            CleanupStaleTransportFiles();
        }

        private const double StaleProcessingMinutes = 0.5;
        private const double OrphanResponseMinutes = 0.5;

        private static void CleanupStaleTransportFiles()
        {
            var nowUtc = DateTime.UtcNow;
            try
            {
                var requestDir = ChievfxMcpToolPolicy.BridgeRequestDirectory;
                if (Directory.Exists(requestDir))
                {
                    foreach (var path in Directory.GetFiles(requestDir, "*.processing"))
                    {
                        if (FileOlderThanMinutes(path, nowUtc, StaleProcessingMinutes))
                        {
                            TryDeleteFile(path);
                        }
                    }
                }

                var responseDir = ChievfxMcpToolPolicy.BridgeResponseDirectory;
                if (Directory.Exists(responseDir))
                {
                    foreach (var path in Directory.GetFiles(responseDir, "*.json"))
                    {
                        if (FileOlderThanMinutes(path, nowUtc, OrphanResponseMinutes))
                        {
                            TryDeleteFile(path);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChievFX MCP transport cleanup failed. {ex.GetBaseException().Message}");
            }
        }

        private static bool FileOlderThanMinutes(string path, DateTime nowUtc, double minutes)
        {
            try
            {
                return File.GetLastWriteTimeUtc(path) < nowUtc.AddMinutes(-minutes);
            }
            catch
            {
                return false;
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Ignore: another process may own the file or it may have been
                // removed concurrently. Cleanup runs again on the next startup.
            }
        }

        public void WriteHeartbeatIfDue(
            BridgeOperationStore operationStore,
            BridgeEventJournal eventJournal,
            BridgeRuntimeBusyStatus busyStatus)
        {
            var now = EditorApplication.timeSinceStartup;
            if (now - lastHeartbeatWriteTime < HeartbeatCadenceSeconds)
            {
                return;
            }

            lastHeartbeatWriteTime = now;
            try
            {
                eventJournal.EnsureCursorLoaded();
                operationStore.CleanupRecords();
                var compileWaitingForPlayModeExit = BridgePendingRecompile.IsCompileWaitingForPlayModeExit();
                var busy = new Dictionary<string, object?>
                {
                    ["isCompiling"] = EditorApplication.isCompiling,
                    ["isUpdating"] = EditorApplication.isUpdating,
                    ["packageBusy"] = busyStatus.PackageBusy,
                    ["testBusy"] = busyStatus.TestBusy,
                    ["editorWindowScreenshotBusy"] = busyStatus.EditorWindowScreenshotBusy,
                    ["scriptBusy"] = busyStatus.ScriptBusy,
                    ["shaderCompiling"] = TryGetShaderCompileFlag(),
                    ["activeOperationCount"] = operationStore.CountActiveRecords()
                };

                var payload = new Dictionary<string, object?>
                {
                    ["heartbeatUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    ["lastEventId"] = eventJournal.CurrentEventId(),
                    ["bridge"] = new Dictionary<string, object?>
                    {
                        ["running"] = isRunning,
                        ["directory"] = ChievfxMcpToolPolicy.BridgeDirectory
                    },
                    ["editor"] = new Dictionary<string, object?>
                    {
                        ["isPlaying"] = EditorApplication.isPlaying,
                        ["isPaused"] = EditorApplication.isPaused,
                        ["isPlayingOrWillChangePlaymode"] = EditorApplication.isPlayingOrWillChangePlaymode,
                        ["isCompiling"] = EditorApplication.isCompiling,
                        ["isUpdating"] = EditorApplication.isUpdating,

                        // Without these two, "isCompiling: true" during Play Mode reads as work in
                        // progress when it is really a queue Unity cannot drain until play ends.
                        ["compileWaitingForPlayModeExit"] = compileWaitingForPlayModeExit,
                        ["pendingRecompileAfterPlayModeExit"] = BridgePendingRecompile.IsPending,
                        ["scriptChangesWhilePlaying"] = BridgePendingRecompile.ScriptChangesWhilePlaying()
                    },
                    ["busy"] = busy,
                    ["busyReasons"] = BuildBusyReasons(busy, compileWaitingForPlayModeExit)
                };

                WriteAllTextAtomic(ChievfxMcpToolPolicy.BridgeStatePath, JsonConvert.SerializeObject(payload, JsonOptions));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChievFX MCP heartbeat write failed. {ex.GetBaseException().Message}");
            }
        }

        public static void WriteAllTextAtomic(string path, string contents)
        {
            WriteAllTextAtomic(path, writer => writer.Write(contents));
        }

        // Streaming overload: the caller writes straight into the temp file instead of first building the
        // whole payload as a string. Used by the event journal, whose ~500 KB mirror is rewritten 20x a
        // second - materializing it would allocate the string and then WriteAllText's UTF-8 buffer on top,
        // both large-object-heap sized, on every flush.
        public static void WriteAllTextAtomic(string path, Action<TextWriter> writeContents)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = Path.Combine(directory ?? string.Empty, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                // UTF-8 without a BOM, matching what File.WriteAllText produced before.
                using var writer = new StreamWriter(tempPath, false, new UTF8Encoding(false));
                writeContents(writer);
            }
            catch
            {
                TryDeleteFile(tempPath);
                throw;
            }

            // On Windows a reader (the Python MCP server polling state.json) can
            // briefly hold a share lock. Delete-then-move then races and throws
            // "used by another process". File.Replace performs the swap in one
            // call and tolerates a concurrent reader; retry a few times before
            // falling back to delete-then-move for first-write (no target yet).
            const int maxAttempts = 5;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Replace(tempPath, path, null, ignoreMetadataErrors: true);
                    }
                    else
                    {
                        File.Move(tempPath, path);
                    }

                    return;
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    Thread.Sleep(10 * attempt);
                }
                catch (UnauthorizedAccessException) when (attempt < maxAttempts)
                {
                    Thread.Sleep(10 * attempt);
                }
                catch
                {
                    TryDeleteFile(tempPath);
                    throw;
                }
            }
        }

        private static string[] BuildBusyReasons(
            IReadOnlyDictionary<string, object?> busy,
            bool compileWaitingForPlayModeExit)
        {
            var reasons = new List<string>();
            if (compileWaitingForPlayModeExit)
            {
                // Distinct from editor-compiling: this one never clears on its own, so a caller that
                // just keeps waiting for idle waits forever.
                reasons.Add("compile-waiting-for-play-mode-exit");
            }

            AddBusyReason(reasons, busy, "isCompiling", "editor-compiling");
            AddBusyReason(reasons, busy, "isUpdating", "asset-database-updating");
            AddBusyReason(reasons, busy, "packageBusy", "package-manager");
            AddBusyReason(reasons, busy, "testBusy", "tests-running");
            AddBusyReason(reasons, busy, "scriptBusy", "script-execute");
            AddBusyReason(reasons, busy, "shaderCompiling", "shader-compiling");
            return reasons.ToArray();
        }

        private static void AddBusyReason(List<string> reasons, IReadOnlyDictionary<string, object?> busy, string key, string reason)
        {
            if (busy.TryGetValue(key, out var value) && value is bool flag && flag)
            {
                reasons.Add(reason);
            }
        }

        private static bool? TryGetShaderCompileFlag()
        {
            try
            {
                var shaderUtilType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.ShaderUtil");
                if (shaderUtilType == null)
                {
                    return null;
                }

                foreach (var propertyName in new[] { "anythingCompiling", "isCompiling", "IsCompiling" })
                {
                    var property = shaderUtilType.GetProperty(propertyName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (property != null && property.PropertyType == typeof(bool))
                    {
                        return (bool)property.GetValue(null);
                    }
                }

                foreach (var methodName in new[] { "IsShaderCompilerBusy", "IsCompiling" })
                {
                    var method = shaderUtilType.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    if (method != null && method.ReturnType == typeof(bool))
                    {
                        return (bool)method.Invoke(null, null);
                    }
                }
            }
            catch
            {
                return null;
            }

            return null;
        }
    }

    internal readonly struct BridgeRuntimeBusyStatus
    {
        public BridgeRuntimeBusyStatus(
            bool packageBusy,
            bool testBusy,
            bool editorWindowScreenshotBusy,
            bool scriptBusy)
        {
            PackageBusy = packageBusy;
            TestBusy = testBusy;
            EditorWindowScreenshotBusy = editorWindowScreenshotBusy;
            ScriptBusy = scriptBusy;
        }

        public bool PackageBusy { get; }

        public bool TestBusy { get; }

        public bool EditorWindowScreenshotBusy { get; }

        public bool ScriptBusy { get; }
    }
}
