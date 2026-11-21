#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;
using PackageManagerClient = UnityEditor.PackageManager.Client;
using PackageManagerPackageInfo = UnityEditor.PackageManager.PackageInfo;


namespace Chievfx.Mcp.Editor
{
    internal sealed partial class ProfilerBridgeService
    {
        public object State()
        {
            return new
            {
                enabled = GetProfilerEnabled(),
                targets = GetProfilerTargets()
            };
        }

        public object Start(JToken args)
        {
            var connectionId = ReadNullableInt(args, "connectionId");
            if (connectionId.HasValue)
            {
                SelectProfilerConnection(connectionId.Value);
            }

            // Frames kept from an earlier recording would be summarized and saved together with this one.
            var clearedFrames = GetProfilerEnabled() ? 0 : ClearRecordedFrames();
            SetProfilerEnabled(true);
            return new
            {
                started = true,
                enabled = GetProfilerEnabled(),
                connectionId,
                clearedFrames = clearedFrames > 0 ? clearedFrames : (int?)null,
                note = IsRecordingNothingOutsidePlay() ? EditorNotProfiledNote : null,
                targets = GetProfilerTargets()
            };
        }

        private const int EditorConnectionId = -1;
        private const string EditorNotProfiledNote =
            "Outside Play Mode the profiler records nothing unless it profiles the editor itself (Profiler window: target Edit Mode): enter Play Mode to measure the game.";

        // Profiling the editor's own process outside Play without Profile Editor: Unity records no frames, the capture is empty.
        private static bool IsRecordingNothingOutsidePlay()
        {
            return EditorApplication.isPlaying == false
                   && UnityEditorInternal.ProfilerDriver.connectedProfiler == EditorConnectionId
                   && UnityEditorInternal.ProfilerDriver.profileEditor == false;
        }

        private static int ClearRecordedFrames()
        {
            var first = UnityEditorInternal.ProfilerDriver.firstFrameIndex;
            var last = UnityEditorInternal.ProfilerDriver.lastFrameIndex;
            if (first < 0 || last < first)
            {
                return 0;
            }

            UnityEditorInternal.ProfilerDriver.ClearAllFrames();
            return last - first + 1;
        }

        public object Stop(JToken args)
        {
            var wasRecording = GetProfilerEnabled();
            SetProfilerEnabled(false);

            var savePath = ReadString(args, "savePath");
            var isDefaultPath = string.IsNullOrWhiteSpace(savePath);
            // A capture of a minute is hundreds of MB: a stop without a recording does not write another copy of old frames.
            if (wasRecording == false && isDefaultPath)
            {
                return new
                {
                    stopped = false,
                    enabled = GetProfilerEnabled(),
                    note = "Recording was already off: no new capture saved. profiler-start-recording starts one; pass savePath to save the frames the profiler holds now.",
                    recorded = SummarizeRecordedFrames()
                };
            }

            var profilesDirectory = Path.Combine(ChievfxMcpToolPolicy.ProjectRoot, "Library", "ChievfxMcpBridge", "profiles");
            if (isDefaultPath)
            {
                // Milliseconds: two stops in one second would otherwise write the same file, the second over the first.
                var fileName = "profile-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".data";
                savePath = Path.Combine(profilesDirectory, fileName);
            }
            else if (!Path.IsPathRooted(savePath))
            {
                savePath = Path.Combine(ChievfxMcpToolPolicy.ProjectRoot, savePath);
            }

            var saved = SaveProfilerCapture(savePath!);
            if (isDefaultPath)
            {
                TrimDefaultCaptures(profilesDirectory);
            }

            var recorded = SummarizeRecordedFrames();
            return new
            {
                stopped = true,
                enabled = GetProfilerEnabled(),
                saved,
                path = savePath,
                exists = File.Exists(savePath),
                recorded,
                note = recorded == null ? ("No frames were recorded: the saved capture is empty. " + (IsRecordingNothingOutsidePlay() ? EditorNotProfiledNote : "")).TrimEnd() : null
            };
        }

