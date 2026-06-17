# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

import math


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


def infer_ui_control_find_screen_size(controls: list[Any]) -> tuple[float | None, float | None]:
    screen_width = None
    screen_height = None
    for control in controls:
        if not isinstance(control, dict):
            continue
        zone = control.get("zone")
        if not isinstance(zone, dict):
            continue
        width = zone.get("screenWidth")
        height = zone.get("screenHeight")
        if isinstance(width, (int, float)) and isinstance(height, (int, float)):
            return float(width), float(height)
        x_max = zone.get("xMax")
        y_max = zone.get("yMax")
        if isinstance(x_max, (int, float)):
            screen_width = max(screen_width or 0.0, float(x_max))
        if isinstance(y_max, (int, float)):
            screen_height = max(screen_height or 0.0, float(y_max))
    if screen_width and screen_height:
        return math.ceil(screen_width), math.ceil(screen_height)
    return None, None


def format_ui_control_find_text(result: dict[str, Any], *, normalize_coords: bool | None = None) -> str:
    controls = result.get("controls")
    if not isinstance(controls, list):
        controls = []
    page = result.get("page")
    total_pages = result.get("totalPages")
    header = ""
    if not should_omit_toon_value(page) and not should_omit_toon_value(total_pages):
        header = f"page:{format_toon_atom(page)}/{format_toon_atom(total_pages)}"
    omit_type = not should_omit_toon_value(result.get("controlTypeFilter"))
    if normalize_coords is None:
        normalize_coords = result.get("normalizeCoords") is True
    screen_size = result.get("screenSize")
    screen_width = None
    screen_height = None
    if isinstance(screen_size, dict):
        width = screen_size.get("width")
        height = screen_size.get("height")
        if isinstance(width, (int, float)) and isinstance(height, (int, float)):
            screen_width = float(width)
            screen_height = float(height)
    if screen_width is None or screen_height is None:
        inferred_width, inferred_height = infer_ui_control_find_screen_size(controls)
        if screen_width is None:
            screen_width = inferred_width
        if screen_height is None:
            screen_height = inferred_height
    lines = [header] if header else []
    warnings = result.get("warnings")
    if isinstance(warnings, list):
        for warning in warnings:
            if isinstance(warning, str) and warning.strip():
                lines.append(f"warning: {warning.strip()}")
    for control in controls:
        if not isinstance(control, dict):
            continue
        lines.append(
            format_ui_control_find_row(
                control,
                omit_type=omit_type,
                normalize_coords=normalize_coords,
                screen_width=screen_width,
                screen_height=screen_height,
            )
        )
    return "\n".join(line for line in lines if line)


def format_ui_runtime_normalized(value: float) -> str:
    if value <= 0.0:
        return "0"
    if value >= 1.0:
        return "1"
    return f"{value:.2f}"


def format_ui_runtime_click_text(result: dict[str, Any]) -> str:
    header_parts = [
        f"playMode:{format_toon_atom(result.get('playMode'))}",
    ]
    if result.get("anyClicked") is True:
        header_parts.append("clicked:true")
    elif result.get("anyResolved") is True:
        header_parts.append("resolved:true")
    lines = ["click " + " ".join(header_parts)]

    coordinate = result.get("coordinateConvention")
    if isinstance(coordinate, dict):
        pos_parts = []
        screen_position = coordinate.get("screenPosition")
        if isinstance(screen_position, dict):
            x = screen_position.get("x")
            y = screen_position.get("y")
            if isinstance(x, (int, float)) and isinstance(y, (int, float)):
                pos_parts.append(f"px:{format_ui_runtime_click_coord(x)},{format_ui_runtime_click_coord(y)}")
        normalized = coordinate.get("normalizedPosition")
        if isinstance(normalized, dict):
            nx = normalized.get("x")
            ny = normalized.get("y")
            if isinstance(nx, (int, float)) and isinstance(ny, (int, float)):
                pos_parts.append(
                    f"norm:{format_ui_runtime_normalized(float(nx))},{format_ui_runtime_normalized(float(ny))}"
                )
        if pos_parts:
            lines.append("pos " + " ".join(pos_parts))

    frameworks = result.get("frameworks")
    if isinstance(frameworks, list):
        for section in frameworks:
            if not isinstance(section, dict):
                continue
            row = format_ui_runtime_click_framework_row(section)
            if row:
                lines.append(row)

    warnings = result.get("warnings")
    if isinstance(warnings, list):
        for warning in warnings:
            if isinstance(warning, str) and warning.strip():
                lines.append(f"! {warning.strip()}")

    return "\n".join(line for line in lines if line)


def format_ui_runtime_click_coord(value: float) -> str:
    rounded = round(float(value), 1)
    if rounded == int(rounded):
        return str(int(rounded))
    return f"{rounded:.1f}"


