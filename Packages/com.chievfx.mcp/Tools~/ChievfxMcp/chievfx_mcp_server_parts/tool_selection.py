# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

from __future__ import annotations

def tool_required_ids_for_metadata(metadata: dict[str, Any]) -> set[str]:
    return {tool["name"] for tool in metadata.get("tools", []) if tool.get("required")} | set(metadata.get("requiredToolIds", []))


def load_tool_selection_payload() -> dict[str, Any]:
    try:
        payload = json.loads(TOOL_SELECTION_PATH.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {}
    return payload if isinstance(payload, dict) else {}


def load_enabled_tool_ids() -> set[str]:
    tools = all_tools()
    tool_names = {tool["name"] for tool in tools}
    required_tool_ids = required_tool_ids_for_tools(tools) & tool_names
    payload = load_tool_selection_payload()

    enabled_ids = payload.get("enabledToolIds")
    if not isinstance(enabled_ids, list):
        # First install: enable every tool except the autonomy/discovery helpers
        # (the "autonomous" category), which stay hidden from the tools tab by default.
        default_enabled = {tool["name"] for tool in tools if _tool_category(tool) != "autonomous"}
        return (default_enabled | required_tool_ids) & tool_names

    selected_tool_ids = {item for item in enabled_ids if isinstance(item, str)}
    if "tools-get-roles" in selected_tool_ids:
        selected_tool_ids.add("tools-get-role")
    return (selected_tool_ids & tool_names) | required_tool_ids


def load_tool_role_state() -> dict[str, Any]:
    payload = load_tool_selection_payload()
    state = payload.get("roleState")
    if isinstance(state, dict):
        return state
    # First install defaults to "all tools except autonomous", which is not a
    # built-in role preset, so report it as a manual selection.
    return {"kind": "manual", "manualOverride": False}


def save_enabled_tool_ids(
    enabled_tool_ids: set[str],
    metadata: dict[str, Any] | None = None,
    role_state: dict[str, Any] | None = None,
    mark_manual_override: bool = False,
) -> None:
    metadata = metadata or build_tool_metadata()
    tool_names = {tool["name"] for tool in all_tools()}
    required_tool_ids = set(metadata.get("requiredToolIds", [])) & tool_names
    persisted_ids = (enabled_tool_ids & tool_names) | required_tool_ids
    if role_state is None:
        role_state = load_tool_role_state()
        if mark_manual_override and role_state.get("kind") in {"built-in", "custom"}:
            role_state = dict(role_state)
            role_state["manualOverride"] = True

    TOOL_SELECTION_PATH.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "schemaVersion": TOOL_SELECTION_SCHEMA_VERSION,
        "updatedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "source": metadata.get("source", "Tools/ChievfxMcp/chievfx_mcp_server.py:TOOLS"),
        "estimator": metadata.get("estimator", "unknown"),
        "note": TOOL_SELECTION_NOTE,
        "descriptorEstimateBasis": metadata.get("descriptorEstimateBasis", DESCRIPTOR_ESTIMATE_BASIS),
        "descriptionEstimateBasis": metadata.get("descriptionEstimateBasis", TOOL_DESCRIPTION_ESTIMATE_BASIS),
        "callEnvelopeEstimateBasis": metadata.get("callEnvelopeEstimateBasis", CALL_ENVELOPE_ESTIMATE_BASIS),
        "responseEstimateNote": metadata.get("responseEstimateNote", RESPONSE_ESTIMATE_NOTE),
        "roleState": role_state,
        "enabledToolIds": sorted(persisted_ids),
        "tools": {
            tool["name"]: {
                "descriptorHash": tool.get("descriptorHash", ""),
                "estimatedTokens": tool.get("estimatedTokens", 0),
                "descriptionEstimatedTokens": tool.get("descriptionEstimatedTokens", 0),
                "descriptorBytes": tool.get("descriptorBytes", 0),
                "callEnvelopeEstimatedTokens": tool.get("callEnvelopeEstimatedTokens", 0),
                "callEnvelopeBytes": tool.get("callEnvelopeBytes", 0),
                "responseEstimateProfile": (tool.get("responseEstimate") or {}).get("profile", ""),
                "required": bool(tool.get("required")),
                "category": tool.get("category", "general"),
                "source": tool.get("source", "core"),
                "sourceExtensionId": tool.get("sourceExtensionId"),
            }
            for tool in sorted(metadata.get("tools", []), key=lambda item: item["name"])
        },
    }
    TOOL_SELECTION_PATH.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    dump_debug_instructions("tool-selection-save")


