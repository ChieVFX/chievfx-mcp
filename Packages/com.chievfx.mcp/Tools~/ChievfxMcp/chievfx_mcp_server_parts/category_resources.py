# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

from __future__ import annotations

CATEGORY_RESOURCE_URI_PREFIX = "chievfx://categories/"


def load_category_settings() -> dict[str, Any]:
    """Read shared category collapse config written by the Unity selection windows.

    Missing or malformed config falls back to defaults: the seeded
    always-supplied categories on, force-all off.
    """
    force_all = False
    always_supplied = {category_slug(name) for name in DEFAULT_ALWAYS_SUPPLIED_CATEGORIES}

    try:
        payload = json.loads(CATEGORY_SELECTION_PATH.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {"forceAll": force_all, "alwaysSupplied": always_supplied}

    if isinstance(payload, dict):
        force_value = payload.get("forceAllCategoriesAlwaysSupplied")
        if isinstance(force_value, bool):
            force_all = force_value
        listed = payload.get("alwaysSuppliedCategories")
        if isinstance(listed, list):
            always_supplied = {category_slug(item) for item in listed if isinstance(item, str) and item.strip()}

    return {"forceAll": force_all, "alwaysSupplied": always_supplied}


def category_slug(name: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", str(name or "").casefold()).strip("-")


def _tool_category(tool: dict[str, Any]) -> str:
    return tool.get("category") or TOOL_CATEGORIES.get(tool.get("name", ""), "general")


def _resource_category(resource: dict[str, Any]) -> str:
    return RESOURCE_CATEGORIES.get(resource.get("id", "")) or resource.get("category") or "general"


def _template_category(template: dict[str, Any]) -> str:
    return template.get("category") or RESOURCE_TEMPLATE_CATEGORIES.get(template.get("id", ""), "general")


def _category_description(name: str) -> str:
    return TOOL_CATEGORY_DESCRIPTIONS.get(name) or RESOURCE_CATEGORY_DESCRIPTIONS.get(name) or ""


def build_category_plan() -> dict[str, Any]:
    """Merge enabled tools/resources/templates by case-folded category name and
    decide which categories collapse in initialize.instructions."""
    settings = load_category_settings()
    force_all = bool(settings.get("forceAll"))
    always_supplied = settings.get("alwaysSupplied") or set()

    enabled_tool_ids = load_enabled_tool_ids()
    enabled_resource_ids, enabled_template_ids = load_enabled_resource_ids()

    categories: dict[str, dict[str, Any]] = {}

    def bucket(name: str) -> dict[str, Any]:
        key = name.casefold()
        entry = categories.get(key)
        if entry is None:
            entry = {
                "name": name,
                "key": key,
                "slug": category_slug(name),
                "description": _category_description(name),
                "tools": [],
                "resources": [],
                "templates": [],
            }
            categories[key] = entry
        return entry

    for tool in all_tools():
        if tool.get("name") in enabled_tool_ids:
            bucket(_tool_category(tool))["tools"].append(format_tool_for_initialize_instructions(compact_tool_descriptor(tool)))

    for resource in all_resources():
        if resource.get("id") in enabled_resource_ids:
            bucket(_resource_category(resource))["resources"].append(
                format_resource_for_initialize_instructions(compact_resource_descriptor(resource))
            )

    for template in all_resource_templates():
        if template.get("id") in enabled_template_ids:
            bucket(_template_category(template))["templates"].append(
                format_resource_template_for_initialize_instructions(compact_resource_template_descriptor(template))
            )

    for entry in categories.values():
        total = len(entry["tools"]) + len(entry["resources"]) + len(entry["templates"])
        entry["total"] = total
        entry["alwaysSupplied"] = force_all or entry["slug"] in always_supplied
        entry["collapsed"] = total > CATEGORY_COLLAPSE_THRESHOLD and not entry["alwaysSupplied"]

    return {"categories": categories, "forceAll": force_all}


def collapsed_categories(plan: dict[str, Any]) -> list[dict[str, Any]]:
    categories = plan.get("categories", {})
    return sorted(
        (entry for entry in categories.values() if entry.get("collapsed")),
        key=lambda entry: entry["name"].casefold(),
    )


def collapsed_item_lines(plan: dict[str, Any]) -> dict[str, set[str]]:
    """Compact descriptor lines that belong to collapsed categories, keyed by
    section so the instruction builder can omit them."""
    tools: set[str] = set()
    resources: set[str] = set()
    templates: set[str] = set()
    for entry in collapsed_categories(plan):
        tools.update(entry["tools"])
        resources.update(entry["resources"])
        templates.update(entry["templates"])
    return {"tools": tools, "resources": resources, "templates": templates}


def collapsed_item_ids(plan: dict[str, Any]) -> dict[str, set[str]]:
    """Ids whose curated initialize blurbs should be suppressed because their
    category collapsed."""
    settings = load_category_settings()
    force_all = bool(settings.get("forceAll"))
    always_supplied = settings.get("alwaysSupplied") or set()
    enabled_tool_ids = load_enabled_tool_ids()
    enabled_resource_ids, enabled_template_ids = load_enabled_resource_ids()
    categories = plan.get("categories", {})

    def is_collapsed(category_name: str) -> bool:
        entry = categories.get(category_name.casefold())
        return bool(entry and entry.get("collapsed"))

    tools = {
        tool["name"]
        for tool in all_tools()
        if tool.get("name") in enabled_tool_ids and is_collapsed(_tool_category(tool))
    }
    resources = {
        resource["id"]
        for resource in all_resources()
        if resource.get("id") in enabled_resource_ids and is_collapsed(_resource_category(resource))
    }
    templates = {
        template["id"]
        for template in all_resource_templates()
        if template.get("id") in enabled_template_ids and is_collapsed(_template_category(template))
    }
    _ = (force_all, always_supplied)
    return {"tools": tools, "resources": resources, "templates": templates}


EXTRA_CAPABILITIES_HEADER = (
    "Extra API capabilities (batched by category to save tokens; "
    "read the linked chievfx://categories resource for full tool/resource details):"
)


def format_collapsed_category_line(entry: dict[str, Any]) -> str:
    tool_count = len(entry["tools"])
    # Resource templates are just parameterized resources to the agent; count them together.
    resource_count = len(entry["resources"]) + len(entry["templates"])
    counts: list[str] = []
    if tool_count:
        counts.append(f"{tool_count} tools")
    if resource_count:
        counts.append(f"{resource_count} resources")
    count_text = ", ".join(counts)
    description = str(entry.get("description") or "").strip()
    suffix = f" {description}" if description else ""
    return f"- {entry['name']} ({count_text}):{suffix} -> {CATEGORY_RESOURCE_URI_PREFIX}{entry['slug']}"


def build_extra_capabilities_section(plan: dict[str, Any]) -> str:
    collapsed = collapsed_categories(plan)
    if not collapsed:
        return ""
    lines = [EXTRA_CAPABILITIES_HEADER]
    lines.extend(format_collapsed_category_line(entry) for entry in collapsed)
    return "\n".join(lines)


def dynamic_category_resources() -> list[dict[str, Any]]:
    """Compact resource descriptors advertised in resources/list for every
    currently collapsed category."""
    plan = build_category_plan()
    resources: list[dict[str, Any]] = []
    for entry in collapsed_categories(plan):
        resources.append(
            {
                "uri": f"{CATEGORY_RESOURCE_URI_PREFIX}{entry['slug']}",
                "name": f"{entry['name']} category",
                "description": (
                    f"Full tool/resource details for the {entry['name']} category "
                    f"(collapsed from initialize.instructions to save tokens)."
                ),
                "mimeType": RESOURCE_MIME_TYPE,
            }
        )
    return resources


def category_slug_from_uri(uri: str) -> str | None:
    if not isinstance(uri, str) or not uri.startswith(CATEGORY_RESOURCE_URI_PREFIX):
        return None
    slug = uri[len(CATEGORY_RESOURCE_URI_PREFIX):]
    if not slug or "/" in slug:
        return None
    return slug


def get_category_resource_by_uri(uri: str) -> dict[str, Any] | None:
    slug = category_slug_from_uri(uri)
    if slug is None:
        return None
    plan = build_category_plan()
    for entry in plan.get("categories", {}).values():
        if entry["slug"] == slug:
            return entry
    return None


# Cross-cutting URI grammar for parameterized (template) resources. Appended to a
# category resource body only when that category exposes resource templates.
RESOURCE_URI_ENCODING_NOTES = [
    "URI encoding for the templates above:",
    "- Encode every scene path, gameobject hierarchy path, component key, and asset filterSpec as one URI segment.",
    "- Use percent-encoding with no safe slash: quote(value, safe='').",
    "- gameobject paths keep ChievFX grammar: / separator, \\/ literal slash, \\\\ literal backslash, [n] duplicate suffix.",
    "- Component keys use simple class names. Duplicate simple names are suffixed 1-based, e.g. BoxCollider.1.",
    "- asset filterSpec uses semicolon key=value clauses: name, type, label, area, folder, limit, subassets.",
    "Outputs are compact text/plain TOON with readAt metadata, drill-down URIs, truncation flags, and hard caps.",
]


def category_resource_body(entry: dict[str, Any]) -> str:
    lines = [f"{entry['name']} category"]
    description = str(entry.get("description") or "").strip()
    if description:
        lines.append(description)
    lines.append("")
    if entry["tools"]:
        lines.append("Tools:")
        lines.extend(sorted(entry["tools"]))
    if entry["resources"]:
        lines.append("Resources:")
        lines.extend(sorted(entry["resources"]))
    if entry["templates"]:
        lines.append("Resource templates:")
        lines.extend(sorted(entry["templates"]))
        lines.append("")
        lines.extend(RESOURCE_URI_ENCODING_NOTES)
    lines.append("")
    lines.append(
        "More optional tools for this category may exist but be disabled; "
        "use tools-list-category to inspect and tools-set-enabled-state to enable them."
    )
    return "\n".join(lines)
