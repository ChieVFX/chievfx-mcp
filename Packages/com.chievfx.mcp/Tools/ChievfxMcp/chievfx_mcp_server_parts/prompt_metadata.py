# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

def compact_prompt_descriptor(prompt: dict[str, Any]) -> dict[str, Any]:
    descriptor = {
        "name": prompt.get("name"),
        "title": prompt.get("title", ""),
        "description": prompt.get("description", ""),
        "category": prompt.get("category") or PROMPT_CATEGORIES.get(prompt.get("name"), "General"),
        "arguments": prompt.get("arguments", []),
    }
    if not descriptor["title"]:
        descriptor.pop("title")
    return descriptor


def compact_prompt_description_surface(prompt: dict[str, Any]) -> dict[str, Any]:
    descriptor = {
        "name": prompt.get("name"),
        "title": prompt.get("title", ""),
        "description": prompt.get("description", ""),
    }
    if not descriptor["title"]:
        descriptor.pop("title")
    return descriptor


def compact_prompt_descriptor_json(prompt: dict[str, Any]) -> str:
    return json.dumps(compact_prompt_descriptor(prompt), ensure_ascii=False, separators=(",", ":"))


def compact_prompt_description_json(prompt: dict[str, Any]) -> str:
    return json.dumps(compact_prompt_description_surface(prompt), ensure_ascii=False, separators=(",", ":"))


def compact_prompt_get_envelope(prompt: dict[str, Any]) -> str:
    return json.dumps(
        {
            "jsonrpc": "2.0",
            "id": 1,
            "method": "prompts/get",
            "params": {
                "name": prompt.get("name"),
                "arguments": {},
            },
        },
        ensure_ascii=False,
        separators=(",", ":"),
    )


def response_estimate_for_prompt(prompt: dict[str, Any]) -> dict[str, str]:
    profile = "dynamic" if prompt.get("dynamic") or prompt.get("bridgeCommand") else "small"
    estimate = PROMPT_RESPONSE_ESTIMATE_PROFILES[profile].copy()
    estimate["profile"] = profile
    estimate["note"] = RESPONSE_ESTIMATE_NOTE
    return estimate


def build_prompt_metadata() -> dict[str, Any]:
    encoder, estimator = get_token_encoder()
    extension_capabilities = collect_extension_capabilities()
    prompts = all_prompts()
    # Required prompts are locked enabled in UI.
    # Exception: diagnostics prompts should not be locked (can be toggled off).
    required_prompt_names = (DEFAULT_REQUIRED_PROMPT_NAMES & {prompt["name"] for prompt in prompts}) | {
        prompt["name"]
        for prompt in prompts
        if prompt.get("required")
        and (str(prompt.get("category", "")).casefold() != "diagnostics")
        and ("diagnostics" not in str(prompt.get("name", "")).casefold())
    }
    metadata_prompts: list[dict[str, Any]] = []

    for prompt in prompts:
        descriptor_json = compact_prompt_descriptor_json(prompt)
        descriptor_bytes = len(descriptor_json.encode("utf-8"))
        description_json = compact_prompt_description_json(prompt)
        get_envelope_json = compact_prompt_get_envelope(prompt)
        get_envelope_bytes = len(get_envelope_json.encode("utf-8"))
        metadata_prompts.append(
            {
                "name": prompt["name"],
                "title": prompt.get("title", ""),
                "description": prompt.get("description", ""),
                "arguments": prompt.get("arguments", []),
                "category": prompt.get("category") or PROMPT_CATEGORIES.get(prompt["name"], "General"),
                "descriptorHash": hashlib.sha256(descriptor_json.encode("utf-8")).hexdigest(),
                "descriptorPreview": descriptor_json,
                "descriptorBytes": descriptor_bytes,
                "estimatedTokens": estimate_descriptor_tokens(descriptor_json, encoder),
                "descriptionEstimatedTokens": estimate_descriptor_tokens(description_json, encoder),
                "getEnvelopePreview": get_envelope_json,
                "getEnvelopeBytes": get_envelope_bytes,
                "getEnvelopeEstimatedTokens": estimate_descriptor_tokens(get_envelope_json, encoder),
                "responseEstimate": response_estimate_for_prompt(prompt),
                "required": prompt["name"] in required_prompt_names,
                "source": prompt.get("source", "core"),
                "sourceExtensionId": prompt.get("sourceExtensionId"),
                "sourceExtensionName": prompt.get("sourceExtensionName"),
                "sourceExtensionVersion": prompt.get("sourceExtensionVersion"),
                "sourceAssembly": prompt.get("sourceAssembly"),
            }
        )

    return {
        "schemaVersion": PROMPT_SELECTION_SCHEMA_VERSION,
        "source": "Tools/ChievfxMcp/chievfx_mcp_server.py:PROMPTS + Unity extension manifest",
        "selectionPath": str(PROMPT_SELECTION_PATH),
        "extensionManifestPath": str(EXTENSION_CAPABILITY_MANIFEST_PATH),
        "estimator": estimator,
        "promptDescriptorEstimateBasis": PROMPT_DESCRIPTOR_ESTIMATE_BASIS,
        "promptDescriptionEstimateBasis": PROMPT_DESCRIPTION_ESTIMATE_BASIS,
        "getEnvelopeEstimateBasis": PROMPT_GET_ENVELOPE_ESTIMATE_BASIS,
        "responseEstimateNote": RESPONSE_ESTIMATE_NOTE,
        "note": PROMPT_SELECTION_NOTE,
        "guidance": PROMPT_RELOAD_GUIDANCE,
        "categoryDescriptions": PROMPT_CATEGORY_DESCRIPTIONS,
        "extensions": extension_capabilities["extensions"],
        "extensionErrors": extension_capabilities["errors"],
        "requiredPromptNames": sorted(required_prompt_names),
        "prompts": metadata_prompts,
    }


