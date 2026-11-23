# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

from __future__ import annotations

import decimal
import math
import struct


def format_diagnostics_lines(diagnostics: Any) -> list[str]:
    if not isinstance(diagnostics, list) or not diagnostics:
        return []
    return ["diagnostics:"] + [f"- {format_toon_atom(item)}" for item in diagnostics if not should_omit_toon_value(item)]


def format_gameobject_find_text(result: dict[str, Any]) -> str:
    objects = result.get("objects")
    if not isinstance(objects, list):
        objects = []

    header_parts = []
    scene_name = result.get("sceneName")
    if not should_omit_toon_value(scene_name):
        header_parts.append(f"scene: {format_toon_atom(scene_name)}")
    count = result.get("count")
    total_matches = result.get("totalMatches")
    if not should_omit_toon_value(count) and not should_omit_toon_value(total_matches):
        header_parts.append(f"({format_toon_atom(count)} shown, {format_toon_atom(total_matches)} match{'' if total_matches == 1 else 'es'})")
    elif not should_omit_toon_value(count):
        header_parts.append(f"({format_toon_atom(count)} shown)")
    if result.get("truncated") is True:
        max_results = result.get("maxResults")
        suffix = f" at maxResults {format_toon_atom(max_results)}" if not should_omit_toon_value(max_results) else ""
        header_parts.append(f"truncated{suffix}")
    inactive_hidden = result.get("inactiveHidden")
    if isinstance(inactive_hidden, int) and not isinstance(inactive_hidden, bool) and inactive_hidden > 0:
        header_parts.append(f"+{inactive_hidden} inactive hidden (includeInactive:true shows them)")

    lines = [" ".join(header_parts)]
    for game_object in objects:
        if isinstance(game_object, dict):
            lines.extend(format_gameobject_find_object_lines(game_object))
    note = result.get("note")
    if isinstance(note, str) and note:
        lines.append(f"note: {note}")
    return "\n".join(lines)


def format_gameobject_get_text(result: dict[str, Any]) -> str:
    game_object = result.get("gameObject")
    matches = result.get("matches")
    if not isinstance(game_object, dict) and isinstance(matches, list):
        lines = [
            f"{format_toon_atom(result.get('reason'))} path:{format_gameobject_text_value(result.get('path'))} "
            f"matches:{format_toon_atom(result.get('count'))}"
        ]
        for match in matches:
            if isinstance(match, dict):
                lines.extend(format_gameobject_find_object_lines(match))
        return "\n".join(line for line in lines if line)
    if not isinstance(game_object, dict):
        return to_toon(result)

    lines = [format_gameobject_detail_row(game_object)]
    components = game_object.get("components")
    if isinstance(components, list):
        lines.append(format_gameobject_components_line(components))
    return "\n".join(lines)


def format_gameobject_hierarchy_text(result: dict[str, Any]) -> str:
    roots = result.get("roots")
    if not isinstance(roots, list):
        roots = []

    header_parts = []
    scene_name = result.get("sceneName")
    scenes = result.get("scenes")
    if not should_omit_toon_value(scene_name):
        header_parts.append(f"scene:{format_toon_atom(scene_name)}")
    elif isinstance(scenes, list) and scenes:
        # In Play the roots of DontDestroyOnLoad came mixed with the scene's own, and the header named no scene.
        names = [format_gameobject_text_value(scene.get("name")) or "<untitled>" for scene in scenes if isinstance(scene, dict)]
        header_parts.append("scenes:" + format_toon_atom(",".join(names)))
    for key in ("count", "totalObjects", "maxDepth", "maxResults", "truncated", "depthLimited"):
        value = result.get(key)
        if not should_omit_toon_value(value):
            header_parts.append(f"{key}:{format_toon_atom(value)}")

    lines = [" ".join(header_parts)]
    warnings = result.get("warnings")
    if isinstance(warnings, list):
        lines.extend(f"warning: {warning.strip()}" for warning in warnings if isinstance(warning, str) and warning.strip())
    scene_names = {
        scene.get("path"): format_gameobject_text_value(scene.get("name")) or "<untitled>"
        for scene in (scenes if isinstance(scenes, list) else [])
        if isinstance(scene, dict)
    }
    current_scene = None
    for root in roots:
        if isinstance(root, dict):
            root_scene = root.get("scenePath")
            if scene_names and isinstance(root_scene, str) and root_scene != current_scene:
                lines.append(f"scene:{format_toon_atom(scene_names.get(root_scene, root_scene))}")
                current_scene = root_scene
            lines.extend(format_gameobject_hierarchy_node(root, depth=0))
    return "\n".join(line for line in lines if line)


