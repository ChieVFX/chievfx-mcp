## ChievFX MCP Initialize Instructions

This file feeds the MCP `initialize.instructions` field.
See `Docs/ChievfxMcpAgentInstructions.md` for the full assembly flow and how the resource guide mirrors enabled selection.

Record format:
- Records are delimited by a line containing only `---`.
- `type: global` records are always included.
- `type: tool`, `type: resource`, `type: resourceTemplate`, and `type: prompt` records are included only when the matching item is enabled in the Unity MCP selection windows.
- `text: |` starts a multiline body.

Keep text compact. This content is injected into agent context at MCP startup.

---
type: global
text: |
  ChievFX Unity MCP is project-local. Prefer enabled ChievFX MCP tools/resources when they provide live Unity evidence.
  Before calling a ChievFX MCP tool, inspect its descriptor/schema from Cursor's MCP tool folder and use exact tool names.
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
  events-wait: wait for specific Unity events or markers; timeout is a normal branch, not failure. Default cursor is lastEventId (future-only), so boot/early logs that fire during the triggering op (Play-mode enter, recompile, script-execute) are skipped. For such logs capture sinceEventId from the trigger result (editor-playmode-set returns eventCursorBefore) or bridge-get-status BEFORE the trigger, or use includeRecentMs with no sinceEventId. Debug.Log lines are source:log; pass source:"log" for clarity. Prefer ASCII-only contains substrings (e.g. "Turn 1", "Player Turn") or a marker: filter over Unicode punctuation (em dash —, smart quotes) in log text, since encoding mismatches can break substring matches. On timeout, inspect result.diagnostic: matchBelowCursor means it fired below your cursor (retry from earlier cursor), nonAsciiContains means your filter had non-ASCII that may have been mangled (retry ASCII-only), possiblyTruncated means it was evicted (verify via console-get-logs contains).
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