def load_enabled_prompt_names() -> set[str]:
    prompts = all_prompts()
    prompt_names = {prompt["name"] for prompt in prompts}
    # Required prompts are locked enabled in UI.
    # Exception: diagnostics prompts should not be locked (can be toggled off).
    required_prompt_names = (DEFAULT_REQUIRED_PROMPT_NAMES & prompt_names) | {
        prompt["name"]
        for prompt in prompts
        if prompt.get("required")
        and (str(prompt.get("category", "")).casefold() != "diagnostics")
        and ("diagnostics" not in str(prompt.get("name", "")).casefold())
    }

    try:
        payload = json.loads(PROMPT_SELECTION_PATH.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return set(required_prompt_names)

    enabled_names = payload.get("enabledPromptNames")
    if not isinstance(enabled_names, list):
        return set(required_prompt_names)

    selected_prompt_names = {item for item in enabled_names if isinstance(item, str)}
    return (selected_prompt_names & prompt_names) | required_prompt_names


def save_enabled_prompt_names(enabled_prompt_names: set[str], metadata: dict[str, Any] | None = None) -> None:
    metadata = metadata or build_prompt_metadata()
    prompt_names = {prompt["name"] for prompt in all_prompts()}
    required_prompt_names = set(metadata.get("requiredPromptNames", [])) & prompt_names
    persisted_names = (enabled_prompt_names & prompt_names) | required_prompt_names

    PROMPT_SELECTION_PATH.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "schemaVersion": PROMPT_SELECTION_SCHEMA_VERSION,
        "updatedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "source": metadata.get("source", "Tools/ChievfxMcp/chievfx_mcp_server.py:PROMPTS"),
        "estimator": metadata.get("estimator", "unknown"),
        "note": PROMPT_SELECTION_NOTE,
        "promptDescriptorEstimateBasis": metadata.get(
            "promptDescriptorEstimateBasis", PROMPT_DESCRIPTOR_ESTIMATE_BASIS
        ),
        "promptDescriptionEstimateBasis": metadata.get(
            "promptDescriptionEstimateBasis", PROMPT_DESCRIPTION_ESTIMATE_BASIS
        ),
        "getEnvelopeEstimateBasis": metadata.get("getEnvelopeEstimateBasis", PROMPT_GET_ENVELOPE_ESTIMATE_BASIS),
        "responseEstimateNote": metadata.get("responseEstimateNote", RESPONSE_ESTIMATE_NOTE),
        "guidance": PROMPT_RELOAD_GUIDANCE,
        "enabledPromptNames": sorted(persisted_names),
        "prompts": {
            prompt["name"]: {
                "descriptorHash": prompt.get("descriptorHash", ""),
                "estimatedTokens": prompt.get("estimatedTokens", 0),
                "descriptionEstimatedTokens": prompt.get("descriptionEstimatedTokens", 0),
                "descriptorBytes": prompt.get("descriptorBytes", 0),
                "getEnvelopeEstimatedTokens": prompt.get("getEnvelopeEstimatedTokens", 0),
                "getEnvelopeBytes": prompt.get("getEnvelopeBytes", 0),
                "responseEstimateProfile": (prompt.get("responseEstimate") or {}).get("profile", ""),
                "required": bool(prompt.get("required")),
                "category": prompt.get("category", "General"),
                "source": prompt.get("source", "core"),
                "sourceExtensionId": prompt.get("sourceExtensionId"),
            }
            for prompt in sorted(metadata.get("prompts", []), key=lambda item: item["name"])
        },
    }
    PROMPT_SELECTION_PATH.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def enabled_prompts() -> list[dict[str, Any]]:
    enabled_names = load_enabled_prompt_names()
    return [compact_prompt_descriptor(prompt) for prompt in all_prompts() if prompt["name"] in enabled_names]


def get_prompt_by_name(name: str) -> dict[str, Any]:
    for prompt in all_prompts():
        if prompt["name"] == name:
            return prompt
    raise PromptNotFoundError(f"ChievFX MCP prompt not found: {name}")


def ensure_prompt_enabled(name: str) -> None:
    prompt = get_prompt_by_name(name)
    if prompt["name"] not in load_enabled_prompt_names():
        raise PromptNotFoundError(
            f"ChievFX MCP prompt '{name}' is disabled. Enable it in Window > ChievFX > MCP Prompts, "
            "then reload MCP prompts or restart Cursor."
        )