def format_tool_result_text(tool_name: str, result: Any, arguments: dict[str, Any]) -> str:
    output_format = arguments.get("outputFormat", "toon")
    if output_format == "json":
        return json.dumps(result, ensure_ascii=False, separators=(",", ":"))

    if tool_name == "ui-control-find":
        if isinstance(result, dict):
            normalize_coords = arguments.get("normalizeCoords") if "normalizeCoords" in arguments else None
            return format_ui_control_find_text(result, normalize_coords=normalize_coords)

    if tool_name == "ui-runtime-click":
        if isinstance(result, dict):
            return format_ui_runtime_click_text(result)

    if tool_name == "ui-runtime-drag":
        if isinstance(result, dict):
            return format_ui_runtime_drag_text(result)

    if tool_name == "ui-runtime-set-control-value":
        if isinstance(result, dict):
            return format_ui_runtime_set_control_value_text(result)

    if tool_name == "ui-runtime-focus":
        if isinstance(result, dict):
            return format_ui_runtime_focus_text(result)

    if tool_name == "ui-runtime-type-text":
        if isinstance(result, dict):
            return format_ui_runtime_type_text_text(result)

    if tool_name == "ui-runtime-clear-focus":
        if isinstance(result, dict):
            return format_ui_runtime_clear_focus_text(result)

    return format_summary_first_text(result)


def format_summary_first_text(result: Any) -> str:
    """Generic result text: a string `summary` field becomes the first line, the rest follows as TOON.

    Core tools open with a one-line headline from their own formatters. An extension cannot ship a Python
    formatter, so this is the extension's way to get the same shape: return `summary` and it reads as the
    headline instead of one more `summary:"..."` row among the fields.
    """
    if not isinstance(result, dict):
        return to_toon(result)
    summary = result.get("summary")
    has_summary = isinstance(summary, str) and bool(summary.strip())
    # The ready wait call is copied as is: as a TOON field its JSON quotes would come out escaped.
    next_call = result.get("next") if isinstance(result.get("next"), str) else None
    if not has_summary and next_call is None:
        return to_toon(result)
    rest = {key: value for key, value in result.items() if not (key == "summary" and has_summary) and not (key == "next" and next_call)}
    rest_text = to_toon(rest) if rest else ""
    lines = [summary] if has_summary else []
    if rest_text.strip():
        lines.append(rest_text)
    if next_call is not None:
        lines.append("next: " + next_call)
    return "\n".join(lines)


def format_ugui_ui_hierarchy_text(result: dict[str, Any]) -> str:
    return format_gameobject_hierarchy_text(result)