def coerce_string_list(value: Any) -> list[str]:
    if value is None:
        return []
    if isinstance(value, str):
        stripped = value.strip()
        return [stripped] if stripped else []
    if isinstance(value, list):
        return [item.strip() for item in value if isinstance(item, str) and item.strip()]
    return []


def tool_argument_names(tool: dict[str, Any]) -> list[str]:
    schema = tool.get("inputSchema")
    if not isinstance(schema, dict):
        return []
    properties = schema.get("properties")
    if not isinstance(properties, dict):
        return []
    return [name for name in properties.keys() if name != "outputFormat"]


def build_tool_inventory(category_filter: str | None = None, include_tools: bool = True) -> dict[str, Any]:
    metadata = build_tool_metadata()
    enabled_ids = load_enabled_tool_ids()
    tools = metadata["tools"]
    category_lookup = build_category_lookup(tools)
    selected_category = None
    unknown_category = None

    if category_filter:
        selected_category = resolve_category_name(category_filter, category_lookup)
        if selected_category is None:
            unknown_category = category_filter

    categories: list[dict[str, Any]] = []
    for category in sorted({tool["category"] for tool in tools}, key=get_category_sort_key):
        if selected_category is not None and category != selected_category:
            continue

        category_tools = [tool for tool in tools if tool["category"] == category]
        required_tools = [tool for tool in category_tools if tool.get("required")]
        optional_tools = [tool for tool in category_tools if not tool.get("required")]
        enabled_tools_in_category = [tool for tool in category_tools if tool["name"] in enabled_ids]
        enabled_optional = [tool for tool in optional_tools if tool["name"] in enabled_ids]
        selected_tokens = sum(int(tool.get("estimatedTokens", 0)) for tool in enabled_tools_in_category)
        total_tokens = sum(int(tool.get("estimatedTokens", 0)) for tool in category_tools)
        selected_description_tokens = sum(
            int(tool.get("descriptionEstimatedTokens", 0)) for tool in enabled_tools_in_category
        )
        total_description_tokens = sum(int(tool.get("descriptionEstimatedTokens", 0)) for tool in category_tools)
        selected_call_tokens = sum(int(tool.get("callEnvelopeEstimatedTokens", 0)) for tool in enabled_tools_in_category)
        total_call_tokens = sum(int(tool.get("callEnvelopeEstimatedTokens", 0)) for tool in category_tools)
        state = category_state(len(optional_tools), len(enabled_optional))
        row: dict[str, Any] = {
            "name": category,
            "description": TOOL_CATEGORY_DESCRIPTIONS.get(category, ""),
            "requiredOnly": len(optional_tools) == 0,
            "enabled": len(enabled_optional) == len(optional_tools),
            "state": state,
            "requiredToolCount": len(required_tools),
            "optionalToolCount": len(optional_tools),
            "enabledToolCount": len(enabled_tools_in_category),
            "totalToolCount": len(category_tools),
            "selectedEstimatedTokens": selected_tokens,
            "totalEstimatedTokens": total_tokens,
            "selectedDescriptionEstimatedTokens": selected_description_tokens,
            "totalDescriptionEstimatedTokens": total_description_tokens,
            "selectedCallEnvelopeEstimatedTokens": selected_call_tokens,
            "totalCallEnvelopeEstimatedTokens": total_call_tokens,
        }
        if include_tools:
            row["tools"] = [
                {
                    "name": tool["name"],
                    "description": tool.get("description", ""),
                    "arguments": tool_argument_names(tool),
                    "required": bool(tool.get("required")),
                    "enabled": tool["name"] in enabled_ids,
                    "estimatedTokens": int(tool.get("estimatedTokens", 0)),
                    "descriptionEstimatedTokens": int(tool.get("descriptionEstimatedTokens", 0)),
                    "descriptorBytes": int(tool.get("descriptorBytes", 0)),
                    "descriptorHash": tool.get("descriptorHash", ""),
                    "descriptorPreview": tool.get("descriptorPreview", ""),
                    "callEnvelopeEstimatedTokens": int(tool.get("callEnvelopeEstimatedTokens", 0)),
                    "callEnvelopeBytes": int(tool.get("callEnvelopeBytes", 0)),
                    "callEnvelopePreview": tool.get("callEnvelopePreview", ""),
                    "responseEstimate": tool.get("responseEstimate", {}),
                    "source": tool.get("source", "core"),
                    "sourceExtensionId": tool.get("sourceExtensionId"),
                    "sourceExtensionName": tool.get("sourceExtensionName"),
                }
                for tool in sorted(category_tools, key=lambda item: (not item.get("required"), item["name"]))
            ]
        categories.append(row)

    selected_tools = [tool for tool in tools if tool["name"] in enabled_ids]
    required_tools = [tool for tool in tools if tool.get("required")]
    optional_tools = [tool for tool in tools if not tool.get("required")]
    enabled_optional = [tool for tool in optional_tools if tool["name"] in enabled_ids]
    selected_descriptor_tokens = sum(int(tool.get("estimatedTokens", 0)) for tool in selected_tools)
    total_descriptor_tokens = sum(int(tool.get("estimatedTokens", 0)) for tool in tools)
    selected_description_tokens = sum(int(tool.get("descriptionEstimatedTokens", 0)) for tool in selected_tools)
    total_description_tokens = sum(int(tool.get("descriptionEstimatedTokens", 0)) for tool in tools)
    selected_call_tokens = sum(int(tool.get("callEnvelopeEstimatedTokens", 0)) for tool in selected_tools)
    total_call_tokens = sum(int(tool.get("callEnvelopeEstimatedTokens", 0)) for tool in tools)

    return {
        "schemaVersion": TOOL_SELECTION_SCHEMA_VERSION,
        "selectionPath": str(TOOL_SELECTION_PATH),
        "policyPath": str(TOOL_POLICY_PATH),
        "rolePresetPath": str(TOOL_ROLE_PRESETS_PATH),
        "estimator": metadata.get("estimator", "unknown"),
        "descriptorEstimateBasis": metadata.get("descriptorEstimateBasis", DESCRIPTOR_ESTIMATE_BASIS),
        "descriptionEstimateBasis": metadata.get("descriptionEstimateBasis", TOOL_DESCRIPTION_ESTIMATE_BASIS),
        "callEnvelopeEstimateBasis": metadata.get("callEnvelopeEstimateBasis", CALL_ENVELOPE_ESTIMATE_BASIS),
        "responseEstimateNote": metadata.get("responseEstimateNote", RESPONSE_ESTIMATE_NOTE),
        "mutated": False,
        "mcpReloadRequired": False,
        "guidance": TOOL_RELOAD_GUIDANCE,
        "unknownCategory": unknown_category,
        "extensions": metadata.get("extensions", []),
        "extensionErrors": metadata.get("extensionErrors", []),
        "roleState": load_tool_role_state(),
        "roles": build_tool_role_catalog(metadata)["roles"],
        "counts": {
            "enabledToolCount": len(selected_tools),
            "totalToolCount": len(tools),
            "requiredToolCount": len(required_tools),
            "enabledOptionalToolCount": len(enabled_optional),
            "optionalToolCount": len(optional_tools),
            "selectedEstimatedTokens": selected_descriptor_tokens,
            "totalEstimatedTokens": total_descriptor_tokens,
            "selectedDescriptionEstimatedTokens": selected_description_tokens,
            "totalDescriptionEstimatedTokens": total_description_tokens,
            "selectedDescriptorBytes": sum(int(tool.get("descriptorBytes", 0)) for tool in selected_tools),
            "totalDescriptorBytes": sum(int(tool.get("descriptorBytes", 0)) for tool in tools),
            "selectedCallEnvelopeEstimatedTokens": selected_call_tokens,
            "totalCallEnvelopeEstimatedTokens": total_call_tokens,
        },
        "categories": categories,
    }


