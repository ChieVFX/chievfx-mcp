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
  Use these tools for Unity work. Do not hand-roll what a tool already does.
  Need more: chievfx://instructions/core-descriptors (all tools + args),
  chievfx://categories/<domain> (one domain).
---

---
type: global
text: |
  Task -> tool:
  - magenta/cyan -> shader-status
  - wrong pixel -> frame-debugger-pick-pixel, then frame-debugger-event-get. Do this before
    toggling effects one by one.
  - see the view -> screenshot-game-view / screenshot-camera. Never RenderTexture+ReadPixels
    (stale frames).
  - find objects, incl. inactive -> gameobject-find (GameObject.Find misses inactive)
  - call one C# method -> reflection-method-call
  - one tool, many inputs -> tool-batch
  - nothing works -> bridge-get-status (may still be compiling)
  Three failed guesses on one symptom -> stop guessing, list that domain's tools.
---

---
type: global
text: |
  When calling `CallMcpTool`, pass tool parameters inside top-level `arguments`. Eg:`CallMcpTool({ server, toolName, arguments: { path: "Assets/Foo" } })`
---
