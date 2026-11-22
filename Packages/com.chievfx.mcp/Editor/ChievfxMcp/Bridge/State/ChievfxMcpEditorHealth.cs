#nullable enable
using System;
using System.Collections.Generic;
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

        private static double lastBuildTime = double.NegativeInfinity;
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
            if (Application.isPlaying == false)
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
