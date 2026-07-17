# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

from __future__ import annotations

import copy

TOOL_DESCRIPTOR_DIR = PACKAGE_ROOT / "Tools~" / "ChievfxMcp" / "chievfx_mcp_server_parts" / "tool_descriptors"

_SCHEMA_PLACEHOLDERS = {
    "outputFormat": OUTPUT_FORMAT_PROPERTY,
    "methodRef": METHOD_REF_SCHEMA,
    "serializedValue": SERIALIZED_VALUE_SCHEMA,
    "vector3Ref": VECTOR3_REF,
    "vector3": VECTOR3_SCHEMA,
}


def _resolve_tool_descriptor_placeholders(value: Any) -> Any:
    if isinstance(value, dict):
        if set(value) == {"$chievfxSchema"}:
            key = value["$chievfxSchema"]
            if not isinstance(key, str) or key not in _SCHEMA_PLACEHOLDERS:
                raise ValueError(f"Unknown ChievFX schema placeholder: {key!r}")
            return copy.deepcopy(_SCHEMA_PLACEHOLDERS[key])
        if set(value) == {"$chievfxConst"}:
            key = value["$chievfxConst"]
            if not isinstance(key, str) or key not in globals():
                raise ValueError(f"Unknown ChievFX constant placeholder: {key!r}")
            return globals()[key]
        return {key: _resolve_tool_descriptor_placeholders(item) for key, item in value.items()}
    if isinstance(value, list):
        return [_resolve_tool_descriptor_placeholders(item) for item in value]
    return value


def _load_tool_descriptor_json(path: Path) -> dict[str, Any]:
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise RuntimeError(f"Could not load tool descriptor {path}: {exc}") from exc
    if not isinstance(raw, dict):
        raise RuntimeError(f"Tool descriptor {path} must contain a JSON object.")
    descriptor = _resolve_tool_descriptor_placeholders(raw)
    if not isinstance(descriptor.get("name"), str) or not descriptor["name"]:
        raise RuntimeError(f"Tool descriptor {path} is missing a string name.")
    return descriptor


def _load_tool_descriptor_order() -> list[str]:
    path = TOOL_DESCRIPTOR_DIR / "tool_order.json"
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise RuntimeError(f"Could not load tool descriptor order {path}: {exc}") from exc
    names = raw.get("tools") if isinstance(raw, dict) else None
    if not isinstance(names, list) or not all(isinstance(name, str) and name for name in names):
        raise RuntimeError(f"Tool descriptor order {path} must contain a non-empty tools array.")
    return names


def load_tool_descriptors() -> list[dict[str, Any]]:
    names = _load_tool_descriptor_order()
    seen: set[str] = set()
    descriptors: list[dict[str, Any]] = []
    for name in names:
        if name in seen:
            raise RuntimeError(f"Duplicate tool descriptor order entry: {name}")
        seen.add(name)
        descriptor = _load_tool_descriptor_json(TOOL_DESCRIPTOR_DIR / f"{name}.json")
        if descriptor["name"] != name:
            raise RuntimeError(f"Tool descriptor filename/order mismatch: {name} != {descriptor['name']}")
        descriptors.append(descriptor)

    extra_names = {
        path.stem
        for path in TOOL_DESCRIPTOR_DIR.glob("*.json")
        if path.name != "tool_order.json" and path.stem not in seen
    }
    if extra_names:
        raise RuntimeError(f"Tool descriptors missing from tool_order.json: {', '.join(sorted(extra_names))}")
    return descriptors


TOOLS: list[dict[str, Any]] = load_tool_descriptors()
