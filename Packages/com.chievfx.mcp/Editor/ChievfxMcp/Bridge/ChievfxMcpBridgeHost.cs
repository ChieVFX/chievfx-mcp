#nullable enable
using static Chievfx.Mcp.Editor.McpLimits;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Chievfx.Mcp.Editor
{
    internal sealed class ChievfxMcpBridgeHost
    {
        internal static readonly BridgeRuntimeState RuntimeState = new();
        internal static readonly BridgeEventJournal EventJournal = new();
        internal static readonly BridgeOperationStore OperationStore = new(EventJournal);
        private readonly global::Chievfx.Mcp.Editor.SceneResourceService sceneResources = new();
        private readonly global::Chievfx.Mcp.Editor.AssetResourceService assetResources = new();
        private readonly global::Chievfx.Mcp.Editor.MaterialProfileResourceService materialProfileResources = new();
        private readonly global::Chievfx.Mcp.Editor.ChievfxMcpResourceRouter resourceRouter;
        private readonly global::Chievfx.Mcp.Editor.ChievfxMcpPromptService promptService;
        private readonly ChievfxMcpToolDispatcher toolDispatcher;
        internal static BridgeFileTransport Transport { get; private set; } = null!;

        public ChievfxMcpBridgeHost()
        {
            var scenes = new SceneBridgeService();
            var gameObjects = new GameObjectBridgeService();
            var prefabs = new PrefabBridgeService();
            var logs = new ConsoleLogBridgeService();
            var reflection = new ReflectionBridgeService();
            var assets = new AssetBridgeService();
            var profiler = new ProfilerBridgeService();
            var screenshots = new ScreenshotBridgeService();
            var editorWindows = new EditorWindowBridgeService();
            resourceRouter = new global::Chievfx.Mcp.Editor.ChievfxMcpResourceRouter(
                sceneResources,
                assetResources,
                materialProfileResources);
            promptService = new global::Chievfx.Mcp.Editor.ChievfxMcpPromptService(sceneResources);
            toolDispatcher = new ChievfxMcpToolDispatcher(
                new IChievfxMcpToolHandler[]
                {
                    new SceneToolService(scenes.ListOpened, scenes.ListAvailable, scenes.Create, scenes.Open, scenes.Save),
                    new GameObjectToolService(
                        gameObjects.Create,
                        gameObjects.Hierarchy,
                        gameObjects.Find,
                        gameObjects.GetComponent,
                        gameObjects.Update,
                        gameObjects.UpdateOrCreateComponent,
                        gameObjects.GetTransform,
                        gameObjects.UpdateTransform,
                        gameObjects.SetParent,
                        gameObjects.Duplicate),
                    new PrefabToolService(prefabs.Open, prefabs.Close, prefabs.Save, prefabs.Create, prefabs.Instantiate),
                    new ConsoleLogToolService(logs.Clear, logs.Get, logs.GetSingle),
                    new ReflectionToolService(reflection.FindMethods, reflection.FindSingleMethod, reflection.CallMethod),
                    new AssetToolService(assets.Refresh, assets.Find, assets.Delete, assets.Create, assets.EnsureFolder, assets.Recompile),
                    new ProfilerToolService(
                        profiler.State,
                        profiler.Start,
                        profiler.Stop,
                        profiler.Counters,
                        profiler.ControlWindow,
                        profiler.ControlFrameDebugger,
                        profiler.ListFrameDebuggerEvents,
                        profiler.GetFrameDebuggerEvent,
                        profiler.ListFrameDebuggerGroups,
                        profiler.ListFrameDebuggerGroupEvents,
                        profiler.GetFrameDebuggerDrawCall,
                        profiler.CaptureFrameDebuggerDrawCall),
                    new ScreenshotToolService(screenshots.CaptureGameView, screenshots.CaptureCamera),
                    new EditorWindowToolService(editorWindows.List, editorWindows.Open, editorWindows.Focus),
                    new RuntimeUiToolService(),
                    new BridgeCoreToolService(resourceRouter.ReadResource, promptService.GetPrompt)
                },
                ChievfxMcpExtensionRegistry.TryRunTool);
            Transport = new BridgeFileTransport(
                RuntimeState,
                OperationStore,
                new BridgeFileTransportHandlers
                {
                    EventJournal = EventJournal,
                    EnsureStarted = EnsureStarted,
                    RunTool = RunTool,
                    ScheduleRefreshAssets = assets.ScheduleRefresh,
                    IsPackageTool = IsPackageTool,
                    StartPackageToolRequest = StartPackageToolRequest,
                    ProcessPendingPackageRequests = ProcessPendingPackageRequests,
                    IsTestTool = IsTestTool,
                    StartTestToolRequest = StartTestToolRequest,
                    ProcessPendingTestRequests = ProcessPendingTestRequests,
                    IsScriptTool = IsScriptTool,
                    StartScriptToolRequest = StartScriptToolRequest,
                    ProcessPendingScriptInvocationRequests = ProcessPendingScriptInvocationRequests,
                    StartEditorWindowScreenshotRequest = StartEditorWindowScreenshotRequest,
                    ProcessPendingEditorWindowScreenshotRequests = ProcessPendingEditorWindowScreenshotRequests
                });
        }

        public bool IsRunning => RuntimeState.IsRunning;

        public void Attach()
        {
            EditorApplication.update += Transport.ProcessRequests;
            ChievfxMcpExternalSceneReloadGuard.Attach(EventJournal);
            Application.logMessageReceivedThreaded += ConsoleLogBridgeService.CollectLog;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            UnityEditor.Compilation.CompilationPipeline.compilationStarted += OnCompilationStarted;
            UnityEditor.Compilation.CompilationPipeline.compilationFinished += OnCompilationFinished;
            UnityEditor.Compilation.CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            EnsureStarted();
            EventJournal.Write("bridge", "started", "info", "ChievFX MCP bridge started.");
            EditorApplication.delayCall += OnBridgeRestoredAfterDomainReload;
        }

        public void EnsureStarted()
        {
            RuntimeState.EnsureInitializedPaths();
            EventJournal.RestoreCursorFromStream();
            EventJournal.EnsureStreamFile();
            RuntimeState.Start();
        }

        public void Stop()
        {
            EventJournal.Write("bridge", "stopped", "info", "ChievFX MCP bridge stopped.");
            RuntimeState.Stop();
        }

        public object? RunTool(string toolName, JToken args)
        {
            return toolDispatcher.RunTool(toolName, args);
        }

        public object ReadResourceUri(string uri)
        {
            return resourceRouter.ReadResourceUri(uri);
        }

        private void OnCompilationStarted(object context)
        {
            EventJournal.Write("editor", "compile-start", "info", "Unity script compilation started.");
        }

        private void OnCompilationFinished(object context)
        {
            EventJournal.Write("editor", "compile-finish", "info", "Unity script compilation finished.");
        }

        private void OnAssemblyCompilationFinished(string assemblyPath, UnityEditor.Compilation.CompilerMessage[] messages)
        {
            foreach (var msg in messages)
            {
                var logType = msg.type == UnityEditor.Compilation.CompilerMessageType.Error
                    ? LogType.Error
                    : LogType.Warning;
                var text = string.IsNullOrEmpty(msg.file)
                    ? msg.message
                    : $"{msg.file}({msg.line},{msg.column}): {msg.message}";
                lock (RuntimeState.LogLock)
                {
                    RuntimeState.LogEntries.Add(new LogEntryDto(logType.ToString(), text, DateTime.UtcNow, string.Empty));
                    if (RuntimeState.LogEntries.Count > MaxLogEntries)
                    {
                        RuntimeState.LogEntries.RemoveRange(0, RuntimeState.LogEntries.Count - MaxLogEntries);
                    }
                }

                EventJournal.Write("log", "message", logType.ToString(), text);
            }
        }

        private void OnBeforeAssemblyReload()
        {
            EventJournal.Write("editor", "domain-reload-before", "info", "Unity domain reload starting.");
        }

        private void OnAfterAssemblyReload()
        {
            EventJournal.Write("editor", "domain-reload-after", "info", "Unity domain reload finished.");
        }

        private void OnBridgeRestoredAfterDomainReload()
        {
            // Re-running EnsureStarted refreshes paths and sweeps stale
            // transport files left from before the reload so the next MCP
            // resource read does not see a permanently busy bridge.
            EnsureStarted();
            EventJournal.Write("editor", "domain-reload-restored", "info", "ChievFX MCP bridge restored after domain reload.");
            RestorePendingPackageOperations();
        }

        private bool IsPackageTool(string toolName)
        {
            return RuntimeState.PackageOperations.IsPackageTool(toolName);
        }

        private void StartPackageToolRequest(string id, string toolName, JToken args)
        {
            RuntimeState.PackageOperations.StartPackageToolRequest(id, toolName, args);
        }

        private void ProcessPendingPackageRequests()
        {
            RuntimeState.PackageOperations.ProcessPendingPackageRequests();
        }

        private void RestorePendingPackageOperations()
        {
            RuntimeState.PackageOperations.RestorePendingPackageOperations();
        }

        private bool IsTestTool(string toolName)
        {
            return RuntimeState.TestOperations.IsTestTool(toolName);
        }

        private void StartTestToolRequest(string id, string toolName, JToken args)
        {
            RuntimeState.TestOperations.StartTestToolRequest(id, toolName, args);
        }

        private void ProcessPendingTestRequests()
        {
            RuntimeState.TestOperations.ProcessPendingTestRequests();
        }

        internal void CompleteTestRun(string id, object result)
        {
            RuntimeState.TestOperations.CompleteTestRun(id, result);
        }

        private bool IsScriptTool(string toolName)
        {
            return RuntimeState.ScriptOperations.IsScriptTool(toolName);
        }

        private void StartScriptToolRequest(string id, JToken args)
        {
            RuntimeState.ScriptOperations.StartScriptToolRequest(id, args);
        }

        private void ProcessPendingScriptInvocationRequests()
        {
            RuntimeState.ScriptOperations.ProcessPendingScriptInvocationRequests();
        }

        private void StartEditorWindowScreenshotRequest(string id, JToken args, int timeoutMs)
        {
            RuntimeState.EditorWindowScreenshotOperations.StartEditorWindowScreenshotRequest(id, args, timeoutMs);
        }

        private void ProcessPendingEditorWindowScreenshotRequests()
        {
            RuntimeState.EditorWindowScreenshotOperations.ProcessPendingEditorWindowScreenshotRequests();
        }
    }

    internal static class ChievfxMcpExternalSceneReloadGuard
    {
        private const double PollIntervalSeconds = 0.25d;
        private static readonly Dictionary<string, DateTime> SceneWriteTimes = new(StringComparer.Ordinal);
        private static readonly MethodInfo? ReloadSceneMethod = typeof(EditorSceneManager).GetMethod(
            "ReloadScene",
            BindingFlags.Static | BindingFlags.NonPublic,
            null,
            new[] { typeof(Scene) },
            null);
        private static readonly MethodInfo? ClearOpenScenesChangedOnDiskMethod = typeof(EditorSceneManager).GetMethod(
            "ClearOpenScenesChangedOnDisk",
            BindingFlags.Static | BindingFlags.NonPublic);

        private static BridgeEventJournal? eventJournal;
        private static double nextPollTime;
        private static bool attached;

        public static void Attach(BridgeEventJournal journal)
        {
            eventJournal = journal;
            if (attached)
            {
                return;
            }

            attached = true;
            EditorApplication.update += PollOpenScenes;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorSceneManager.sceneSaved += OnSceneSaved;
        }

        private static void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            TrackSceneWriteTime(scene);
        }

        private static void OnSceneSaved(Scene scene)
        {
            TrackSceneWriteTime(scene);
        }

        private static void PollOpenScenes()
        {
            if (EditorApplication.timeSinceStartup < nextPollTime)
            {
                return;
            }

            nextPollTime = EditorApplication.timeSinceStartup + PollIntervalSeconds;
            if (!ChievfxMcpToolPolicy.AutoReloadExternallyChangedScenes)
            {
                RefreshOpenSceneWriteTimes();
                return;
            }

            PruneClosedScenes();

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid() || string.IsNullOrWhiteSpace(scene.path))
                {
                    continue;
                }

                var absolutePath = Path.Combine(ChievfxMcpToolPolicy.ProjectRoot, scene.path);
                if (!File.Exists(absolutePath))
                {
                    continue;
                }

                var writeTime = File.GetLastWriteTimeUtc(absolutePath);
                if (!SceneWriteTimes.TryGetValue(scene.path, out var previousWriteTime))
                {
                    TrackSceneWriteTime(scene);
                    continue;
                }

                if (writeTime == previousWriteTime)
                {
                    continue;
                }

                ReloadChangedScene(scene, writeTime);
            }
        }

        private static void ReloadChangedScene(Scene scene, DateTime writeTime)
        {
            SceneWriteTimes[scene.path] = writeTime;
            if (ReloadSceneMethod == null)
            {
                eventJournal?.Write("editor", "scene-external-reload", "warning", "Unity internal scene reload API unavailable.");
                return;
            }

            try
            {
                var wasDirty = scene.isDirty;
                var reloaded = ReloadSceneMethod.Invoke(null, new object[] { scene }) is true;
                ClearOpenScenesChangedOnDiskMethod?.Invoke(null, null);

                if (reloaded)
                {
                    SceneWriteTimes[scene.path] = GetSceneWriteTime(scene);
                    eventJournal?.Write(
                        "editor",
                        "scene-external-reload",
                        wasDirty ? "warning" : "info",
                        wasDirty
                            ? $"Reloaded externally modified dirty scene '{scene.path}'. Unsaved in-memory scene changes were discarded."
                            : $"Reloaded externally modified scene '{scene.path}'.");
                }
                else
                {
                    eventJournal?.Write("editor", "scene-external-reload", "warning", $"Unity did not reload externally modified scene '{scene.path}'.");
                }
            }
            catch (Exception ex)
            {
                eventJournal?.Write("editor", "scene-external-reload", "error", $"Failed to reload externally modified scene '{scene.path}'. {ex.GetBaseException().Message}");
            }
        }

        private static DateTime GetSceneWriteTime(Scene scene)
        {
            var absolutePath = Path.Combine(ChievfxMcpToolPolicy.ProjectRoot, scene.path);
            return File.Exists(absolutePath) ? File.GetLastWriteTimeUtc(absolutePath) : DateTime.MinValue;
        }

        private static void TrackSceneWriteTime(Scene scene)
        {
            if (!scene.IsValid() || string.IsNullOrWhiteSpace(scene.path))
            {
                return;
            }

            SceneWriteTimes[scene.path] = GetSceneWriteTime(scene);
        }

        private static void PruneClosedScenes()
        {
            var openScenePaths = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var path = SceneManager.GetSceneAt(i).path;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    openScenePaths.Add(path);
                }
            }

            var trackedPaths = new List<string>(SceneWriteTimes.Keys);
            foreach (var path in trackedPaths)
            {
                if (!openScenePaths.Contains(path))
                {
                    SceneWriteTimes.Remove(path);
                }
            }
        }

        private static void RefreshOpenSceneWriteTimes()
        {
            PruneClosedScenes();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                TrackSceneWriteTime(SceneManager.GetSceneAt(i));
            }
        }
    }
}
