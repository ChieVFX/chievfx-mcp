# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

from __future__ import annotations

from typing import Any

def core_descriptor_instructions_header() -> str:
    return "Commonly used tools:"


def build_domain_inventory_line(plan: dict[str, Any]) -> str:
    """One line naming every domain that has enabled items, near the top.

    Cheap, truncation-proof, and enough to decide which chievfx://categories/<domain> to read.
    """
    categories = plan.get("categories", {})
    names = sorted(
        (entry["name"] for entry in categories.values() if entry.get("total")),
        key=lambda name: name.casefold(),
    )
    if not names:
        return ""
    return f"Domains: {', '.join(names)}. Read chievfx://categories/<domain> for any of them."


def build_initialize_instructions() -> str:
    records = load_initialize_instruction_records_from_md()
    lines: list[str] = []
    lines.extend(records.get("global", []))

    enabled_tool_ids = load_enabled_tool_ids()
    enabled_resource_ids, enabled_template_ids = load_enabled_resource_ids()
    enabled_prompt_names = load_enabled_prompt_names()

    plan = build_category_plan()
    collapsed_ids = collapsed_item_ids(plan)

    tool_records = records.get("tool", {})
    if isinstance(tool_records, dict):
        for tool_id in sorted(enabled_tool_ids):
            if tool_id in collapsed_ids["tools"]:
                continue
            text = tool_records.get(tool_id)
            if isinstance(text, str) and text.strip():
                lines.append(text.strip())

    resource_records = records.get("resource", {})
    if isinstance(resource_records, dict):
        for resource_id in sorted(enabled_resource_ids):
            if resource_id in collapsed_ids["resources"]:
                continue
            text = resource_records.get(resource_id)
            if isinstance(text, str) and text.strip():
                lines.append(text.strip())

    template_records = records.get("resourceTemplate", {})
    if isinstance(template_records, dict):
        for template_id in sorted(enabled_template_ids):
            if template_id in collapsed_ids["templates"]:
                continue
            text = template_records.get(template_id)
            if isinstance(text, str) and text.strip():
                lines.append(text.strip())

    prompt_records = records.get("prompt", {})
    if isinstance(prompt_records, dict):
        for prompt_name in sorted(enabled_prompt_names):
            text = prompt_records.get(prompt_name)
            if isinstance(text, str) and text.strip():
                lines.append(text.strip())

    # Domains first (one cheap line, every domain), then the commonly used tools with signatures. The
    # old per-domain "Extra API capabilities" block is gone: the domain line plus
    # chievfx://categories/<domain> covers it, and that budget pays for tool signatures instead.
    domain_line = build_domain_inventory_line(plan)
    if domain_line:
        lines.append(domain_line)

    descriptor_blob = build_enabled_descriptor_instructions(plan)
    if descriptor_blob:
        lines.append(descriptor_blob)

    return "\n".join(lines).strip()


def build_initialize_server_info(instructions: str | None = None) -> dict[str, str]:
    """Expose a changing version when generated startup instructions change.

    Cursor materializes initialize.instructions into its MCP file-system cache.
    Selection and extension availability affect those instructions, but MCP has
    no instructions/list_changed notification. A deterministic version suffix
    gives reload/re-handshake flows a cheap cache-busting signal.
    """
    if instructions is None:
        instructions = build_initialize_instructions()
    fingerprint = hashlib.sha256(instructions.encode("utf-8")).hexdigest()[:12]
    # Cursor caches initialize.instructions per server identity and only adopts a fresh
    # payload when serverInfo.version is semver-NEWER. Two consequences shape this:
    #   1. Build metadata (the "+..." suffix) is ignored by semver precedence, so a
    #      fingerprint placed there is invisible and the version looks constant.
    #   2. A lower/equal version is ignored; only a strictly higher one triggers a refresh.
    # Cursor also relaunches this process on every reconnect, so no in-process counter
    # survives. A monotonic wall-clock patch guarantees the version increases across
    # reconnects, so Cursor refreshes instructions whenever the selection changed. The
    # instruction fingerprint stays in build metadata purely for human/debug visibility.
    base = SERVER_VERSION.split("+", 1)[0].split(".")
    major = base[0] if len(base) > 0 else "0"
    minor = base[1] if len(base) > 1 else "1"
    patch = int(time.time())
    return {"name": CURSOR_SERVER_NAME, "version": f"{major}.{minor}.{patch}+instructions.{fingerprint}"}


