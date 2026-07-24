# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

from __future__ import annotations

def compact_resource_descriptor(resource: dict[str, Any]) -> dict[str, Any]:
    return {
        "uri": resource.get("uri"),
        "name": resource.get("name"),
        "description": resource.get("description"),
        "mimeType": resource.get("mimeType", RESOURCE_MIME_TYPE),
    }


def compact_resource_template_descriptor(template: dict[str, Any]) -> dict[str, Any]:
    return {
        "uriTemplate": template.get("uriTemplate"),
        "name": template.get("name"),
        "description": template.get("description"),
        "mimeType": template.get("mimeType", RESOURCE_MIME_TYPE),
    }


def compact_resource_description_surface(resource: dict[str, Any]) -> dict[str, Any]:
    return {
        "uri": resource.get("uri"),
        "name": resource.get("name"),
        "description": resource.get("description"),
    }


def compact_resource_template_description_surface(template: dict[str, Any]) -> dict[str, Any]:
    return {
        "uriTemplate": template.get("uriTemplate"),
        "name": template.get("name"),
        "description": template.get("description"),
    }


def compact_resource_descriptor_json(resource: dict[str, Any]) -> str:
    return json.dumps(compact_resource_descriptor(resource), ensure_ascii=False, separators=(",", ":"))


def compact_resource_template_descriptor_json(template: dict[str, Any]) -> str:
    return json.dumps(compact_resource_template_descriptor(template), ensure_ascii=False, separators=(",", ":"))


def compact_resource_description_json(resource: dict[str, Any]) -> str:
    return json.dumps(compact_resource_description_surface(resource), ensure_ascii=False, separators=(",", ":"))


def compact_resource_template_description_json(template: dict[str, Any]) -> str:
    return json.dumps(compact_resource_template_description_surface(template), ensure_ascii=False, separators=(",", ":"))