def format_ugui_rect_get_text(result: dict[str, Any]) -> str:
    rects = result.get("rects")
    if not isinstance(rects, list):
        rects = []
    header_parts = []
    count = result.get("count")
    total_matches = result.get("totalMatches")
    if not should_omit_toon_value(count) and not should_omit_toon_value(total_matches):
        header_parts.append(f"({format_toon_atom(count)} shown, {format_toon_atom(total_matches)} match{'' if total_matches == 1 else 'es'})")
    elif not should_omit_toon_value(count):
        header_parts.append(f"({format_toon_atom(count)} shown)")
    lines = [" ".join(header_parts)]
    for row in rects:
        if not isinstance(row, dict):
            continue
        path = format_gameobject_text_value(row.get("path"))
        rect = row.get("rectTransform")
        if isinstance(rect, dict):
            lines.append(f"- {path} (id: {format_toon_atom(row.get('instanceId'))})")
            lines.append(
                f"  anchors {format_vector2_inline(rect.get('anchorMin'))}->{format_vector2_inline(rect.get('anchorMax'))}"
                f" pos {format_vector2_inline(rect.get('anchoredPosition') or rect.get('position'))}"
                f" size {format_vector2_inline(rect.get('sizeDelta') or rect.get('size'))}"
            )
            lines.append(f"  pivot {format_vector2_inline(rect.get('pivot'))}")
            if isinstance(rect.get("rect"), dict):
                rect_size = rect["rect"]
                lines.append(
                    "  rect "
                    + f"{format_float_compact(rect_size.get('width'))}x{format_float_compact(rect_size.get('height'))}"
                )
            if isinstance(rect.get("offsetMin"), dict) or isinstance(rect.get("offsetMax"), dict):
                lines.append(f"  offsets min:{format_vector2_inline(rect.get('offsetMin'))} max:{format_vector2_inline(rect.get('offsetMax'))}")
        else:
            lines.append(f"- {path} (id: {format_toon_atom(row.get('instanceId'))}) no RectTransform")
    return "\n".join(line for line in lines if line)


def format_ugui_textmeshpro_hierarchy_text(result: dict[str, Any]) -> str:
    groups = result.get("groups")
    if not isinstance(groups, list):
        groups = []
    header_parts = []
    for key in ("count", "groupCount", "maxResults"):
        value = result.get(key)
        if not should_omit_toon_value(value):
            header_parts.append(f"{key}:{format_toon_atom(value)}")
    lines = [" ".join(header_parts)]
    for group in groups:
        if not isinstance(group, dict):
            continue
        lines.append(f"style: {format_toon_atom(group.get('style'))} count:{format_toon_atom(group.get('count'))}")
        items = group.get("items")
        if isinstance(items, list):
            for item in items:
                if isinstance(item, dict):
                    text = format_toon_atom(item.get("text"))
                    lines.append(f"  • {format_gameobject_text_value(item.get('name'))} (id: {format_toon_atom(item.get('instanceId'))}) text:{text}")
    return "\n".join(line for line in lines if line)


def format_ugui_textmeshpro_get_text(result: dict[str, Any]) -> str:
    texts = result.get("texts")
    if not isinstance(texts, list):
        texts = []
    lines = [f"({format_toon_atom(result.get('count'))} shown)"]
    for row in texts:
        if not isinstance(row, dict):
            continue
        style = row.get("styleKey")
        lines.append(f"- {format_gameobject_text_value(row.get('path'))} (id: {format_toon_atom(row.get('instanceId'))}) text:{format_toon_atom(row.get('text'))}")
        if not should_omit_toon_value(style):
            lines.append(f"  style: {format_toon_atom(style)}")
    return "\n".join(line for line in lines if line)


def format_component_types_preview(
    game_object: dict[str, Any],
    component_types: list[Any],
    full_list_hint: str = "includeComponents:true lists all",
) -> str:
    """components[<total>]: the first types; a cut list says how many are shown, so 12 is not read as all of them."""
    component_text = ", ".join(str(component) for component in component_types)
    total = game_object.get("componentCount")
    if game_object.get("componentTypesTruncated") is True:
        total_text = str(total) if isinstance(total, int) else f"{len(component_types)}+"
        return f"components[{total_text}]: {component_text}, ... ({len(component_types)} shown; {full_list_hint})"
    return f"components[{len(component_types)}]: {component_text}"


