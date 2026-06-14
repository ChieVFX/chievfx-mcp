## ChievFX MCP Initialize Instructions

This file feeds the MCP `initialize.instructions` field.
See `../../Documentation~/ChievfxMcpAgentInstructions.md` for the full assembly flow and how the resource guide mirrors enabled selection.

Record format:
- Records are delimited by a line containing only `---`.
- `type: global` records are always included.
- `type: tool`, `type: resource`, `type: resourceTemplate`, and `type: prompt` records are included only when the matching item is enabled in the Unity MCP selection windows.
- `text: |` starts a multiline body.

Keep text compact. This content is injected into agent context at MCP startup.

---
type: global
text: |
  Prefer enabled ChievFX MCP tools/resources when they provide live Unity evidence.
---

---
type: tool
id: bridge-get-status
text: |
  bridge-get-status: inspect Unity bridge heartbeat, compile/import busy state, recent operations, and event-wait liveness before longer orchestration.
---

---
type: tool
id: events-wait
text: |
  events-wait: long-poll for the next matching Unity event; an elapsed timeoutMs is a normal branch, not a failure. Match by contains (case-insensitive log substring) or marker (exact planted-beacon name). Events that fire DURING a trigger (Play-mode enter, recompile, script-execute) are skipped unless you capture sinceEventId before the trigger (e.g. bridge-get-status lastEventId) or pass includeRecentMs. On timeout, read result.diagnostic and recover via events-check-since.
---

---
type: tool
id: events-check-since
text: |
  events-check-since: recover after waits/timeouts using sinceEventId and sinceTimestampUtc from prior wait results.
---

---
type: resource
id: editor-context
text: |
  chievfx://editor/context: compact current Unity editor, play mode, active scene, prefab stage, and selection context.
---

---
type: tool
id: console-get-logs
text: |
  console-get-logs: filter console severity with levels, not contains. Default levels are Error, Exception, Assert, Warning. Exact contains tokens error, exception, warning, or issue are reinterpreted as severity filters so Assert rows like "Map must be contained in state" still match.
---

---
type: prompt
id: unity-editor-context
text: |
  unity-editor-context: dynamic prompt backed by current Unity scene, selection, and editor state.
---

