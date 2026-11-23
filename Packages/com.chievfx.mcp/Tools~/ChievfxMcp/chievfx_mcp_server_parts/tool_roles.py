# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

from __future__ import annotations

# Tool roles: presets that switch a whole set of tools on for the project, and their reports.

def resolve_tool_role(arguments: dict[str, Any], metadata: dict[str, Any]) -> dict[str, Any]:
    catalog = build_tool_role_catalog(metadata)
    roles = catalog["roles"]
    role_index = arguments.get("roleIndex", arguments.get("index"))
    if isinstance(role_index, int) and not isinstance(role_index, bool):
        if 1 <= role_index <= len(roles):
            return roles[role_index - 1]
        raise ValueError(f"Unknown ChievFX MCP role index {role_index}. Available role indexes: 1-{len(roles)}.")

    # The schema names it customAssetPath; the misspelling is what this handler used to read, kept for old callers.
    custom_asset_path = arguments.get("customAssetPath", arguments.get("customassetPath"))
    role_name = arguments.get("role", arguments.get("roleId"))
    candidates: list[tuple[str, dict[str, Any]]] = []

    for role in roles:
        candidates.append((str(role.get("id", "")).casefold(), role))
        candidates.append((normalize_role_id(str(role.get("displayName", ""))).casefold(), role))
        asset_path = role.get("assetPath")
        if isinstance(asset_path, str) and asset_path:
            candidates.append((asset_path.casefold(), role))

    if isinstance(custom_asset_path, str) and custom_asset_path.strip():
        wanted = custom_asset_path.strip().casefold()
        for key, role in candidates:
            if key == wanted and role.get("kind") == "custom":
                return role
        raise ValueError(f"Unknown ChievFX MCP custom role asset '{custom_asset_path}'. Create it in Window > ChievFX > MCP Tools.")

    if isinstance(role_name, str) and role_name.strip():
        wanted = normalize_role_id(role_name)
        for key, role in candidates:
            if key == wanted.casefold():
                return role
        available = ", ".join(role["id"] for role in roles)
        raise ValueError(f"Unknown ChievFX MCP role '{role_name}'. Available roles: {available}.")

    raise ValueError("tools-set-role requires 'role', 'roleId', 'roleIndex', or 'customAssetPath'.")


def enabled_ids_for_role(role: dict[str, Any], metadata: dict[str, Any]) -> set[str]:
    categories = set(coerce_string_list(role.get("enabledCategoryIds")))
    explicit_tools = set(coerce_string_list(role.get("enabledToolIds")))
    required_ids = tool_required_ids_for_metadata(metadata)
    known_tool_ids = {tool["name"] for tool in metadata.get("tools", [])}
    enabled: set[str] = set(required_ids) | (DEFAULT_ENABLED_TOOL_IDS & known_tool_ids)
    for tool in metadata.get("tools", []):
        name = tool["name"]
        if tool.get("category") in categories or name in explicit_tools:
            enabled.add(name)
    return enabled


def get_tool_role_state(arguments: dict[str, Any]) -> dict[str, Any]:
    return {
        "roles": [
            {
                "index": index,
                "title": role.get("displayName") or role.get("id", ""),
                "description": role.get("description", ""),
            }
            for index, role in enumerate(load_builtin_tool_roles() + load_custom_tool_roles(), start=1)
        ],
    }


def format_tool_role_catalog_text(result: dict[str, Any]) -> str:
    lines = ["Available roles:"]
    roles = result.get("roles")
    if not isinstance(roles, list):
        return "\n".join(lines)

    for role in roles:
        if not isinstance(role, dict):
            continue
        index = role.get("index")
        title = str(role.get("title", "")).strip()
        description = str(role.get("description", "")).strip()
        prefix = f"{index}. " if isinstance(index, int) else "- "
        if title and description:
            lines.append(f"{prefix}{title}: {description}")
        elif title:
            lines.append(f"{prefix}{title}")
    return "\n".join(lines)


def get_tool_role_details(arguments: dict[str, Any]) -> dict[str, Any]:
    metadata = build_tool_metadata()
    role = resolve_tool_role(arguments, metadata)
    roles = build_tool_role_catalog(metadata)["roles"]
    role_index = next(
        (
            index
            for index, candidate in enumerate(roles, start=1)
            if candidate.get("id") == role.get("id")
            and candidate.get("kind") == role.get("kind")
            and candidate.get("assetPath") == role.get("assetPath")
        ),
        0,
    )
    enabled_ids = enabled_ids_for_role(role, metadata)
    enabled_tools_by_category: dict[str, list[str]] = {}

    for tool in metadata.get("tools", []):
        name = tool["name"]
        if name not in enabled_ids:
            continue
        category = tool.get("category") or TOOL_CATEGORIES.get(name, "general")
        enabled_tools_by_category.setdefault(category, []).append(name)

    return {
        "role": {
            "index": role_index,
            "title": role.get("displayName") or role.get("id", ""),
            "description": role.get("description", ""),
        },
        "categories": [
            {
                "name": category,
                "tools": sorted(tools),
            }
            for category, tools in sorted(enabled_tools_by_category.items(), key=lambda item: get_category_sort_key(item[0]))
        ],
    }