        // Captures without a savePath pile up in Library: keep the newest few, as the frame recorder does with its sheets.
        private const int KeptDefaultCaptures = 5;

        private static void TrimDefaultCaptures(string directory)
        {
            if (Directory.Exists(directory) == false)
            {
                return;
            }

            var captures = new DirectoryInfo(directory).GetFiles("profile-*.data").OrderByDescending(file => file.LastWriteTimeUtc).Skip(KeptDefaultCaptures);
            foreach (var capture in captures)
            {
                try
                {
                    capture.Delete();
                }
                catch (IOException)
                {
                    // Open elsewhere (the Profiler window loaded it): left for the next stop.
                }
                catch (UnauthorizedAccessException)
                {
                    // Same as above.
                }
            }
        }

        public object Counters()
        {
            var counters = new Dictionary<string, object?>
            {
                ["enabled"] = GetProfilerEnabled(),
                ["totalAllocatedMemory"] = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong(),
                ["totalReservedMemory"] = UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong(),
                ["monoUsedSize"] = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong(),
                ["monoHeapSize"] = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong(),
            };
            if (!Application.isPlaying)
            {
                counters["note"] = "fps, frame time and draw calls are reported in Play Mode only.";
                return counters;
            }

            // The last Game view frame, the numbers of its Stats overlay. ProfilerRecorder render counters ("Draw Calls Count")
            // read 0 in the editor while the Profiler is not recording, so they are not used here.
            counters["fps"] = Math.Round(1f / Mathf.Max(Time.smoothDeltaTime, MinFrameSeconds), 1);
            // FrameTimingManager, the suggested replacement, reports nothing in the editor unless frame timing stats are on
            // in Player Settings; the Stats overlay values work as they are.
#pragma warning disable CS0618
            counters["frameMs"] = Math.Round(UnityStats.frameTime * 1000f, 2);
            counters["renderMs"] = Math.Round(UnityStats.renderTime * 1000f, 2);
#pragma warning restore CS0618
            counters["drawCalls"] = UnityStats.drawCalls;
            counters["setPassCalls"] = UnityStats.setPassCalls;
            counters["triangles"] = UnityStats.triangles;
            counters["vertices"] = UnityStats.vertices;
            return counters;
        }

        private const float MinFrameSeconds = 0.0001f;

        private static Type GetProfilerDriverType()
        {
            return typeof(EditorWindow).Assembly.GetType("UnityEditorInternal.ProfilerDriver")
                ?? throw new NotSupportedException("UnityEditorInternal.ProfilerDriver is unavailable in this Unity version.");
        }

        private static bool GetProfilerEnabled()
        {
            var driverType = GetProfilerDriverType();
            var property = driverType.GetProperty("IsProfilingEnabled", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null)
            {
                return Convert.ToBoolean(property.GetValue(null), CultureInfo.InvariantCulture);
            }

            var method = driverType.GetMethod("get_IsProfilingEnabled", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method != null)
            {
                return Convert.ToBoolean(method.Invoke(null, null), CultureInfo.InvariantCulture);
            }

            method = driverType.GetMethod("IsProfilingEnabled", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (method != null)
            {
                return Convert.ToBoolean(method.Invoke(null, null), CultureInfo.InvariantCulture);
            }

            throw new NotSupportedException("Profiler enabled state is unsupported in this Unity version.");
        }

        private static void SetProfilerEnabled(bool enabled)
        {
            // An open Profiler window keeps its own Record toggle and applies it to the profiler again later (on a draw while
            // the editor is in the background): switching only the profiler left recording to come back after the reply.
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (window.GetType().Name == "ProfilerWindow")
                {
                    TryInvokeReflectedMethod(window, "SetRecordingEnabled", new object?[] { enabled }, out _, out _);
                }
            }

            var driverType = GetProfilerDriverType();
            var method = driverType.GetMethod("SetProfilingEnabled", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(bool) }, null)
                ?? throw new NotSupportedException("Profiler recording controls are unsupported in this Unity version.");
            method.Invoke(null, new object[] { enabled });
        }

