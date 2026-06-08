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
   - Items in a collapsed category (see below) are omitted here and their curated blurbs are skipped

## Category auto-collapse

Built in `chievfx_mcp_server_parts/category_resources.py`.

To cut token cost, large optional categories are collapsed in `initialize.instructions`. Tool and resource/template categories that share a case-folded name merge into one category (e.g. `GameObject`, `Scene`, `cinemachine-and-timeline`). A merged category collapses when its enabled item count (tools + resources + templates) exceeds `CATEGORY_COLLAPSE_THRESHOLD` (3) and it is not flagged always-supplied.

When collapsed:

- The per-item descriptor lines and curated blurbs are omitted.
- One header line is emitted under `Collapsed categories (...)`: `- <Name> (<n> tools, <m> resources): <description> -> chievfx://categories/<slug>`.
- A dynamic, non-template resource `chievfx://categories/<slug>` is advertised in `resources/list` and served locally by `read_resource`. Its body lists the full compact tool/resource/template lines for that category's enabled items. These resources are not part of the user-selectable catalog, metadata, selection files, or the guide.

Always-supplied control lives in `UserSettings/ChievfxMcpCategorySelection.json`:

```json
{
  "schemaVersion": 1,
  "forceAllCategoriesAlwaysSupplied": false,
  "alwaysSuppliedCategories": ["Essentials", "Editor Window", "Script Execution / Tests", "Control"]
}
```

- A category is always-supplied (never collapses, full inline) when `forceAllCategoriesAlwaysSupplied` is true or its name is in `alwaysSuppliedCategories`.
- Missing file falls back to those four default categories on, force-all off.
- Unity writes the file: per-category "Always supply" toggle in the Tools and Resources tabs (info mode / "i"), and a global "Force all categories always-supplied" toggle in the Connection tab "Advanced details" foldout. Both trigger the debug dump.

## Descriptor sources

| Capability | Primary catalog | Also merged from |
|------------|-----------------|------------------|
| Tools | `tool_descriptors/*.json` | Unity bridge `extension-capabilities-get` |
| Resources / templates | `chievfx_mcp_text_prompts_resources.md` (fallback `static_catalog.py`) | Unity bridge `extension-capabilities-get` |
| Prompts | same MD catalog + `static_catalog.py` | Unity bridge `extension-capabilities-get` |

Full descriptors are advertised through normal MCP list methods:

- `tools/list` → `enabled_tools()`
- `resources/list` → `enabled_resources()` + `dynamic_category_resources()` (collapsed-category resources)
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

## Debug dump

When tool/resource/prompt selection changes (Unity selection windows or MCP `tools-set-*` / role APIs), the server writes:

```text
.temp/debug_instructions.md
```

Contents: selection snapshot, exact `initialize.instructions` payload, and exact `chievfx://resources/guide` body for the current project state.

Manual refresh:

```bash
python3 Tools/ChievfxMcp/chievfx_mcp_server.py --project-root . --dump-debug-instructions --debug-trigger manual
```

## Related files

| File | Role |
|------|------|
| `chievfx_mcp_initialize_instructions.md` | Curated initialize blurbs |
| `initialize_instructions.py` | Assembles `initialize.instructions` |
| `category_resources.py` | Category merge/collapse plan + `chievfx://categories/<slug>` resources |
| `resource_text.py` | Formats dynamic resource payloads, including the guide |
| `resource_metadata.py` | Loads selection, exposes `enabled_resources()` / `enabled_resource_templates()` |
| `tool_selection.py` | Tool enablement + `enabled_tools()` |
| `prompt_metadata.py` | Prompt enablement + `enabled_prompts()` |
