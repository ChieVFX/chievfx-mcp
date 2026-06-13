#nullable enable
using System.Collections;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Chievfx.Tests
{
    /// <summary>
    /// Play-mode sandbox for exercising the ChievFX MCP events-wait / events-check-since tools.
    /// Every Debug.Log becomes a source:log event; a log shaped MCPEventReachedLocation(name)
    /// becomes a type:marker event with marker=name. This component emits a deterministic
    /// timeline plus on-demand triggers so an agent can practice each matcher and recovery knob.
    ///
    /// Matchers to try:
    ///   contains  -> "[Sandbox] wave N starting"          (free-text log substring)
    ///   marker    -> "sandbox-wave-N", "sandbox-ready"     (exact planted beacon)
    ///   level     -> Warning / Error from the mid-timeline burst
    ///   sinceEventId / includeRecentMs -> the early "sandbox-ready" + race burst (R) demonstrate
    ///                                     the future-only cursor trap.
    /// Keys (new Input System): Space=manual marker, L=log line, W=warning, E=error, R=race burst.
    /// </summary>
    public sealed class EventsWaitSandbox : MonoBehaviour
    {
        private const string MarkerPrefix = "MCPEventReachedLocation(";
        private const string MarkerSuffix = ")";

        [Header("Timeline")]
        [Tooltip("Run the automatic wave timeline on Start.")]
        public bool autoRunTimeline = true;

        [Tooltip("Delay before the first wave fires.")]
        public float startDelaySeconds = 1f;

        [Tooltip("Seconds between waves. Use a value larger than your events-wait timeoutMs to test timeouts.")]
        public float waveIntervalSeconds = 5f;

        [Tooltip("Number of waves to emit before completing.")]
        public int waveCount = 5;

        [Tooltip("Emit a Warning + Error during the middle wave so level filters have something to catch.")]
        public bool emitWarningAndError = true;

        private void Awake()
        {
            // Keep play mode ticking while another app (e.g. the MCP client) holds focus, otherwise
            // the time-based timeline below stalls after Start whenever the Game view is unfocused.
            Application.runInBackground = true;
        }

        private void Start()
        {
            // Fires almost immediately: agents that did not capture a cursor before Play started will
            // miss this unless they pass includeRecentMs or a sinceEventId taken before entering Play.
            EmitMarker("sandbox-ready");
            EmitLog("[Sandbox] EventsWaitSandbox ready");

            if (autoRunTimeline)
            {
                StartCoroutine(RunTimeline());
            }
        }

        private IEnumerator RunTimeline()
        {
            if (startDelaySeconds > 0f)
            {
                yield return new WaitForSeconds(startDelaySeconds);
            }

            var midWave = Mathf.Max(1, waveCount / 2);
            for (var wave = 1; wave <= waveCount; wave++)
            {
                EmitLog($"[Sandbox] wave {wave} starting");
                EmitMarker($"sandbox-wave-{wave}");

                if (emitWarningAndError && wave == midWave)
                {
                    Debug.LogWarning("[Sandbox] sample warning (level=Warning)");
                    Debug.LogError("[Sandbox] sample error (level=Error)");
                }

                if (wave < waveCount && waveIntervalSeconds > 0f)
                {
                    yield return new WaitForSeconds(waveIntervalSeconds);
                }
            }

            EmitLog("[Sandbox] timeline complete");
            EmitMarker("sandbox-timeline-complete");
        }

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            var keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }

            if (keyboard.spaceKey.wasPressedThisFrame)
            {
                EmitMarker("sandbox-manual");
                EmitLog("[Sandbox] manual marker fired");
            }

            if (keyboard.lKey.wasPressedThisFrame)
            {
                EmitLog("[Sandbox] manual log line");
            }

            if (keyboard.wKey.wasPressedThisFrame)
            {
                Debug.LogWarning("[Sandbox] manual warning (level=Warning)");
            }

            if (keyboard.eKey.wasPressedThisFrame)
            {
                Debug.LogError("[Sandbox] manual error (level=Error)");
            }

            if (keyboard.rKey.wasPressedThisFrame)
            {
                // Race burst: log + marker fire in the same frame with no lead time, so a wait that
                // arms after this lands needs includeRecentMs (or an earlier sinceEventId) to catch it.
                EmitLog("[Sandbox] race burst");
                EmitMarker("sandbox-race");
            }
#endif
        }

        /// <summary>Emit a planted marker beacon (matchable via events-wait marker=name).</summary>
        public void EmitMarker(string markerName)
        {
            if (string.IsNullOrWhiteSpace(markerName))
            {
                return;
            }

            Debug.Log(MarkerPrefix + markerName.Trim() + MarkerSuffix);
        }

        /// <summary>Emit a plain log line (matchable via events-wait contains=substring).</summary>
        public void EmitLog(string message)
        {
            Debug.Log(message);
        }

        /// <summary>Emit an error log (matchable via the level filter / console error watchers).</summary>
        public void EmitError(string message)
        {
            Debug.LogError(message);
        }
    }
}