        private static void SelectProfilerConnection(int connectionId)
        {
            var driverType = GetProfilerDriverType();
            var method = driverType.GetMethod("SetRemoteEditorConnection", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int) }, null)
                ?? throw new NotSupportedException("Profiler connection selection is unsupported in this Unity version.");
            method.Invoke(null, new object[] { connectionId });
        }

        private static bool SaveProfilerCapture(string savePath)
        {
            var driverType = GetProfilerDriverType();
            var method = driverType.GetMethod("SaveProfile", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null)
                ?? throw new NotSupportedException("Profiler capture saving is unsupported in this Unity version.");

            Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
            method.Invoke(null, new object[] { savePath });
            // Only a capture saved under Assets/ is an asset; a full refresh in a big project also compiled pending script edits.
            var assetsRoot = Path.GetFullPath(Path.Combine(ChievfxMcpToolPolicy.ProjectRoot, "Assets")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(savePath).StartsWith(assetsRoot, StringComparison.Ordinal))
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }

            return true;
        }

        // What the capture holds, so the reply answers "record and say what you see" without opening the file.
        private static Dictionary<string, object?>? SummarizeRecordedFrames()
        {
            try
            {
                var first = UnityEditorInternal.ProfilerDriver.firstFrameIndex;
                var last = UnityEditorInternal.ProfilerDriver.lastFrameIndex;
                if (first < 0 || last < first)
                {
                    return null;
                }

                // The first frame holds the start of the recording itself (session metadata, memory counters flush): it was
                // the maximum of a short capture while the game frames were a sixth of it.
                var firstCounted = last > first ? first + 1 : first;
                var sum = 0d;
                var max = 0f;
                var worstFrame = -1;
                var count = 0;
                for (var frame = firstCounted; frame <= last; frame++)
                {
                    using var view = UnityEditorInternal.ProfilerDriver.GetRawFrameDataView(frame, 0);
                    if (view == null || !view.valid)
                    {
                        continue;
                    }

                    sum += view.frameTimeMs;
                    if (view.frameTimeMs > max)
                    {
                        max = view.frameTimeMs;
                        worstFrame = frame;
                    }

                    count++;
                }

                return count == 0
                    ? null
                    : new Dictionary<string, object?>
                    {
                        ["frames"] = count,
                        ["frameMsAvg"] = Math.Round(sum / count, 2),
                        ["frameMsMax"] = Math.Round(max, 2),
                        ["worstFrame"] = worstFrame,
                        ["basis"] = "main thread time of each recorded frame (the first one, the recording start, left out); worstFrame is the frame index for "
                                    + "profiler-window-control selectedFrameIndex while the frames are still in the profiler; open the .data file for markers"
                    };
            }
            catch (Exception exception)
            {
                return new Dictionary<string, object?> { ["error"] = exception.Message };
            }
        }

        private static object[] GetProfilerTargets()
        {
            var driverType = GetProfilerDriverType();
            var method = driverType.GetMethod("GetAvailableProfilers", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (method == null)
            {
                return Array.Empty<object>();
            }

            if (!(method.Invoke(null, null) is Array rawTargets))
            {
                return Array.Empty<object>();
            }

            var targets = new List<object>();
            foreach (var rawTarget in rawTargets)
            {
                if (rawTarget == null)
                {
                    continue;
                }

                var id = Convert.ToInt32(rawTarget, CultureInfo.InvariantCulture);
                targets.Add(new
                {
                    id,
                    name = InvokeProfilerString(driverType, "GetConnectionName", id),
                    identifier = InvokeProfilerString(driverType, "GetConnectionIdentifier", id)
                });
            }

            return targets.ToArray();
        }

        private static string? InvokeProfilerString(Type driverType, string methodName, int connectionId)
        {
            var method = driverType.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int) }, null);
            if (method == null)
            {
                return null;
            }

            try
            {
                return method.Invoke(null, new object[] { connectionId }) as string;
            }
            catch (TargetInvocationException)
            {
                return null;
            }
        }

    }
}
