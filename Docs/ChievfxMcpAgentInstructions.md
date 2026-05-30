# ChievFX MCP Agent Instructions

Short reference for how agent-facing MCP text is assembled and how it respects tool/resource selection.

## Selection files

| Capability | Selection file | Required defaults |
|------------|----------------|-------------------|
| Tools | `UserSettings/ChievfxMcpToolSelection.json` | Essentials + policy-required tools |
| Resources / templates | `UserSettings/ChievfxMcpResourceSelection.json` | `resources-guide` |
| Prompts | `UserSettings/ChievfxMcpPromptSelection.json` | policy-required prompts |

Unity windows under `Window > ChievFX > MCP *` write these files. The Python MCP server reads them at runtime.

## MCP `initialize.instructions`

Built by `build_initialize_instructions()` in `Tools/ChievfxMcp/chievfx_mcp_server_parts/initialize_instructions.py` and returned from the MCP `initialize` response in `server_core.py`.

Two layers, both filtered to **enabled** capabilities only:

1. **Curated blurbs** from `Tools/ChievfxMcp/chievfx_mcp_initialize_instructions.md`
   - Records keyed by `type` + `id` (`global`, `tool`, `resource`, `resourceTemplate`, `prompt`)
   - `global` records are always included
   - Other records are included only when the matching item is enabled
   - Optional hand-written agent hints; text may differ from descriptor `description`

2. **Compact descriptor list** from `build_enabled_descriptor_instructions()`
   - One line per enabled tool, resource, template, and prompt
   - Tools: `name`, `description`, compressed `inputSchema` args
   - Resources/templates: `uri` or `uriTemplate`, `description`
   - Prompts: `name`, `description`, argument names

## Descriptor sources

| Capability | Primary catalog | Also merged from |
|------------|-----------------|------------------|
| Tools | `tool_descriptors/*.json` | Unity extension manifest |
| Resources / templates | `chievfx_mcp_text_prompts_resources.md` (fallback `static_catalog.py`) | Unity extension manifest |
| Prompts | same MD catalog + `static_catalog.py` | Unity extension manifest |

Full descriptors are advertised through normal MCP list methods:

- `tools/list` → `enabled_tools()`
- `resources/list` → `enabled_resources()`
- `resources/templates/list` → `enabled_resource_templates()`
- `prompts/list` → `enabled_prompts()`

Disabled items are omitted from lists and from `initialize.instructions`. `resources/read` rejects disabled URIs via `ensure_resource_enabled()`.

## `chievfx://resources/guide`

Required static resource. Body built by `resource_guide_text()` in `resource_text.py`.

Behavior mirrors the compact initialize/resource list layer:

- **Static resource and template sections** are generated from the same enabled descriptors as `resources/list` and `resources/templates/list`
- Line format reuses `format_resource_for_initialize_instructions()` and `format_resource_template_for_initialize_instructions()` (`uri: description`)
- **Encoding and output rules** remain static trailing prose (URI grammar, filterSpec, TOON output notes)

Previously the guide hard-coded the full catalog and could mention resources that were disabled in the Unity selection window. It now tracks the current selection.

Read path: `server_core.py` serves the guide locally without calling the Unity bridge.

## Related files

| File | Role |
|------|------|
| `chievfx_mcp_initialize_instructions.md` | Curated initialize blurbs |
| `initialize_instructions.py` | Assembles `initialize.instructions` |
| `resource_text.py` | Formats dynamic resource payloads, including the guide |
| `resource_metadata.py` | Loads selection, exposes `enabled_resources()` / `enabled_resource_templates()` |
| `tool_selection.py` | Tool enablement + `enabled_tools()` |
| `prompt_metadata.py` | Prompt enablement + `enabled_prompts()` |
