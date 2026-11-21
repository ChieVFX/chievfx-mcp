#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Chievfx.Mcp.Editor
{
    /// <summary>
    /// Black box of a Play session: while running, copies a small frame of the Game view into a ring buffer (every 0.5 s by
    /// default, so the last minute) and on request lays the buffer out as one storyboard image with a legend. A game state
    /// does not show how a run looked (a character stuck at a wall, the camera in the ground) - the storyboard does, in one
    /// image an agent can read. Extensions mark frames (a skill step, a task end: captured at once and always kept), add a
    /// state line to every frame, and watch captured frames for checks of their own.
    /// </summary>
    public static class ChievfxMcpFrameRecorder
    {
        private const double DefaultIntervalSeconds = 0.5d;
        private const double MinIntervalSeconds = 0.05d;
        private const double MaxIntervalSeconds = 2d;
        private const int Capacity = 120;
        private const int FrameWidth = 320;
        private const int MinFrameHeight = 90;
        private const int MaxFrameHeight = 400;
        private const int SheetColumns = 6;
        private const int SheetMaxFrames = 30;
        private const int JpgQuality = 80;
        private const string OutputDirectory = "Logs/chievfx_frames";
        private const int MaxSavedSheets = 200;
        private const int MaxReasonLength = 40;

        private static readonly RenderTexture?[] Frames = new RenderTexture?[Capacity];
        private static readonly double[] TimesSeconds = new double[Capacity];
        private static readonly string?[] Notes = new string?[Capacity];
        private static readonly string?[] States = new string?[Capacity];
        private static bool isRunning;
        private static bool isSubscribedToLifecycle;
        private static bool isStateProviderFailureLogged;
        private static int next;
        private static int count;
        private static int frameHeight;
        private static double intervalSeconds = DefaultIntervalSeconds;
        private static double nextCaptureAt;
        private static string? pendingNote;
        private static FieldInfo? gameViewRenderTextureField;

        // One line of state for every captured frame (position, health, speed): what the picture alone does not say.
        public static Func<string?>? StateProvider { get; set; }

        // Each captured frame, for checks of an extension's own (a black screen, a stuck camera). The texture is reused:
        // read it inside the handler.
        public static event Action<RenderTexture>? FrameCaptured;

        public static bool IsRunning => isRunning;

        public static int FrameCount => count;

        // How far back the buffer reaches at the current interval: the interval times the frames it holds.
        public static float WindowSeconds => (float)(intervalSeconds * Capacity);

        public static float IntervalSeconds => (float)intervalSeconds;

        // Start recording if it is not running yet. Only in Play Mode; recording ends with it.
        public static void EnsureRunning()
        {
            if (isRunning || EditorApplication.isPlaying == false || EditorApplication.isPlayingOrWillChangePlaymode == false)
            {
                return;
            }

            if (isSubscribedToLifecycle == false)
            {
                isSubscribedToLifecycle = true;
                EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
                AssemblyReloadEvents.beforeAssemblyReload += Stop;
            }

            isRunning = true;
            nextCaptureAt = 0d;
            EditorApplication.update += Tick;
        }

        public static void Stop()
        {
            if (isRunning)
            {
                isRunning = false;
                EditorApplication.update -= Tick;
            }

            for (var i = 0; i < Frames.Length; i++)
            {
                if (Frames[i] != null)
                {
                    Frames[i]!.Release();
                    UnityEngine.Object.DestroyImmediate(Frames[i]);
                    Frames[i] = null;
                }

                Notes[i] = null;
                States[i] = null;
            }

            next = 0;
            count = 0;
            frameHeight = 0;
            pendingNote = null;
        }

        // The interval for this Play session, clamped; returns the one applied. Starts recording.
        public static float SetInterval(float seconds)
        {
            intervalSeconds = Math.Max(MinIntervalSeconds, Math.Min(MaxIntervalSeconds, seconds));
            EnsureRunning();
            return (float)intervalSeconds;
        }

        // Capture a frame at the next editor tick and label it (a skill step, a task end). Labelled frames always make
        // it into the storyboard.
        public static void Mark(string note)
        {
            EnsureRunning();
            pendingNote = pendingNote == null ? note : pendingNote + " | " + note;
        }

        // Add a label to the newest captured frame: for a FrameCaptured handler whose finding belongs to that very frame.
        public static void AddNoteToLatestFrame(string note)
        {
            if (count == 0)
            {
                return;
            }

            var latest = (next - 1 + Capacity) % Capacity;
            Notes[latest] = Notes[latest] == null ? note : Notes[latest] + " | " + note;
        }

        // Save the storyboard of the buffer as a .jpg with a .txt legend next to it. Returns the path from the project
        // root, or an empty string when there are no frames yet.
        public static string Save(string reason)
        {
            if (TryBuildSheet(out var jpg, out var legend, out _, out _) == false)
            {
                return string.Empty;
            }

            return WriteSheet(jpg, legend, reason);
        }

        // coveredSeconds: from the oldest frame on the sheet to now, which is less than the window until the buffer fills.
        internal static bool TryBuildSheet(out byte[] jpg, out string legend, out int frames, out double coveredSeconds)
        {
            jpg = Array.Empty<byte>();
            legend = string.Empty;
            frames = 0;
            coveredSeconds = 0d;
            if (count == 0 || frameHeight <= 0)
            {
                return false;
            }

            var selected = SelectFrames();
            var rows = (selected.Count + SheetColumns - 1) / SheetColumns;
            var sheet = new Texture2D(SheetColumns * FrameWidth, rows * frameHeight, TextureFormat.RGB24, false);
            // Cells after the last frame stay black instead of a new texture's grey.
            sheet.SetPixels32(new Color32[sheet.width * sheet.height]);
            var previous = RenderTexture.active;
            var builder = new StringBuilder("Storyboard: frames left to right, top to bottom; time is seconds before the save.\n");
            var now = EditorApplication.timeSinceStartup;
            try
            {
                for (var i = 0; i < selected.Count; i++)
                {
                    var slot = selected[i];
                    RenderTexture.active = Frames[slot];
                    // A texture's origin is at the bottom; the sheet reads from the top.
                    sheet.ReadPixels(new Rect(0, 0, FrameWidth, frameHeight), i % SheetColumns * FrameWidth, (rows - 1 - i / SheetColumns) * frameHeight);
                    builder.Append(i + 1).Append(": ").Append((TimesSeconds[slot] - now).ToString("F1", CultureInfo.InvariantCulture)).Append(" s");
                    if (States[slot] != null)
                    {
                        builder.Append("  ").Append(States[slot]);
                    }

                    if (Notes[slot] != null)
                    {
                        builder.Append("  | ").Append(Notes[slot]);
                    }

                    builder.Append('\n');
                }

                RenderTexture.active = previous;
                sheet.Apply();
                jpg = sheet.EncodeToJPG(JpgQuality) ?? Array.Empty<byte>();
                legend = builder.ToString();
                frames = selected.Count;
                coveredSeconds = now - TimesSeconds[selected[0]];
                return jpg.Length > 0;
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(sheet);
            }
        }

        internal static string WriteSheet(byte[] jpg, string legend, string reason)
        {
            var directory = Path.Combine(Directory.GetCurrentDirectory(), OutputDirectory);
            Directory.CreateDirectory(directory);
            var name = DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture) + "_" + Sanitize(reason);
            File.WriteAllBytes(Path.Combine(directory, name + ".jpg"), jpg);
            File.WriteAllText(Path.Combine(directory, name + ".txt"), legend);
            TrimOldSheets(directory);
            return OutputDirectory + "/" + name + ".jpg";
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingPlayMode)
            {
                Stop();
            }
        }

        private static void Tick()
        {
            if (EditorApplication.isPlaying == false)
            {
                Stop();
                return;
            }

            var now = EditorApplication.timeSinceStartup;
            if (pendingNote == null && now < nextCaptureAt)
            {
                return;
            }

            if (TryGetGameViewTexture(out var source) == false)
            {
                return;
            }

            nextCaptureAt = now + intervalSeconds;
            Capture(source, pendingNote, now);
            pendingNote = null;
        }

        private static void Capture(RenderTexture source, string? note, double now)
        {
            var height = Mathf.Clamp(Mathf.RoundToInt(FrameWidth * source.height / (float)source.width), MinFrameHeight, MaxFrameHeight);
            if (height != frameHeight)
            {
                // A resized Game view changes the frame shape: frames of two shapes do not tile into one sheet.
                Stop();
                isRunning = true;
                EditorApplication.update += Tick;
                frameHeight = height;
            }

            var frame = Frames[next];
            if (frame == null)
            {
                frame = new RenderTexture(FrameWidth, frameHeight, 0) { name = "ChievFX frame recorder" };
                Frames[next] = frame;
            }

            if (SystemInfo.graphicsUVStartsAtTop)
            {
                Graphics.Blit(source, frame, new Vector2(1f, -1f), new Vector2(0f, 1f));
            }
            else
            {
                Graphics.Blit(source, frame);
            }

            TimesSeconds[next] = now;
            Notes[next] = note;
            States[next] = DescribeState();
            next = (next + 1) % Capacity;
            count = Math.Min(count + 1, Capacity);
            NotifyFrameCaptured(frame);
        }

        private static string? DescribeState()
        {
            if (StateProvider == null)
            {
                return null;
            }

            try
            {
                return StateProvider();
            }
            catch (Exception exception)
            {
                if (isStateProviderFailureLogged == false)
                {
                    isStateProviderFailureLogged = true;
                    Debug.LogWarning("ChievFX frame recorder: the state provider threw, frames go without a state line. " + exception.GetBaseException().Message);
                }

                return null;
            }
        }

        private static void NotifyFrameCaptured(RenderTexture frame)
        {
            var handlers = FrameCaptured;
            if (handlers == null)
            {
                return;
            }

            foreach (Action<RenderTexture> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(frame);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

        // The texture the Game view renders the game into: what screenshot-game-view captures, without repainting it.
        private static bool TryGetGameViewTexture(out RenderTexture source)
        {
            source = null!;
            var gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            if (gameViewType == null)
            {
                return false;
            }

            gameViewRenderTextureField ??= gameViewType.GetField("m_RenderTexture", BindingFlags.Instance | BindingFlags.NonPublic);
            var gameView = ScreenshotBridgeService.FindOpenGameView(gameViewType);
            if (gameView == null || gameViewRenderTextureField?.GetValue(gameView) is not RenderTexture texture || texture.width <= 0 || texture.height <= 0)
            {
                return false;
            }

            source = texture;
            return true;
        }

        // All labelled frames plus an even sample of the rest, in time order, at most SheetMaxFrames.
        private static List<int> SelectFrames()
        {
            var ordered = new List<int>(count);
            for (var i = 0; i < count; i++)
            {
                ordered.Add((next - count + i + Capacity) % Capacity);
            }

            if (ordered.Count <= SheetMaxFrames)
            {
                return ordered;
            }

            var marked = 0;
            foreach (var slot in ordered)
            {
                if (Notes[slot] != null)
                {
                    marked++;
                }
            }

            var plainBudget = Math.Max(1, SheetMaxFrames - marked);
            var plainTotal = ordered.Count - marked;
            var step = Math.Max(1f, plainTotal / (float)plainBudget);
            var selected = new List<int>(SheetMaxFrames);
            var plainIndex = 0;
            var nextPlain = 0f;
            foreach (var slot in ordered)
            {
                if (Notes[slot] != null)
                {
                    selected.Add(slot);
                    continue;
                }

                if (plainIndex >= nextPlain)
                {
                    selected.Add(slot);
                    nextPlain += step;
                }

                plainIndex++;
            }

            // More labels than places: the newest frames stay.
            if (selected.Count > SheetMaxFrames)
            {
                selected.RemoveRange(0, selected.Count - SheetMaxFrames);
            }

            return selected;
        }

        // The folder keeps the last MaxSavedSheets .jpg + .txt pairs instead of growing forever.
        private static void TrimOldSheets(string directory)
        {
            try
            {
                var files = new DirectoryInfo(directory).GetFiles("*.jpg");
                if (files.Length <= MaxSavedSheets)
                {
                    return;
                }

                Array.Sort(files, (left, right) => left.LastWriteTimeUtc.CompareTo(right.LastWriteTimeUtc));
                for (var i = 0; i < files.Length - MaxSavedSheets; i++)
                {
                    var textPath = Path.ChangeExtension(files[i].FullName, ".txt");
                    files[i].Delete();
                    if (File.Exists(textPath))
                    {
                        File.Delete(textPath);
                    }
                }
            }
            catch (IOException)
            {
                // A sheet open elsewhere stays; the next save trims again.
            }
            catch (UnauthorizedAccessException)
            {
                // Same as above.
            }
        }

        private static string Sanitize(string reason)
        {
            var builder = new StringBuilder(reason.Length);
            foreach (var symbol in reason)
            {
                builder.Append(char.IsLetterOrDigit(symbol) ? symbol : '_');
            }

            return builder.Length > MaxReasonLength ? builder.ToString(0, MaxReasonLength) : builder.ToString();
        }
    }
}
