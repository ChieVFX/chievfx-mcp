# events-wait sandbox fixture

Play-mode scene for trying the `events-wait` / `events-check-since` MCP tools against a known,
deterministic event stream before shipping the reshaped descriptors to production.

- Scene: `Assets/Scenes/EventsWaitSandbox/EventsWaitSandbox.unity`
- Driver: `Assets/Scenes/EventsWaitSandbox/EventsWaitSandbox.cs` (`Chievfx.Tests.EventsWaitSandbox`)

## How the events are produced

Every `Debug.Log` is captured by the bridge as a `source:log` event. A log shaped
`MCPEventReachedLocation(<name>)` is additionally tagged as `type:marker` with `marker=<name>`.
So the same component feeds both matchers:

| What fires | When | contains match | marker match | level |
| --- | --- | --- | --- | --- |
| `sandbox-ready` | immediately on Start | `EventsWaitSandbox ready` | `sandbox-ready` | Log |
| `sandbox-wave-N` | every `waveIntervalSeconds` (default 5s), N=1..5 | `wave N starting` | `sandbox-wave-N` | Log |
| warning + error | during middle wave (N=2) | `sample warning` / `sample error` | - | Warning / Error |
| `sandbox-timeline-complete` | after last wave | `timeline complete` | `sandbox-timeline-complete` | Log |
| `sandbox-manual` | Space key | `manual marker fired` | `sandbox-manual` | Log |
| `sandbox-race` | R key (no lead time) | `race burst` | `sandbox-race` | Log |

Keys (new Input System): `Space` manual marker, `L` log line, `W` warning, `E` error, `R` race burst.

Inspector knobs on the `EventsWaitSandbox` GameObject: `autoRunTimeline`, `startDelaySeconds`,
`waveIntervalSeconds`, `waveCount`, `emitWarningAndError`.

## Setup

1. Open the scene (Unity, or `scene-open` via MCP).
2. `bridge-get-status` -> note `lastEventId` (this is your pre-trigger cursor).
3. Enter Play mode (`editor-playmode-set isPlaying:true`). `editor-playmode-set` returns
   `eventCursorBefore` - use that as `sinceEventId` to catch events that fire during boot.

## Cases to run with each agent

### 1. contains (free-text log)
```
events-wait { "contains": "wave 1 starting", "timeoutMs": 15000 }
```
Expect `matched:true`, message `[Sandbox] wave 1 starting`.

### 2. marker (exact beacon)
```
events-wait { "marker": "sandbox-wave-2", "timeoutMs": 15000 }
```
Expect `matched:true`, `event.marker == "sandbox-wave-2"`.

### 3. Future-only cursor trap + includeRecentMs recovery
`sandbox-ready` fires within the first frame of Play. If you arm a wait for it *after* Play is
already running with a default cursor, it times out (it is below your cursor). Recover with:
```
events-wait { "marker": "sandbox-ready", "includeRecentMs": 3000, "timeoutMs": 5000 }
```
or by passing the `sinceEventId` captured before entering Play. On timeout, read
`result.diagnostic` - expect `matchBelowCursor` when the cursor was too late.

### 4. Race burst (R key)
Arm, then press `R`. Without `includeRecentMs` a wait armed *after* the burst misses it:
```
events-wait { "marker": "sandbox-race", "includeRecentMs": 2000, "timeoutMs": 8000 }
```

### 5. Timeout is a normal branch
```
events-wait { "marker": "does-not-exist", "timeoutMs": 2000 }
```
Expect `matched:false, timedOut:true` and no thrown error.

### 6. Recovery with events-check-since
After any wait, take its `sinceEventId` + `startedAtUtc` and confirm whether the target landed
inside that window:
```
events-check-since { "sinceEventId": <fromWait>, "sinceTimestampUtc": "<startedAtUtc>", "marker": "sandbox-wave-3" }
```

## What to evaluate per agent

- Does it pick the right matcher (`contains` vs `marker`) without being told the field semantics?
- Does it capture a cursor before triggering, or reach for `includeRecentMs` after a miss?
- Does it treat `timedOut:true` as a normal branch instead of an error?
- Does it use `events-check-since` for recovery rather than re-waiting blindly?
