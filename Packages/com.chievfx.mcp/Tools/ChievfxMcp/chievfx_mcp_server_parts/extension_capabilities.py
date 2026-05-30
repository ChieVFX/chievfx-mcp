# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

def extension_source_fields(extension: dict[str, Any]) -> dict[str, Any]:
    return {
        "source": "extension",
        "sourceExtensionId": extension["id"],
        "sourceExtensionName": extension.get("displayName") or extension["id"],
        "sourceExtensionVersion": extension.get("version", ""),
        "sourceAssembly": extension.get("sourceAssembly", ""),
    }


def core_source_fields() -> dict[str, Any]:
    return {
        "source": "core",
        "sourceExtensionId": None,
        "sourceExtensionName": None,
        "sourceExtensionVersion": None,
        "sourceAssembly": None,
    }


def load_extension_capability_manifest() -> dict[str, Any]:
    try:
        payload = json.loads(EXTENSION_CAPABILITY_MANIFEST_PATH.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {
            "schemaVersion": EXTENSION_CAPABILITY_MANIFEST_SCHEMA_VERSION,
            "extensions": [],
            "errors": [],
        }

    if not isinstance(payload, dict):
        return {
            "schemaVersion": EXTENSION_CAPABILITY_MANIFEST_SCHEMA_VERSION,
            "extensions": [],
            "errors": ["extension manifest root is not an object"],
        }

    if payload.get("schemaVersion") != EXTENSION_CAPABILITY_MANIFEST_SCHEMA_VERSION:
        return {
            "schemaVersion": EXTENSION_CAPABILITY_MANIFEST_SCHEMA_VERSION,
            "extensions": [],
            "errors": [f"unsupported extension manifest schemaVersion {payload.get('schemaVersion')}"],
        }

    extensions = payload.get("extensions", [])
    if not isinstance(extensions, list):
        return {
            "schemaVersion": EXTENSION_CAPABILITY_MANIFEST_SCHEMA_VERSION,
            "extensions": [],
            "errors": ["extension manifest `extensions` is not an array"],
        }

    return {
        "schemaVersion": EXTENSION_CAPABILITY_MANIFEST_SCHEMA_VERSION,
        "extensions": [item for item in extensions if isinstance(item, dict)],
        "errors": [],
    }


def coerce_extension_category(value: Any, fallback: str = "Extensions") -> str:
    return value.strip() if isinstance(value, str) and value.strip() else fallback


def normalize_extension_schema(value: Any) -> dict[str, Any]:
    if isinstance(value, dict):
        return value
    return {"type": "object"}


def normalize_extension_arguments(value: Any) -> list[dict[str, Any]]:
    if not isinstance(value, list):
        return []
    return [item for item in value if isinstance(item, dict)]


def extension_dict_items(value: Any) -> list[dict[str, Any]]:
    if not isinstance(value, list):
        return []
    return [item for item in value if isinstance(item, dict)]


def collect_extension_capabilities() -> dict[str, Any]:
    manifest = load_extension_capability_manifest()
    errors: list[str] = list(manifest.get("errors", []))
    extension_summaries: list[dict[str, Any]] = []
    tools: list[dict[str, Any]] = []
    resources: list[dict[str, Any]] = []
    templates: list[dict[str, Any]] = []
    prompts: list[dict[str, Any]] = []

    seen_extension_ids: set[str] = set()
    seen_tool_names = {tool["name"] for tool in TOOLS}
    seen_resource_ids = {resource["id"] for resource in RESOURCES} | {template["id"] for template in RESOURCE_TEMPLATES}
    seen_resource_uris = {resource["uri"] for resource in RESOURCES} | {
        template["uriTemplate"] for template in RESOURCE_TEMPLATES
    }
    seen_prompt_names = {prompt["name"] for prompt in PROMPTS}

    for extension in sorted(manifest.get("extensions", []), key=lambda item: str(item.get("id", ""))):
        extension_id = extension.get("id")
        if not isinstance(extension_id, str) or not extension_id:
            errors.append("extension skipped: missing string id")
            continue
        if extension_id in seen_extension_ids:
            errors.append(f"extension skipped: duplicate extension id {extension_id}")
            continue
        seen_extension_ids.add(extension_id)
        extension["id"] = extension_id
        source = extension_source_fields(extension)
        extension_summaries.append(
            {
                "id": extension_id,
                "displayName": source["sourceExtensionName"],
                "version": source["sourceExtensionVersion"],
                "description": extension.get("description", ""),
                "sourceAssembly": source["sourceAssembly"],
            }
        )

        for tool in sorted(extension_dict_items(extension.get("tools")), key=lambda item: str(item.get("name", ""))):
            name = tool.get("name")
            if not isinstance(name, str) or not name:
                errors.append(f"tool skipped in {extension_id}: missing string name")
                continue
            if name in seen_tool_names:
                errors.append(f"tool skipped in {extension_id}: name collision {name}")
                continue
            seen_tool_names.add(name)
            tools.append(
                {
                    "name": name,
                    "description": tool.get("description", ""),
                    "inputSchema": normalize_extension_schema(tool.get("inputSchema")),
                    "category": coerce_extension_category(tool.get("category")),
                    "required": bool(tool.get("required", False)),
                    **source,
                }
            )

        for resource in sorted(extension_dict_items(extension.get("resources")), key=lambda item: str(item.get("id", ""))):
            resource_id = resource.get("id")
            uri = resource.get("uri")
            if not isinstance(resource_id, str) or not resource_id:
                errors.append(f"resource skipped in {extension_id}: missing string id")
                continue
            if not isinstance(uri, str) or not uri.startswith(EXTENSION_URI_PREFIX):
                errors.append(f"resource skipped in {extension_id}: URI must start with {EXTENSION_URI_PREFIX}")
                continue
            if resource_id in seen_resource_ids:
                errors.append(f"resource skipped in {extension_id}: id collision {resource_id}")
                continue
            if uri in seen_resource_uris:
                errors.append(f"resource skipped in {extension_id}: URI collision {uri}")
                continue
            seen_resource_ids.add(resource_id)
            seen_resource_uris.add(uri)
            resources.append(
                {
                    "id": resource_id,
                    "uri": uri,
                    "name": resource.get("name", ""),
                    "description": resource.get("description", ""),
                    "mimeType": resource.get("mimeType", RESOURCE_MIME_TYPE),
                    "category": coerce_extension_category(resource.get("category")),
                    "required": bool(resource.get("required", False)),
                    "staticText": resource.get("staticText") if isinstance(resource.get("staticText"), str) else None,
                    **source,
                }
            )

        for template in sorted(extension_dict_items(extension.get("resourceTemplates")), key=lambda item: str(item.get("id", ""))):
            template_id = template.get("id")
            uri_template = template.get("uriTemplate")
            if not isinstance(template_id, str) or not template_id:
                errors.append(f"resource template skipped in {extension_id}: missing string id")
                continue
            if not isinstance(uri_template, str) or not uri_template.startswith(EXTENSION_URI_PREFIX):
                errors.append(f"resource template skipped in {extension_id}: URI template must start with {EXTENSION_URI_PREFIX}")
                continue
            if template_id in seen_resource_ids:
                errors.append(f"resource template skipped in {extension_id}: id collision {template_id}")
                continue
            if uri_template in seen_resource_uris:
                errors.append(f"resource template skipped in {extension_id}: URI collision {uri_template}")
                continue
            seen_resource_ids.add(template_id)
            seen_resource_uris.add(uri_template)
            templates.append(
                {
                    "id": template_id,
                    "uriTemplate": uri_template,
                    "name": template.get("name", ""),
                    "description": template.get("description", ""),
                    "mimeType": template.get("mimeType", RESOURCE_MIME_TYPE),
                    "category": coerce_extension_category(template.get("category")),
                    "required": bool(template.get("required", False)),
                    **source,
                }
            )

        for prompt in sorted(extension_dict_items(extension.get("prompts")), key=lambda item: str(item.get("name", ""))):
            name = prompt.get("name")
            if not isinstance(name, str) or not name:
                errors.append(f"prompt skipped in {extension_id}: missing string name")
                continue
            if name in seen_prompt_names:
                errors.append(f"prompt skipped in {extension_id}: name collision {name}")
                continue
            seen_prompt_names.add(name)
            prompts.append(
                {
                    "name": name,
                    "title": prompt.get("title", ""),
                    "description": prompt.get("description", ""),
                    "arguments": normalize_extension_arguments(prompt.get("arguments")),
                    "category": coerce_extension_category(prompt.get("category")),
                    "required": bool(prompt.get("required", False)),
                    "staticText": prompt.get("staticText") if isinstance(prompt.get("staticText"), str) else None,
                    **source,
                }
            )

    return {
        "manifestPath": str(EXTENSION_CAPABILITY_MANIFEST_PATH),
        "extensions": extension_summaries,
        "tools": tools,
        "resources": resources,
        "resourceTemplates": templates,
        "prompts": prompts,
        "errors": errors,
    }


def all_tools() -> list[dict[str, Any]]:
    return [dict(tool, **core_source_fields()) for tool in TOOLS] + collect_extension_capabilities()["tools"]


def all_resources() -> list[dict[str, Any]]:
    catalogs = load_text_catalogs_from_md() if CATALOGS_MD_PATH.exists() else {}
    core_resources = catalogs.get("resources") if isinstance(catalogs, dict) else None
    if not core_resources:
        core_resources = RESOURCES
    return [dict(resource, **core_source_fields()) for resource in core_resources] + collect_extension_capabilities()["resources"]


def all_resource_templates() -> list[dict[str, Any]]:
    catalogs = load_text_catalogs_from_md() if CATALOGS_MD_PATH.exists() else {}
    core_templates = catalogs.get("resourceTemplates") if isinstance(catalogs, dict) else None
    if not core_templates:
        core_templates = RESOURCE_TEMPLATES
    return [dict(template, **core_source_fields()) for template in core_templates] + collect_extension_capabilities()["resourceTemplates"]


def all_prompts() -> list[dict[str, Any]]:
    catalogs = load_text_catalogs_from_md() if CATALOGS_MD_PATH.exists() else {}
    core_prompts = catalogs.get("prompts") if isinstance(catalogs, dict) else None
    if core_prompts:
        catalog_names = {prompt.get("name") for prompt in core_prompts}
        core_prompts = list(core_prompts) + [prompt for prompt in PROMPTS if prompt.get("name") not in catalog_names]
    else:
        core_prompts = PROMPTS
    return [dict(prompt, **core_source_fields()) for prompt in core_prompts] + collect_extension_capabilities()["prompts"]