def format_gameobject_hierarchy_node(game_object: dict[str, Any], depth: int) -> list[str]:
    indent = "  " * depth
    lines = [indent + format_gameobject_hierarchy_row(game_object)]
    component_types = game_object.get("componentTypes")
    components = game_object.get("components")
    if not isinstance(components, list) and isinstance(component_types, list):
        # The hierarchy keeps a preview even with includeComponents:true; one object's full list is gameobject-find's.
        lines.append(indent + "  " + format_component_types_preview(
            game_object, component_types, "gameobject-find instanceId:<id> includeComponents:true lists all"
        ))
    if isinstance(components, list):
        lines.append(indent + "  " + format_gameobject_components_line(components))
    children = game_object.get("children")
    if isinstance(children, list):
        for child in children:
            if isinstance(child, dict):
                lines.extend(format_gameobject_hierarchy_node(child, depth + 1))
    return lines


def format_gameobject_hierarchy_row(game_object: dict[str, Any]) -> str:
    name = format_gameobject_text_value(game_object.get("name"))
    if not name:
        path = format_gameobject_text_value(game_object.get("path"))
        name = path.rsplit("/", 1)[-1] if path else ""
    badges = [f"id: {format_toon_atom(game_object.get('instanceId'))}"]
    if game_object.get("activeSelf") is False:
        badges.append("inactiveSelf")
    if game_object.get("activeInHierarchy") is False:
        badges.append("inactiveHierarchy")
    return f"• {name} ({', '.join(badges)})"


def format_gameobject_find_row(game_object: dict[str, Any]) -> str:
    path = format_gameobject_text_value(game_object.get("path"))
    badges = [f"id: {format_toon_atom(game_object.get('instanceId'))}"]
    if game_object.get("activeSelf") is False:
        badges.append("inactiveSelf")
    if game_object.get("activeInHierarchy") is False:
        badges.append("inactiveHierarchy")

    return f"- {path} ({', '.join(badges)})"


def format_gameobject_find_object_lines(game_object: dict[str, Any]) -> list[str]:
    components = game_object.get("components")
    has_detail_fields = any(
        not should_omit_toon_value(game_object.get(key))
        for key in ("tag", "layer", "childCount", "screenRect")
    ) or game_object.get("isStatic") is True
    lines = ["- " + format_gameobject_detail_row(game_object)]
    if not has_detail_fields:
        lines = [format_gameobject_find_row(game_object)]
    component_types = game_object.get("componentTypes")
    if not isinstance(components, list) and isinstance(component_types, list):
        lines.append("  " + format_component_types_preview(game_object, component_types))
    if isinstance(components, list):
        lines.append("  " + format_gameobject_components_line(components))
    screen_rect = format_ugui_screen_rect(game_object.get("screenRect"))
    if screen_rect:
        lines.append(f"  zone {screen_rect}")
    return lines


def format_gameobject_detail_row(game_object: dict[str, Any]) -> str:
    path = format_gameobject_text_value(game_object.get("path"))
    badges = [f"id: {format_toon_atom(game_object.get('instanceId'))}"]
    if game_object.get("activeSelf") is False:
        badges.append("inactiveSelf")
    if game_object.get("activeInHierarchy") is False:
        badges.append("inactiveHierarchy")
    parts = [f"{path} ({', '.join(badges)})"]
    detail_parts = []
    for key, label in (("tag", "tag"), ("layer", "layer"), ("childCount", "children")):
        value = game_object.get(key)
        if not should_omit_toon_value(value):
            detail_parts.append(f"{label}: {format_gameobject_text_value(value)}")
    if game_object.get("isStatic") is True:
        detail_parts.append("static")
    if detail_parts:
        parts.append("\n  details: " + ", ".join(detail_parts))
    return "".join(parts)


