#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Chievfx.Mcp.Editor
{
    /// <summary>
    /// game-view-record: a video of the Game view in Play Mode, for a person to review a run (an agent reads storyboards,
    /// not videos). Frames come from the Game view's texture every 1/fps of real time and ffmpeg builds the mp4 once
    /// recording ends; the end is a journal event the reply names in waitFor. An editor in the background or under load
    /// renders few frames per second, and the video gets that many. Game time is left alone: stepping
    /// Time.captureFramerate lifts the frame cap, and heavy offscreen rendering then brings the editor down.
    /// </summary>
    internal sealed class GameViewRecordToolService : IChievfxMcpToolHandler
    {
        private const string ToolName = "game-view-record";
        private const string EventPrefix = "[ChievFX][record] #";
        private const string RecordNumberKey = "Chievfx.Mcp.RecordNumber";
        private const string OutputDirectory = "Logs/chievfx_video";
        private const float DefaultSeconds = 10f;
        private const float MaxSeconds = 60f;
        private const int DefaultFps = 15;
        private const int MinFps = 5;
        private const int MaxFps = 30;
        private const double FfmpegTimeoutSeconds = 120d;
        // How long the wait for the end may take beyond the recording itself: ffmpeg builds a minute of video in seconds.
        private const int EndWaitReserveMs = 60000;
        private const int FrameWidth = 960;
        private const int JpgQuality = 90;

        /// <summary>
        /// One recording: the frame folder, the timeline of captured frames and the ffmpeg build.
        /// </summary>
        private sealed class Recording
        {
            public int Number;
            public string Directory = string.Empty;
            public int Fps;
            public float Seconds;
            public double StartedAt;
            public double NextShotAt;
            public int LastFrame = -1;
            public int Shots;
            public readonly StringBuilder Timeline = new();
            public int Written;
            public string? StopReason;
            public double StoppedAt;
            public Process? Ffmpeg;
            public string? SavePath;
            public RenderTexture? Scaled;
            public Texture2D? Readback;
        }

        private static readonly Type? GameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
        private static readonly FieldInfo? GameViewTextureField =
            GameViewType?.GetField("m_RenderTexture", BindingFlags.Instance | BindingFlags.NonPublic);

        private static Recording? current;

        public bool TryRunTool(string toolName, JToken args, out object? result)
        {
            if (toolName != ToolName)
            {
                result = null;
                return false;
            }

            var arguments = args as JObject ?? new JObject();
            if (arguments.Value<bool?>("stop") == true)
            {
                if (current == null || current.StopReason != null)
                {
                    throw new InvalidOperationException("Not recording: nothing to stop.");
                }

                result = Stop(current, "stopped");
                return true;
            }

            if (EditorApplication.isPlaying == false)
            {
                throw new InvalidOperationException("game-view-record records Play Mode only: enter Play Mode first.");
            }

            result = Start(arguments);
            return true;
        }

        private static Dictionary<string, object?> Start(JObject args)
        {
            if (current != null)
            {
                throw new InvalidOperationException("Already recording: wait for '" + EventPrefix + current.Number + " ' or pass stop:true.");
            }

            var number = SessionState.GetInt(RecordNumberKey, 0) + 1;
            SessionState.SetInt(RecordNumberKey, number);
            var seconds = Mathf.Clamp(args.Value<float?>("seconds") ?? DefaultSeconds, 1f, MaxSeconds);
            var fps = Math.Clamp(args.Value<int?>("fps") ?? DefaultFps, MinFps, MaxFps);
            var directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", OutputDirectory,
                DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture) + "_" + number));
            System.IO.Directory.CreateDirectory(directory);
            current = new Recording
            {
                Number = number,
                Directory = directory,
                Fps = fps,
                Seconds = seconds,
                StartedAt = EditorApplication.timeSinceStartup,
                SavePath = args.Value<string?>("savePath")
            };
            EditorApplication.update += Update;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            var end = EventPrefix + number + " ";
            var reply = new Dictionary<string, object?>
            {
                ["summary"] = "Recording #" + number + ": " + seconds.ToString("0.#", CultureInfo.InvariantCulture) + " s at " + fps + " fps",
                ["waitFor"] = end,
                ["waitForFailed"] = end + "failed",
                ["waitTimeoutMs"] = (int)(seconds * 1000f) + EndWaitReserveMs
            };
            // With savePath the frames folder is removed once the mp4 is moved out of it: the end event names the file.
            var savePath = args.Value<string?>("savePath");
            if (string.IsNullOrEmpty(savePath))
            {
                reply["directory"] = directory;
            }
            else
            {
                reply["savePath"] = savePath;
            }

            return reply;
        }

        // An exception out of an update subscriber would cut off the subscribers after it (the MCP bridge among them), and a
        // write error (a full disk, a folder without access) would repeat every tick: the recording ends with a failed event.
        private static void Update()
        {
            try
            {
                Tick();
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogException(exception);
                var recording = current;
                if (recording != null)
                {
                    KillFfmpeg(recording);
                    Finish(recording, "failed:" + exception.GetType().Name + " " + exception.Message + " " + recording.Directory);
                }
            }
        }

        // A domain reload wipes the recorder's static state: the recording ends with an event now, ffmpeg stops, and the
        // frames taken so far stay in the folder.
        private static void OnBeforeAssemblyReload()
        {
            var recording = current;
            if (recording != null)
            {
                KillFfmpeg(recording);
                Finish(recording, "failed:domain_reload frames:" + recording.Shots + " " + recording.Directory);
            }
        }

        private static void KillFfmpeg(Recording recording)
        {
            var process = recording.Ffmpeg;
            if (process == null)
            {
                return;
            }

            try
            {
                if (process.HasExited == false)
                {
                    process.Kill();
                }
            }
            catch (InvalidOperationException)
            {
                // Exited between the check and the kill.
            }

            process.Dispose();
            recording.Ffmpeg = null;
        }

        private static void Tick()
        {
            var recording = current;
            if (recording == null)
            {
                EditorApplication.update -= Update;
                return;
            }

            if (recording.StopReason != null)
            {
                Build(recording);
                return;
            }

            if (EditorApplication.isPlaying == false)
            {
                Stop(recording, "play_mode_exited");
                return;
            }

            // The deadline goes before the wait for a new frame: a paused editor repeats one frame, and the recording would
            // never end otherwise.
            var now = EditorApplication.timeSinceStartup;
            if (now - recording.StartedAt >= recording.Seconds)
            {
                Stop(recording, "done");
                return;
            }

            // The editor may call update several times in one game frame: wait for the next one.
            if (Time.frameCount == recording.LastFrame || now < recording.NextShotAt)
            {
                return;
            }

            recording.LastFrame = Time.frameCount;
            recording.NextShotAt = now + 1d / recording.Fps;
            var name = "f" + recording.Shots.ToString("D5", CultureInfo.InvariantCulture);
            if (TryCaptureGameViewTexture(recording, Path.Combine(recording.Directory, name + ".jpg")))
            {
                name += ".jpg";
            }
            else
            {
                name += ".png";
                ScreenCapture.CaptureScreenshot(Path.Combine(recording.Directory, name));
            }

            var time = now - recording.StartedAt;
            recording.Timeline.Append(name).Append(' ').Append(time.ToString("F4", CultureInfo.InvariantCulture)).Append('\n');
            recording.Shots++;
        }

        // The Game view texture holds the last frame it rendered: a frame without a repaint repeats the previous one.
        private static bool TryCaptureGameViewTexture(Recording recording, string path)
        {
            if (GameViewType == null || GameViewTextureField == null)
            {
                return false;
            }

            var gameView = ScreenshotBridgeService.FindOpenGameView(GameViewType);
            if (gameView == null || GameViewTextureField.GetValue(gameView) is not RenderTexture source || source.width <= 0 || source.height <= 0)
            {
                return false;
            }

            var width = Math.Min(FrameWidth, source.width) & ~1;
            var height = Mathf.RoundToInt(source.height * (width / (float)source.width)) & ~1;
            if (recording.Scaled == null || recording.Readback == null || recording.Scaled.width != width || recording.Scaled.height != height)
            {
                ReleaseTextures(recording);
                recording.Scaled = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                recording.Readback = new Texture2D(width, height, TextureFormat.RGB24, false);
            }

            var previousActive = RenderTexture.active;
            try
            {
                // On D3D and Metal the Game view texture is upside down relative to ReadPixels.
                if (SystemInfo.graphicsUVStartsAtTop)
                {
                    Graphics.Blit(source, recording.Scaled, new Vector2(1f, -1f), new Vector2(0f, 1f));
                }
                else
                {
                    Graphics.Blit(source, recording.Scaled);
                }

                RenderTexture.active = recording.Scaled;
                recording.Readback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            }
            finally
            {
                RenderTexture.active = previousActive;
            }

            File.WriteAllBytes(path, recording.Readback.EncodeToJPG(JpgQuality));
            return true;
        }

        private static void ReleaseTextures(Recording recording)
        {
            if (recording.Scaled != null)
            {
                recording.Scaled.Release();
                UnityEngine.Object.DestroyImmediate(recording.Scaled);
                recording.Scaled = null;
            }

            if (recording.Readback != null)
            {
                UnityEngine.Object.DestroyImmediate(recording.Readback);
                recording.Readback = null;
            }
        }

        private static Dictionary<string, object?> Stop(Recording recording, string reason)
        {
            File.WriteAllText(Path.Combine(recording.Directory, "frames.txt"), recording.Timeline.ToString());
            // A ScreenCapture shot is written at the end of the frame: the last files appear by the next tick, and the
            // build reads them then.
            recording.StopReason = reason;
            recording.StoppedAt = EditorApplication.timeSinceStartup;
            var end = EventPrefix + recording.Number + " ";
            return new Dictionary<string, object?>
            {
                ["summary"] = "Recording #" + recording.Number + " stopping (" + reason + ") after " + recording.Shots + " frames",
                ["waitFor"] = end,
                ["waitForFailed"] = end + "failed",
                ["waitTimeoutMs"] = EndWaitReserveMs
            };
        }

        private static void Build(Recording recording)
        {
            if (recording.Ffmpeg == null)
            {
                var concat = BuildConcat(recording, out var written);
                if (written < 2)
                {
                    Finish(recording, "failed:no_frames shots:" + recording.Shots + " written:" + written + " " + recording.Directory);
                    return;
                }

                File.WriteAllText(Path.Combine(recording.Directory, "concat.txt"), concat);
                recording.Written = written;
                try
                {
                    recording.Ffmpeg = Process.Start(new ProcessStartInfo(ResolveFfmpeg(),
                        "-y -loglevel error -f concat -safe 0 -i concat.txt -vf \"scale=960:-2,format=yuv420p\" -c:v libx264 -crf 28 -r "
                        + recording.Fps + " video.mp4")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = recording.Directory
                    });
                }
                catch (Exception)
                {
                    Finish(recording, "frames_only " + recording.Directory + " (ffmpeg not found in PATH, /opt/homebrew/bin or /usr/local/bin) frames:" + written);
                }

                return;
            }

            var process = recording.Ffmpeg;
            if (process.HasExited == false)
            {
                if (EditorApplication.timeSinceStartup - recording.StoppedAt > FfmpegTimeoutSeconds)
                {
                    process.Kill();
                    process.Dispose();
                    recording.Ffmpeg = null;
                    Finish(recording, "failed:ffmpeg_timeout " + recording.Directory);
                }

                return;
            }

            var video = Path.Combine(recording.Directory, "video.mp4");
            var exitCode = process.ExitCode;
            var isBuilt = exitCode == 0 && File.Exists(video);
            process.Dispose();
            recording.Ffmpeg = null;
            if (isBuilt)
            {
                DeleteFrames(recording.Directory);
                DeleteQuietly(Path.Combine(recording.Directory, "concat.txt"));
            }

            if (isBuilt && string.IsNullOrEmpty(recording.SavePath) == false)
            {
                video = MoveToSavePath(video, recording.SavePath!);
            }

            // Frames follow real time: a slow editor shoots fewer than seconds * fps, and the video holds each one longer.
            var realSeconds = recording.StoppedAt - recording.StartedAt;
            var expectedFrames = (int)Math.Round(realSeconds * recording.Fps);
            Finish(recording, isBuilt
                ? recording.StopReason + " " + video + " frames:" + recording.Written + " of " + expectedFrames + " fps:" + recording.Fps
                  + " realSeconds:" + realSeconds.ToString("F1", CultureInfo.InvariantCulture)
                : "failed:ffmpeg_exit_" + exitCode + " " + recording.Directory);
        }

        // An editor started from the macOS Dock or Finder gets a short PATH without Homebrew's folders, so ffmpeg installed
        // there is looked up by its usual location too. Not found anywhere: the bare name, and the start fails as before.
        private static string ResolveFfmpeg()
        {
            var fileName = Application.platform == RuntimePlatform.WindowsEditor ? "ffmpeg.exe" : "ffmpeg";
            var folders = new List<string>((Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator));
            folders.AddRange(new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin" });
            foreach (var folder in folders)
            {
                if (string.IsNullOrWhiteSpace(folder))
                {
                    continue;
                }

                var candidate = Path.Combine(folder.Trim(), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return fileName;
        }

        // Frames only feed the build (a minute of video is up to 1800 of them). A frame some other process holds open must
        // not leave the recording unfinished.
        private static void DeleteFrames(string directory)
        {
            foreach (var frame in System.IO.Directory.GetFiles(directory, "f*.*"))
            {
                DeleteQuietly(frame);
            }
        }

        private static void DeleteQuietly(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Left behind; the folder is under Logs.
            }
            catch (UnauthorizedAccessException)
            {
                // Same as above.
            }
        }

        // A frame ScreenCapture did not write (the Game view did not render that frame) is replaced by the one before: its
        // time goes to the neighbour and the video keeps its length. The first written frame holds from the start.
        private static string BuildConcat(Recording recording, out int written)
        {
            var builder = new StringBuilder();
            written = 0;
            string? lastName = null;
            var lastTime = 0d;
            foreach (var line in recording.Timeline.ToString().Split('\n'))
            {
                var parts = line.Split(' ');
                if (parts.Length != 2 || File.Exists(Path.Combine(recording.Directory, parts[0])) == false
                    || double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var time) == false)
                {
                    continue;
                }

                if (lastName != null)
                {
                    builder.Append("file '").Append(lastName).Append("'\nduration ").Append((time - lastTime).ToString("F4", CultureInfo.InvariantCulture)).Append('\n');
                }

                lastName = parts[0];
                lastTime = written == 0 ? 0d : time;
                written++;
            }

            if (lastName != null)
            {
                // The last frame holds to the end of the recording: the video is not shorter than real time.
                var lastDuration = Math.Max(recording.StoppedAt - recording.StartedAt - lastTime, 1d / recording.Fps);
                builder.Append("file '").Append(lastName).Append("'\nduration ").Append(lastDuration.ToString("F4", CultureInfo.InvariantCulture)).Append('\n');
                builder.Append("file '").Append(lastName).Append("'\n");
            }

            return builder.ToString();
        }

        // savePath is a .mp4 file or a folder. The video moves there whole and its folder under Logs goes: a copy next to it
        // would double the disk use. A failed copy leaves the original, and the event says why.
        private static string MoveToSavePath(string video, string savePath)
        {
            try
            {
                var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                var target = Path.IsPathRooted(savePath) ? savePath : Path.Combine(projectRoot, savePath);
                target = Path.GetFullPath(target);
                if (target.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) == false)
                {
                    target = Path.Combine(target, Path.GetFileName(Path.GetDirectoryName(video)) + ".mp4");
                }

                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(video, target, true);
                try
                {
                    System.IO.Directory.Delete(Path.GetDirectoryName(video)!, true);
                }
                catch (IOException)
                {
                    // The copy is in place; the original folder stays under Logs.
                }
                catch (UnauthorizedAccessException)
                {
                    // Same as above.
                }

                return target;
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException
                                               || exception is ArgumentException || exception is NotSupportedException)
            {
                return video + " (savePath copy failed: " + exception.Message + ")";
            }
        }

        private static void Finish(Recording recording, string text)
        {
            ReleaseTextures(recording);
            current = null;
            EditorApplication.update -= Update;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            var level = text.StartsWith("failed", StringComparison.Ordinal) ? "warning" : "info";
            ChievfxMcpBridgeHost.EventJournal.Write("bridge", "record", level, EventPrefix + recording.Number + " " + text);
        }
    }
}