def category_state(optional_count: int, enabled_optional_count: int) -> str:
    if optional_count == 0:
        return "required-only"
    if enabled_optional_count == 0:
        return "optional-disabled"
    if enabled_optional_count == optional_count:
        return "optional-enabled"
    return "optional-partial"


def build_category_lookup(tools: list[dict[str, Any]]) -> dict[str, str]:
    lookup: dict[str, str] = {}
    for tool in tools:
        category = tool["category"]
        lookup.setdefault(category.casefold(), category)
        lookup.setdefault(category_slug(category), category)
    return lookup


def resolve_category_name(category_filter: str, lookup: dict[str, str]) -> str | None:
    stripped = category_filter.strip()
    if not stripped:
        return None
    resolved = lookup.get(stripped.casefold())
    if resolved is not None:
        return resolved
    return lookup.get(category_slug(stripped))


def get_category_sort_key(category: str) -> tuple[int, str]:
    order = {
        "essentials": 0,
        "autonomous": 1,
        "editor-window": 2,
        "scene": 3,
        "gameobject": 4,
        "prefab": 5,
        "package-manager": 6,
        "script-execution-tests": 7,
        "profiler": 8,
        "frame-debugger": 9,
        "obsolete": 999,
    }
    return (order.get(category, 100), category)


