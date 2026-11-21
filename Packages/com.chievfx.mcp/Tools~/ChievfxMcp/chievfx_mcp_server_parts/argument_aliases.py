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
                declared.setdefault(alias.lower(), property_name)
    resolved = dict(arguments)
    renamed: list[str] = []
    for key in list(arguments):
        if not isinstance(key, str) or key in known:
            continue
        canonical = declared.get(key.lower())
        if canonical is None:
            matches = by_normalized.get(_normalized_argument_name(key)) or []
            canonical = matches[0] if len(matches) == 1 else None
        if canonical is None or canonical in resolved:
            continue
        resolved[canonical] = resolved.pop(key)
        renamed.append(f"{key}→{canonical}")
    return resolved, renamed