def format_ui_runtime_click_framework_row(section: dict[str, Any]) -> str:
    framework = section.get("framework")
    if should_omit_toon_value(framework):
        return ""

    parts = [f"- {format_toon_atom(framework)}"]
    if section.get("available") is False:
        parts.append("unavailable")
        return " ".join(parts)

    if section.get("resolved") is not True:
        parts.append("miss")
        return " ".join(parts)

    target = section.get("target")
    if isinstance(target, dict):
        path = target.get("path")
        if not should_omit_toon_value(path):
            parts.append(f"path:{format_toon_atom(path)}")
        if framework == "uitoolkit":
            visual_element_ref = target.get("visualElementRef")
            if not should_omit_toon_value(visual_element_ref):
                parts.append(f"ref:{format_toon_atom(visual_element_ref)}")
        else:
            instance_id = target.get("instanceId")
            if not should_omit_toon_value(instance_id):
                parts.append(f"id:{format_toon_atom(instance_id)}")

    if section.get("clicked") is True:
        parts.append("clicked")
    else:
        parts.append("resolved")

    events = section.get("events")
    if isinstance(events, list) and events:
        parts.append("events:" + ",".join(str(event) for event in events if event))

    return " ".join(parts)


def format_ui_runtime_drag_text(result: dict[str, Any]) -> str:
    header_parts = [
        f"playMode:{format_toon_atom(result.get('playMode'))}",
    ]
    if result.get("anyDragged") is True:
        header_parts.append("dragged:true")
    elif result.get("anyResolved") is True:
        header_parts.append("resolved:true")
    lines = ["drag " + " ".join(header_parts)]

    for label, key in (("start", "startCoordinateConvention"), ("end", "endCoordinateConvention")):
        pos_line = format_ui_runtime_drag_coordinate_line(label, result.get(key))
        if pos_line:
            lines.append(pos_line)

    screen_delta = result.get("screenDelta")
    if isinstance(screen_delta, dict):
        dx = screen_delta.get("x")
        dy = screen_delta.get("y")
        if isinstance(dx, (int, float)) and isinstance(dy, (int, float)):
            lines.append(
                f"delta px:{format_ui_runtime_click_coord(float(dx))},{format_ui_runtime_click_coord(float(dy))}"
            )

    frameworks = result.get("frameworks")
    if isinstance(frameworks, list):
        for section in frameworks:
            if not isinstance(section, dict):
                continue
            row = format_ui_runtime_drag_framework_row(section)
            if row:
                lines.append(row)

    warnings = result.get("warnings")
    if isinstance(warnings, list):
        for warning in warnings:
            if isinstance(warning, str) and warning.strip():
                lines.append(f"! {warning.strip()}")

    return "\n".join(line for line in lines if line)


def format_ui_runtime_drag_coordinate_line(label: str, coordinate: Any) -> str:
    if not isinstance(coordinate, dict):
        return ""

    pos_parts = []
    screen_position = coordinate.get("screenPosition")
    if isinstance(screen_position, dict):
        x = screen_position.get("x")
        y = screen_position.get("y")
        if isinstance(x, (int, float)) and isinstance(y, (int, float)):
            pos_parts.append(f"px:{format_ui_runtime_click_coord(float(x))},{format_ui_runtime_click_coord(float(y))}")
    normalized = coordinate.get("normalizedPosition")
    if isinstance(normalized, dict):
        nx = normalized.get("x")
        ny = normalized.get("y")
        if isinstance(nx, (int, float)) and isinstance(ny, (int, float)):
            pos_parts.append(
                f"norm:{format_ui_runtime_normalized(float(nx))},{format_ui_runtime_normalized(float(ny))}"
            )
    if not pos_parts:
        return ""
    return f"{label} " + " ".join(pos_parts)


def format_ui_runtime_drag_framework_row(section: dict[str, Any]) -> str:
    framework = section.get("framework")
    if should_omit_toon_value(framework):
        return ""

    parts = [f"- {format_toon_atom(framework)}"]
    if section.get("available") is False:
        parts.append("unavailable")
        return " ".join(parts)

    if section.get("resolved") is not True:
        parts.append("miss")
        return " ".join(parts)

    target = section.get("target")
    if isinstance(target, dict):
        path = target.get("path")
        if not should_omit_toon_value(path):
            parts.append(f"path:{format_toon_atom(path)}")
        if framework == "uitoolkit":
            visual_element_ref = target.get("visualElementRef")
            if not should_omit_toon_value(visual_element_ref):
                parts.append(f"ref:{format_toon_atom(visual_element_ref)}")
        else:
            instance_id = target.get("instanceId")
            if not should_omit_toon_value(instance_id):
                parts.append(f"id:{format_toon_atom(instance_id)}")

    if section.get("dragged") is True:
        parts.append("dragged")
    else:
        parts.append("resolved")

    events = section.get("events")
    if isinstance(events, list) and events:
        parts.append("events:" + ",".join(str(event) for event in events if event))

    return " ".join(parts)


