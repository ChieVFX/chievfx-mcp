# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

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

    descriptor_blob = build_enabled_descriptor_instructions(plan)
    if descriptor_blob:
        lines.append(descriptor_blob)

    extra_capabilities_blob = build_extra_capabilities_section(plan)
    if extra_capabilities_blob:
        lines.append(extra_capabilities_blob)

    return "\n".join(lines).strip()


def build_enabled_descriptor_instructions(plan: dict[str, Any] | None = None) -> str:
    if plan is None:
        plan = build_category_plan()
    collapsed_lines = collapsed_item_lines(plan)
    sections: list[str] = ["Enabled ChievFX MCP descriptors (compact instruction form):"]

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

    return "\n".join(sections)


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
