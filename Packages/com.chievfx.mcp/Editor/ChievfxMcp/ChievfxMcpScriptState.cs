#nullable enable
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Chievfx.Mcp.Editor
{
    /// <summary>
    /// State and cleanup that outlive one script-execute call. Every call compiles a new assembly, so a script's
    /// own static fields start empty each time; values kept here last until the next domain reload.
    /// Cleanups undo what a script started (event handlers, update loops, spawned objects) so a long session does
    /// not pile them up: they run on <see cref="Clear"/>, before a domain reload, and, for the ones registered in
    /// Play Mode, when Play Mode ends (with domain reload disabled nothing else would stop them).
    /// </summary>
    public static class ChievfxMcpScriptState
    {
        private static readonly Dictionary<string, object?> Values = new(StringComparer.Ordinal);
        private static readonly List<(string Name, Action Undo, bool PlayModeOnly)> Cleanups = new();

        static ChievfxMcpScriptState()
        {
            AssemblyReloadEvents.beforeAssemblyReload += () => RunCleanups(playModeOnly: false);
            EditorApplication.playModeStateChanged += change =>
            {
                if (change == PlayModeStateChange.ExitingPlayMode)
                {
                    RunCleanups(playModeOnly: true);
                }
            };
        }

        public static string[] Keys
        {
            get
            {
                var keys = new string[Values.Count];
                Values.Keys.CopyTo(keys, 0);
                return keys;
            }
        }

        public static void Set(string key, object? value)
        {
            Values[key] = value;
        }

        // Missing key or another type: default, so a first call can start from scratch with ?? .
        public static T? Get<T>(string key)
        {
            return Values.TryGetValue(key, out var value) && value is T typed ? typed : default;
        }

        public static bool TryGet<T>(string key, out T value)
        {
            if (Values.TryGetValue(key, out var stored) && stored is T typed)
            {
                value = typed;
                return true;
            }

            value = default!;
            return false;
        }

        public static bool Remove(string key)
        {
            return Values.Remove(key);
        }

        // Same name replaces the earlier cleanup, so running a script twice does not stack two of them.
        public static void OnCleanup(string name, Action undo)
        {
            Cleanups.RemoveAll(cleanup => string.Equals(cleanup.Name, name, StringComparison.Ordinal));
            Cleanups.Add((name, undo, EditorApplication.isPlaying));
        }

        // Runs and forgets every cleanup; returns how many ran.
        public static int RunCleanups()
        {
            return RunCleanups(playModeOnly: false);
        }

        public static void Clear()
        {
            RunCleanups(playModeOnly: false);
            Values.Clear();
        }

        private static int RunCleanups(bool playModeOnly)
        {
            var due = Cleanups.FindAll(cleanup => !playModeOnly || cleanup.PlayModeOnly);
            Cleanups.RemoveAll(cleanup => !playModeOnly || cleanup.PlayModeOnly);
            // Newest first: a later script usually builds on what an earlier one set up.
            for (var i = due.Count - 1; i >= 0; i--)
            {
                try
                {
                    due[i].Undo();
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"[ChievFX MCP] script cleanup '{due[i].Name}' failed: {exception.GetBaseException().Message}");
                }
            }

            return due.Count;
        }
    }
}
