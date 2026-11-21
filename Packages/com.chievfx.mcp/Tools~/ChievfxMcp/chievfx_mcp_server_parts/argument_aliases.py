# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Argument names a caller guessed in another form, renamed to the tool's own before the call.

from __future__ import annotations

# A property's other names, declared in its schema: {"grep": {"type": "string", "x-aliases": ["text", "query"]}}.
ARGUMENT_ALIASES_KEYWORD = "x-aliases"


def _normalized_argument_name(name: str) -> str:
    return "".join(character for character in name.lower() if character.isalnum())


def resolve_argument_aliases(name: str, arguments: Any) -> tuple[Any, list[str]]:
    """The call's arguments with guessed names renamed to the tool's, and the renames as "old→new".

    Models guess names their own way: car_name or CarName for carName, text for grep. Such a call used to reach
    the tool with the argument silently ignored, or bounce with unknown_argument and cost a turn. Two kinds are
    renamed: the same name in another case or with separators, when it matches exactly one property, and a
    name the property declares in its schema under x-aliases. A name the tool knows, or whose target the call
    already set, is left alone; anything else still gets the unknown-argument warning.
    """
    if not isinstance(arguments, dict) or not arguments:
        return arguments, []
    properties = _tool_schema_properties(name)
    if not properties:
        return arguments, []
    known = (
        set(properties)
        | UNIVERSAL_TOOL_ARGUMENT_KEYS
        | set(TOOL_ARGUMENT_ALIASES.get(name, ()))
        | set(BACK_COMPAT_ARGUMENT_ALIASES.get(name, ()))
    )
    declared: dict[str, str] = {}
    by_normalized: dict[str, list[str]] = {}
    for property_name, schema in properties.items():
        by_normalized.setdefault(_normalized_argument_name(property_name), []).append(property_name)
        aliases = schema.get(ARGUMENT_ALIASES_KEYWORD) if isinstance(schema, dict) else None
        for alias in aliases if isinstance(aliases, list) else []:
            if isinstance(alias, str) and alias:
                declared.setdefault(_normalized_argument_name(alias), property_name)
    resolved = dict(arguments)
    renamed: list[str] = []
    for key in list(arguments):
        if not isinstance(key, str) or key in properties:
            continue
        # text:true is the flag texts, normalized:true the flag isNormalized, even where text or normalized is a known
        # alias of another argument: true is no filter and no point.
        flag = _boolean_flag(key, arguments[key], properties)
        if flag is not None and flag not in resolved:
            resolved.pop(key)
            resolved[flag] = _as_boolean(arguments[key])
            renamed.append(f"{key}→{flag}")
            continue
        if key in known:
            continue
        # A declared alias also in another case or with separators: frame_rate and frameRate for framerate.
        normalized = _normalized_argument_name(key)
        canonical = declared.get(normalized)
        if canonical is None:
            matches = by_normalized.get(normalized) or []
            canonical = matches[0] if len(matches) == 1 else None
        value = resolved[key]
        if canonical is None:
            # level for levels: the singular of a list argument, a single value becomes its one item.
            matches = by_normalized.get(normalized + "s") or []
            canonical = matches[0] if len(matches) == 1 else None
            if canonical is not None and _schema_type(properties[canonical]) == "array" and not isinstance(value, list):
                value = [value]
        if canonical is None or canonical in resolved:
            continue
        resolved.pop(key)
        resolved[canonical] = value
        renamed.append(f"{key}→{canonical}")
    return resolved, renamed


def _schema_type(schema: Any) -> Any:
    return schema.get("type") if isinstance(schema, dict) else None


def _is_boolean_like(value: Any) -> bool:
    # A client sends an argument its copy of the schema lacks as a string: true arrives as "true".
    return isinstance(value, bool) or (isinstance(value, str) and value.strip().lower() in ("true", "false"))


def _as_boolean(value: Any) -> Any:
    return value.strip().lower() == "true" if isinstance(value, str) else value


def _boolean_flag(key: str, value: Any, properties: dict[str, Any]) -> str | None:
    if not _is_boolean_like(value):
        return None
    normalized = _normalized_argument_name(key)
    for candidate in (normalized + "s", "is" + normalized):
        for property_name, schema in properties.items():
            if _normalized_argument_name(property_name) == candidate and _schema_type(schema) == "boolean":
                return property_name
    return None
