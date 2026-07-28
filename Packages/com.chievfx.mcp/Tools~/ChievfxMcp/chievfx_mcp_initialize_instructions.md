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
  ChievFX Unity MCP is project-local. Prefer enabled ChievFX MCP tools/resources when they provide live Unity evidence.
---

---
type: global
text: |
  Read resource to learn more about mcp capabilities: chievfx://instructions/core-descriptors (all tools + args),
  chievfx://categories/<domain> (one domain).
---

---
type: global
text: |
  When calling `CallMcpTool`, pass tool parameters inside top-level `arguments`. Eg:`CallMcpTool({ server, toolName, arguments: { path: "Assets/Foo" } })`
---