def format_tool_role_details_text(result: dict[str, Any]) -> str:
    role = result.get("role")
    lines: list[str] = []
    if isinstance(role, dict):
        index = role.get("index")
        title = str(role.get("title", "")).strip()
        description = str(role.get("description", "")).strip()
        heading = f"Role {index}: {title}" if isinstance(index, int) else f"Role: {title}"
        lines.append(heading)
        if description:
            lines.append(description)

    categories = result.get("categories")
    if not isinstance(categories, list):
        return "\n".join(lines)

    lines.append("Tools:")
    for category in categories:
        if not isinstance(category, dict):
            continue
        name = str(category.get("name", "")).strip()
        tools = category.get("tools")
        if not name or not isinstance(tools, list):
            continue
        lines.append(f"- {name}")
        for tool in tools:
            lines.append(f"  - {tool}")
    return "\n".join(lines)


def format_tool_role_set_compact_text(result: dict[str, Any]) -> str:
    return "success"


def format_set_enabled_state_text(result: dict[str, Any]) -> str:
    skipped = result.get("skipped")
    if isinstance(skipped, list) and skipped and not result.get("mutated"):
        reason = format_set_enabled_reason(skipped)
        return f"failure: {reason}" if reason else "failure"

    if result.get("mutated"):
        return "success"

    reason = format_no_change_reason(result)
    return f"success: {reason}" if reason else "success"


def format_set_enabled_reason(skipped: list[Any]) -> str:
    reasons: list[str] = []
    for row in skipped:
        if not isinstance(row, dict):
            continue
        target = row.get("target")
        reason = row.get("reason")
        if target and reason:
            reasons.append(f"{target} {reason}")
    return ", ".join(reasons)


def format_no_change_reason(result: dict[str, Any]) -> str:
    unchanged = result.get("unchanged")
    if not isinstance(unchanged, list) or not unchanged:
        return "no changes"

    requested_enabled = result.get("requestedEnabled")
    state = "already enabled" if requested_enabled is True else "already disabled"
    targets = [str(row.get("tool")) for row in unchanged if isinstance(row, dict) and row.get("tool")]
    if len(targets) == 1:
        return f"{targets[0]} {state}"
    return f"{len(targets)} tools {state}" if targets else "no changes"


def set_tool_role(arguments: dict[str, Any]) -> dict[str, Any]:
    metadata = build_tool_metadata()
    role = resolve_tool_role(arguments, metadata)
    roles = build_tool_role_catalog(metadata)["roles"]
    role_index = next(
        (
            index
            for index, candidate in enumerate(roles, start=1)
            if candidate.get("id") == role.get("id")
            and candidate.get("kind") == role.get("kind")
            and candidate.get("assetPath") == role.get("assetPath")
        ),
        0,
    )
    before = load_enabled_tool_ids()
    after = enabled_ids_for_role(role, metadata)
    changed = before != after
    availability = summarize_role_availability(role, metadata)
    role_state = {
        "kind": role.get("kind", "built-in"),
        "roleId": role.get("id"),
        "displayName": role.get("displayName"),
        "description": role.get("description", ""),
        "customassetPath": role.get("assetPath"),
        "manualOverride": False,
        "appliedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "appliedEnabledToolIds": sorted(after),
        "matchedCategoryIds": availability["matchedCategoryIds"],
        "matchedToolIds": availability["matchedToolIds"],
        "unavailableCategoryIds": availability["unavailableCategoryIds"],
        "unavailableToolIds": availability["unavailableToolIds"],
    }
    save_enabled_tool_ids(after, metadata, role_state=role_state)
    inventory = build_tool_inventory(include_tools=False)
    return {
        "mutated": changed,
        "roleIndex": role_index,
        "roleState": role_state,
        "role": role,
        "counts": inventory["counts"],
        "mcpReloadRequired": changed,
        "guidance": TOOL_RELOAD_GUIDANCE,
        "selectionPath": str(TOOL_SELECTION_PATH),
    }
