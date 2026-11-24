# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

from __future__ import annotations

import math

# Compact text of the runtime UI tools: ui-control-find, ui-runtime-* and ugui-ui-find.

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
    warnings = result.get("warnings")
    if isinstance(warnings, list):
        for warning in warnings:
            if isinstance(warning, str) and warning.strip():
                lines.append(f"warning: {warning.strip()}")
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
    lines.extend(format_ui_control_find_texts(result))
    return "\n".join(line for line in lines if line)


def format_ui_control_find_texts(result: dict[str, Any]) -> list[str]:
    """texts:true: one line per window, its texts top to bottom joined by ' | '."""
    windows = result.get("texts")
    if not isinstance(windows, list):
        return ["texts: listed with page 1"] if result.get("textsOnPage") == 1 else []
    rows: list[str] = []
    shown = 0
    for window in windows:
        if not isinstance(window, dict):
            continue
        texts = [text for text in window.get("texts") or [] if isinstance(text, str)]
        if not texts:
            continue
        shown += len(texts)
        rows.append(f"  {window.get('window')}: " + " | ".join(texts))
    total = result.get("textsTotal")
    if not rows:
        if result.get("frameworkFilter") == "uitoolkit":
            return ["UI Toolkit text: use mcp-resource-read uri:chievfx://extensions/chievfx.uitoolkit/runtime/visible-tree"]
        # Filtered by wildcards, an empty list means no visible text matched, not an empty screen.
        if result.get("wildcards"):
            return ["texts: none matched the wildcards (call without wildcards for every visible text)"]
        return ["texts: none visible"]
    header = f"texts ({shown}):"
    if isinstance(total, int) and total > shown:
        header = f"texts ({shown} of {total}, narrow with wildcards):"
    return [header] + rows


def format_ui_runtime_normalized(value: float) -> str:
    if value <= 0.0:
        return "0"
    if value >= 1.0:
        return "1"
    return f"{value:.2f}"


def format_ui_runtime_warning_lines(warnings: Any) -> list[str]:
    """Warning lines for the ui-runtime-* text outputs, with the per-canvas flood collapsed.

    A real UI emits one warning per canvas about raycasters and visibility — 77 of them in a shipping
    game — and they are static scene facts, identical on every call, not findings about this interaction.
    Aggregate them the same way the probe does so a click result stays a handful of lines.
    """
    if not isinstance(warnings, list):
        return []
    return [f"! {warning}" for warning in summarize_probe_warnings(warnings)]


def format_ui_runtime_click_text(result: dict[str, Any]) -> str:
    header_parts = [
        f"playMode:{format_toon_atom(result.get('playMode'))}",
    ]
    if result.get("anyClicked") is True:
        header_parts.append("clicked:true")
    elif result.get("anyResolved") is True:
        # A target was hit but no handler responded: make the "nothing happened" case explicit.
        header_parts.append("clicked:false")
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

    lines.extend(format_ui_runtime_warning_lines(result.get("warnings")))

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
        handler_ref = format_ui_runtime_click_handler_ref(section, framework)
        if handler_ref:
            parts.append("handler:" + handler_ref)
    else:
        # Hit a raycast target, but no recognized click handler responded. press-only: PointerDown/Up reached a
        # handler (a button that reads the held pointer itself) — a synthetic click does not press it.
        events = section.get("events")
        is_press_only = isinstance(events, list) and any(
            isinstance(event, str) and event.startswith(("pointerDown:", "pointerUp:")) and not event.endswith(":none")
            for event in events
        )
        parts.append("press-only" if is_press_only else "no-handler")

    events = section.get("events")
    if isinstance(events, list) and events:
        parts.append("events:" + ",".join(str(event) for event in events if event))

    return " ".join(parts)


