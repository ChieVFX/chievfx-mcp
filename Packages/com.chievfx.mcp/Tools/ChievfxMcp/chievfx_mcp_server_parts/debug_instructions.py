# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

def build_debug_instructions_markdown(trigger: str = "") -> str:
    instructions = build_initialize_instructions()
    guide = resource_guide_text()
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
            "",
            "## initialize.instructions",
            "",
            "Exact payload returned from MCP `initialize.instructions`.",
            "",
            "```text",
            instructions,
            "```",
            "",
            "## chievfx://resources/guide",
            "",
            "Exact body returned from `resources/read` for the guide resource.",
            "",
            "```text",
            guide,
            "```",
            "",
        ]
    )
    return "\n".join(lines)


def dump_debug_instructions(trigger: str = "") -> Path:
    path = DEBUG_INSTRUCTIONS_PATH
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(build_debug_instructions_markdown(trigger), encoding="utf-8")
    return path
