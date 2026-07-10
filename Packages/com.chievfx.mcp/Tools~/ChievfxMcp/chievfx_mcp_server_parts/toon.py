# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

def to_toon(value: Any) -> str:
    if value is None:
        return "null"

    lines: list[str] = []
    write_toon(value, lines, None)
    return "\n".join(line for line in lines if line)


def write_toon(value: Any, lines: list[str], label: str | None) -> None:
    if isinstance(value, dict):
        scalar_parts: list[str] = []
        nested_items: list[tuple[str, Any]] = []
        for key, item in value.items():
            if should_omit_toon_value(item):
                continue
            if isinstance(item, (list, dict)):
                nested_items.append((key, item))
            else:
                scalar_parts.append(f"{key}:{format_toon_atom(item)}")

        if label is not None:
            if scalar_parts:
                lines.append(f"{label} {' '.join(scalar_parts)}")
            else:
                lines.append(f"{label}:")
        elif scalar_parts:
            lines.append(" ".join(scalar_parts))

        for key, item in nested_items:
            write_toon(item, lines, key)
        return

    if isinstance(value, list):
        item_count = len(value)
        prefix = f"{label}[{item_count}]" if label else f"[{item_count}]"
        lines.append(f"{prefix}:")
        for item in value:
            if isinstance(item, dict):
                parts: list[str] = []
                nested: list[tuple[str, Any]] = []
                for key, nested_item in item.items():
                    if should_omit_toon_value(nested_item):
                        continue
                    if isinstance(nested_item, (list, dict)):
                        nested.append((key, nested_item))
                    else:
                        parts.append(f"{key}:{format_toon_atom(nested_item)}")
                lines.append(f"- {' '.join(parts)}" if parts else "-")
                for key, nested_item in nested:
                    write_toon(nested_item, lines, f"  {key}")
            else:
                lines.append(f"- {format_toon_atom(item)}")
        return

    if label is not None:
        lines.append(f"{label}:{format_toon_atom(value)}")
    else:
        lines.append(format_toon_atom(value))


def should_omit_toon_value(value: Any) -> bool:
    if value is None:
        return True
    if value == "":
        return True
    if value == [] or value == {}:
        return True
    return False


def format_toon_atom(value: Any) -> str:
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, (int, float)):
        return str(value)
    if isinstance(value, str):
        if value == "":
            return '""'
        if any(ch.isspace() for ch in value) or any(ch in value for ch in ':[]"{}#,'):
            return json.dumps(value, ensure_ascii=False, separators=(",", ":"))
        return value
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))