def build_core_tool_name_lines(plan: dict[str, Any]) -> list[str]:
    """One line per enabled tool in a non-collapsed category: name(args) plus a short summary.

    Agents did not follow a pointer to the full descriptor resource however it was phrased, so the
    callable detail lives here instead: the signature makes the tool callable and the summary makes it
    selectable, with no second fetch. tools/list still carries the full schema and prose.
    """
    categories = plan.get("categories", {})
    enabled_tool_ids = load_enabled_tool_ids()
    rows: list[tuple[str, str, str]] = []
    for tool in all_tools():
        name = tool.get("name", "")
        if name not in enabled_tool_ids:
            continue
        category = _tool_category(tool)
        entry = categories.get(category.casefold())
        if entry is not None and entry.get("collapsed"):
            continue
        arguments = _compact_signature_arguments(_schema_arguments(tool.get("inputSchema")))
        summary = _short_tool_summary(tool)
        rows.append((category, f"- {name}({arguments}){f': {summary}' if summary else ''}", name))

    # Flat list, essentials first then category order, so it reads as one "commonly used" inventory.
    rows.sort(key=lambda row: (row[0] != "essentials", row[0].casefold(), row[2]))
    return [line for _, line, _ in rows]


# A short descriptor per tool: enough to pick the right one without a second fetch, short enough that
# ~35 of them still fit the truncated instruction budget.
_MAX_TOOL_SUMMARY_CHARS = 60


def _short_tool_summary(tool: dict[str, Any]) -> str:
    description = str(tool.get("description") or "").strip()
    if not description:
        return ""
    # First sentence only; tool descriptions lead with what the tool does and follow with caveats.
    sentence = re.split(r"(?<=[.!?])\s", description, maxsplit=1)[0].strip().rstrip(".")
    if len(sentence) <= _MAX_TOOL_SUMMARY_CHARS:
        return sentence
    return sentence[: _MAX_TOOL_SUMMARY_CHARS - 1].rstrip() + "…"


# Cap the inline signature: a handful of tools declare a dozen arguments, and the tail of a long list
# costs budget that other tools need. tools/list and core-descriptors still carry the full schema.
_MAX_INLINE_SIGNATURE_ARGUMENTS = 6
# Long enum unions (import options, log levels, capture areas) dominate a line while adding little at
# selection time, so collapse the type and let tools/list supply the allowed values.
_MAX_INLINE_ARGUMENT_TYPE_CHARS = 30


def _compact_signature_arguments(arguments: str) -> str:
    if not arguments:
        return ""
    parts = [_shorten_argument(part) for part in arguments.split(", ") if part]
    if len(parts) <= _MAX_INLINE_SIGNATURE_ARGUMENTS:
        return ", ".join(parts)
    return ", ".join(parts[:_MAX_INLINE_SIGNATURE_ARGUMENTS]) + ", ..."


def _shorten_argument(argument: str) -> str:
    name, separator, type_name = argument.partition(":")
    if not separator or len(type_name) <= _MAX_INLINE_ARGUMENT_TYPE_CHARS:
        return argument
    if type_name.startswith("{"):
        # Nested object shape: the field list belongs in tools/list, not here.
        return f"{name}:obj"
    if "|" in type_name:
        return f"{name}:{type_name.split('|', 1)[0]}|..."
    return f"{name}:{type_name[:_MAX_INLINE_ARGUMENT_TYPE_CHARS]}..."


def _build_descriptor_section_lines(plan: dict[str, Any], include_tools: bool = True) -> list[str]:
    collapsed_lines = collapsed_item_lines(plan)
    sections: list[str] = []

    if include_tools:
        tool_descriptors = sorted(enabled_tools(), key=lambda item: item.get("name", ""))
        tool_lines = [
            line
            for descriptor in tool_descriptors
            if (line := format_tool_for_initialize_instructions(descriptor)) not in collapsed_lines["tools"]
        ]
        if tool_lines:
            sections.append("Tools:")
            sections.extend(tool_lines)

    resource_descriptors = sorted(enabled_resources(), key=lambda item: item.get("uri", ""))
    resource_lines = [
        line
        for descriptor in resource_descriptors
        if (line := format_resource_for_initialize_instructions(descriptor)) not in collapsed_lines["resources"]
    ]
    if resource_lines:
        sections.append("Resources:")
        sections.extend(resource_lines)

    template_descriptors = sorted(enabled_resource_templates(), key=lambda item: item.get("uriTemplate", ""))
    template_lines = [
        line
        for descriptor in template_descriptors
        if (line := format_resource_template_for_initialize_instructions(descriptor)) not in collapsed_lines["templates"]
    ]
    if template_lines:
        sections.append("Resource templates:")
        sections.extend(template_lines)

    prompt_descriptors = sorted(enabled_prompts(), key=lambda item: item.get("name", ""))
    if prompt_descriptors:
        sections.append("Prompts:")
        sections.extend(format_prompt_for_initialize_instructions(descriptor) for descriptor in prompt_descriptors)

    return sections