def format_ui_runtime_set_control_value_text(result: dict[str, Any]) -> str:
    header_parts = [f"playMode:{format_toon_atom(result.get('playMode'))}"]
    if result.get("resolved") is True:
        header_parts.append("resolved:true")
    else:
        header_parts.append("resolved:false")
    framework = result.get("framework")
    if not should_omit_toon_value(framework):
        header_parts.append(f"framework:{format_toon_atom(framework)}")
    lines = ["set-value " + " ".join(header_parts)]

    target = result.get("target")
    if isinstance(target, dict):
        path = target.get("path")
        if not should_omit_toon_value(path):
            lines.append(f"target path:{format_toon_atom(path)}")
        instance_id = target.get("instanceId")
        if not should_omit_toon_value(instance_id):
            lines.append(f"id:{format_toon_atom(instance_id)}")
        visual_element_ref = target.get("visualElementRef")
        if not should_omit_toon_value(visual_element_ref):
            lines.append(f"ref:{format_toon_atom(visual_element_ref)}")

    for label, key in (("before", "targetStateBefore"), ("after", "targetStateAfter")):
        state = result.get(key)
        if not isinstance(state, dict):
            continue
        controls = state.get("controls")
        if isinstance(controls, list) and controls:
            control = controls[0] if isinstance(controls[0], dict) else None
        else:
            control = state
        if isinstance(control, dict):
            value = control.get("value", control.get("isOn", control.get("text")))
            if not should_omit_toon_value(value):
                lines.append(f"{label} {format_toon_atom(value)}")

    attempts = result.get("attempts")
    if isinstance(attempts, list):
        for attempt in attempts:
            if not isinstance(attempt, dict):
                continue
            row_parts = [f"- {format_toon_atom(attempt.get('framework'))}"]
            if attempt.get("available") is False:
                row_parts.append("unavailable")
            elif attempt.get("resolved") is False:
                row_parts.append("miss")
            elif attempt.get("error"):
                row_parts.append(f"error:{format_toon_atom(attempt.get('error'))}")
            lines.append(" ".join(row_parts))

    warnings = result.get("warnings")
    if isinstance(warnings, list):
        for warning in warnings:
            if isinstance(warning, str) and warning.strip():
                lines.append(f"! {warning.strip()}")

    return "\n".join(line for line in lines if line)


def format_ui_runtime_focus_text(result: dict[str, Any]) -> str:
    header_parts = [f"playMode:{format_toon_atom(result.get('playMode'))}"]
    if result.get("resolved") is True or result.get("focused") is True:
        header_parts.append("focused:true")
    else:
        header_parts.append("resolved:false")
    framework = result.get("framework")
    if not should_omit_toon_value(framework):
        header_parts.append(f"framework:{format_toon_atom(framework)}")
    lines = ["focus " + " ".join(header_parts)]

    target = result.get("target")
    if isinstance(target, dict):
        path = target.get("path")
        if not should_omit_toon_value(path):
            lines.append(f"target path:{format_toon_atom(path)}")
        instance_id = target.get("instanceId")
        if not should_omit_toon_value(instance_id):
            lines.append(f"id:{format_toon_atom(instance_id)}")
        visual_element_ref = target.get("visualElementRef")
        if not should_omit_toon_value(visual_element_ref):
            lines.append(f"ref:{format_toon_atom(visual_element_ref)}")

    selected_before = result.get("selectedObjectBefore")
    selected_after = result.get("selectedObjectAfter")
    if isinstance(selected_after, dict):
        path = selected_after.get("path")
        if not should_omit_toon_value(path):
            lines.append(f"selected:{format_toon_atom(path)}")
    elif isinstance(selected_before, dict) or isinstance(selected_after, dict):
        lines.append("selected:none")

    focused_before = result.get("focusedElementBefore")
    focused_after = result.get("focusedElementAfter")
    if isinstance(focused_after, dict):
        path = focused_after.get("path")
        if not should_omit_toon_value(path):
            lines.append(f"uitk-focus:{format_toon_atom(path)}")
    elif isinstance(focused_before, dict):
        lines.append("uitk-focus:none")

    attempts = result.get("attempts")
    if isinstance(attempts, list):
        for attempt in attempts:
            if not isinstance(attempt, dict):
                continue
            row_parts = [f"- {format_toon_atom(attempt.get('framework'))}"]
            if attempt.get("available") is False:
                row_parts.append("unavailable")
            elif attempt.get("resolved") is False:
                row_parts.append("miss")
            elif attempt.get("error"):
                row_parts.append(f"error:{format_toon_atom(attempt.get('error'))}")
            lines.append(" ".join(row_parts))

    warnings = result.get("warnings")
    if isinstance(warnings, list):
        for warning in warnings:
            if isinstance(warning, str) and warning.strip():
                lines.append(f"! {warning.strip()}")

    return "\n".join(line for line in lines if line)