def prompt_argument_names(prompt: dict[str, Any]) -> set[str]:
    return {
        argument["name"]
        for argument in prompt.get("arguments", [])
        if isinstance(argument, dict) and isinstance(argument.get("name"), str)
    }


def required_prompt_argument_names(prompt: dict[str, Any]) -> set[str]:
    return {
        argument["name"]
        for argument in prompt.get("arguments", [])
        if isinstance(argument, dict) and isinstance(argument.get("name"), str) and bool(argument.get("required"))
    }


def validate_prompt_arguments(prompt: dict[str, Any], arguments: Any) -> dict[str, str]:
    if arguments is None:
        arguments = {}
    if not isinstance(arguments, dict):
        raise PromptArgumentError("prompts/get param 'arguments' must be an object when provided.")

    known_names = prompt_argument_names(prompt)
    required_names = required_prompt_argument_names(prompt)
    unknown_names = sorted(name for name in arguments if name not in known_names)
    if unknown_names:
        raise PromptArgumentError(f"Unknown argument(s) for prompt '{prompt['name']}': {', '.join(unknown_names)}")

    missing_names = sorted(name for name in required_names if name not in arguments)
    if missing_names:
        raise PromptArgumentError(f"Missing required argument(s) for prompt '{prompt['name']}': {', '.join(missing_names)}")

    coerced: dict[str, str] = {}
    for name, value in arguments.items():
        if value is None or isinstance(value, (list, dict)):
            raise PromptArgumentError(f"Argument '{name}' for prompt '{prompt['name']}' must be a scalar value.")
        coerced[name] = str(value)

    for name in known_names:
        coerced.setdefault(name, "")
    return coerced


class PromptFormatArgs(dict[str, str]):
    def __missing__(self, key: str) -> str:
        return ""


def format_prompt_template(template: str, arguments: dict[str, str]) -> str:
    try:
        return template.format_map(PromptFormatArgs(arguments))
    except (KeyError, ValueError) as exc:
        raise PromptArgumentError(f"Could not format prompt template: {exc}") from exc


def coerce_prompt_messages(value: Any) -> list[dict[str, Any]]:
    if not isinstance(value, list) or not value:
        raise PromptArgumentError("Prompt result must contain a non-empty messages array.")

    messages: list[dict[str, Any]] = []
    for message in value:
        if not isinstance(message, dict):
            raise PromptArgumentError("Prompt message entries must be objects.")
        role = message.get("role", "user")
        if role not in {"user", "assistant"}:
            raise PromptArgumentError("Prompt message role must be 'user' or 'assistant'.")

        content = message.get("content")
        if isinstance(content, str):
            content = {"type": "text", "text": content}
        if not isinstance(content, dict):
            raise PromptArgumentError("Prompt message content must be text or a content object.")
        if content.get("type") != "text" or not isinstance(content.get("text"), str):
            raise PromptArgumentError("Prompt message content must be a text content object.")
        messages.append({"role": role, "content": {"type": "text", "text": content["text"]}})

    return messages


def render_static_prompt(prompt: dict[str, Any], arguments: dict[str, str]) -> dict[str, Any]:
    if isinstance(prompt.get("staticText"), str):
        messages = [{"role": "user", "content": {"type": "text", "text": format_prompt_template(prompt["staticText"], arguments)}}]
    else:
        prompt_messages = prompt.get("messages")
        if not isinstance(prompt_messages, list) or not prompt_messages:
            raise PromptNotFoundError(f"ChievFX MCP prompt '{prompt['name']}' has no static template.")
        messages = []
        for message in prompt_messages:
            if not isinstance(message, dict):
                raise PromptArgumentError(f"Prompt '{prompt['name']}' has malformed static message metadata.")
            role = message.get("role", "user")
            if role not in {"user", "assistant"}:
                raise PromptArgumentError(f"Prompt '{prompt['name']}' has unsupported static message role.")
            text = message.get("text")
            if not isinstance(text, str):
                raise PromptArgumentError(f"Prompt '{prompt['name']}' has malformed static message text.")
            messages.append(
                {
                    "role": role,
                    "content": {"type": "text", "text": format_prompt_template(text, arguments)},
                }
            )

    result: dict[str, Any] = {"messages": messages}
    if isinstance(prompt.get("description"), str) and prompt["description"]:
        result["description"] = prompt["description"]
    return result


def coerce_bridge_prompt_result(prompt: dict[str, Any], result: Any) -> dict[str, Any]:
    if isinstance(result, str):
        payload: dict[str, Any] = {"messages": [{"role": "user", "content": {"type": "text", "text": result}}]}
    elif isinstance(result, dict):
        payload = dict(result)
        if isinstance(payload.get("text"), str) and "messages" not in payload:
            payload["messages"] = [{"role": "user", "content": {"type": "text", "text": payload["text"]}}]
    else:
        raise PromptArgumentError(f"Prompt '{prompt['name']}' bridge result must be text or an object.")

    payload["messages"] = coerce_prompt_messages(payload.get("messages"))
    if "description" not in payload and isinstance(prompt.get("description"), str) and prompt["description"]:
        payload["description"] = prompt["description"]
    return payload