def format_ugui_screen_rect(value: Any) -> str:
    if not isinstance(value, dict):
        return ""
    units = value.get("units")
    rect = value.get("rect")
    center = value.get("center")
    parts: list[str] = []
    prefix = "norm" if units == "normalized" else "px"
    rect_text = format_rect_bounds_inline(rect)
    if rect_text:
        parts.append(f"{prefix}:{rect_text}")
    center_text = format_vector2_pair(center)
    if center_text:
        parts.append(f"center:{center_text}")
    return " ".join(parts)


def format_rect_bounds_inline(value: Any) -> str:
    if not isinstance(value, dict):
        return ""
    x_min = value.get("xMin")
    y_min = value.get("yMin")
    x_max = value.get("xMax")
    y_max = value.get("yMax")
    if any(should_omit_toon_value(item) for item in (x_min, y_min, x_max, y_max)):
        return ""
    return f"{format_toon_atom(x_min)},{format_toon_atom(y_min)}..{format_toon_atom(x_max)},{format_toon_atom(y_max)}"


def format_gameobject_text_value(value: Any) -> str:
    if should_omit_toon_value(value):
        return ""
    return str(value)


def format_gameobject_component_summary(component: dict[str, Any]) -> str:
    component_type = str(component.get("type") or "MissingScript")
    return component_type


def format_gameobject_components_line(components: list[Any]) -> str:
    enabled_components = []
    disabled_components = []
    for component in components:
        if not isinstance(component, dict):
            continue
        target = disabled_components if component.get("enabled") is False else enabled_components
        target.append(format_gameobject_component_summary(component))

    enabled_text = ", ".join(enabled_components)
    if disabled_components:
        return f"components[{len(components)}]: {enabled_text}; disabled: {', '.join(disabled_components)}"
    return f"components[{len(components)}]: {enabled_text}"


def format_gameobject_transform_get_text(result: dict[str, Any]) -> str:
    transform = result.get("transform")
    if not isinstance(transform, dict):
        return to_toon(result)

    space = "world" if result.get("isWorld") is True else "local"
    lines = [
        f"space: {space}",
        f"position: {format_vector3_inline(transform.get('position'))}",
        f"rotationEuler: {format_vector3_inline(transform.get('rotationEuler'))}",
        f"scale: {format_vector3_inline(transform.get('scale'))}",
    ]
    note = result.get("note")
    if isinstance(note, str) and note:
        lines.append(f"note: {note}")
    return "\n".join(lines)


def format_vector3_inline(value: Any) -> str:
    if not isinstance(value, dict):
        return format_toon_atom(value)

    return f"{format_float_exact(value.get('x'))}, {format_float_exact(value.get('y'))}, {format_float_exact(value.get('z'))}"


def format_float_exact(value: Any) -> str:
    # Unity stores 32-bit floats: the shortest text that reads back to the same float, so a transform read here and
    # written back with transform-update stays as it was (4 decimals lost the last digits of -271.474335).
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        return format_toon_atom(value)

    try:
        target = struct.unpack("f", struct.pack("f", float(value)))[0]
    except OverflowError:
        return repr(float(value))
    text = repr(target)
    for digits in range(1, 10):
        candidate = f"{target:.{digits}g}"
        if struct.unpack("f", struct.pack("f", float(candidate)))[0] == target:
            text = candidate
            break
    # 'g' writes 180 as 1.8e+02 when 2 digits are enough; positions and angles read as plain numbers, so only
    # values Python itself prints with an exponent (below 1e-4, from 1e16) keep it.
    if "e" in text and 1e-4 <= abs(target) < 1e16:
        text = format(decimal.Decimal(text), "f")
    return text


def format_vector2_inline(value: Any) -> str:
    if not isinstance(value, dict):
        return format_toon_atom(value)

    return f"{format_float_compact(value.get('x'))}, {format_float_compact(value.get('y'))}"


def format_color_inline(value: Any) -> str:
    if not isinstance(value, dict):
        return format_toon_atom(value)

    return f"{format_float_compact(value.get('r'))}, {format_float_compact(value.get('g'))}, {format_float_compact(value.get('b'))}, {format_float_compact(value.get('a'))}"