def build_resource_metadata() -> dict[str, Any]:
    encoder, estimator = get_token_encoder()
    extension_capabilities = collect_extension_capabilities()
    resources = all_resources()
    templates = all_resource_templates()
    required_resource_ids = (DEFAULT_REQUIRED_RESOURCE_IDS & {resource["id"] for resource in resources}) | {
        resource["id"] for resource in resources if resource.get("required")
    }
    required_template_ids = (DEFAULT_REQUIRED_RESOURCE_TEMPLATE_IDS & {template["id"] for template in templates}) | {
        template["id"] for template in templates if template.get("required")
    }
    metadata_resources: list[dict[str, Any]] = []
    metadata_templates: list[dict[str, Any]] = []

    for resource in resources:
        descriptor_json = compact_resource_descriptor_json(resource)
        descriptor_bytes = len(descriptor_json.encode("utf-8"))
        description_json = compact_resource_description_json(resource)
        read_envelope_json = compact_resource_read_envelope(resource["uri"])
        read_envelope_bytes = len(read_envelope_json.encode("utf-8"))
        metadata_resources.append(
            {
                "id": resource["id"],
                "uri": resource["uri"],
                "name": resource.get("name", ""),
                "description": resource.get("description", ""),
                "mimeType": resource.get("mimeType", RESOURCE_MIME_TYPE),
                "category": RESOURCE_CATEGORIES.get(resource["id"]) or resource.get("category") or "general",
                "descriptorHash": hashlib.sha256(descriptor_json.encode("utf-8")).hexdigest(),
                "descriptorPreview": descriptor_json,
                "descriptorBytes": descriptor_bytes,
                "estimatedTokens": estimate_descriptor_tokens(descriptor_json, encoder),
                "descriptionEstimatedTokens": estimate_descriptor_tokens(description_json, encoder),
                "readEnvelopePreview": read_envelope_json,
                "readEnvelopeBytes": read_envelope_bytes,
                "readEnvelopeEstimatedTokens": estimate_descriptor_tokens(read_envelope_json, encoder),
                "responseEstimate": response_estimate_for_resource(resource["id"]),
                "required": resource["id"] in required_resource_ids,
                "source": resource.get("source", "core"),
                "sourceExtensionId": resource.get("sourceExtensionId"),
                "sourceExtensionName": resource.get("sourceExtensionName"),
                "sourceExtensionVersion": resource.get("sourceExtensionVersion"),
                "sourceAssembly": resource.get("sourceAssembly"),
            }
        )

    for template in templates:
        descriptor_json = compact_resource_template_descriptor_json(template)
        descriptor_bytes = len(descriptor_json.encode("utf-8"))
        description_json = compact_resource_template_description_json(template)
        read_envelope_json = compact_resource_read_envelope(template["uriTemplate"])
        read_envelope_bytes = len(read_envelope_json.encode("utf-8"))
        metadata_templates.append(
            {
                "id": template["id"],
                "uriTemplate": template["uriTemplate"],
                "name": template.get("name", ""),
                "description": template.get("description", ""),
                "mimeType": template.get("mimeType", RESOURCE_MIME_TYPE),
                "category": template.get("category") or RESOURCE_TEMPLATE_CATEGORIES.get(template["id"], "general"),
                "descriptorHash": hashlib.sha256(descriptor_json.encode("utf-8")).hexdigest(),
                "descriptorPreview": descriptor_json,
                "descriptorBytes": descriptor_bytes,
                "estimatedTokens": estimate_descriptor_tokens(descriptor_json, encoder),
                "descriptionEstimatedTokens": estimate_descriptor_tokens(description_json, encoder),
                "readEnvelopePreview": read_envelope_json,
                "readEnvelopeBytes": read_envelope_bytes,
                "readEnvelopeEstimatedTokens": estimate_descriptor_tokens(read_envelope_json, encoder),
                "responseEstimate": response_estimate_for_resource(template["id"]),
                "required": template["id"] in required_template_ids,
                "source": template.get("source", "core"),
                "sourceExtensionId": template.get("sourceExtensionId"),
                "sourceExtensionName": template.get("sourceExtensionName"),
                "sourceExtensionVersion": template.get("sourceExtensionVersion"),
                "sourceAssembly": template.get("sourceAssembly"),
            }
        )

    return {
        "schemaVersion": RESOURCE_SELECTION_SCHEMA_VERSION,
        "source": "Tools/ChievfxMcp/chievfx_mcp_server.py:RESOURCES + Unity extension manifest",
        "selectionPath": str(RESOURCE_SELECTION_PATH),
        "extensionManifestPath": str(EXTENSION_CAPABILITY_MANIFEST_PATH),
        "estimator": estimator,
        "resourceDescriptorEstimateBasis": RESOURCE_DESCRIPTOR_ESTIMATE_BASIS,
        "resourceTemplateDescriptorEstimateBasis": RESOURCE_TEMPLATE_DESCRIPTOR_ESTIMATE_BASIS,
        "resourceDescriptionEstimateBasis": RESOURCE_DESCRIPTION_ESTIMATE_BASIS,
        "resourceTemplateDescriptionEstimateBasis": RESOURCE_TEMPLATE_DESCRIPTION_ESTIMATE_BASIS,
        "readEnvelopeEstimateBasis": RESOURCE_READ_ENVELOPE_ESTIMATE_BASIS,
        "responseEstimateNote": RESPONSE_ESTIMATE_NOTE,
        "note": RESOURCE_SELECTION_NOTE,
        "guidance": RESOURCE_RELOAD_GUIDANCE,
        "categoryDescriptions": RESOURCE_CATEGORY_DESCRIPTIONS,
        "extensions": extension_capabilities["extensions"],
        "extensionErrors": extension_capabilities["errors"],
        "requiredResourceIds": sorted(required_resource_ids),
        "requiredResourceTemplateIds": sorted(required_template_ids),
        "resources": metadata_resources,
        "resourceTemplates": metadata_templates,
    }