def list_tool_categories_for_agents() -> dict[str, Any]:
    return build_tool_inventory(include_tools=False)


def list_tool_category_for_agents(arguments: dict[str, Any]) -> dict[str, Any]:
    category = arguments.get("category")
    if not isinstance(category, str) or not category.strip():
        raise ValueError("tools-list-category requires string argument 'category'.")
    return build_tool_inventory(category, include_tools=True)


def read_include_disabled(arguments: dict[str, Any]) -> bool:
    return arguments.get("includeDisabled") is True


def format_tool_batch_text(result: dict[str, Any]) -> str:
    lines = [
        "tool:{tool} success:{success} successCount:{successCount} failedCount:{failedCount} totalCount:{totalCount}".format(
            tool=format_toon_atom(result.get("tool")),
            success=format_toon_atom(result.get("success")),
            successCount=format_toon_atom(result.get("successCount")),
            failedCount=format_toon_atom(result.get("failedCount")),
            totalCount=format_toon_atom(result.get("totalCount")),
        )
    ]
    failures = result.get("failures")
    if isinstance(failures, list) and failures:
        lines.append("failures:")
        for failure in failures:
            if isinstance(failure, dict):
                lines.append(f"- index:{format_toon_atom(failure.get('index'))} error:{format_toon_atom(failure.get('error'))}")
    return "\n".join(lines)


def format_tool_categories_text(inventory: dict[str, Any], include_disabled: bool = False) -> str:
    lines: list[str] = []
    categories = inventory.get("categories")
    if isinstance(categories, list):
        enabled_categories = [
            category for category in categories if isinstance(category, dict) and int(category.get("enabledToolCount", 0)) > 0
        ]
        disabled_categories = [
            category for category in categories if isinstance(category, dict) and int(category.get("enabledToolCount", 0)) == 0
        ]
        lines.append("enabled:")
        for category in enabled_categories:
            lines.extend(format_tool_category_summary_lines(category))
        if include_disabled and disabled_categories:
            lines.append("")
            lines.append("disabled:")
            for category in disabled_categories:
                lines.extend(format_tool_category_summary_lines(category))
    return "\n".join(lines)


def format_tool_category_summary_lines(category: dict[str, Any]) -> list[str]:
    name = str(category.get("name") or "")
    enabled = category.get("enabledToolCount")
    total = category.get("totalToolCount")
    description = str(category.get("description") or "").strip()
    lines = [f"- {name} ({enabled}/{total})"]
    if description:
        lines.append(description)
    return lines


def format_tool_category_text(inventory: dict[str, Any], include_disabled: bool = False) -> str:
    unknown = inventory.get("unknownCategory")
    if unknown:
        return f"unknown category: {unknown}"

    categories = inventory.get("categories")
    if not isinstance(categories, list) or not categories:
        return "no tools found"

    category = categories[0]
    if not isinstance(category, dict):
        return "no tools found"

    lines: list[str] = []
    tools = category.get("tools")
    if isinstance(tools, list):
        enabled_tools = [tool for tool in tools if isinstance(tool, dict) and tool.get("enabled") is True]
        disabled_tools = [tool for tool in tools if isinstance(tool, dict) and tool.get("enabled") is not True]
        if enabled_tools or include_disabled:
            lines.append("enabled:")
            if enabled_tools:
                for tool in enabled_tools:
                    lines.extend(format_tool_row_lines(tool))
            else:
                lines.append("- none")
        if include_disabled and disabled_tools:
            if lines:
                lines.append("")
            lines.append("disabled:")
            for tool in disabled_tools:
                lines.extend(format_tool_row_lines(tool))
    return "\n".join(lines)


def format_tool_row_lines(tool: dict[str, Any]) -> list[str]:
    name = str(tool.get("name") or "")
    arguments = tool.get("arguments")
    args_text = ", ".join(str(arg) for arg in arguments) if isinstance(arguments, list) else ""
    description = str(tool.get("description") or "").strip()
    lines = [f"- {name} ({args_text})" if args_text else f"- {name}"]
    if description:
        lines.append(description)
    return lines


