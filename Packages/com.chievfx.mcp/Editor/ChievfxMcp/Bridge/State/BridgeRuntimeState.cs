#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
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
                eventJournal.RestoreCursorFromStream();
                operationStore.CleanupRecords();
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
                        ["isUpdating"] = EditorApplication.isUpdating
                    },
                    ["busy"] = busy,
                    ["busyReasons"] = BuildBusyReasons(busy)
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
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = Path.Combine(directory ?? string.Empty, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(tempPath, contents);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(tempPath, path);
        }

        private static string[] BuildBusyReasons(IReadOnlyDictionary<string, object?> busy)
        {
            var reasons = new List<string>();
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
