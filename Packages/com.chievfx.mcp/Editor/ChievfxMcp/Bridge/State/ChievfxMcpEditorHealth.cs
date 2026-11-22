#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

namespace Chievfx.Mcp.Editor
{
    /// <summary>
    /// Editor health for agents, written into the heartbeat and shown by bridge-get-status: reserved memory, Play Mode fps, and
    /// what scripts loaded from bytes (script-execute, extension scripts) left behind - EditorApplication.update subscribers and
    /// render textures. Leftovers pile up over a long agent run until the editor crawls; the cure is cleanup or a restart.
    /// Extensions read it through Build() to fold it into their own replies.
    /// </summary>
    public static class ChievfxMcpEditorHealth
    {
        private const double RefreshSeconds = 15d;
        private const float LowFps = 10f;
        private const int ManyRenderTextures = 200;
        private const long HighReservedCapMb = 12000;
        private const long BytesPerMb = 1024 * 1024;
        private const string PlayEnteredKey = "ChievfxMcp.PlayEntered";
        private const string DomainReloadedInPlayKey = "ChievfxMcp.DomainReloadedInPlayUtc";
        private const string StaleStaticStateKey = "ChievfxMcp.StaleStaticStateSinceUtc";
        private const string ReloadDomainCure = "call recompile (it stops Play Mode and reloads the domain even with no script changes)";

        private static double lastBuildTime = double.NegativeInfinity;
        private static double lastLogCheckTime = double.NegativeInfinity;
        private static string? sessionLogPath;
        // Unity creates its log a moment after the process starts; a file born much later is a newer editor's.
        private const double LogBornAfterStartSeconds = 120d;
        private const string DefaultLogFileName = "Editor.log";
        private static Dictionary<string, object?>? cached;
        private static int fpsFrame = -1;
        private static double fpsTime;
        private static float? averageFps;

        // The heartbeat is written twice a second; counting render textures and walking the update delegate that often would
        // cost the editor more than the leak it reports.
        internal static Dictionary<string, object?> GetCached(double now)
        {
            if (cached == null || now - lastBuildTime >= RefreshSeconds)
            {
                cached = Build();
                lastBuildTime = now;
            }

            return cached;
        }

        // UTC time of a domain reload during the current Play Mode (scripts changed while playing are compiled in Play:
        // Preferences > General > Script Changes While Playing), null when there was none. Static state is reset mid-run, and a
        // game that does not survive it stays dead (stuck on loading, errors every frame) while replies said "ready" and "ok".
        public static string? DomainReloadedInPlayUtc
        {
            get
            {
                var value = SessionState.GetString(DomainReloadedInPlayKey, string.Empty);
                return value.Length > 0 ? value : null;
            }
        }

        // UTC time of the domain reload in a Play Mode that has ended, while Play Mode here starts without a domain reload (Enter
        // Play Mode Settings): static state that run broke stays until the next domain reload, and every later Play inherits it
        // (uGUI's Selectable count went negative: IndexOutOfRangeException in Selectable.OnEnable on every button). Null when there
        // was none or a domain reload has happened since.
        public static string? StaleStaticStateSinceUtc
        {
            get
            {
                var value = SessionState.GetString(StaleStaticStateKey, string.Empty);
                return value.Length > 0 ? value : null;
            }
        }

        // Without a domain reload on entering Play, leaving Play does not reset static state either.
        public static bool PlayStartsWithoutDomainReload =>
            EditorSettings.enterPlayModeOptionsEnabled && (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) != 0;

