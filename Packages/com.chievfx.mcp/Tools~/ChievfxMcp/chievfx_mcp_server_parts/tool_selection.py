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


# Categories that "expose all non-hidden" never auto-enables: autonomy/discovery helpers and
# deprecated tools. Mirrors the C# panel's hidden set and the Tools tab's "Enable All" skip list.
# remote-shell is hidden from every bulk path too — expose-all, first-install defaults, "enable all"
# in a category, and any role preset. When armed it is added back explicitly in load_enabled_tool_ids,
# so the Connection tab switch stays the only thing that can expose a command-running tool.
HIDDEN_TOOL_CATEGORIES = ("autonomous", "obsolete", "remote-shell")


def manual_tool_resource_selection_enabled() -> bool:
    """Master toggle (Connection tab). False (default, file missing) = expose every non-hidden tool and
    resource and ignore any saved per-tool/category selection. True = honor the saved manual selection.
    The project's UserSettings/ChievfxMcpAvailability.json holds it: the editor writes it when the switch changes and
    seeds it from EditorPrefs only when the file is missing; tools-set-role turns it on."""
    try:
        payload = json.loads(AVAILABILITY_SETTINGS_PATH.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return False
    return isinstance(payload, dict) and payload.get("manualSelection") is True


def load_enabled_tool_ids() -> set[str]:
    tools = all_tools()
    tool_names = {tool["name"] for tool in tools}
    required_tool_ids = required_tool_ids_for_tools(tools) & tool_names
    # remote-cmd reaches required_tool_ids through its descriptor's required flag, which is what keeps
    # the Connection tab switch authoritative: all_tools() only lists it once armed, and then it is
    # locked enabled rather than needing a second opt-in from a saved selection that predates it.

    if not manual_tool_resource_selection_enabled():
        # Expose-all mode (default): every non-hidden tool, regardless of any saved selection.
        exposed = {tool["name"] for tool in tools if _tool_category(tool) not in HIDDEN_TOOL_CATEGORIES}
        return (exposed | required_tool_ids) & tool_names

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
        "video": 10,
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


# Arguments every tool accepts regardless of its own schema: outputFormat is injected into each
# descriptor, timeoutMs is honored generically by get_tool_timeout_ms, and awaitMs is taken off the call
# by the server, which then waits for the end event the tool names in its reply.
UNIVERSAL_TOOL_ARGUMENT_KEYS = frozenset({"outputFormat", "timeoutMs", "awaitMs"})

# Tools that wait themselves or wrap other calls; awaitMs on them is a mistake worth an error.
AWAIT_IGNORING_TOOL_IDS = frozenset({"events-wait", "events-check-since", "tool-batch"})

# Undocumented-but-honored aliases. The editor reads these even though only the canonical name is in
# the schema, so they must NOT be reported as unrecognized — never tell a caller an argument had no
# effect when Unity actually acts on it. (Sources: the alias arrays / multi-key reads in
# ChievfxMcpFirstPartyExtensionLoader, ChievfxMcpRuntimeUiAdapterRegistry, ChievfxMcpRuntimeUiInteractionInput.)
_RUNTIME_UI_TARGET_ALIASES = frozenset({"targetPath", "targetRef", "targetName", "visualElementRef", "name"})
_RUNTIME_UI_POINT_ALIASES = frozenset({"normalized", "screenPosition"})
_RUNTIME_UI_DRAG_ALIASES = _RUNTIME_UI_POINT_ALIASES | frozenset(
    {"startScreenPosition", "startNormalized", "endScreenPosition", "endNormalized", "delta"}
)

TOOL_ARGUMENT_ALIASES: dict[str, frozenset[str]] = {
    "editor-playmode-set": frozenset({"play", "playing", "enabled"}),
    "ui-control-find": frozenset({"query", "nameContains", "namePattern", "search", "pattern", "text"}),
    "bridge-get-operation": frozenset({"operationId"}),
    "ui-runtime-click": _RUNTIME_UI_TARGET_ALIASES | _RUNTIME_UI_POINT_ALIASES,
    "ui-runtime-focus": _RUNTIME_UI_TARGET_ALIASES | _RUNTIME_UI_POINT_ALIASES,
    "ui-runtime-set-control-value": _RUNTIME_UI_TARGET_ALIASES | _RUNTIME_UI_POINT_ALIASES,
    "ui-runtime-probe": _RUNTIME_UI_POINT_ALIASES,
    "ui-runtime-clear-focus": _RUNTIME_UI_TARGET_ALIASES,
    "ui-runtime-type-text": _RUNTIME_UI_TARGET_ALIASES | _RUNTIME_UI_POINT_ALIASES,
    "ui-runtime-drag": _RUNTIME_UI_TARGET_ALIASES | _RUNTIME_UI_DRAG_ALIASES,
}


def _find_tool(name: str) -> dict[str, Any] | None:
    return next((item for item in all_tools() if item.get("name") == name), None)


def _tool_schema_properties(name: str) -> dict[str, Any]:
    tool = _find_tool(name)
    schema = tool.get("inputSchema") if isinstance(tool, dict) else None
    properties = schema.get("properties") if isinstance(schema, dict) else None
    return properties if isinstance(properties, dict) else {}


def _extension_validates_own_arguments(name: str) -> bool:
    """An extension tool with an open schema (additionalProperties:true) judges unknown arguments itself.

    It may honor names its schema does not list, or reject them with its own error. Either way the core's
    "expect no effect" would be a guess about code it cannot see, and a wrong one teaches the caller to
    distrust the warning everywhere else. Core tools keep the check: their aliases are listed above.
    """
    tool = _find_tool(name)
    if not isinstance(tool, dict) or tool.get("source") != "extension":
        return False
    schema = tool.get("inputSchema")
    return isinstance(schema, dict) and schema.get("additionalProperties") is True


def extension_owns_save_path(name: str) -> bool:
    """A tool that lists savePath in its schema and writes its own file (an extension's video, a profiler capture).

    The server keeps savePath only for the image a screenshot tool returns: taking it from any other tool left the
    call "successful" with the file never written where the caller asked (profiler-stop-recording saved to its
    default folder whatever savePath said).
    """
    if "savePath" not in _tool_schema_properties(name):
        return False
    tool = _find_tool(name)
    return (isinstance(tool, dict) and tool.get("source") == "extension") or not name.startswith("screenshot-")


def unknown_tool_arguments(name: str, arguments: Any) -> list[str]:
    """Argument names the caller passed that this tool does not declare and does not alias.

    Silently dropping a mistyped argument (outputPath vs savePath) costs the caller several turns
    hunting for an effect that never happened, so surface it on the very first call.
    """
    if not isinstance(arguments, dict) or not arguments:
        return []
    if _extension_validates_own_arguments(name):
        return []
    properties = _tool_schema_properties(name)
    if not properties:
        # No declared surface to judge against; stay silent rather than guess.
        return []
    known = set(properties) | UNIVERSAL_TOOL_ARGUMENT_KEYS | set(TOOL_ARGUMENT_ALIASES.get(name, ()))
    return sorted(key for key in arguments if isinstance(key, str) and key not in known)


def _camel_tokens(value: str) -> list[str]:
    import re

    return [token.lower() for token in re.findall(r"[A-Za-z][a-z0-9]*", value)] or [value.lower()]


def _suggest_tool_argument(key: str, candidates: list[str]) -> str | None:
    import difflib

    close = difflib.get_close_matches(key, candidates, n=1, cutoff=0.6)
    if close:
        return close[0]
    # Wrong word, right concept: outputPath vs savePath share the trailing camelCase token. Matching on
    # that is precise where a fuzzy ratio is not (it would pair 'banana' with 'contains').
    suffix = _camel_tokens(key)[-1]
    suffix_matches = [item for item in candidates if _camel_tokens(item)[-1] == suffix]
    return min(suffix_matches, key=len) if suffix_matches else None


def describe_unknown_tool_arguments(name: str, unknown: list[str]) -> str:
    candidates = sorted(_tool_schema_properties(name))
    parts: list[str] = []
    for key in unknown:
        suggestion = _suggest_tool_argument(key, candidates)
        parts.append(f"'{key}'" + (f" (did you mean '{suggestion}'?)" if suggestion else ""))
    return f"! Unrecognized {name} argument(s): {', '.join(parts)}. Not in this tool's schema — expect no effect."


def attach_result_notice(result: Any, notice: str) -> bool:
    """Attach an advisory line to a tool result through every channel a client might render.

    A text block alone is not enough. When a result carries structuredContent, some clients
    (Claude Code among them) render that object and drop the content text blocks entirely, so a
    notice written only into text is silently discarded — precisely on the screenshot tools, which
    are the likeliest first call of a session. Mirroring into structuredContent.notices costs one
    key (no tool here declares an outputSchema, so nothing can fail validation) and makes the
    advisory survive whichever channel the client chose to display.

    Returns True if the notice landed somewhere, so one-shot callers can stay pending otherwise.
    """
    if not isinstance(result, dict):
        return False

    delivered = False

    content = result.get("content")
    if isinstance(content, list):
        # Only extend a result that already carries text. An image-only result has a shape callers
        # rely on, so leave its content list alone and let structuredContent carry the notice.
        if any(isinstance(block, dict) and block.get("type") == "text" for block in content):
            # Some clients join text blocks with nothing between them ("ok:true! awaitMs ignored").
            content.append({"type": "text", "text": "\n" + notice})
            delivered = True

    structured = result.get("structuredContent")
    if isinstance(structured, dict):
        notices = structured.get("notices")
        if not isinstance(notices, list):
            notices = []
            structured["notices"] = notices
        notices.append(notice)
        delivered = True

    return delivered


def prepend_result_headline(result: Any, line: str, previous_prefix: str = "") -> bool:
    """Put a line first in a tool result's text, for an outcome that supersedes the reply's own headline.

    awaitMs is the case: the tool's summary was written when the action started ("GoTo#3 running"), and a
    caller who reads the first line must see how it ended. previous_prefix marks the reply below as that
    earlier state (not for JSON text). Falls back to a trailing notice without text.
    """
    if not isinstance(result, dict):
        return False
    content = result.get("content")
    text_block = next(
        (block for block in content if isinstance(block, dict) and block.get("type") == "text" and isinstance(block.get("text"), str)),
        None,
    ) if isinstance(content, list) else None
    if text_block is None:
        return attach_result_notice(result, line)
    previous = text_block["text"]
    if previous and previous_prefix and not previous.lstrip().startswith(("{", "[")):
        previous = previous_prefix + previous
    text_block["text"] = line + ("\n" + previous if previous else "")
    structured = result.get("structuredContent")
    if isinstance(structured, dict):
        notices = structured.get("notices")
        if not isinstance(notices, list):
            notices = []
            structured["notices"] = notices
        notices.insert(0, line)
    return True


def with_unknown_argument_warning(result: Any, name: str, arguments: Any) -> Any:
    """Prepend an unrecognized-argument warning to a tool result's text so it is seen on the first call."""
    if not isinstance(result, dict):
        return result
    unknown = unknown_tool_arguments(name, arguments)
    if not unknown:
        return result
    warning = describe_unknown_tool_arguments(name, unknown)
    # Mirror into structuredContent first: clients that render it drop the text blocks below, and a
    # dropped warning is exactly the silent failure this chokepoint exists to prevent.
    structured = result.get("structuredContent")
    if isinstance(structured, dict):
        warnings = structured.get("notices")
        if not isinstance(warnings, list):
            warnings = []
            structured["notices"] = warnings
        warnings.append(warning)
    content = result.get("content")
    if not isinstance(content, list):
        return result
    for block in content:
        if not (isinstance(block, dict) and block.get("type") == "text" and isinstance(block.get("text"), str)):
            continue
        # Never prepend into a machine-readable block: outputFormat:"json" results are parsed by the
        # caller, and a leading warning line would make them unparseable.
        if _looks_like_json_text(block["text"]):
            break
        block["text"] = warning + "\n" + block["text"]
        return result
    content.append({"type": "text", "text": warning})
    return result


def _looks_like_json_text(text: str) -> bool:
    stripped = text.lstrip()
    if not stripped or stripped[0] not in "{[":
        return False
    try:
        json.loads(text)
    except (ValueError, TypeError):
        return False
    return True
