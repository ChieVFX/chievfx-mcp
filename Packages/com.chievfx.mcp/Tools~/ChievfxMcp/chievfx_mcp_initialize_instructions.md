## ChievFX MCP Initialize Instructions

This file feeds the MCP `initialize.instructions` field.
See `../../Documentation~/ChievfxMcpAgentInstructions.md` for the full assembly flow and how the resource guide mirrors enabled selection.

Record format:
- Records are delimited by a line containing only `---`.
- `type: global` records are always included.
- `type: tool`, `type: resource`, `type: resourceTemplate`, and `type: prompt` records are included only when the matching item is enabled in the Unity MCP selection windows.
- `text: |` starts a multiline body.

Keep text compact. This content is injected into agent context at MCP startup, and
Claude Desktop truncates initialize.instructions around 5KB, so only cross-cutting
guidance belongs here. Per-tool usage notes go in the tool's descriptor JSON
(`chievfx_mcp_server_parts/tool_descriptors/<tool>.json`) — descriptions arrive via
tools/list with no such budget, exactly when the model considers the tool. Resource
and prompt descriptions are already advertised by the generated descriptor sections,
so curated records here should not repeat them.

---
type: global
text: |
  These instructions are truncated by most clients. Before hand-writing a script-execute for
  anything Unity already exposes, read chievfx://instructions/core-descriptors — it lists every
  tool with argument signatures. Reimplementing an existing tool is the most common failure here.
---

---
type: global
text: |
  Start from the task, not from script-execute:
  - Wrong colours, magenta or cyan -> shader-status (compile errors + whether variants still compile).
  - Any wrong-looking pixel -> frame-debugger-pick-pixel (which draw call wrote it), then
    frame-debugger-event-get. Ask this BEFORE toggling effects to bisect by elimination.
  - Capture the view -> screenshot-game-view / screenshot-camera. Never hand-roll
    RenderTexture + ReadPixels; the hand-rolled path returns stale frames.
  - Find objects, including inactive ones -> gameobject-find (GameObject.Find misses inactive).
  - Call one existing C# method -> reflection-method-call, not hand-written reflection.
  - Same tool over many inputs -> tool-batch, not one round trip each.
  - Nothing seems to apply -> bridge-get-status first; a compile or import may still be running.
  If three hypotheses about one symptom have failed, stop hypothesising and inventory the
  instruments for that domain (chievfx://categories/<category>).
---

---
type: global
text: |
  Prefer enabled ChievFX MCP tools/resources when they provide live Unity evidence.
---

---
type: global
text: |
  When calling `CallMcpTool`, pass tool parameters inside top-level `arguments`. Eg:`CallMcpTool({ server, toolName, arguments: { path: "Assets/Foo" } })`
---