        // SessionState outlives the reload: a Play entered earlier and still on after the reload is a reload in Play. Entering
        // Play with a domain reload (the project default) reloads before EnteredPlayMode, so it is not counted. Any other domain
        // reload resets static state, so what a reloaded Play left behind is gone.
        [InitializeOnLoadMethod]
        private static void TrackDomainReloadsInPlay()
        {
            if (EditorApplication.isPlaying && SessionState.GetBool(PlayEnteredKey, false))
            {
                SessionState.SetString(DomainReloadedInPlayKey, DateTime.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            }
            else
            {
                SessionState.EraseString(StaleStaticStateKey);
            }

            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            // Play Mode problems (fps, a reload in Play) must not outlive the Play they describe by the cache period.
            cached = null;
            if (change == PlayModeStateChange.EnteredPlayMode)
            {
                SessionState.SetBool(PlayEnteredKey, true);
            }
            else if (change == PlayModeStateChange.ExitingPlayMode || change == PlayModeStateChange.EnteredEditMode)
            {
                var domainReloadedInPlayUtc = DomainReloadedInPlayUtc;
                if (domainReloadedInPlayUtc != null && PlayStartsWithoutDomainReload)
                {
                    SessionState.SetString(StaleStaticStateKey, domainReloadedInPlayUtc);
                }

                SessionState.EraseBool(PlayEnteredKey);
                SessionState.EraseString(DomainReloadedInPlayKey);
            }
        }

        public static Dictionary<string, object?> Build()
        {
            var fps = MeasureFps();
            var reservedMb = Profiler.GetTotalReservedMemoryLong() / BytesPerMb;
            var highReservedMb = HighReservedThresholdMb();
            var scriptUpdates = FindScriptUpdateSubscribers();
            var renderTextures = Resources.FindObjectsOfTypeAll<RenderTexture>().Length;
            var problems = new List<string>();
            if (fps < LowFps)
            {
                problems.Add("low_fps " + fps);
            }

            if (scriptUpdates.Count > 0)
            {
                problems.Add("script_update_subscribers " + scriptUpdates.Count + ": a script loaded from bytes left EditorApplication.update subscribed (see scriptUpdates)");
            }

            if (renderTextures > ManyRenderTextures)
            {
                problems.Add("many_render_textures " + renderTextures);
            }

            if (reservedMb > highReservedMb)
            {
                problems.Add("high_memory " + reservedMb + " MB reserved (over " + highReservedMb + " MB)");
            }

            // A Profiler left recording (its window's Record button starts it again with every Play Mode) keeps each frame in
            // memory: an agent found the editor "ok" while 2000 frames had been recorded.
            if (UnityEditorInternal.ProfilerDriver.enabled)
            {
                problems.Add("profiler_recording: the Profiler records every frame and keeps them in memory; stop it with profiler-stop-recording unless you profile now");
            }

            var domainReloadedInPlayUtc = DomainReloadedInPlayUtc;
            if (Application.isPlaying && domainReloadedInPlayUtc != null)
            {
                problems.Add("domain_reloaded_in_play: scripts were compiled and the domain reloaded during this Play Mode at " + domainReloadedInPlayUtc
                             + " UTC (Preferences > General > Script Changes While Playing); static state was reset mid-run, which many games do not "
                             + "survive (stuck on loading, errors every frame): " + DescribeRecovery());
            }

            var staleStaticStateSinceUtc = StaleStaticStateSinceUtc;
            if (staleStaticStateSinceUtc != null)
            {
                problems.Add("stale_static_state: the domain reloaded during a Play Mode at " + staleStaticStateSinceUtc + " UTC, and Play Mode here starts "
                             + "without a domain reload (Project Settings > Editor > Enter Play Mode Settings), so static state that run broke carries into "
                             + "every next Play (seen: IndexOutOfRangeException in uGUI Selectable.OnEnable on every button): before playing, " + ReloadDomainCure);
            }

            var health = new Dictionary<string, object?>
            {
                ["fps"] = fps,
                ["allocatedMb"] = Profiler.GetTotalAllocatedMemoryLong() / BytesPerMb,
                ["reservedMb"] = reservedMb,
                ["managedMb"] = GC.GetTotalMemory(false) / BytesPerMb,
                ["renderTextures"] = renderTextures,
                ["scriptUpdates"] = scriptUpdates,
                // Assemblies loaded from bytes (script-execute, extension scripts) stay loaded until the next domain reload:
                // the count grows with every run, and so does the memory they hold.
                ["bytesAssemblies"] = CountBytesAssemblies(),
                ["problems"] = problems
            };
            if (problems.Count == 0)
            {
                // An empty list vanishes from a compact reply: "checked, clean" must read differently from "not checked".
                health["verdict"] = "ok: no update subscribers left by scripts, fps >= " + LowFps + " in Play, render textures <= "
                                    + ManyRenderTextures + ", reserved memory <= " + highReservedMb + " MB, Profiler not recording";
            }
            else
            {
                health["hint"] = DescribeCure(fps < LowFps, scriptUpdates.Count > 0 || renderTextures > ManyRenderTextures, reservedMb > highReservedMb);
            }

            return health;
        }

        // The file holding this editor session's log, or null when it is gone from disk. Every editor on the machine writes the
        // same Editor.log: a newer one renames it to Editor-prev.log, and a third start deletes that, so the second editor's
        // "Editor.log" was another project's log while its own was only an open handle. Checked again every RefreshSeconds.
        public static string? GetSessionLogPath(double now)
        {
            if (now - lastLogCheckTime >= RefreshSeconds)
            {
                sessionLogPath = FindSessionLogPath();
                lastLogCheckTime = now;
            }

            return sessionLogPath;
        }

        internal static string? FindSessionLogPath()
        {
            var logPath = Application.consoleLogPath;
            if (string.IsNullOrWhiteSpace(logPath))
            {
                return null;
            }

            // A log given with -logFile is this editor's alone.
            if (!string.Equals(Path.GetFileName(logPath), DefaultLogFileName, StringComparison.OrdinalIgnoreCase))
            {
                return File.Exists(logPath) ? logPath : null;
            }

            DateTime startedUtc;
            try
            {
                startedUtc = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
            }
            catch (Exception)
            {
                return File.Exists(logPath) ? logPath : null;
            }

            var previousLogPath = Path.Combine(Path.GetDirectoryName(logPath) ?? string.Empty, Path.GetFileNameWithoutExtension(logPath) + "-prev" + Path.GetExtension(logPath));
            foreach (var candidate in new[] { logPath, previousLogPath })
            {
                if (File.Exists(candidate) && Math.Abs((File.GetCreationTimeUtc(candidate) - startedUtc).TotalSeconds) < LogBornAfterStartSeconds)
                {
                    return candidate;
                }
            }

            return null;
        }

        // What to do after a domain reload in Play: entering Play again resets static state only when it reloads the domain.
        public static string DescribeRecovery()
        {
            return PlayStartsWithoutDomainReload
                ? ReloadDomainCure + ", then enter Play again: Play Mode here starts without a domain reload, so only exiting would carry the broken "
                  + "static state into the next run"
                : "exit Play and enter it again";
        }

        // Each problem has its own cure: a low fps after a freeze is no reason to restart the editor.
        private static string DescribeCure(bool isLowFps, bool hasScriptLeftovers, bool isHighMemory)
        {
            var cures = new List<string>();
            if (hasScriptLeftovers)
            {
                cures.Add("Unsubscribe and destroy what your scripts created (a script must stop what it started), or restart the editor.");
            }

            if (isHighMemory)
            {
                cures.Add("Memory: restart the editor when convenient; a restart writes assets the game dirtied in memory to disk - check git status after it.");
            }

            if (isLowFps)
            {
                cures.Add("Low fps is the average since the last check: a freeze (loading, import, compile) or a heavy scene; check again, and profile if it stays low.");
            }

            return string.Join(" ", cures);
        }

        // Frames per second over the time since the previous check (the heartbeat checks every RefreshSeconds): one frame's
        // delta read 0 fps from a single freeze while the game ran at 60.
        private static float? MeasureFps()
        {
            // A paused Play Mode (the pause button, a Frame Debugger turned on) renders no game frames: low_fps 0 with a hint
            // about loading or a heavy scene sent the agent the wrong way, and the average after the pause still held it.
            if (Application.isPlaying == false || EditorApplication.isPaused)
            {
                fpsFrame = -1;
                averageFps = null;
                return null;
            }

            var now = EditorApplication.timeSinceStartup;
            var frame = Time.frameCount;
            if (fpsFrame < 0 || frame < fpsFrame)
            {
                fpsFrame = frame;
                fpsTime = now;
            }
            else if (now - fpsTime >= 1d)
            {
                averageFps = Mathf.Round((float)((frame - fpsFrame) / (now - fpsTime)));
                fpsFrame = frame;
                fpsTime = now;
            }

            return averageFps ?? (Time.unscaledDeltaTime > 0f ? Mathf.Round(1f / Time.unscaledDeltaTime) : null);
        }

        // A fixed cap fits a workstation; on a small machine half of its memory is already a lot.
        private static long HighReservedThresholdMb()
        {
            var systemMb = SystemInfo.systemMemorySize;
            return systemMb > 0 ? Math.Min(HighReservedCapMb, systemMb / 2) : HighReservedCapMb;
        }

        private static int CountBytesAssemblies()
        {
            var count = 0;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (IsLoadedFromBytes(assembly))
                {
                    count++;
                }
            }

            return count;
        }

        // Update subscribers whose code was not loaded from an assembly file: that is how script-execute and extension
        // scripts are loaded.
        private static List<string> FindScriptUpdateSubscribers()
        {
            var result = new List<string>();
            var update = EditorApplication.update;
            if (update == null)
            {
                return result;
            }

            foreach (var subscriber in update.GetInvocationList())
            {
                var type = subscriber.Method.DeclaringType;
                if (type != null && IsLoadedFromBytes(type.Assembly))
                {
                    result.Add(type.FullName + "." + subscriber.Method.Name);
                }
            }

            return result;
        }

        private static bool IsLoadedFromBytes(System.Reflection.Assembly assembly)
        {
            if (assembly.IsDynamic)
            {
                return true;
            }

            try
            {
                return string.IsNullOrEmpty(assembly.Location);
            }
            catch (NotSupportedException)
            {
                return true;
            }
        }
    }
}
