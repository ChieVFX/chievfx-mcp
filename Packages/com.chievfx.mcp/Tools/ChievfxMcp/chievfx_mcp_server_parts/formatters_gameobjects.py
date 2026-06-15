# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

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

    lines = [" ".join(header_parts)]
    for game_object in objects:
        if isinstance(game_object, dict):
            lines.extend(format_gameobject_find_object_lines(game_object))
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
    if not should_omit_toon_value(scene_name):
        header_parts.append(f"scene:{format_toon_atom(scene_name)}")
    for key in ("count", "totalObjects", "maxDepth", "maxResults", "truncated", "depthLimited"):
        value = result.get(key)
        if not should_omit_toon_value(value):
            header_parts.append(f"{key}:{format_toon_atom(value)}")

    lines = [" ".join(header_parts)]
    for root in roots:
        if isinstance(root, dict):
            lines.extend(format_gameobject_hierarchy_node(root, depth=0))
    return "\n".join(line for line in lines if line)


def format_ugui_ui_find_text(result: dict[str, Any]) -> str:
    objects = result.get("objects")
    if not isinstance(objects, list):
        objects = []
    header_parts = []
    count = result.get("count")
    total_matches = result.get("totalMatches")
    if not should_omit_toon_value(count) and not should_omit_toon_value(total_matches):
        header_parts.append(f"({format_toon_atom(count)} shown, {format_toon_atom(total_matches)} match{'' if total_matches == 1 else 'es'})")
    elif not should_omit_toon_value(count):
        header_parts.append(f"({format_toon_atom(count)} shown)")
    if result.get("truncated") is True:
        header_parts.append("truncated")
    lines = [" ".join(header_parts)]
    for game_object in objects:
        if isinstance(game_object, dict):
            lines.extend(format_gameobject_find_object_lines(game_object))
    return "\n".join(line for line in lines if line)


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


def format_gameobject_hierarchy_node(game_object: dict[str, Any], depth: int) -> list[str]:
    indent = "  " * depth
    lines = [indent + format_gameobject_hierarchy_row(game_object)]
    component_types = game_object.get("componentTypes")
    components = game_object.get("components")
    if not isinstance(components, list) and isinstance(component_types, list):
        component_text = ", ".join(str(component) for component in component_types)
        if game_object.get("componentTypesTruncated") is True:
            component_text += ", ..."
        lines.append(indent + f"  components[{len(component_types)}]: {component_text}")
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
        component_text = ", ".join(str(component) for component in component_types)
        if game_object.get("componentTypesTruncated") is True:
            component_text += ", ..."
        lines.append(f"  components[{len(component_types)}]: {component_text}")
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
    return "\n".join(
        [
            f"space: {space}",
            f"position: {format_vector3_inline(transform.get('position'))}",
            f"rotationEuler: {format_vector3_inline(transform.get('rotationEuler'))}",
            f"scale: {format_vector3_inline(transform.get('scale'))}",
        ]
    )


def format_vector3_inline(value: Any) -> str:
    if not isinstance(value, dict):
        return format_toon_atom(value)

    return f"{format_float_compact(value.get('x'))}, {format_float_compact(value.get('y'))}, {format_float_compact(value.get('z'))}"


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


def format_component_value(value: Any) -> str:
    if isinstance(value, str):
        normalized = value
        if normalized.startswith("RGBA(") and normalized.endswith(")"):
            normalized = re.sub(r"(?<=\d)0+\b", "", normalized)
            normalized = normalized.replace(".000", ".0").replace(", ", ",")
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


def format_events_check_since_text(result: dict[str, Any]) -> str:
    events = result.get("events")
    if not isinstance(events, list):
        events = []

    shown_events = [event for event in events if isinstance(event, dict)]
    omitted_count = max(0, len(events) - len(shown_events))
    header_parts = [
        f"matched:{format_toon_atom(bool(result.get('matched')))}",
        f"count:{result.get('count', len(events))}",
        f"shown:{len(shown_events)}",
    ]
    if omitted_count:
        header_parts.append(f"omitted:{omitted_count}")
    for key in ("hasMore", "sinceEventId", "sinceTimestampUtc", "lastEventId", "truncatedBeforeEventId"):
        value = result.get(key)
        if not should_omit_toon_value(value):
            header_parts.append(f"{key}:{format_toon_atom(value)}")

    lines = [" ".join(header_parts)]
    if shown_events:
        lines.append("events:")
        for event in shown_events:
            lines.append(format_event_row(event))
    if omitted_count:
        lines.append(
            f"... {omitted_count} non-object events omitted. Use outputFormat:\"json\" for raw detail."
        )
    return "\n".join(lines)


def format_event_row(event: dict[str, Any]) -> str:
    event_id = event.get("eventId")
    timestamp = compact_event_timestamp(event.get("timestamp"))
    source = event.get("source")
    event_type = event.get("type")
    level = event.get("level")
    marker = event.get("marker")
    operation_id = event.get("operationId")
    message = compact_event_message(event.get("message"))

    parts: list[str] = []
    if isinstance(event_id, int):
        parts.append(f"#{event_id}")
    if timestamp:
        parts.append(timestamp)
    source_type = "/".join(str(value) for value in (source, event_type) if value)
    if source_type:
        parts.append(source_type)
    if level:
        parts.append(str(level))
    if marker:
        parts.append(f"marker={marker}")
    if operation_id:
        parts.append(f"op={operation_id}")
    if message:
        parts.append(message)

    return "- " + " ".join(parts) if parts else "-"


def compact_event_timestamp(value: Any) -> str:
    if not isinstance(value, str) or not value:
        return ""
    timestamp = value
    if "." in timestamp:
        head, tail = timestamp.split(".", 1)
        suffix = "Z" if tail.endswith("Z") else ""
        timestamp = head + suffix
    return timestamp


def compact_event_message(value: Any) -> str:
    if not isinstance(value, str):
        return ""
    message = " ".join(value.split())
    if len(message) <= DEFAULT_EVENTS_TEXT_MESSAGE_CHARS:
        return message
    return message[: DEFAULT_EVENTS_TEXT_MESSAGE_CHARS - 3].rstrip() + "..."
