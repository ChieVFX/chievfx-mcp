#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEditor.Compilation;
using Debug = UnityEngine.Debug;

namespace Chievfx.Mcp.Editor
{
    /// <summary>
    /// Carries a recompile request across a Play Mode exit.
    /// <para>
    /// Unity does not compile scripts on demand while Play Mode runs. Depending on the project's
    /// Editor setting "Script Changes While Playing", a <see cref="CompilationPipeline.RequestScriptCompilation"/>
    /// issued during play is either dropped (nothing compiles, yet the tool reports success) or parked
    /// as a pending compile that keeps <see cref="EditorApplication.isCompiling"/> true until play ends
    /// — which reads as a compile that never finishes and times the caller out. Either way the caller
    /// gets no diagnostics, which is the opposite of what `recompile` is for.
    /// </para>
    /// <para>
    /// So `recompile` leaves Play Mode first and re-issues the request from edit mode. The intent is
    /// kept in <see cref="SessionState"/> rather than a static field because leaving Play Mode
    /// normally triggers a domain reload, which wipes statics before the request could be re-issued.
    /// </para>
    /// </summary>
    internal static class BridgePendingRecompile
    {
        private const string SessionKey = "ChievfxMcpBridge.PendingRecompileAfterPlayModeExit.v1";

        // SessionState is a native call; the transport asks every editor tick, so keep the answer in
        // a static mirror and only touch SessionState when this domain has not read it yet.
        private static bool? cachedPending;

        public static bool IsPending
        {
            get
            {
                cachedPending ??= SessionState.GetBool(SessionKey, false);
                return cachedPending.Value;
            }
        }

        /// <summary>
        /// Leaves Play Mode and records that a compile is owed once the editor is back in edit mode.
        /// </summary>
        public static void RequestAfterPlayModeExit(BridgeEventJournal eventJournal)
        {
            SetPending(true);
            eventJournal.Write(
                "editor",
                "compile-request-deferred",
                "info",
                "MCP requested compilation while Play Mode was running. Exiting Play Mode first; the compile is re-requested from edit mode.",
                data: new Dictionary<string, object?>
                {
                    ["scriptChangesWhilePlaying"] = ScriptChangesWhilePlaying()
                });

            try
            {
                EditorApplication.ExitPlaymode();
            }
            catch (Exception ex)
            {
                // Leave the marker set: the tick handler still fires the compile if play mode ends
                // some other way (the user stopping it, say).
                Debug.LogWarning($"ChievFX MCP could not exit Play Mode for recompile. {ex.GetBaseException().Message}");
            }
        }

        /// <summary>
        /// Called from the bridge's editor tick. Fires the owed compile once the editor is settled in
        /// edit mode. Covers both exit paths — with a domain reload and, when Enter Play Mode Options
        /// suppress it, without one.
        /// </summary>
        public static void ProcessIfDue(BridgeEventJournal eventJournal)
        {
            if (!IsPending)
            {
                return;
            }

            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isUpdating)
            {
                return;
            }

            SetPending(false);

            // Unity kicks off the deferred compile itself when "Recompile After Finished Playing"
            // parked one. Re-requesting on top of that would compile twice, so treat the running
            // compile as the one we owed.
            if (EditorApplication.isCompiling)
            {
                eventJournal.Write(
                    "editor",
                    "compile-request",
                    "info",
                    "MCP recompile satisfied by the compile Unity started on leaving Play Mode.",
                    data: new Dictionary<string, object?>
                    {
                        ["resumedAfterPlayModeExit"] = true,
                        ["alreadyCompiling"] = true
                    });
                return;
            }

            try
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                CompilationPipeline.RequestScriptCompilation();
                eventJournal.Write(
                    "editor",
                    "compile-request",
                    "info",
                    "MCP requested Unity script compilation after leaving Play Mode.",
                    data: new Dictionary<string, object?>
                    {
                        ["resumedAfterPlayModeExit"] = true
                    });
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChievFX MCP could not request compilation after Play Mode exit. {ex.GetBaseException().Message}");
            }
        }

        // Values of Unity's ScriptChangesDuringPlayOptions, stable across versions.
        private const int RecompileAndContinuePlaying = 0;
        private const int RecompileAfterFinishedPlaying = 1;
        private const int StopPlayingAndRecompile = 2;

        /// <summary>The effective "Script Changes While Playing" setting, for status and diagnostics.</summary>
        public static string ScriptChangesWhilePlaying()
        {
            return ScriptChangesWhilePlayingValue() switch
            {
                RecompileAndContinuePlaying => "RecompileAndContinuePlaying",
                RecompileAfterFinishedPlaying => "RecompileAfterFinishedPlaying",
                StopPlayingAndRecompile => "StopPlayingAndRecompile",
                _ => "unknown"
            };
        }

        /// <summary>
        /// Reads the setting without binding to one Unity version's home for it: Unity 6 keeps it in the
        /// "ScriptCompilationDuringPlay" editor preference, while older editors exposed
        /// EditorSettings.scriptChangesDuringPlay (a project setting). Returns -1 when neither answers.
        /// </summary>
        private static int ScriptChangesWhilePlayingValue()
        {
            try
            {
                var property = typeof(EditorSettings).GetProperty(
                    "scriptChangesDuringPlay",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null)
                {
                    var value = property.GetValue(null);
                    if (value != null)
                    {
                        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    }
                }
            }
            catch (Exception)
            {
                // Fall through to the preference below.
            }

            try
            {
                if (EditorPrefs.HasKey(ScriptCompilationDuringPlayPrefKey))
                {
                    return EditorPrefs.GetInt(ScriptCompilationDuringPlayPrefKey, RecompileAndContinuePlaying);
                }

                // Absent key means the user never changed it, which is Unity's default.
                return RecompileAndContinuePlaying;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private const string ScriptCompilationDuringPlayPrefKey = "ScriptCompilationDuringPlay";

        /// <summary>
        /// True when Unity is holding a compile it cannot run because Play Mode is active, so
        /// "isCompiling" describes a queue rather than work in progress.
        /// </summary>
        public static bool IsCompileWaitingForPlayModeExit()
        {
            if (!EditorApplication.isPlaying)
            {
                return false;
            }

            if (IsPending)
            {
                return true;
            }

            // Only claim a queue when the setting actually defers compilation. Under
            // RecompileAndContinuePlaying (Unity's default) an in-play compile really does progress,
            // and under an unreadable setting a guess would be a false alarm.
            var mode = ScriptChangesWhilePlayingValue();
            return EditorApplication.isCompiling
                && (mode == RecompileAfterFinishedPlaying || mode == StopPlayingAndRecompile);
        }

        private static void SetPending(bool pending)
        {
            cachedPending = pending;
            SessionState.SetBool(SessionKey, pending);
        }
    }
}