def set_tools_enabled_state(arguments: dict[str, Any]) -> dict[str, Any]:
    enabled = arguments.get("enabled")
    if not isinstance(enabled, bool):
        raise ValueError("tools-set-enabled-state requires boolean argument 'enabled'.")

    requested_categories = coerce_string_list(arguments.get("category")) + coerce_string_list(arguments.get("categories"))
    requested_tools = coerce_string_list(arguments.get("tool")) + coerce_string_list(arguments.get("tools"))
    if not requested_categories and not requested_tools:
        raise ValueError("tools-set-enabled-state requires 'category', 'categories', 'tool', or 'tools'.")

    metadata = build_tool_metadata()
    known_tools = {tool["name"]: tool for tool in metadata["tools"]}
    tool_lookup = {name.casefold(): name for name in known_tools}
    category_lookup = build_category_lookup(metadata["tools"])

    skipped: list[dict[str, Any]] = []
    target_tool_ids: set[str] = set()
    matched_categories: list[str] = []

    for category in requested_categories:
        resolved = resolve_category_name(category, category_lookup)
        if resolved is None:
            skipped.append({"target": category, "kind": "category", "reason": "unknown-category"})
            continue
        matched_categories.append(resolved)
        target_tool_ids.update(tool["name"] for tool in metadata["tools"] if tool["category"] == resolved)

    for tool_name in requested_tools:
        resolved = tool_lookup.get(tool_name.casefold())
        if resolved is None:
            skipped.append({"target": tool_name, "kind": "tool", "reason": "unknown-tool"})
            continue
        target_tool_ids.add(resolved)

    enabled_ids = load_enabled_tool_ids()
    required_ids = set(metadata.get("requiredToolIds", [])) & set(known_tools)
    # essentials tools must always stay enabled (lock in UI).
    required_ids |= {
        tool["name"]
        for tool in metadata.get("tools", [])
        if tool.get("category") == "essentials"
    }
    # Ensure first-party essentials tool cannot be disabled via UI.
    required_ids.add("editor-playmode-set")
    changed: list[dict[str, Any]] = []
    unchanged: list[dict[str, Any]] = []

    for tool_id in sorted(target_tool_ids):
        tool = known_tools[tool_id]
        before = tool_id in enabled_ids
        if tool_id in required_ids and not enabled:
            skipped.append(
                {
                    "target": tool_id,
                    "kind": "tool",
                    "category": tool.get("category", "general"),
                    "reason": "required-tool-locked-enabled",
                }
            )
            enabled_ids.add(tool_id)
            continue

        if enabled:
            enabled_ids.add(tool_id)
        else:
            enabled_ids.discard(tool_id)

        after = tool_id in enabled_ids
        change_row = {
            "tool": tool_id,
            "category": tool.get("category", "general"),
            "required": tool_id in required_ids,
            "before": before,
            "after": after,
        }
        if before == after:
            unchanged.append(change_row)
        else:
            changed.append(change_row)

    if changed:
        save_enabled_tool_ids(enabled_ids, metadata, mark_manual_override=True)

    inventory = build_tool_inventory(include_tools=False)
    return {
        "mutated": bool(changed),
        "requestedEnabled": enabled,
        "mcpReloadRequired": bool(changed),
        "guidance": TOOL_RELOAD_GUIDANCE,
        "matchedCategories": sorted(set(matched_categories), key=get_category_sort_key),
        "changed": changed,
        "unchanged": unchanged,
        "skipped": skipped,
        "counts": inventory["counts"],
        "selectionPath": str(TOOL_SELECTION_PATH),
    }


def resolve_tool_role(arguments: dict[str, Any], metadata: dict[str, Any]) -> dict[str, Any]:
    catalog = build_tool_role_catalog(metadata)
    roles = catalog["roles"]
    role_index = arguments.get("roleIndex", arguments.get("index"))
    if isinstance(role_index, int) and not isinstance(role_index, bool):
        if 1 <= role_index <= len(roles):
            return roles[role_index - 1]
        raise ValueError(f"Unknown ChievFX MCP role index {role_index}. Available role indexes: 1-{len(roles)}.")

    custom_asset_path = arguments.get("customassetPath")
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

    raise ValueError("tools-set-role requires 'role', 'roleId', 'roleIndex', or 'customassetPath'.")


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


def enabled_tools() -> list[dict[str, Any]]:
    enabled_ids = load_enabled_tool_ids()
    tools: list[dict[str, Any]] = []
    for tool in all_tools():
        if tool["name"] not in enabled_ids:
            continue

        advertised_tool = {
            "name": tool["name"],
            "description": tool.get("description", ""),
            "inputSchema": advertised_input_schema(tool),
        }
        tools.append(advertised_tool)

    return tools