def format_ui_runtime_click_handler_ref(section: dict[str, Any], framework: Any) -> str:
    """Compact path/id|ref of the element that actually caught the click.

    Returns "" when the responder is the same as the raycast target (already shown) or
    when no responder was reported.
    """
    handler_object = section.get("handlerObject")
    if not isinstance(handler_object, dict):
        return ""

    handler_path = handler_object.get("path")
    if should_omit_toon_value(handler_path):
        return ""

    target = section.get("target")
    target_path = target.get("path") if isinstance(target, dict) else None
    if handler_path == target_path:
        return ""

    parts = [format_toon_atom(handler_path)]
    if framework == "uitoolkit":
        ref = handler_object.get("visualElementRef")
        if not should_omit_toon_value(ref):
            parts.append(format_toon_atom(ref))
    else:
        instance_id = handler_object.get("instanceId")
        if not should_omit_toon_value(instance_id):
            parts.append(f"id:{format_toon_atom(instance_id)}")
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

    lines.extend(format_ui_runtime_warning_lines(result.get("warnings")))

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

    lines.extend(format_ui_runtime_warning_lines(result.get("warnings")))

    return "\n".join(line for line in lines if line)


def format_ui_runtime_type_text_text(result: dict[str, Any]) -> str:
    """Compact text for ui-runtime-type-text.

    Without this the tool fell through to the generic dump: the whole hit stack, both target states and
    every canvas warning, several KB per keystroke-equivalent call. What a caller needs is whether the
    field was found and what it now contains.
    """
    header_parts = [f"playMode:{format_toon_atom(result.get('playMode'))}"]
    header_parts.append("resolved:true" if result.get("resolved") is True else "resolved:false")
    framework = result.get("framework")
    if not should_omit_toon_value(framework):
        header_parts.append(f"framework:{format_toon_atom(framework)}")
    control_type = result.get("controlType")
    if not should_omit_toon_value(control_type):
        header_parts.append(f"control:{format_toon_atom(control_type)}")
    if result.get("submitted") is True or result.get("submit") is True:
        header_parts.append("submitted:true")
    lines = ["type-text " + " ".join(header_parts)]

    target = result.get("target")
    if isinstance(target, dict):
        target_parts = []
        path = target.get("path")
        if not should_omit_toon_value(path):
            target_parts.append(f"path:{format_toon_atom(path)}")
        instance_id = target.get("instanceId")
        if not should_omit_toon_value(instance_id):
            target_parts.append(f"id:{format_toon_atom(instance_id)}")
        visual_element_ref = target.get("visualElementRef")
        if not should_omit_toon_value(visual_element_ref):
            target_parts.append(f"ref:{format_toon_atom(visual_element_ref)}")
        if target_parts:
            lines.append("target " + " ".join(target_parts))

    for label, key in (("before", "textBefore"), ("after", "textAfter")):
        value = result.get(key)
        # An empty text is an answer too: after "" is how a caller sees the field was cleared.
        if isinstance(value, str):
            lines.append(f'{label} "{value}"')

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

    # Only surfaced when the field was not found; that is when the hit stack is the answer.
    stack = result.get("stack")
    if isinstance(stack, list) and stack and result.get("resolved") is not True:
        for hit in stack[:5]:
            if isinstance(hit, dict):
                path = hit.get("path")
                if not should_omit_toon_value(path):
                    lines.append(f"hit {format_toon_atom(path)}")

    lines.extend(format_ui_runtime_warning_lines(result.get("warnings")))

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

    lines.extend(format_ui_runtime_warning_lines(result.get("warnings")))

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

    lines.extend(format_ui_runtime_warning_lines(result.get("warnings")))

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
    covered_by = control.get("coveredBy")
    if not should_omit_toon_value(covered_by):
        line += f"; covered by {format_gameobject_text_value(covered_by)} (a player's tap lands there)"
    elif control.get("centreMissed") is True:
        # A Toggle whose label takes no raycasts: a tap in the middle of its zone did nothing and nothing said why.
        hit_zone = format_ui_control_zone(
            control.get("hitZone"),
            normalize_coords=normalize_coords,
            screen_width=screen_width,
            screen_height=screen_height,
        )
        line += "; its centre takes no taps" + (f", tap its graphic at {hit_zone}" if hit_zone else ", click it by path")
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