def load_enabled_resource_ids() -> tuple[set[str], set[str]]:
    resources = all_resources()
    templates = all_resource_templates()
    resource_ids = {resource["id"] for resource in resources}
    template_ids = {template["id"] for template in templates}
    required_resource_ids = (DEFAULT_REQUIRED_RESOURCE_IDS & resource_ids) | {
        resource["id"] for resource in resources if resource.get("required")
    }
    required_template_ids = (DEFAULT_REQUIRED_RESOURCE_TEMPLATE_IDS & template_ids) | {
        template["id"] for template in templates if template.get("required")
    }

    if not manual_tool_resource_selection_enabled():
        # Expose-all mode (default): every resource and template, regardless of saved selection.
        return set(resource_ids), set(template_ids)

    try:
        payload = json.loads(RESOURCE_SELECTION_PATH.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return set(resource_ids), set(template_ids)

    enabled_resources = payload.get("enabledResourceIds")
    enabled_templates = payload.get("enabledResourceTemplateIds")
    selected_resource_ids = (
        {item for item in enabled_resources if isinstance(item, str)}
        if isinstance(enabled_resources, list)
        else set(resource_ids)
    )
    selected_resource_ids.update(
        replacement
        for legacy, replacement in RESOURCE_SELECTION_ALIASES.items()
        if legacy in selected_resource_ids
    )
    selected_template_ids = (
        {item for item in enabled_templates if isinstance(item, str)}
        if isinstance(enabled_templates, list)
        else set(template_ids)
    )
    selected_template_ids.update(
        replacement
        for legacy, replacement in RESOURCE_TEMPLATE_SELECTION_ALIASES.items()
        if legacy in selected_template_ids
    )
    return (selected_resource_ids & resource_ids) | required_resource_ids, (
        selected_template_ids & template_ids
    ) | required_template_ids


def enabled_resources() -> list[dict[str, Any]]:
    enabled_resource_ids, _ = load_enabled_resource_ids()
    return [compact_resource_descriptor(resource) for resource in all_resources() if resource["id"] in enabled_resource_ids]


def enabled_resource_templates() -> list[dict[str, Any]]:
    _, enabled_template_ids = load_enabled_resource_ids()
    return [
        compact_resource_template_descriptor(template)
        for template in all_resource_templates()
        if template["id"] in enabled_template_ids
    ]


def save_enabled_resource_ids(
    enabled_resource_ids: set[str],
    enabled_template_ids: set[str],
    metadata: dict[str, Any] | None = None,
) -> None:
    metadata = metadata or build_resource_metadata()
    resource_ids = {resource["id"] for resource in all_resources()}
    template_ids = {template["id"] for template in all_resource_templates()}
    required_resource_ids = set(metadata.get("requiredResourceIds", [])) & resource_ids
    required_template_ids = set(metadata.get("requiredResourceTemplateIds", [])) & template_ids
    persisted_resource_ids = (enabled_resource_ids & resource_ids) | required_resource_ids
    persisted_template_ids = (enabled_template_ids & template_ids) | required_template_ids

    RESOURCE_SELECTION_PATH.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "schemaVersion": RESOURCE_SELECTION_SCHEMA_VERSION,
        "updatedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "source": metadata.get("source", "Tools/ChievfxMcp/chievfx_mcp_server.py:RESOURCES"),
        "estimator": metadata.get("estimator", "unknown"),
        "note": RESOURCE_SELECTION_NOTE,
        "resourceDescriptorEstimateBasis": metadata.get(
            "resourceDescriptorEstimateBasis", RESOURCE_DESCRIPTOR_ESTIMATE_BASIS
        ),
        "resourceTemplateDescriptorEstimateBasis": metadata.get(
            "resourceTemplateDescriptorEstimateBasis", RESOURCE_TEMPLATE_DESCRIPTOR_ESTIMATE_BASIS
        ),
        "resourceDescriptionEstimateBasis": metadata.get(
            "resourceDescriptionEstimateBasis", RESOURCE_DESCRIPTION_ESTIMATE_BASIS
        ),
        "resourceTemplateDescriptionEstimateBasis": metadata.get(
            "resourceTemplateDescriptionEstimateBasis", RESOURCE_TEMPLATE_DESCRIPTION_ESTIMATE_BASIS
        ),
        "readEnvelopeEstimateBasis": metadata.get("readEnvelopeEstimateBasis", RESOURCE_READ_ENVELOPE_ESTIMATE_BASIS),
        "responseEstimateNote": metadata.get("responseEstimateNote", RESPONSE_ESTIMATE_NOTE),
        "enabledResourceIds": sorted(persisted_resource_ids),
        "enabledResourceTemplateIds": sorted(persisted_template_ids),
        "resources": {
            resource["id"]: {
                "descriptorHash": resource.get("descriptorHash", ""),
                "estimatedTokens": resource.get("estimatedTokens", 0),
                "descriptionEstimatedTokens": resource.get("descriptionEstimatedTokens", 0),
                "descriptorBytes": resource.get("descriptorBytes", 0),
                "readEnvelopeEstimatedTokens": resource.get("readEnvelopeEstimatedTokens", 0),
                "readEnvelopeBytes": resource.get("readEnvelopeBytes", 0),
                "required": bool(resource.get("required")),
                "category": resource.get("category", "general"),
                "source": resource.get("source", "core"),
                "sourceExtensionId": resource.get("sourceExtensionId"),
            }
            for resource in sorted(metadata.get("resources", []), key=lambda item: item["id"])
        },
        "resourceTemplates": {
            template["id"]: {
                "descriptorHash": template.get("descriptorHash", ""),
                "estimatedTokens": template.get("estimatedTokens", 0),
                "descriptionEstimatedTokens": template.get("descriptionEstimatedTokens", 0),
                "descriptorBytes": template.get("descriptorBytes", 0),
                "readEnvelopeEstimatedTokens": template.get("readEnvelopeEstimatedTokens", 0),
                "readEnvelopeBytes": template.get("readEnvelopeBytes", 0),
                "required": bool(template.get("required")),
                "category": template.get("category", "general"),
                "source": template.get("source", "core"),
                "sourceExtensionId": template.get("sourceExtensionId"),
            }
            for template in sorted(metadata.get("resourceTemplates", []), key=lambda item: item["id"])
        },
    }
    RESOURCE_SELECTION_PATH.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    dump_debug_instructions("resource-selection-save")