def format_float_compact(value: Any) -> str:
    if not isinstance(value, (int, float)):
        return format_toon_atom(value)

    return f"{float(value):.4f}".rstrip("0").rstrip(".")


def format_gameobject_component_get_text(result: dict[str, Any]) -> str:
    game_object = result.get("gameObject")
    component = result.get("component")
    if not isinstance(game_object, dict) or not isinstance(component, dict):
        return to_toon(result)

    component_parts = [format_toon_atom(component.get("type"))]
    enabled = component.get("enabled")
    if enabled is not None:
        component_parts.append("enabled" if enabled is True else "disabled")
    fields_mode = component.get("serializedFieldsMode")
    if fields_mode == "debug":
        component_parts.append("debug")
    if result.get("serializedDataTruncated") is True:
        component_parts.append("truncated")
    lines = [" ".join(str(part) for part in component_parts if part)]

    fields = component.get("serializedFields")
    if isinstance(fields, list):
        for field in fields:
            if isinstance(field, dict):
                lines.append(format_serialized_field_row(field))

    match_count = result.get("matchCount")
    if isinstance(match_count, int) and match_count > 1:
        lines.append(
            f"note: {match_count} components match; this is componentIndex {result.get('componentIndex', 0)} (pass componentIndex for another)."
        )
    # Agents looked here for current health or speed: runtime state that is not serialized is not in this list.
    lines.append("note: serialized values only; runtime state (fields without [SerializeField], properties like Rigidbody.velocity) - read it with script-execute.")
    return "\n".join(lines)


def format_serialized_field_row(field: dict[str, Any]) -> str:
    name = field.get("name")
    type_name = field.get("typeName")
    value = field.get("value")
    left = format_toon_atom(name)
    if not should_omit_toon_value(type_name):
        left = f"{left}:{format_toon_atom(type_name)}"
    if should_omit_toon_value(value):
        return f"- {left}"
    return f"- {left} = {format_component_value(value)}"


def _compact_decimal(number: str) -> str:
    if "e" in number.lower():
        return number
    if "." not in number:
        return f"{number}.0" if number.lstrip("-").isdigit() else number
    number = number.rstrip("0")
    return f"{number}0" if number.endswith(".") else number


def format_component_value(value: Any) -> str:
    if isinstance(value, str):
        normalized = value
        if normalized.startswith("RGBA(") and normalized.endswith(")"):
            # Only zeros after the decimal point go: "10.000" stayed "1.0" when every trailing zero was stripped.
            normalized = ",".join(_compact_decimal(part.strip()) for part in normalized[5:-1].split(","))
            normalized = f"RGBA({normalized})"
        return format_toon_atom(normalized)
    return format_toon_atom(value)


def format_reflection_parameter(parameter: dict[str, Any]) -> str:
    type_name = compact_reflection_type_name(parameter.get("type"))
    name = str(parameter.get("name") or "")
    return " ".join(part for part in (type_name, name) if part)


def compact_reflection_type_name(value: Any) -> str:
    if not isinstance(value, str) or not value:
        return ""
    aliases = {
        "System.Void": "void",
        "System.Boolean": "bool",
        "System.Byte": "byte",
        "System.Char": "char",
        "System.Decimal": "decimal",
        "System.Double": "double",
        "System.Single": "float",
        "System.Int16": "short",
        "System.Int32": "int",
        "System.Int64": "long",
        "System.String": "string",
        "System.Object": "object",
    }
    if value in aliases:
        return aliases[value]
    if value.endswith("[]"):
        return f"{compact_reflection_type_name(value[:-2])}[]"
    if "[[" in value:
        value = value.split("[[", 1)[0]
    if "," in value:
        value = value.split(",", 1)[0]
    compact = value.rsplit(".", 1)[-1].replace("+", ".")
    return re.sub(r"`\d+$", "", compact)
