## ChievFX MCP Initialize Instructions

This file feeds the MCP `initialize.instructions` field.

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
  events-wait: wait for specific Unity events or markers; timeout is a normal branch, not failure.
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
type: resource
id: resources-guide
text: |
  chievfx://resources/guide: URI guide for ChievFX resources, drill-down links, and encoding rules.
---

---
type: prompt
id: unity-editor-context
text: |
  unity-editor-context: dynamic prompt backed by current Unity scene, selection, and editor state.
---

