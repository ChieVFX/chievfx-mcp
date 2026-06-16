# ChievFX Unity MCP Instructions

This project includes a local Unity MCP setup named `unity-mcp-chievfx`.
Cursor talks to `Tools/ChievfxMcp/chievfx_mcp_server.py`; that server forwards tool calls to the Unity editor bridge at `Library/ChievfxMcpBridge`.

The Unity editor bridge depends on `com.unity.nuget.newtonsoft-json` (Unity's official Newtonsoft.Json UPM package, version `3.2.2` or newer). This project's `Packages/manifest.json` already lists it; the [`Packages/com.chievfx.mcp/Install/`](Packages/com.chievfx.mcp/Install/) installer auto-adds it to other Unity projects when present-check fails.

## Setup And Connection

1. Open this Unity project and wait until compilation/domain reload completes.
2. Open `Window > ChievFX > MCP`.
3. Keep transport as `stdio` for normal local client use. Use `http` only when you need a long-running local HTTP server for manual testing.
4. Click `Start Bridge`.
5. Keep `Cursor` selected, or switch the client to `Claude Code` or `Codex`, then click `Write <client> Config`.
6. Reload your MCP client's tools or restart it. The server should appear as `unity-mcp-chievfx` or the project-unique `unity-<hash>` name.

The generated stdio config runs:

```json
{
  "mcpServers": {
    "unity-mcp-chievfx": {
      "type": "stdio",
      "command": "python3",
      "args": [
        "Tools/ChievfxMcp/chievfx_mcp_server.py",
        "--transport",
        "stdio",
        "--bridge-dir",
        "Library/ChievfxMcpBridge",
        "--timeout",
        "10000"
      ]
    }
  }
}
```

Paths written by the Unity window are absolute in the real `.cursor/mcp.json`, `.mcp.json`, or `.codex/config.toml`. These files are intentionally git-ignored because they are machine-local.

## Tool Discovery

Open `Window > ChievFX > MCP Tools` to choose advertised tools and inspect estimated descriptor-token cost. Tools are grouped into `Essentials`, `Autonomous`, `Editor Window`, `Scene`, `GameObject`, `Prefab`, `Package Manager`, `Script Execution / Tests`, and `Profiler`, and each category has controls to enable or disable its optional tools together. Required tools are locked enabled; optional tools can be enabled when a workflow needs them. The selection is stored in project-local user settings:

```text
UserSettings/ChievfxMcpToolSelection.json
```

Fresh `tools/list` from the ChievFX MCP server advertises enabled tools only. Default required tools:

- `screenshot-game-view`
- `screenshot-camera`
- `screenshot-editor-window`
- `bridge-get-status`
- `events-check-since`
- `events-wait`
- `assets-refresh`
- `recompile`
- `console-clear-logs`
- `console-get-logs`
- `reflection-method-find`
- `reflection-method-find-single`
- `reflection-method-call`
- `editor-window-list`
- `editor-window-open`
- `editor-window-focus`

Optional tools:

- `scene-list-opened`
- `scene-list-available`
- `scene-open`
- `scene-save`
- `gameobject-hierarchy`
- `gameobject-find`
- `gameobject-component-get`
- `gameobject-transform-get`
- `gameobject-transform-update`
- `gameobject-set-parent`
- `gameobject-duplicate`
- `prefab-open`
- `prefab-close`
- `prefab-save`
- `prefab-create`
- `prefab-instantiate`
- `package-list`
- `package-search`
- `package-add`
- `package-remove`
- `script-execute`
- `tests-run`
- `profiler-get-state`
- `profiler-start-recording`
- `profiler-stop-recording`
- `profiler-counters-get`

Token counts in the tool window are estimates, not exact billable request tokens. Descriptor availability cost uses this exact compact preview per tool:

```python
json.dumps(
    {"name": name, "description": description, "inputSchema": advertised_input_schema(tool)},
    ensure_ascii=False,
    separators=(",", ":"),
)
```

Tokens use optional Python `tiktoken` encoding `o200k_base` when installed, else deterministic fallback `ceil(utf8Bytes / 4)`. Descriptor byte count, SHA-256 hash, exact compact preview, and estimated tokens come from the same preview string. Advertised schemas omit verbose detail (`description`, `default`, `minimum`, `maximum`, `$defs`), compact Vector3 refs to object placeholders, and hide runtime-only/advanced knobs such as `outputFormat`, `assets-refresh.options`, `bridge-get-status.maxOperations`, log/test output controls, transform/profiler aliases, reflection target controls, and screenshot capture tuning. Manual calls can still pass supported hidden args.

The tool window also shows rough empty-call overhead from a compact JSON-RPC `tools/call` envelope with empty `arguments`; user-provided arguments are not included in that base. Real client/model tool-use blocks are hidden and can add model-specific overhead, often another small wrapper plus argument tokens, so the preview must not be treated as exact billing.

Response-size labels are guidance by output profile, not a promise: small scalar results are around `25-50` wrapped tokens, logs/events/method lists are often `100-300`, and hierarchy/package/test listings scale with row count and can reach `500-2000+`. Screenshot tools return image content; image and visual-token accounting is model/client specific and is not represented by the text token estimates.

Use `tools-list-categories` to inspect tool categories and `tools-list-category` to inspect tools in one category. Use `tools-set-enabled-state` to enable or disable optional categories or explicit optional tools through the same `UserSettings/ChievfxMcpToolSelection.json` store used by the MCP Tools window. After changing enabled tools, call `reload_cursor_mcp` for `unity-mcp-chievfx` or the full runtime id shown in `SERVER_METADATA.json` before using changed tool descriptors.

## Tool Roles

Roles are presets above manual category/tool controls. Applying a role enables its category list and explicit tool IDs, disables other optional tools, and keeps required tools locked enabled. Built-in roles live in `Tools/ChievfxMcp/chievfx_mcp_role_presets.json`: `Developer` and `QA`. Fresh installs default to `Developer` until a saved project-local selection exists.

Open `Window > ChievFX > MCP Tools` to apply roles, inspect missing categories/tools, see token totals, and create custom project roles. Custom roles are Unity `ChievfxMcpToolRoleAsset` assets under `Assets/ChievfxMcp/Roles` by default; create one from the MCP Tools window, edit it in Inspector, or save the current tool selection back into the asset. The selected role and modified/manual state are stored only in:

```text
UserSettings/ChievfxMcpToolSelection.json
```

Agents can switch roles programmatically:

```json
{ "name": "tools-set-role", "arguments": { "role": "qa" } }
```

Use `tools-get-roles` for current state and available built-in/custom roles. Use `customAssetPath` to apply a project role asset, for example `Assets/ChievfxMcp/Roles/MyRole.asset`. After role changes, call `reload_cursor_mcp` for `unity-mcp-chievfx` or the full runtime id shown in `SERVER_METADATA.json`; running server processes read the selection file at runtime, but Cursor caches `tools/list`.

If Cursor shows stale tools after changing selection, call `reload_cursor_mcp` first. If it is unavailable, reload MCP tools or restart Cursor. Stale descriptor caches can lag behind `Tools/ChievfxMcp/chievfx_mcp_server.py` and `UserSettings/ChievfxMcpToolSelection.json`. Existing `.cursor/mcp.json` does not embed tool selection; the MCP server reads selection at runtime.

Manual fresh list check:

```bash
python3 Tools/ChievfxMcp/chievfx_mcp_server.py --transport stdio
```

Then send JSON-RPC `initialize` followed by `tools/list`.

## Resource Discovery

Open `Window > ChievFX > MCP Resources` to choose advertised MCP resources/templates and inspect descriptor-token estimates. Static resources and templates are grouped into `Editor`, `Scene`, and `GameObject`; each category has controls to enable or disable optional rows together. Required resources are locked enabled. The selection is stored in project-local user settings:

```text
UserSettings/ChievfxMcpResourceSelection.json
```

Fresh `resources/list` and `resources/templates/list` advertise enabled resources only, plus a `chievfx://categories/<slug>` resource for each collapsed category. See `Packages/com.chievfx.mcp/Documentation~/ChievfxMcpAgentInstructions.md` for how `initialize.instructions` and category resources are assembled. No resources are required by default.

Static resources:

- `chievfx://editor/context`
- `chievfx://scene/opened`

Resource templates:

- `chievfx://scene/{scenePath}/go/{goPath}`
- `chievfx://scene/{scenePath}/go/{goPath}/component/{componentKey}`
- `chievfx://scene/all/go/{goPath}`
- `chievfx://scene/all/go/{goPath}/component/{componentKey}`

URI rule: encode every scene path, GameObject hierarchy path, and component key as one URI segment. Use percent-encoding with no safe slash, for example Python `quote(value, safe='')`. GameObject hierarchy paths keep ChievFX grammar: `/` separator, `\/` literal slash, `\\` literal backslash, and `[n]` duplicate suffix. Component keys use simple class names; duplicate simple names are suffixed 1-based, for example `BoxCollider.1`.

Token counts in the Resources window are estimates, not exact billable request tokens. Static resource availability cost uses this exact compact preview per resource:

```python
json.dumps(
    {"uri": uri, "name": name, "description": description, "mimeType": mime_type},
    ensure_ascii=False,
    separators=(",", ":"),
)
```

Template availability cost uses this exact compact preview per template:

```python
json.dumps(
    {"uriTemplate": uri_template, "name": name, "description": description, "mimeType": mime_type},
    ensure_ascii=False,
    separators=(",", ":"),
)
```

Tokens use optional Python `tiktoken` encoding `o200k_base` when installed, else deterministic fallback `ceil(utf8Bytes / 4)`. Descriptor byte count, SHA-256 hash, exact compact preview, and estimated tokens come from the same preview string.

The Resources window also shows rough `resources/read` base overhead from a compact JSON-RPC envelope with one `uri` param. Template read-envelope estimates use the advertised `uriTemplate` string as the sample URI; real encoded scene/GameObject/component path lengths are not included. Response-size labels are rough profiles only: editor context is usually status-sized, list resources scale with row count, and component/value resources can reach larger output sizes. Outputs are compact `text/plain` TOON with `readAt` metadata, drill-down URIs, truncation flags, and hard caps.

If Cursor shows stale resources after changing selection, reload MCP resources or restart Cursor. Stale resource-list caches can lag behind `Tools/ChievfxMcp/chievfx_mcp_server.py` and `UserSettings/ChievfxMcpResourceSelection.json`. Existing `.cursor/mcp.json` does not embed resource selection; the MCP server reads selection at runtime. Cursor may cache resource lists, so treat the selection file as source of truth when checking freshness.

## Output Format

Text tools default to compact TOON-like output:

```json
{ "outputFormat": "toon" }
```

Use compact JSON when exact structure matters:

```json
{ "outputFormat": "json" }
```

Large text fields are trimmed. Console logs and reflection results also have default and hard limits, so prefer narrow filters before increasing result counts.

## Common Workflows

### Refresh Assets

Use `assets-refresh` for non-script assets created or modified outside Unity APIs: textures, shaders, materials, FBX/model files, prefabs, and similar imported assets. Use `recompile` for C# scripts and assembly definition changes.

```json
{"folder":"Assets/Art","type":"Texture"}
```

Target by `path`, `folder`, `pathContains`, `type`, or `extensions`. Folder searches are recursive and skip script/asmdef-style assets. Hidden legacy/manual `options` support remains available for direct calls that need a specific `UnityEditor.ImportAssetOptions` value: `Default`, `ForceUpdate`, `ForceSynchronousImport`, `ImportRecursive`, `DontDownloadFromCacheServer`, `ForceUncompressedImport`.

### Recompile

Use `recompile` after script edits when Unity must do a full compile. The MCP server waits for the bridge/editor to be idle, requests compilation immediately, then returns only after Unity reports compile/import idle again.

```json
{"timeoutMs":300000}
```

### Console Logs

Use `console-clear-logs` before a scenario to isolate new messages. It clears the ChievFX MCP log cache and Unity developer console.

Use `console-get-logs` after the scenario. Defaults are intentionally quiet:

- `levels`: `Error`, `Exception`, `Assert`, `Warning`
- `maxEntries`: 50, clamped to 200
- `lastMinutes`: 10
- `stackTrace`: `none`

Do not use `contains: "error"` or `contains: "exception"` to hunt console problems unless you truly need that substring in the message. Those exact single-token values are reinterpreted as severity filters (Error, Exception, Assert). For message text search, pass a longer phrase such as `contains: "error CS0234"`.

Level aliases:

- `ConsoleErrors`: `Error`, `Exception`, `Assert` (Unity console red/error filter)
- `ConsoleIssues`: `Error`, `Exception`, `Assert`, `Warning`

Useful filtered calls:

```json
{
  "levels": ["Warning", "Error", "Exception"],
  "contains": "ChievFX",
  "stackTrace": "firstLine",
  "maxEntries": 25
}
```

Use `stackTrace: "full"` only when needed; full stacks can consume output budget quickly. `logTypeFilter` still works for one legacy level, but `levels` is preferred.

### Event Stream And Waits

Use `events-wait` when an agent needs to react to a specific Unity activity without repeatedly dumping console logs. Use `events-check-since` to recover after a wait timeout/cancel and ask whether the same target happened inside the original wait window. This is the MCP "event subscription" workflow: it is implemented as Python-side long polling over durable files, not as Unity-side in-memory subscribers.

When delegating `events-wait` or `events-check-since` to a Cursor subagent, do not launch that subagent in read-only/Ask mode. MCP tool calls are blocked there even for read-like event tools. Pass the exact MCP tool descriptor path and the final tool arguments to the subagent so it can validate schema before calling.

Unity writes a compact event stream here:

```text
Library/ChievfxMcpBridge/events.json
```

The Python MCP server reads that file directly. Because waits live in Python and the stream is durable, an active `events-wait` can survive Unity domain reload and still match events written after reload.

Each event has:

- `eventId`: monotonic cursor.
- `timestamp`: UTC ISO timestamp.
- `source`: usually `log`, `bridge`, `editor`, or `structured`.
- `type`: event kind, for example `message`, `marker`, `request-state`, `compile-start`, `compile-finish`, `domain-reload-before`, `domain-reload-after`, `domain-reload-restored`, `asset-refresh-scheduled`, `asset-refresh-finish`, `package-start`, `package-finish`, `test-run-start`, `test-run-finish`.
- `level`: log level or severity-like string.
- `message`: trimmed event text.
- `marker`: optional exact marker parsed from logs.
- `operationId`: optional MCP bridge operation id.
- `data`: optional structured payload when `includeData` is true.

Unity logs still go through the normal console collector. When a log message exactly matches:

```text
MCPEventReachedLocation(<location_marker>)
```

the bridge writes it as `source: "log"`, `type: "marker"`, and `marker: "<location_marker>"`. General console history belongs to `console-get-logs`; event tools should be used for specific markers, bridge/editor lifecycle events, or operation state changes. Marker values may contain punctuation and spaces, but must be at most 256 characters and cannot contain newlines or NUL.

Use `events-check-since` for focused recovery after a wait window:

```json
{
  "sinceEventId": 124,
  "sinceTimestampUtc": "2026-05-04T07:41:12Z",
  "type": "marker",
  "marker": "spawn-complete",
  "includeData": true,
  "maxEntries": 12
}
```

`events-check-since` returns `matched`, `events`, `count`, `hasMore`, `sinceEventId`, `sinceTimestampUtc`, `lastEventId`, and `truncatedBeforeEventId`. Events must be newer than both the supplied cursor and timestamp, so stale markers/logs from before the original wait window do not match.

Use `events-wait` as a subscription-like wait for the next matching event:

```json
{
  "marker": "spawn-complete",
  "timeoutMs": 10000,
  "includeData": true
}
```

By default, `events-wait` starts from the current stream `lastEventId`, so it does not match stale markers already present in the file. It polls the event file every 50 ms until match, timeout, or client cancellation.

Successful wait result:

```json
{
  "matched": true,
  "timedOut": false,
  "event": {
    "eventId": 123,
    "source": "log",
    "type": "marker",
    "level": "Log",
    "message": "MCPEventReachedLocation(spawn-complete)",
    "marker": "spawn-complete"
  },
  "sinceEventId": 122,
  "startedAtUtc": "2026-05-04T07:41:12Z",
  "lastEventId": 124,
  "elapsedMs": 42,
  "bridgeState": {}
}
```

Timeout is not an error:

```json
{
  "matched": false,
  "timedOut": true,
  "event": null,
  "sinceEventId": 122,
  "startedAtUtc": "2026-05-04T07:41:12Z",
  "lastEventId": 124,
  "elapsedMs": 10000,
  "bridgeState": {}
}
```

Use `sinceEventId` only when you intentionally want to wait from a known cursor:

```json
{
  "sinceEventId": 124,
  "source": "editor",
  "type": "compile-finish",
  "timeoutMs": 30000
}
```

Use `includeRecentMs` only when matching a recent event that may have happened just before the wait call:

```json
{
  "marker": "spawn-complete",
  "includeRecentMs": 1000,
  "timeoutMs": 5000
}
```

Filters are literal, not regex. `source`, `type`, and `level` compare case-insensitively. `contains` is a case-insensitive substring match against `message`. `marker` is an exact match. Filter strings are capped at 256 characters and reject newlines/NUL.

Recommended agent pattern:

1. Trigger Unity action or arm watcher before triggering it.
2. Call `events-wait` with `marker`, `type`, or `contains`.
3. Save returned `sinceEventId` and `startedAtUtc`.
4. If needed, call `events-check-since` with those values and the same filters to recover from timeout/cancel or descriptor/client interruptions.
5. Treat `timedOut: true` as a normal branch, not a thrown tool error.

Event retention is bounded. Unity keeps at most 1000 events and trims the stream to roughly 512 KB; long messages and data strings are truncated. If `sinceEventId` is older than `truncatedBeforeEventId`, the old events are gone and the client should continue from `lastEventId`.

### Subagent Unity QA Orchestration

No custom Cursor extension is required for the MVP. Use normal Cursor background subagents plus the durable ChievFX event stream when the main agent needs several Unity watchers running at the same time. This is enough when watchers only need MCP tool calls, event waits, screenshots, profiler capture, console summaries, or focused script/test actions. Build a custom extension only if the workflow needs a persistent UI, shared agent memory outside the chat/task, or non-Cursor automation.

Recommended roles:

- Test lifecycle watcher: wait for Unity test events and report start/finish, failures, and timeout branch.
- Console error watcher: monitor new `Error`, `Exception`, and `Assert` logs during the run.
- Marker/snapshot watcher: wait for scenario markers, then capture `screenshot-game-view`, `screenshot-camera`, or profiler counters.
- Marker-driven script intervention watcher: wait for a marker, then run a focused trusted `script-execute` intervention or another narrow tool call.
- Final summary collector: gather final `console-get-logs`, focused `events-check-since` results, test output, screenshot/profiler paths, watcher timeouts, and cleanup state into one handoff.

Main agent launch pattern:

1. Generate a unique run id and marker names.
2. Launch watcher subagents in background before triggering Unity work. Do not mark MCP watcher subagents read-only; give each subagent its role, tool descriptor path, event filter, timeout/chunk policy, and expected final fields.
3. Trigger the test, play mode scenario, `script-execute`, or manual Unity action.
4. Let each watcher do exactly one narrow job, then return a compact result: matched event, timeout/cancel state, follow-up artifacts, and next cursor.
5. Run the final summary collector after scenario completion or cancellation, then decide pass/fail from watcher outputs plus final console and focused event-check data.

Exact event filters for common watchers:

```json
{ "type": "test-run-start", "timeoutMs": 1000000 }
```

```json
{ "type": "test-run-finish", "timeoutMs": 1000000, "includeData": true }
```

```json
{ "source": "log", "level": "Error", "timeoutMs": 60000, "includeData": true }
```

```json
{ "source": "log", "level": "Exception", "timeoutMs": 60000, "includeData": true }
```

```json
{ "source": "log", "level": "Assert", "timeoutMs": 60000, "includeData": true }
```

```json
{ "source": "log", "type": "marker", "marker": "qa-ready-for-snapshot", "timeoutMs": 60000 }
```

```json
{ "source": "editor", "type": "compile-start", "timeoutMs": 60000 }
```

```json
{ "source": "editor", "type": "compile-finish", "timeoutMs": 1000000, "includeData": true }
```

```json
{ "source": "editor", "type": "domain-reload-before", "timeoutMs": 60000 }
```

```json
{ "source": "editor", "type": "domain-reload-after", "timeoutMs": 1000000 }
```

The same filters work with `events-check-since`; add `sinceEventId`, `sinceTimestampUtc`, and `maxEntries` when checking a previous wait window instead of waiting.

Current `events-wait` supports waits up to `1000000` ms. Prefer one long wait for a watcher that truly owns the wait; otherwise use 60 second chunks so the subagent can periodically inspect `bridge-get-status`, report liveness, and stop cleanly if the main agent cancels the run. In both modes, treat `timedOut: true` as a normal branch: record it, refresh status, decide whether to re-arm, collect summary, or fail the scenario.

Stale marker avoidance matters. By default, `events-wait` starts after the current stream `lastEventId`, so it ignores old markers. Use `sinceEventId` when the main agent captured a cursor before triggering the scenario and wants every watcher to start from that point. Use `includeRecentMs` only for races where the event might have landed just before the watcher armed; keep it small, for example `500` to `2000`, and avoid it for unique scenario markers unless needed.

For long runs, use the cursor and timestamp returned by `events-wait`:

```json
{
  "sinceEventId": 124,
  "sinceTimestampUtc": "2026-05-04T07:41:12Z",
  "marker": "qa-ready-for-snapshot",
  "maxEntries": 12
}
```

Then pass those values to `events-check-since` when a watcher needs to know whether its target happened after it first armed. `events-check-since` returns `hasMore`, `lastEventId`, and `truncatedBeforeEventId`; if the requested cursor is older than `truncatedBeforeEventId`, the stream has already trimmed those events. Continue from current `lastEventId`, note the truncation in the final summary, and rely on `console-get-logs`, test results, and saved artifacts for older context.

Use `bridge-get-status` before and during orchestration. Common calls pass `{}`; manual calls may pass hidden `maxOperations` to change recent operation row count. It reports bridge heartbeat, busy flags, active operations, recent requests, `lastEventId`, active event waits, stale files, and wait capacity. If a watcher is stuck, `bridge-get-status` tells whether Unity is compiling, script execution is still busy, request files are stale, or too many `events-wait` calls are active.

Descriptor cache issues are expected during active tool development. If `events-check-since`, `events-wait`, `bridge-get-status`, `script-execute`, `tests-run`, or profiler tools are missing from Cursor even though project docs mention them, first enable the relevant category with `tools-set-enabled-state` if that tool is available. Then call `reload_cursor_mcp` for `unity-mcp-chievfx` (or the full runtime id) so Cursor reads fresh descriptors from `Tools/ChievfxMcp/chievfx_mcp_server.py` and `UserSettings/ChievfxMcpToolSelection.json`. If descriptors are still stale, use `unity-mcp-cli run-tool <tool>` as the local fallback, or reduce the run to available required tools and explicitly report the missing descriptor gap.

Cancellation and cleanup:

- Main agent owns the run id, unique marker names, and final decision.
- Background watcher subagents should use finite `timeoutMs`, report `timedOut`, `matched`, `cancelled`, and returned `lastEventId`, then exit.
- If the main agent cancels the task, stop launching new watcher calls and let active waits receive client cancellation or hit their finite timeout.
- After tools that start recording or mutate state, always schedule a matching stop/save/summary step. For profiler flow, pair `profiler-start-recording` with `profiler-stop-recording` and include the saved path.
- End with `console-get-logs` for `Error`, `Exception`, and `Assert`, plus focused `events-check-since` calls for any watcher targets that need recovery.

Agent event self-test:

Use this when validating event tooling, descriptor reloads, or agent orchestration. Choose a unique marker such as `agent-event-check-since-YYYYMMDD-HHMM`. Launch a write-capable background subagent, pass it the `events-wait` descriptor path and exact arguments, and have it wait for:

```json
{
  "source": "log",
  "contains": "MCPEventReachedLocation(agent-event-check-since-YYYYMMDD-HHMM)",
  "timeoutMs": 60000
}
```

After `bridge-get-status` shows an active event wait, trigger the marker with `script-execute`:

```json
{
  "csharpCode": "using UnityEngine;\npublic static class Script\n{\n    public static string Main()\n    {\n        const string marker = \"agent-event-check-since-YYYYMMDD-HHMM\";\n        Debug.Log($\"MCPEventReachedLocation({marker})\");\n        return marker;\n    }\n}"
}
```

The subagent should return `matched: true`, the marker event, `sinceEventId`, `startedAtUtc`, `lastEventId`, and `elapsedMs`. Then call `events-check-since` with the returned `sinceEventId`, `startedAtUtc` as `sinceTimestampUtc`, and the same marker/contains filter. It should return `matched: true` and the same marker event. If the subagent reports Ask/read-only permission errors, relaunch it without read-only mode.

Minimal marker-driven QA harness:

1. Main agent chooses a unique marker, for example `qa-smoke-20260427-0958`, and starts a marker watcher:

```json
{
  "source": "log",
  "type": "marker",
  "marker": "qa-smoke-20260427-0958",
  "timeoutMs": 60000,
  "includeData": true
}
```

Call tool: `events-wait`.

2. Main agent or another subagent triggers the marker with trusted `script-execute`:

```json
{
  "csharpCode": "using UnityEngine;\npublic class Script\n{\n    public static string Main()\n    {\n        Debug.Log(\"MCPEventReachedLocation(qa-smoke-20260427-0958)\");\n        return \"marker logged\";\n    }\n}",
  "timeoutMs": 5000,
  "includeLogs": true
}
```

3. Marker watcher receives `matched: true`, saves returned `event.eventId` and `lastEventId`, then immediately performs the follow-up action:

```json
{}
```

Call tool: `screenshot-game-view`. It defaults to `maxDimension: 960`; raise or lower that single longest-side cap only when useful. If visual timing matters, also start/stop profiler recording around the marker or call `profiler-counters-get`.

4. Final summary collector runs:

```json
{
  "levels": ["Error", "Exception", "Assert"],
  "maxEntries": 50,
  "stackTrace": "firstLine",
  "lastMinutes": 10
}
```

Call tool: `console-get-logs`.

Then check any important watcher target since its wait window:

```json
{
  "sinceEventId": 124,
  "sinceTimestampUtc": "2026-05-04T07:41:12Z",
  "marker": "qa-smoke-20260427-0958",
  "maxEntries": 12,
  "includeData": true
}
```

Call tool: `events-check-since`.

The final handoff should include watcher role results, any `timedOut: true` branches, last event cursor, console error counts, screenshot/profiler artifact paths, descriptor-cache fallbacks used, and cleanup status.

Known gaps and risks:

- Cursor may keep stale MCP descriptors until `reload_cursor_mcp` refreshes this server or Cursor restarts.
- Event retention is bounded, so very noisy or long runs can truncate early events.
- `includeRecentMs` can match a stale marker if marker names are reused; prefer unique run markers.
- `script-execute` is high risk and optional; use trusted local snippets only and keep timeouts short.
- Many concurrent waits can hit event-wait capacity; share watcher roles or use `events-check-since` for recovery after finite waits.
- Screenshot and profiler tools verify state at capture time only; they do not replace test assertions or console summaries.

### Reflection Find And Call

Use `reflection-method-find` first. It returns compact indexed signature rows. Filters match exactly by default; pass `"match": "contains"` for fuzzy discovery. Namespace filters are treated as known/exact unless `knownNamespace` is set explicitly. Use `page` with `maxResults` to continue through large result sets. For nested types, prefer the nested class simple name (`TestClass`) or CLR nested form (`Outer+Inner`); dotted source form (`Outer.Inner`) may not match.

```json
{
  "filter": {
    "namespace": "Chievfx.Mcp.Editor",
    "typeName": "ChievfxMcpBridge",
    "methodName": "GetProfilerState"
  },
  "match": "exact",
  "maxResults": 10,
  "page": 1
}
```

Use `reflection-method-find-single` with the same query plus page-local zero-based `index` when you need full method info and a reusable `callFilter`:

```json
{
  "filter": {
    "methodName": "TestMethod"
  },
  "match": "contains",
  "maxResults": 10,
  "page": 1,
  "index": 0
}
```

Then use `reflection-method-call` with the matching filter and serialized `inputParameters`. For instance methods, pass `targetObject.value` as JSON that can deserialize into the declaring type:

```json
{
  "filter": {
    "namespace": "Chievfx.Mcp.Editor.Tests.Auxillary",
    "typeName": "TestClass",
    "methodName": "TestMethod"
  },
  "knownNamespace": true,
  "targetObject": {
    "value": {
      "TestProperty": 123
    }
  },
  "inputParameters": []
}
```

Safety/current limits:

- Generic methods are rejected.
- `ref`/`out` parameters are rejected.
- Unsafe pointer parameters and pointer return values are rejected.
- Instance calls require `targetObject`.
- `UnityEngine.Object` instance calls through `targetObject` are unsupported; use static methods or a dedicated MCP tool.
- Return values are compacted; Unity objects return `instanceId` and `name`.

Prefer dedicated tools over reflection for asset, scene, object, and profiler operations.

### Scene

Enable the optional `Scene` category when you need scene inventory or open/save control. Use `scene-list-opened` for current editor state and `scene-list-available` to find project scene assets under `Assets/`:

```json
{ "filter": "Sample", "searchInFolders": ["Assets"], "maxResults": 50 }
```

Open scenes additively when you want to preserve the current editor setup:

```json
{ "scenePath": "Assets/Scenes/Sample.unity", "mode": "Additive" }
```

`scene-open` with `mode: "Single"` refuses to discard dirty open scenes by default. Pass `saveDirtyScenes: true` only when saving those dirty scenes first is intentional. Use `scene-save` with no arguments to save the active scene, or pass `openedSceneName`/`path` when you need a specific opened scene or save-as path under `Assets/`.

### GameObject Read/Query

Enable the optional `GameObject` category when you need read-only GameObject discovery. These tools inspect the current active scene; when Unity is in prefab stage, they prefer the opened prefab contents and report `source: "prefabStage"` in output.

Start broad, then resolve exact refs:

```json
{ "maxDepth": 2, "maxResults": 50 }
```

Call `gameobject-hierarchy` for compact structure. Pass `path` to inspect a subtree. Paths are deterministic hierarchy paths like `Root/Enemy[2]/Mesh`; duplicate sibling names get one-based indexes. If a plain path is ambiguous, use the indexed path from output or `instanceId`.

Find candidates with narrow filters:

```json
{ "namePattern": "Enemy*", "includeInactive": true, "maxResults": 25 }
```

`gameobject-find` supports `path`, `instanceId`, exact `name`, wildcard `namePattern`, `componentType`, `includeInactive`, `maxResults`, `includeDetails`, and `includeComponents`. Results include compact refs by default; pass `includeDetails:true` for tag/layer/children detail, or `includeComponents:true` to also include component summaries. With `path` or `instanceId`, detail requests resolve that exact object. `gameobject-component-get` reads one component by `componentType` and includes inspector-visible serialized state by default. Pass `includeSerializedData:false` for identity only, or `isDebug:true` for debug serialized traversal.

Keep outputs small. Prefer low `maxDepth`/`maxResults`, filter by `path` or `componentType`, and only include components/serialized data when necessary. GameObject tools return `truncated` and `totalMatches`/`totalObjects` metadata when caps are hit.

### GameObject Mutation

GameObject mutation tools are in the same optional `GameObject` category and are disabled by default with the rest of the category. Use them for focused Transform and hierarchy edits only; component field edits, destroy, script execution, and test running are intentionally separate tasks/tools. Prefab asset workflows live in the `Prefab` category; package changes live in the `Package Manager` category.

Safe workflow: call `gameobject-find` first, copy the exact `instanceId` or indexed `path`, then mutate that one object. These tools register Undo where practical, mark edited scene/prefab objects dirty, and repaint editor views. Use `gameobject-transform-get` or `gameobject-find` to verify after mutation when needed.

Update supplied Transform fields:

```json
{
  "instanceId": 12345,
  "position": { "x": 0, "y": 1.5, "z": 0 },
  "rotationEuler": { "x": 0, "y": 45, "z": 0 },
  "scale": { "x": 1, "y": 2, "z": 1 }
}
```

Use `gameobject-transform-get` to inspect `position`, `rotationEuler`, and `scale`. Use `gameobject-transform-update` to set any subset of those fields. `isWorld:false` is local space; `isWorld:true` is world space.

Move under another object, or omit/null the parent fields to unparent to scene root:

```json
{
  "path": "Root/Enemy[2]",
  "parentInstanceId": 67890,
  "worldPositionStays": true
}
```

Duplicate one object. Returned path and id can be passed to `gameobject-find`.

```json
{
  "instanceId": 12345,
  "newName": "EnemyCopy"
}
```

### Prefab

Enable the optional `Prefab` category when you need focused prefab asset workflows. Prefab tools are disabled by default like other optional categories, and they reuse GameObject `path`/`instanceId` conventions for scene or prefab-stage objects they return.

Create a prefab from exactly one scene or prefab-stage GameObject:

```json
{
  "sourcePath": "Root/Enemy",
  "prefabPath": "Assets/Prefabs/Enemy.prefab",
  "overwrite": false,
  "connectGameObjectToPrefab": true
}
```

`prefabPath` must start with `Assets/`, end with `.prefab`, and use an existing parent folder. Existing assets are refused unless `overwrite: true` is passed.

Open, edit, save, and close a prefab stage:

```json
{ "prefabPath": "Assets/Prefabs/Enemy.prefab" }
```

Call tool: `prefab-open`. Then use GameObject tools against the prefab stage, call `prefab-save`, and close with:

```json
{ "saveBeforeClose": true }
```

`prefab-open` refuses to replace a dirty current prefab stage. `prefab-close` refuses to close dirty contents unless `saveBeforeClose: true` is explicit. `prefab-save` fails clearly when no prefab stage is open.

Instantiate a prefab into the active scene, or into the current prefab stage when one is open:

```json
{
  "prefabPath": "Assets/Prefabs/Enemy.prefab",
  "parentPath": "Root/SpawnPoint",
  "name": "EnemyInstance",
  "position": { "x": 0, "y": 0, "z": 0 },
  "rotationEuler": { "x": 0, "y": 90, "z": 0 },
  "scale": { "x": 1, "y": 1, "z": 1 }
}
```

Returned GameObject paths are deterministic and can be passed to `gameobject-find`.

### Package Manager

Enable the optional `Package Manager` category only when you need Unity Package Manager reads or explicit dependency changes. `package-list` and `package-search` are read-oriented; `package-add` and `package-remove` can edit `Packages/manifest.json` and trigger package resolution or domain reload.

List direct dependencies:

```json
{
  "directDependenciesOnly": true,
  "offlineMode": true
}
```

Search installed and registry packages:

```json
{
  "query": "textmesh",
  "maxResults": 5,
  "offlineMode": true
}
```

Install package by explicit id, version, git URL, or `file:` local package path. If a git package declares URL dependencies, install those URL dependencies first recursively, then install the root package:

```json
{ "packageId": "com.unity.textmeshpro" }
```

Remove only direct non-built-in dependencies, using package id without version:

```json
{ "packageId": "com.company.package" }
```

### Editor Window

Editor window tools are required so agents can prepare hidden docked tabs before screenshots or UI review.

List open windows and docked tabs:

```json
{}
```

Call tool: `editor-window-list`. It returns `instanceId`, title, simple/full/assembly-qualified type names, focused/mouse-over/selected flags, docked/floating state, content rect, host view/container rects, tab indexes, tab count, dock area summaries, and reflection diagnostics.

Open a built-in or project editor window by type or menu path:

```json
{
  "typeName": "UnityEditor.ProfilerWindow",
  "menuPath": "Window/Analysis/Profiler",
  "focus": true
}
```

Call tool: `editor-window-open`. It uses `EditorWindow.GetWindow` when a type resolves, then falls back to `EditorApplication.ExecuteMenuItem(menuPath)` when supplied.

Focus/select an existing tab before capture:

```json
{
  "titleContains": "Frame Debugger"
}
```

Call tool: `editor-window-focus`. Prefer `instanceId` from `editor-window-list` for exact targeting. For docked windows, it reflects `DockArea.m_Panes` and `DockArea.selected`/`m_Selected` to select hidden tabs without resizing or moving docked layouts.

Capture a visible editor window or selected docked tab:

```json
{
  "target": {
    "titleContains": "Console",
    "menuPath": "Window/General/Console"
  },
  "openIfMissing": true,
  "captureArea": "view"
}
```

Call tool: `screenshot-editor-window`. Advertised common args are `target` and `openIfMissing`; manual calls may still pass hidden `captureArea`, `selectDockedTab`, `delayFrames`, `delayMs`, `maxDimension`, and `timeoutMs`. It uses Unity Editor reflection (`EditorWindow.m_Parent`, HostView/DockArea `screenPosition`, and `GUIView.GrabPixels`) to capture Unity-rendered pixels for focused, mouse-over, typed, titled, or menu-opened EditorWindows without moving editor focus. The common backend reports `captureBackend=guiView.grabPixels`; it does not require Unity to be OS-frontmost or the target to be unobscured on the desktop, but the target tab must still be the selected/visible HostView content. If `GUIView.GrabPixels` is unavailable or fails, the tool falls back to `captureBackend=desktop.readScreenPixel`, which uses `InternalEditorUtility.ReadScreenPixel` and can only capture visible desktop pixels while Unity is frontmost and the target rect is unobscured. `captureArea=view` includes the host/dock region and tab/header when Unity exposes it; `content` captures `EditorWindow.position`; `window` captures the floating/container window only in the desktop fallback, while GUIView capture reports a warning and falls back to `view` because it cannot include native OS window chrome. When `delayFrames`/`delayMs` are omitted, capture waits a conservative 2 editor updates plus 1000 ms after tab selection/repaint; metadata reports the effective wait strategy and actual waited time. Hard limitation: inactive hidden docked tabs cannot be captured without first making them visible, so use hidden `selectDockedTab=true` before capture or `editor-window-focus` as a deliberate separate action.

### Profiler

Current profiler support is basic editor recording plus memory counters.

1. Inspect state and available targets:

```json
{}
```

Call tool: `profiler-get-state`. It returns `enabled` and `targets`. Each target has `id`, `name`, and `identifier` when Unity exposes them.

2. Start recording:

```json
{}
```

Call tool: `profiler-start-recording`. To profile a specific available target, pass `connectionId` from `profiler-get-state`.

3. Poll basic counters while the scenario runs:

```json
{}
```

Call tool: `profiler-counters-get`. It returns `enabled`, `totalAllocatedMemory`, `totalReservedMemory`, `monoUsedSize`, and `monoHeapSize`.

4. Stop and save:

```json
{}
```

Call tool: `profiler-stop-recording`. With no `savePath`, it writes a `.data` capture under:

```text
Library/ChievfxMcpBridge/profiles/profile-YYYYMMDD-HHMMSS.data
```

You can pass a relative or absolute `savePath`. Relative paths resolve from the project root. The result returns `stopped`, `enabled`, `saved`, `path`, and `exists`.

Share or attach the saved `.data` file path/output when handing work to another agent or reviewer. Current limitation: the tool saves a profiler capture file and returns its path; it does not attach the capture contents, summarize frames, export charts, or provide rich profiler analysis.

### Script Execution / Tests

Enable the optional `Script Execution / Tests` category only for trusted local workflows. These tools are disabled by default and are intentionally not required Essentials.

`script-execute` compiles caller-provided C# with Roslyn in memory and invokes a static method. It does not write project files. Default class/method are `Script.Main`; pass `className`, `methodName`, and serialized `parameters` for explicit calls. Compile, runtime, and timeout failures return structured `success:false` results with diagnostics/logs. The invoked method body runs on a background worker with `timeoutMs` default `60000` and hard cap `300000`; on timeout, Unity stays responsive and later `script-execute` calls are blocked until that worker returns or Unity restarts.

```json
{
  "csharpCode": "public class Script { public static int Main() { return 42; } }",
  "timeoutMs": 5000
}
```

`tests-run` runs Unity Test Framework tests asynchronously through the editor. It refuses to start when any open scene is dirty, defaults to `EditMode`, supports `testAssembly`, `testNamespace`, `testClass`, and `testMethod` filters, and has a Unity-side `timeoutMs` default of `9000`. The MCP server `--timeout` must also be high enough for longer test runs.

```json
{
  "testMode": "EditMode",
  "testClass": "MyEditorTests",
  "includePassingTests": false,
  "timeoutMs": 30000
}
```

## Troubleshooting

- `unity-mcp-chievfx` missing in Cursor: open `Window > ChievFX > MCP`, click `Write Cursor Config`, then reload MCP tools or restart Cursor.
- Optional tools missing but enabled in the tool window: Cursor likely has stale MCP descriptors. Call `reload_cursor_mcp` for `unity-mcp-chievfx` or the full runtime id; fresh `tools/list` from the Python server plus `UserSettings/ChievfxMcpToolSelection.json` is the source of truth.
- Tool call times out with `Is Unity open and compiled?`: make sure Unity is open, compilation/domain reload has finished, and `Start Bridge` was clicked or the project was reloaded after scripts compiled.
- Tool output too large or truncated: use `outputFormat: "toon"`, reduce `maxDepth`, `maxEntries`/`maxResults`, add `levels`, `contains`, `lastMinutes`, scene/GameObject paths, component filters, or reflection filter fields.
- Console logs are empty: default filters hide normal `Log` and `Warning` entries. Pass `levels` when you need them.
- Asset changes are not visible in Unity: call `assets-refresh`, then wait for imports/compilation before calling tools that depend on new types or assets.
- HTTP transport fails: confirm `Start HTTP` is running in the Unity MCP window and port `27247` is free. Prefer stdio for Cursor unless HTTP is required.