def build_enabled_descriptor_instructions(plan: dict[str, Any] | None = None) -> str:
    if plan is None:
        plan = build_category_plan()
    lines: list[str] = []
    name_lines = build_core_tool_name_lines(plan)
    if name_lines:
        lines.append(core_descriptor_instructions_header())
        lines.extend(name_lines)
    # Resources, templates and prompts are deliberately NOT advertised here. Only core-descriptors and
    # chievfx://categories/<domain> are named (in the header records), and that budget is spent on
    # callable tool signatures instead — agents used the tools and ignored the resource list.
    if not lines:
        return ""
    return "\n".join(lines)


def build_core_descriptor_instructions_resource_body(plan: dict[str, Any] | None = None) -> str:
    """Mirror initialize.instructions from the Tools: block through Extra API capabilities."""
    if plan is None:
        plan = build_category_plan()
    parts: list[str] = []
    descriptor_lines = _build_descriptor_section_lines(plan)
    if descriptor_lines:
        parts.append("\n".join(descriptor_lines))
    extra = build_extra_capabilities_section(plan, detailed=True)
    if extra:
        parts.append(extra)
    return "\n".join(parts).strip()


def _schema_type_name(schema: Any) -> str:
    if not isinstance(schema, dict):
        return "any"
    if "enum" in schema and isinstance(schema["enum"], list):
        return "|".join(str(item) for item in schema["enum"])
    if "oneOf" in schema and isinstance(schema["oneOf"], list):
        return "|".join(_schema_type_name(item) for item in schema["oneOf"])
    raw_type = schema.get("type")
    if isinstance(raw_type, list):
        return "|".join(_schema_short_type_name(item) for item in raw_type if isinstance(item, str)) or "any"
    if isinstance(raw_type, str):
        if raw_type == "object":
            properties = schema.get("properties")
            if isinstance(properties, dict) and set(properties.keys()) == {"x", "y"}:
                return "{x,y}"
            if isinstance(properties, dict) and set(properties.keys()) == {"x", "y", "z"}:
                return "{x,y,z}"
            if isinstance(properties, dict) and len(properties) <= 4:
                return "{" + ",".join(str(key) for key in properties.keys()) + "}"
        if raw_type == "array":
            items = schema.get("items")
            return f"{_schema_type_name(items)}[]" if items else "array"
        return _schema_short_type_name(raw_type)
    if "properties" in schema:
        properties = schema.get("properties")
        if isinstance(properties, dict):
            return "{" + ",".join(str(key) for key in properties.keys()) + "}"
    return "any"


def _schema_short_type_name(raw_type: str) -> str:
    return {
        "boolean": "bool",
        "integer": "int",
        "number": "num",
        "string": "str",
        "object": "obj",
    }.get(raw_type, raw_type)


def _schema_arguments(schema: Any) -> str:
    if not isinstance(schema, dict):
        return ""
    properties = schema.get("properties")
    if not isinstance(properties, dict) or not properties:
        return ""
    required = set(schema.get("required", [])) if isinstance(schema.get("required"), list) else set()
    parts: list[str] = []
    for name, prop_schema in properties.items():
        if name == "outputFormat":
            continue
        suffix = "" if name in required else "?"
        parts.append(f"{name}{suffix}:{_schema_type_name(prop_schema)}")
    return ", ".join(parts)


def _compact_line(identifier: Any, description: Any, args: str = "") -> str:
    identifier_text = str(identifier or "").strip()
    description_text = str(description or "").strip()
    if args:
        return f"- {identifier_text}: {description_text} args=({args})"
    return f"- {identifier_text}: {description_text}"


def format_tool_for_initialize_instructions(descriptor: dict[str, Any]) -> str:
    return _compact_line(
        descriptor.get("name"),
        descriptor.get("description"),
        _schema_arguments(descriptor.get("inputSchema")),
    )


def format_resource_for_initialize_instructions(descriptor: dict[str, Any]) -> str:
    return _compact_line(descriptor.get("uri"), descriptor.get("description"))


def format_resource_template_for_initialize_instructions(descriptor: dict[str, Any]) -> str:
    return _compact_line(descriptor.get("uriTemplate"), descriptor.get("description"))


def format_prompt_for_initialize_instructions(descriptor: dict[str, Any]) -> str:
    arguments = descriptor.get("arguments")
    arg_text = ""
    if isinstance(arguments, list):
        parts = []
        for argument in arguments:
            if isinstance(argument, dict) and isinstance(argument.get("name"), str):
                suffix = "" if argument.get("required") else "?"
                parts.append(f"{argument['name']}{suffix}")
        arg_text = ", ".join(parts)
    return _compact_line(descriptor.get("name"), descriptor.get("description"), arg_text)
