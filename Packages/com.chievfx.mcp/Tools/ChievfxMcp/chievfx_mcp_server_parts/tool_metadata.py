# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

def load_required_tool_ids() -> set[str]:
    try:
        payload = json.loads(TOOL_POLICY_PATH.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return set(DEFAULT_REQUIRED_TOOL_IDS)

    ids = payload.get("requiredToolIds")
    if not isinstance(ids, list):
        return set(DEFAULT_REQUIRED_TOOL_IDS)

    required_ids = {item for item in ids if isinstance(item, str) and item}
    return required_ids or set(DEFAULT_REQUIRED_TOOL_IDS)


def required_tool_ids_for_tools(tools: list[dict[str, Any]]) -> set[str]:
    policy_required_ids = load_required_tool_ids()
    return {
        tool["name"]
        for tool in tools
        if tool.get("required")
        or tool["name"] in policy_required_ids
        or (tool.get("category") or TOOL_CATEGORIES.get(tool["name"], "General")) == "Essentials"
    }


def slim_advertised_schema_detail(value: Any, detail_keys: set[str] = ADVERTISED_SCHEMA_DETAIL_KEYS) -> Any:
    if isinstance(value, dict):
        if value == VECTOR3_REF:
            return ADVERTISED_VECTOR3_SCHEMA.copy()
        return {
            key: slim_advertised_schema_detail(item, detail_keys)
            for key, item in value.items()
            if key not in detail_keys
        }
    if isinstance(value, list):
        return [slim_advertised_schema_detail(item, detail_keys) for item in value]
    return value


def advertised_input_schema(tool: dict[str, Any]) -> dict[str, Any]:
    tool_name = tool.get("name")
    if tool_name == "ugui-canvas-ensure":
        return {
            "type": "object",
            "properties": {
                "name": {},
                "canvasPath": {},
                "canvasInstanceId": {},
                "rect": {},
            },
            "additionalProperties": False,
        }
    if tool_name in {
        "runtime-ui-probe-screen-position",
        "ugui-runtime-probe-screen-position",
        "uitoolkit-runtime-probe-screen-position",
    }:
        properties: dict[str, Any] = {
            "x": {},
            "y": {},
            "screenPosition": {},
            "normalized": {},
        }
        if tool_name != "ugui-runtime-probe-screen-position":
            properties["maxRows"] = {}
        if tool_name in {"ugui-runtime-probe-screen-position", "uitoolkit-runtime-probe-screen-position"}:
            properties["includeAllComponents"] = {}
        if tool_name == "uitoolkit-runtime-probe-screen-position":
            properties["includeUssClasses"] = {}
        return {
            "type": "object",
            "properties": properties,
            "additionalProperties": tool_name == "runtime-ui-probe-screen-position",
        }
    if tool_name == "ugui-scrollrect-create":
        scalar = lambda kind: {"type": kind}
        return {
            "type": "object",
            "properties": {
                "name": scalar("string"),
                "canvasPath": scalar("string"),
                "canvasInstanceId": scalar("integer"),
                "parentPath": scalar("string"),
                "parentInstanceId": scalar("integer"),
                "rect": scalar("object"),
                "direction": {"enum": ["vertical", "horizontal", "both"]},
                "contentLayout": {"enum": ["vertical", "horizontal", "grid", "none"]},
                "contentSizeFitter": scalar("boolean"),
                "padding": scalar("object"),
                "spacing": {"type": ["number", "object"]},
                "gridSpacing": scalar("object"),
                "cellSize": scalar("object"),
                "constraint": scalar("string"),
                "constraintCount": scalar("integer"),
                "backgroundColor": {"type": ["string", "object"]},
            },
            "additionalProperties": False,
        }
    if tool_name == "ugui-runtime-drag":
        return {
            "type": "object",
            "properties": {
                "targetPath": {},
                "instanceId": {},
                "startScreenPosition": {},
                "startNormalized": {},
                "endScreenPosition": {},
                "endNormalized": {},
                "dryRun": {},
                "allowStateMutation": {},
            },
            "additionalProperties": False,
        }
    if tool_name == "ugui-runtime-click":
        return {
            "type": "object",
            "properties": {
                "targetPath": {},
                "instanceId": {},
                "x": {},
                "y": {},
                "screenPosition": {},
                "normalized": {},
                "sequence": {"enum": ["pointer", "submit"]},
                "dryRun": {},
                "allowStateMutation": {},
            },
            "additionalProperties": False,
        }
    if tool_name == "ugui-image-set":
        return {
            "type": "object",
            "properties": {
                "targetPath": {},
                "instanceId": {},
                "spritePath": {},
                "spriteGuid": {},
                "color": {},
                "raycastTarget": {},
                "preserveAspect": {},
                "imageType": {"enum": ["Auto", "Simple", "Sliced", "Tiled", "Filled"]},
            },
            "required": ["targetPath"],
            "additionalProperties": False,
        }
    if tool_name == "ugui-image-primitive-create":
        primitive_enum = {"enum": ["rect", "rounded-rect", "circle", "oval"]}
        return {
            "type": "object",
            "properties": {
                "name": {},
                "canvasPath": {},
                "canvasInstanceId": {},
                "parentPath": {},
                "parentInstanceId": {},
                "path": {},
                "pngPath": {},
                "assetPath": {},
                "primitiveType": primitive_enum,
                "type": primitive_enum,
                "width": {},
                "height": {},
                "radius": {},
                "pixelsPerUnit": {},
                "color": {},
                "raycastTarget": {},
                "imageType": {"enum": ["Auto", "Simple", "Sliced", "Tiled", "Filled"]},
                "spriteBorder": {},
                "rect": {},
            },
            "required": ["path"],
            "additionalProperties": False,
        }
    if tool_name == "ugui-grid-create":
        return {
            "type": "object",
            "properties": {
                "name": {},
                "canvasPath": {},
                "canvasInstanceId": {},
                "parentPath": {},
                "parentInstanceId": {},
                "rect": {},
                "count": {},
                "cellNamePrefix": {},
                "cellType": {"enum": ["empty", "image", "button", "text"]},
                "color": {},
                "colors": {},
                "padding": {},
                "spacing": {},
                "gridSpacing": {},
                "cellSize": {},
                "constraint": {},
                "constraintCount": {},
            },
            "additionalProperties": False,
        }
    if tool_name == "ugui-create-simple":
        return {
            "type": "object",
            "properties": {
                "name": {},
                "canvasPath": {},
                "canvasInstanceId": {},
                "parentPath": {},
                "parentInstanceId": {},
                "rect": {},
                "image": {
                    "type": "object",
                    "properties": {
                        "spritePath": {},
                        "spriteGuid": {},
                        "color": {},
                        "raycastTarget": {},
                        "preserveAspect": {},
                        "imageType": {"enum": ["Auto", "Simple", "Sliced", "Tiled", "Filled"]},
                    },
                },
            },
            "additionalProperties": False,
        }
    if tool_name == "ugui-create-control":
        control_enum = {"enum": ["button", "slider", "progressbar"]}
        return {
            "type": "object",
            "properties": {
                "controlType": control_enum,
                "type": control_enum,
                "name": {},
                "canvasPath": {},
                "canvasInstanceId": {},
                "parentPath": {},
                "parentInstanceId": {},
                "rect": {},
                "text": {},
                "textBackend": {"enum": ["auto", "legacy", "tmp"]},
                "image": {
                    "type": "object",
                    "properties": {
                        "spritePath": {},
                        "spriteGuid": {},
                        "color": {},
                        "raycastTarget": {},
                        "preserveAspect": {},
                        "imageType": {"enum": ["Auto", "Simple", "Sliced", "Tiled", "Filled"]},
                    },
                },
                "value": {},
            },
            "required": ["controlType"],
            "additionalProperties": False,
        }
    if tool_name == "ugui-rect-update":
        return {
            "type": "object",
            "properties": {
                "paths": {},
                "instanceIds": {},
                "rect": {
                    "type": "object",
                    "properties": {
                        "preset": {"enum": ["fill", "stretch", "center", "dock", "dock-top", "dock-bottom", "dock-left", "dock-right", "anchor-size"]},
                        "dock": {"enum": ["top", "bottom", "left", "right"]},
                        "size": {},
                        "position": {},
                        "margin": {},
                        "pivot": {},
                        "anchorMin": {},
                        "anchorMax": {},
                        "anchoredPosition": {},
                        "sizeDelta": {},
                        "offsetMin": {},
                        "offsetMax": {},
                    },
                },
            },
            "additionalProperties": False,
        }
    if tool_name == "ugui-layout-group-set":
        return {
            "type": "object",
            "properties": {
                "paths": {},
                "instanceIds": {},
                "layoutGroup": {"enum": ["vertical", "horizontal", "grid"]},
                "padding": {},
                "spacing": {},
                "gridSpacing": {},
                "childAlignment": {},
                "childControlWidth": {},
                "childControlHeight": {},
                "childForceExpandWidth": {},
                "childForceExpandHeight": {},
                "childScaleWidth": {},
                "childScaleHeight": {},
                "reverseArrangement": {},
                "cellSize": {},
                "startCorner": {},
                "startAxis": {},
                "constraint": {},
                "constraintCount": {},
            },
            "additionalProperties": False,
        }
    if tool_name == "ugui-layout-element-set":
        return {
            "type": "object",
            "properties": {
                "paths": {},
                "instanceIds": {},
                "ignoreLayout": {},
                "minWidth": {},
                "minHeight": {},
                "preferredWidth": {},
                "preferredHeight": {},
                "flexibleWidth": {},
                "flexibleHeight": {},
                "layoutPriority": {},
            },
            "additionalProperties": False,
        }
    if tool_name == "ugui-sprite-configure":
        return {
            "type": "object",
            "properties": {
                "path": {},
                "guid": {},
                "spritePath": {},
                "spriteGuid": {},
                "pixelsPerUnit": {},
                "spritePixelsPerUnit": {},
                "spriteBorder": {},
            },
            "additionalProperties": False,
        }

    schema = json.loads(json.dumps(tool.get("inputSchema", {})))
    properties = schema.get("properties")
    if isinstance(properties, dict):
        # Output remains available at runtime, but default TOON output keeps prompt descriptors lean.
        properties.pop("outputFormat", None)
        for property_name in ADVERTISED_PROPERTY_OMISSIONS.get(str(tool_name), set()):
            properties.pop(property_name, None)
        if str(tool_name).startswith("reflection-method-") and "filter" in properties:
            properties["filter"] = {"type": "object"}

    detail_keys = ADVERTISED_SCHEMA_DETAIL_KEYS
    if tool_name in ADVERTISED_SCHEMA_DESCRIPTION_TOOLS:
        # Keep per-property descriptions for these tools; still drop default/min/max/$defs noise.
        detail_keys = ADVERTISED_SCHEMA_DETAIL_KEYS - {"description"}
    return slim_advertised_schema_detail(schema, detail_keys)


def compact_tool_descriptor(tool: dict[str, Any]) -> dict[str, Any]:
    return {
        "name": tool.get("name"),
        "description": tool.get("description"),
        "inputSchema": advertised_input_schema(tool),
    }


def compact_tool_description_surface(tool: dict[str, Any]) -> dict[str, Any]:
    return {
        "name": tool.get("name"),
        "description": tool.get("description"),
    }


def compact_descriptor_json(tool: dict[str, Any]) -> str:
    return json.dumps(compact_tool_descriptor(tool), ensure_ascii=False, separators=(",", ":"))


def compact_tool_description_json(tool: dict[str, Any]) -> str:
    return json.dumps(compact_tool_description_surface(tool), ensure_ascii=False, separators=(",", ":"))


def compact_call_envelope(tool: dict[str, Any]) -> str:
    return json.dumps(
        {
            "jsonrpc": "2.0",
            "id": 1,
            "method": "tools/call",
            "params": {
                "name": tool.get("name"),
                "arguments": {},
            },
        },
        ensure_ascii=False,
        separators=(",", ":"),
    )


def response_estimate_for_tool(tool: dict[str, Any]) -> dict[str, str]:
    profile = RESPONSE_PROFILE_BY_TOOL.get(tool.get("name"), "small")
    estimate = RESPONSE_ESTIMATE_PROFILES[profile].copy()
    estimate["profile"] = profile
    estimate["note"] = RESPONSE_ESTIMATE_NOTE
    return estimate


def compact_resource_read_envelope(uri: str) -> str:
    return json.dumps(
        {
            "jsonrpc": "2.0",
            "id": 1,
            "method": "resources/read",
            "params": {
                "uri": uri,
            },
        },
        ensure_ascii=False,
        separators=(",", ":"),
    )


def response_estimate_for_resource(resource_id: str) -> dict[str, str]:
    profile = RESPONSE_PROFILE_BY_RESOURCE.get(resource_id, "row-list")
    estimate = RESOURCE_RESPONSE_ESTIMATE_PROFILES[profile].copy()
    estimate["profile"] = profile
    estimate["note"] = RESPONSE_ESTIMATE_NOTE
    return estimate


def get_token_encoder() -> tuple[Any | None, str]:
    try:
        import tiktoken  # type: ignore

        return tiktoken.get_encoding("o200k_base"), "tiktoken-o200k_base"
    except Exception:  # noqa: BLE001 - optional dependency.
        return None, "utf8-bytes-div-4"


def estimate_descriptor_tokens(descriptor_json: str, encoder: Any | None) -> int:
    if encoder is not None:
        return len(encoder.encode(descriptor_json))

    return math.ceil(len(descriptor_json.encode("utf-8")) / 4)


def normalize_role_id(value: str) -> str:
    normalized = re.sub(r"[^a-z0-9]+", "-", value.strip().casefold()).strip("-")
    return normalized or "role"


def load_builtin_tool_roles() -> list[dict[str, Any]]:
    try:
        payload = json.loads(TOOL_ROLE_PRESETS_PATH.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        payload = {}

    roles = payload.get("roles")
    if not isinstance(roles, list):
        return []

    result: list[dict[str, Any]] = []
    for role in roles:
        if not isinstance(role, dict):
            continue
        role_id = str(role.get("id") or "").strip()
        if not role_id:
            continue
        result.append(
            {
                "id": normalize_role_id(role_id),
                "kind": "built-in",
                "displayName": str(role.get("displayName") or role_id),
                "description": str(role.get("description") or ""),
                "enabledCategoryIds": coerce_string_list(role.get("enabledCategoryIds")),
                "enabledToolIds": coerce_string_list(role.get("enabledToolIds")),
                "assetPath": None,
            }
        )
    return result


def read_unity_yaml_string(text: str, field: str) -> str:
    match = re.search(rf"(?m)^\s*{re.escape(field)}:\s*(.*)$", text)
    if match is None:
        return ""
    value = match.group(1).strip()
    if len(value) >= 2 and value[0] == value[-1] == '"':
        try:
            return json.loads(value)
        except json.JSONDecodeError:
            return value.strip('"')
    return value


def read_unity_yaml_string_list(text: str, field: str) -> list[str]:
    match = re.search(rf"(?ms)^\s*{re.escape(field)}:\s*\n(?P<body>(?:\s*-\s+.*\n?)*)", text)
    if match is None:
        return []
    result: list[str] = []
    for line in match.group("body").splitlines():
        item_match = re.match(r"\s*-\s+(.*)$", line)
        if item_match is None:
            continue
        item = item_match.group(1).strip()
        if len(item) >= 2 and item[0] == item[-1] == '"':
            try:
                item = json.loads(item)
            except json.JSONDecodeError:
                item = item.strip('"')
        if item:
            result.append(item)
    return result


def project_relative_path(path: Path) -> str:
    try:
        return path.resolve().relative_to(PROJECT_ROOT.resolve()).as_posix()
    except ValueError:
        return path.as_posix()


def load_custom_tool_roles() -> list[dict[str, Any]]:
    assets_root = PROJECT_ROOT / "Assets"
    if not assets_root.exists():
        return []

    result: list[dict[str, Any]] = []
    for path in sorted(assets_root.rglob("*.asset")):
        try:
            text = path.read_text(encoding="utf-8", errors="ignore")
        except OSError:
            continue
        if "ChievfxMcpToolRoleAsset" not in text and "enabledCategoryIds:" not in text and "enabledToolIds:" not in text:
            continue

        role_id = read_unity_yaml_string(text, "roleId")
        display_name = read_unity_yaml_string(text, "displayName")
        description = read_unity_yaml_string(text, "description")
        asset_path = project_relative_path(path)
        if not role_id:
            role_id = normalize_role_id(Path(asset_path).stem)
        if not display_name:
            display_name = Path(asset_path).stem
        result.append(
            {
                "id": normalize_role_id(role_id),
                "kind": "custom",
                "displayName": display_name,
                "description": description,
                "enabledCategoryIds": read_unity_yaml_string_list(text, "enabledCategoryIds"),
                "enabledToolIds": read_unity_yaml_string_list(text, "enabledToolIds"),
                "assetPath": asset_path,
            }
        )
    return result


def summarize_role_availability(role: dict[str, Any], metadata: dict[str, Any]) -> dict[str, Any]:
    categories = {tool["category"] for tool in metadata.get("tools", [])}
    tools = {tool["name"] for tool in metadata.get("tools", [])}
    enabled_categories = coerce_string_list(role.get("enabledCategoryIds"))
    enabled_tools = coerce_string_list(role.get("enabledToolIds"))
    matched_categories = [category for category in enabled_categories if category in categories]
    matched_tools = [tool for tool in enabled_tools if tool in tools]
    return {
        "matchedCategoryIds": matched_categories,
        "unavailableCategoryIds": [category for category in enabled_categories if category not in categories],
        "matchedToolIds": matched_tools,
        "unavailableToolIds": [tool for tool in enabled_tools if tool not in tools],
    }


def build_tool_role_catalog(metadata: dict[str, Any] | None = None) -> dict[str, Any]:
    metadata = metadata or build_tool_metadata()
    roles = load_builtin_tool_roles() + load_custom_tool_roles()
    for role in roles:
        role["availability"] = summarize_role_availability(role, metadata)
    return {
        "presetPath": str(TOOL_ROLE_PRESETS_PATH),
        "roles": roles,
    }


def build_tool_metadata() -> dict[str, Any]:
    encoder, estimator = get_token_encoder()
    extension_capabilities = collect_extension_capabilities()
    tools = all_tools()
    required_tool_ids = required_tool_ids_for_tools(tools)
    metadata_tools: list[dict[str, Any]] = []

    for tool in tools:
        descriptor_json = compact_descriptor_json(tool)
        descriptor_bytes = len(descriptor_json.encode("utf-8"))
        description_json = compact_tool_description_json(tool)
        call_envelope_json = compact_call_envelope(tool)
        call_envelope_bytes = len(call_envelope_json.encode("utf-8"))
        metadata_tools.append(
            {
                "name": tool["name"],
                "description": tool.get("description", ""),
                "category": tool.get("category") or TOOL_CATEGORIES.get(tool["name"], "General"),
                "inputSchema": advertised_input_schema(tool),
                "descriptorHash": hashlib.sha256(descriptor_json.encode("utf-8")).hexdigest(),
                "descriptorPreview": descriptor_json,
                "descriptorBytes": descriptor_bytes,
                "estimatedTokens": estimate_descriptor_tokens(descriptor_json, encoder),
                "descriptionEstimatedTokens": estimate_descriptor_tokens(description_json, encoder),
                "callEnvelopePreview": call_envelope_json,
                "callEnvelopeBytes": call_envelope_bytes,
                "callEnvelopeEstimatedTokens": estimate_descriptor_tokens(call_envelope_json, encoder),
                "responseEstimate": response_estimate_for_tool(tool),
                "required": tool["name"] in required_tool_ids,
                "source": tool.get("source", "core"),
                "sourceExtensionId": tool.get("sourceExtensionId"),
                "sourceExtensionName": tool.get("sourceExtensionName"),
                "sourceExtensionVersion": tool.get("sourceExtensionVersion"),
                "sourceAssembly": tool.get("sourceAssembly"),
            }
        )

    return {
        "schemaVersion": TOOL_SELECTION_SCHEMA_VERSION,
        "source": "Tools/ChievfxMcp/chievfx_mcp_server.py:TOOLS + Unity extension manifest",
        "selectionPath": str(TOOL_SELECTION_PATH),
        "policyPath": str(TOOL_POLICY_PATH),
        "extensionManifestPath": str(EXTENSION_CAPABILITY_MANIFEST_PATH),
        "estimator": estimator,
        "descriptorEstimateBasis": DESCRIPTOR_ESTIMATE_BASIS,
        "descriptionEstimateBasis": TOOL_DESCRIPTION_ESTIMATE_BASIS,
        "callEnvelopeEstimateBasis": CALL_ENVELOPE_ESTIMATE_BASIS,
        "responseEstimateNote": RESPONSE_ESTIMATE_NOTE,
        "categoryDescriptions": TOOL_CATEGORY_DESCRIPTIONS,
        "extensions": extension_capabilities["extensions"],
        "extensionErrors": extension_capabilities["errors"],
        "requiredToolIds": sorted(required_tool_ids),
        "roles": build_tool_role_catalog({"tools": metadata_tools})["roles"],
        "tools": metadata_tools,
    }