def format_ui_runtime_clear_focus_text(result: dict[str, Any]) -> str:
    header_parts = [f"playMode:{format_toon_atom(result.get('playMode'))}"]
    if result.get("anyCleared") is True:
        header_parts.append("cleared:true")
    lines = ["clear-focus " + " ".join(header_parts)]

    frameworks = result.get("frameworks")
    if isinstance(frameworks, list):
        for section in frameworks:
            if not isinstance(section, dict):
                continue
            parts = [f"- {format_toon_atom(section.get('framework'))}"]
            if section.get("available") is False:
                parts.append("unavailable")
            elif section.get("cleared") is True:
                parts.append("cleared")
            else:
                parts.append("noop")
            lines.append(" ".join(parts))

    warnings = result.get("warnings")
    if isinstance(warnings, list):
        for warning in warnings:
            if isinstance(warning, str) and warning.strip():
                lines.append(f"! {warning.strip()}")

    return "\n".join(line for line in lines if line)


def format_ui_control_find_row(
    control: dict[str, Any],
    *,
    omit_type: bool,
    normalize_coords: bool = False,
    screen_width: float | None = None,
    screen_height: float | None = None,
) -> str:
    path = format_gameobject_text_value(control.get("path"))
    identity_parts = [path]
    if control.get("framework") == "uitoolkit":
        visual_element_ref = control.get("visualElementRef")
        if not should_omit_toon_value(visual_element_ref):
            identity_parts.append(f"({visual_element_ref})")
    else:
        instance_id = control.get("instanceId")
        if not should_omit_toon_value(instance_id):
            identity_parts.append(f"(id: {format_toon_atom(instance_id)})")
    line = "- " + " ".join(identity_parts)
    if not omit_type:
        control_type = control.get("controlType")
        if not should_omit_toon_value(control_type):
            line += f" : {format_gameobject_text_value(control_type)}"
    zone_text = format_ui_control_zone(
        control.get("zone"),
        normalize_coords=normalize_coords,
        screen_width=screen_width,
        screen_height=screen_height,
    )
    if zone_text:
        line += f"; zone:{zone_text}"
    return line


def format_ui_control_zone(
    value: Any,
    *,
    normalize_coords: bool = False,
    screen_width: float | None = None,
    screen_height: float | None = None,
) -> str:
    if not isinstance(value, dict):
        return ""
    return format_ui_control_zone_bounds(
        value,
        normalize_coords=normalize_coords,
        screen_width=screen_width,
        screen_height=screen_height,
    )


def format_normalized_coord(value: float) -> str:
    if not math.isfinite(value):
        return "0"
    clamped = max(0.0, min(1.0, value))
    rounded = round(clamped, 2)
    if rounded <= 0.0:
        return "0"
    if rounded >= 1.0:
        return "1"
    return f"{rounded:.2f}"


def format_ui_control_zone_bounds(
    value: dict[str, Any],
    *,
    normalize_coords: bool = False,
    screen_width: float | None = None,
    screen_height: float | None = None,
) -> str:
    x_min = value.get("xMin")
    y_min = value.get("yMin")
    x_max = value.get("xMax")
    y_max = value.get("yMax")
    if any(should_omit_toon_value(item) for item in (x_min, y_min, x_max, y_max)):
        return ""
    if not all(isinstance(item, (int, float)) for item in (x_min, y_min, x_max, y_max)):
        return ""
    if normalize_coords:
        if not screen_width or not screen_height:
            return ""
        width = max(1.0, float(screen_width))
        height = max(1.0, float(screen_height))
        return (
            f"{format_normalized_coord(float(x_min) / width)},{format_normalized_coord(float(y_min) / height)}"
            f"..{format_normalized_coord(float(x_max) / width)},{format_normalized_coord(float(y_max) / height)}"
        )
    return (
        f"{math.ceil(float(x_min))},{math.ceil(float(y_min))}"
        f"..{math.floor(float(x_max))},{math.floor(float(y_max))}"
    )


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

    if tool_name == "ui-runtime-clear-focus":
        if isinstance(result, dict):
            return format_ui_runtime_clear_focus_text(result)

    return to_toon(result)


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
