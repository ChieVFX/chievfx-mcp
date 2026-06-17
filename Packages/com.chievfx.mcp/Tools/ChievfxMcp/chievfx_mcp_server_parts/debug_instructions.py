# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

import shutil


def load_debug_settings() -> dict[str, Any]:
    default = {
        "schemaVersion": DEBUG_SETTINGS_SCHEMA_VERSION,
        "debugMode": False,
    }
    try:
        payload = json.loads(DEBUG_SETTINGS_PATH.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return default
    if not isinstance(payload, dict):
        return default
    return {**default, **payload}


def is_debug_mode_enabled() -> bool:
    return bool(load_debug_settings().get("debugMode"))


def clear_tool_descriptors_directory() -> None:
    if DEBUG_DESCRIPTORS_DIR.exists():
        shutil.rmtree(DEBUG_DESCRIPTORS_DIR)


def dump_tool_descriptors() -> Path:
    """Write one JSON file per enabled tool exactly as tools/list returns it."""
    tools = enabled_tools()
    clear_tool_descriptors_directory()
    DEBUG_DESCRIPTORS_DIR.mkdir(parents=True, exist_ok=True)

    for tool in tools:
        name = tool.get("name")
        if not isinstance(name, str) or not name:
            continue
        path = DEBUG_DESCRIPTORS_DIR / f"{name}.json"
        path.write_text(json.dumps(tool, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    (DEBUG_DESCRIPTORS_DIR / "tools-list.json").write_text(
        json.dumps({"tools": tools}, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    return DEBUG_DESCRIPTORS_DIR


def refresh_debug_artifacts_on_tools_list_changed(trigger: str = "tools-list-changed") -> Path | None:
    """Drop stale disabled-tool descriptors whenever the enabled tool set changes."""
    if not is_debug_mode_enabled():
        clear_tool_descriptors_directory()
        return None
    return dump_debug_instructions(trigger)


def build_debug_instructions_markdown(trigger: str = "", instructions: str | None = None) -> str:
    if instructions is None:
        instructions = build_initialize_instructions()
    enabled_tool_count = len(enabled_tools())
    enabled_resource_count = len(enabled_resources())
    enabled_template_count = len(enabled_resource_templates())
    enabled_prompt_count = len(enabled_prompts())

    lines = [
        "# ChievFX MCP debug instructions",
        "",
        f"Generated at (UTC): {time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())}",
        f"Project root: {PROJECT_ROOT}",
    ]
    if trigger.strip():
        lines.append(f"Trigger: {trigger.strip()}")
    lines.extend(
        [
            "",
            "## Selection snapshot",
            "",
            f"- Enabled tools: {enabled_tool_count}",
            f"- Enabled resources: {enabled_resource_count}",
            f"- Enabled resource templates: {enabled_template_count}",
            f"- Enabled prompts: {enabled_prompt_count}",
            f"- Tool selection: `{TOOL_SELECTION_PATH}`",
            f"- Resource selection: `{RESOURCE_SELECTION_PATH}`",
            f"- Prompt selection: `{PROMPT_SELECTION_PATH}`",
            f"- Debug settings: `{DEBUG_SETTINGS_PATH}`",
            "",
            "## Tool descriptors (tools/list)",
            "",
            f"Exact MCP `tools/list` payloads for each enabled tool are written under `{DEBUG_DESCRIPTORS_DIR}`.",
            "Each `{tool-name}.json` is one tools/list entry (`name`, `description`, `inputSchema`).",
            "`tools-list.json` is the full `{ \"tools\": [...] }` response.",
            "",
            "## initialize.instructions",
            "",
            "Exact payload returned from MCP `initialize.instructions`.",
            "",
            "```text",
            instructions,
            "```",
            "",
        ]
    )
    return "\n".join(lines)


def dump_debug_instructions(trigger: str = "") -> Path | None:
    if not is_debug_mode_enabled():
        return None

    instructions = build_initialize_instructions()
    dump_tool_descriptors()
    path = PROJECT_ROOT / ".temp" / "debug_instructions.md"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(build_debug_instructions_markdown(trigger, instructions), encoding="utf-8")
    return path
