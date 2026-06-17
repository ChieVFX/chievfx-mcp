# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

def format_tool_text(result: Any, arguments: dict[str, Any], tool_name: str | None = None) -> str:
    if tool_name is not None:
        return format_tool_result_text(tool_name, result, arguments)

    output_format = arguments.get("outputFormat", "toon")
    if output_format == "json":
        return json.dumps(result, ensure_ascii=False, separators=(",", ":"))

    return to_toon(result)


def format_ugui_sibling_draworder_set_text(result: dict[str, Any]) -> str:
    lines: list[str] = []
    header_parts: list[str] = []
    for key in ("success", "updatedCount", "parentPath"):
        if key in result:
            header_parts.append(f"{key}:{format_toon_atom(result.get(key))}")
    if header_parts:
        lines.append(" ".join(header_parts))

    targets = result.get("targets")
    if isinstance(targets, list):
        lines.append(f"targets[{len(targets)}]:")
        for target in targets:
            if isinstance(target, dict):
                parts = [
                    f"{key}:{format_toon_atom(target.get(key))}"
                    for key in ("name", "path", "instanceId", "siblingIndex")
                    if key in target and not should_omit_toon_value(target.get(key))
                ]
                lines.append("- " + " ".join(parts))

    sibling_order = result.get("siblingOrder")
    if isinstance(sibling_order, list):
        showing = None
        truncated = None
        order_lines: list[str] = []
        for row in sibling_order:
            if not isinstance(row, dict):
                continue
            if "showing" in row:
                showing = row.get("showing")
                continue
            if "truncated" in row:
                truncated = row.get("truncated")
                continue
            for key, value in row.items():
                if should_omit_toon_value(value):
                    continue
                order_lines.append(f"{key}:{format_toon_atom(value)}")

        order_header = "new siblingOrder"
        meta: list[str] = []
        if showing is not None:
            meta.append(f"showing:{format_toon_atom(showing)}")
        if truncated is not None:
            meta.append(f"truncated:{format_toon_atom(truncated)}")
        if meta:
            order_header += " (" + " ".join(meta) + ")"
        lines.append(order_header)
        lines.extend(order_lines)
    elif sibling_order is not None:
        write_toon(sibling_order, lines, "new siblingOrder")

    return "\n".join(line for line in lines if line)


def format_ugui_sprite_configure_text(result: dict[str, Any]) -> str:
    lines: list[str] = []
    header_parts: list[str] = []
    for key in ("path", "guid", "found"):
        value = result.get(key)
        if not should_omit_toon_value(value):
            header_parts.append(f"{key}:{format_toon_atom(value)}")
    if header_parts:
        lines.append("sprite " + " ".join(header_parts))

    importer_parts: list[str] = []
    for key, label in (("textureType", "type"), ("meshType", "mesh"), ("pixelsPerUnit", "ppu")):
        value = result.get(key)
        if not should_omit_toon_value(value):
            importer_parts.append(f"{label}:{format_toon_atom(value)}")
    size_text = format_sprite_dimensions(result.get("dimensions"))
    if size_text:
        importer_parts.append(f"size:{size_text}")
    if importer_parts:
        lines.append("importer " + " ".join(importer_parts))

    border_text = format_sprite_border(result.get("spriteBorder"))
    if border_text:
        lines.append("border " + border_text)

    alpha = result.get("alpha")
    if isinstance(alpha, dict):
        alpha_parts = []
        for key, label in (("alphaIsTransparency", "transparency"), ("textureHasAlpha", "hasAlpha")):
            value = alpha.get(key)
            if not should_omit_toon_value(value):
                alpha_parts.append(f"{label}:{format_toon_atom(value)}")
        if alpha_parts:
            lines.append("alpha " + " ".join(alpha_parts))

    warnings = result.get("warnings")
    if isinstance(warnings, list):
        for warning in warnings:
            if isinstance(warning, str) and warning:
                lines.append(f"! {warning}")

    return "\n".join(line for line in lines if line)


def format_ugui_image_set_text(result: dict[str, Any]) -> str:
    target = get_dict(result.get("target"))
    image = get_dict(result.get("image"))
    sprite = get_dict(image.get("sprite"))

    lines: list[str] = []
    target_parts = []
    path = target.get("path")
    if not should_omit_toon_value(path):
        target_parts.append(f"path:{format_toon_atom(path)}")
    instance_id = target.get("instanceId")
    if not should_omit_toon_value(instance_id):
        target_parts.append(f"id:{format_toon_atom(instance_id)}")
    if target_parts:
        lines.append("target " + " ".join(target_parts))

    rect = get_dict(target.get("rectTransform"))
    if rect:
        lines.append(
            "rect "
            + f"anchors:{format_vector2_pair(rect.get('anchorMin'))}->{format_vector2_pair(rect.get('anchorMax'))} "
            + f"pos:{format_vector2_pair(rect.get('anchoredPosition') or rect.get('position'))} "
            + f"size:{format_vector2_pair(rect.get('sizeDelta') or rect.get('size'))}"
        )

    image_parts = []
    for key, label in (
        ("imageType", "type"),
        ("raycastTarget", "raycast"),
        ("preserveAspect", "preserveAspect"),
    ):
        value = image.get(key)
        if not should_omit_toon_value(value):
            image_parts.append(f"{label}:{format_toon_atom(value)}")
    color_text = format_color_rgba_compact(image.get("color"))
    if color_text:
        image_parts.append(f"color:{color_text}")
    if image_parts:
        lines.append("image " + " ".join(image_parts))

    if sprite:
        sprite_parts = []
        for key in ("name", "path", "guid"):
            value = sprite.get(key)
            if not should_omit_toon_value(value):
                sprite_parts.append(f"{key}:{format_toon_atom(value)}")
        ppu = sprite.get("pixelsPerUnit")
        if not should_omit_toon_value(ppu):
            sprite_parts.append(f"ppu:{format_float_compact(ppu)}")
        border_text = format_sprite_border_compact(sprite.get("border"))
        if border_text:
            sprite_parts.append(f"border:{border_text}")
        if sprite_parts:
            lines.append("sprite " + " ".join(sprite_parts))

    warnings = result.get("warnings")
    if isinstance(warnings, list):
        for warning in warnings:
            if isinstance(warning, str) and warning:
                lines.append(f"! {warning}")

    return "\n".join(line for line in lines if line)


def format_ugui_canvas_ensure_text(result: dict[str, Any]) -> str:
    canvas = get_dict(result.get("canvas"))
    event_system = get_dict(result.get("eventSystem"))
    lines: list[str] = []

    canvas_parts = []
    path = canvas.get("path")
    if not should_omit_toon_value(path):
        canvas_parts.append(f"path:{format_toon_atom(path)}")
    instance_id = canvas.get("instanceId")
    if not should_omit_toon_value(instance_id):
        canvas_parts.append(f"id:{format_toon_atom(instance_id)}")
    if canvas_parts:
        lines.append("canvas " + " ".join(canvas_parts))

    event_parts = []
    event_path = event_system.get("path")
    if not should_omit_toon_value(event_path):
        event_parts.append(f"path:{format_toon_atom(event_path)}")
    modules = event_system.get("inputModules")
    if isinstance(modules, list) and modules:
        event_parts.append("modules:" + ",".join(format_toon_atom(module) for module in modules if not should_omit_toon_value(module)))
    if event_parts:
        lines.append("eventSystem " + " ".join(event_parts))

    warnings = result.get("warnings")
    if isinstance(warnings, list):
        for warning in warnings:
            if isinstance(warning, str) and warning:
                lines.append(f"! {warning}")

    return "\n".join(line for line in lines if line)


def format_ugui_grid_create_text(result: dict[str, Any]) -> str:
    grid = get_dict(result.get("grid"))
    layout = get_dict(result.get("layout"))
    cells = get_dict(result.get("cells"))
    lines: list[str] = []

    grid_parts = []
    path = grid.get("path")
    if not should_omit_toon_value(path):
        grid_parts.append(f"path:{format_toon_atom(path)}")
    cell_count = result.get("cellCount")
    if not should_omit_toon_value(cell_count):
        grid_parts.append(f"cells:{format_toon_atom(cell_count)}")
    if grid_parts:
        lines.append("grid " + " ".join(grid_parts))

    layout_parts = []
    for key, label in (
        ("constraint", "constraint"),
        ("constraintCount", "count"),
    ):
        value = layout.get(key)
        if not should_omit_toon_value(value):
            layout_parts.append(f"{label}:{format_toon_atom(value)}")
    cell_size = format_vector2_pair(layout.get("cellSize"))
    if cell_size:
        layout_parts.append(f"cell:{cell_size}")
    spacing = format_vector2_pair(layout.get("spacing"))
    if spacing:
        layout_parts.append(f"spacing:{spacing}")
    padding = format_padding_compact(layout.get("padding"))
    if padding:
        layout_parts.append(f"padding:{padding}")
    if layout_parts:
        lines.append("layout " + " ".join(layout_parts))

    first = get_dict(cells.get("first"))
    last = get_dict(cells.get("last"))
    first_name = first.get("name")
    last_name = last.get("name")
    if not should_omit_toon_value(first_name):
        if not should_omit_toon_value(last_name) and last_name != first_name:
            lines.append(f"cells {first_name}..{last_name}")
        else:
            lines.append(f"cells {first_name}")

    warnings = result.get("warnings")
    if isinstance(warnings, list):
        for warning in warnings:
            if isinstance(warning, str) and warning:
                lines.append(f"! {warning}")

    return "\n".join(line for line in lines if line)


def format_ugui_element_create_text(result: dict[str, Any]) -> str:
    target = get_dict(result.get("target"))
    lines: list[str] = []

    element_parts = []
    element_type = result.get("elementType")
    if not should_omit_toon_value(element_type):
        element_parts.append(f"type:{format_toon_atom(element_type)}")
    path = target.get("path")
    if not should_omit_toon_value(path):
        element_parts.append(f"path:{format_toon_atom(path)}")
    if element_parts:
        lines.append("element " + " ".join(element_parts))

    rect = get_dict(target.get("rectTransform"))
    if rect:
        lines.append(
            "rect "
            + f"anchors:{format_vector2_pair(rect.get('anchorMin'))}->{format_vector2_pair(rect.get('anchorMax'))} "
            + f"pos:{format_vector2_pair(rect.get('anchoredPosition') or rect.get('position'))} "
            + f"size:{format_vector2_pair(rect.get('sizeDelta') or rect.get('size'))}"
        )

    components = compact_ugui_component_names(target.get("components"))
    if components:
        lines.append("components " + ",".join(components))

    text_backend = result.get("textBackend")
    if not should_omit_toon_value(text_backend):
        lines.append(f"textBackend {format_toon_atom(text_backend)}")

    warnings = result.get("warnings")
    if isinstance(warnings, list):
        for warning in warnings:
            if isinstance(warning, str) and warning:
                lines.append(f"! {warning}")

    return "\n".join(line for line in lines if line)


def compact_ugui_component_names(value: Any) -> list[str]:
    if not isinstance(value, list):
        return []
    ignored = {"RectTransform", "CanvasRenderer"}
    names: list[str] = []
    for item in value:
        if not isinstance(item, str) or item in ignored:
            continue
        names.append(item)
    return names


def format_padding_compact(value: Any) -> str:
    if not isinstance(value, dict):
        return ""
    parts = [value.get(key) for key in ("left", "right", "top", "bottom")]
    if any(not isinstance(part, (int, float)) for part in parts):
        return ""
    return ",".join(format_float_compact(part) for part in parts)


def format_color_rgba_compact(value: Any) -> str:
    if not isinstance(value, dict):
        return ""
    parts = [value.get(key) for key in ("r", "g", "b", "a")]
    if any(not isinstance(part, (int, float)) for part in parts):
        return ""
    return ",".join(format_float_compact(part) for part in parts)


def format_sprite_border_compact(value: Any) -> str:
    if not isinstance(value, dict):
        return ""
    parts: list[str] = []
    for key, label in (("left", "l"), ("bottom", "b"), ("right", "r"), ("top", "t")):
        item = value.get(key)
        if not should_omit_toon_value(item):
            parts.append(f"{label}:{format_float_compact(item)}")
    return " ".join(parts)


def format_sprite_dimensions(value: Any) -> str:
    if not isinstance(value, dict):
        return ""
    width = value.get("width")
    height = value.get("height")
    if should_omit_toon_value(width) or should_omit_toon_value(height):
        return ""
    return f"{format_toon_atom(width)}x{format_toon_atom(height)}"


def format_sprite_border(value: Any) -> str:
    if not isinstance(value, dict):
        return ""
    parts: list[str] = []
    for key, label in (("left", "l"), ("bottom", "b"), ("right", "r"), ("top", "t")):
        item = value.get(key)
        if not should_omit_toon_value(item):
            parts.append(f"{label}:{format_toon_atom(item)}")
    return " ".join(parts)


def format_editor_playmode_set_text(result: dict[str, Any]) -> str:
    if result.get("ok") is False:
        errors = result.get("validationErrors")
        if isinstance(errors, list) and errors:
            return "ok:false " + " ".join(f"! {error}" for error in errors if isinstance(error, str) and error)
        return "ok:false"

    is_playing = result.get("isPlaying")
    requested = result.get("requestedIsPlaying")
    cursor_before = result.get("eventCursorBefore")
    cursor_hint = ""
    if isinstance(cursor_before, int):
        # Surface the pre-toggle cursor so callers can events-wait from it and catch boot logs.
        cursor_hint = f" eventCursorBefore:{cursor_before} (use as events-wait sinceEventId to catch boot logs)"
    if result.get("status") == "unchanged":
        return f"playmode already {format_toon_atom(is_playing)}{cursor_hint}"
    if requested is not None:
        return f"playmode switching to {format_toon_atom(requested)}{cursor_hint}"
    return f"playmode {format_toon_atom(is_playing)}{cursor_hint}"


def format_ugui_runtime_probe_text(result: dict[str, Any]) -> str:
    if isinstance(result.get("ugui"), dict) or isinstance(result.get("uitoolkit"), dict):
        return format_runtime_ui_probe_markdown(result)
    if isinstance(result.get("adapters"), list):
        return format_runtime_ui_probe_markdown_from_adapters(result)
    if isinstance(result.get("probe"), dict):
        return format_runtime_ui_probe_markdown(result)

    return format_runtime_ui_probe_markdown_legacy(result)


def format_runtime_ui_probe_markdown_from_adapters(result: dict[str, Any]) -> str:
    lines: list[str] = ["## Runtime UI probe", ""]

    page = result.get("page")
    total_pages = result.get("totalPages")
    if page is not None and total_pages is not None:
        lines.append(f"page:{page}/{total_pages}")
        lines.append("")

    runtime_available = result.get("runtimeAvailable")
    truncated = result.get("truncated")
    total_hits = result.get("totalHits")
    max_rows = result.get("maxRows")
    meta_parts: list[str] = []
    if runtime_available is not None:
        meta_parts.append(f"**Runtime available:** {'yes' if runtime_available else 'no'}")
    if total_hits is not None:
        meta_parts.append(f"**Total hits:** {total_hits}")
    if truncated is True:
        meta_parts.append("**Truncated:** yes")
    if max_rows is not None:
        meta_parts.append(f"**Max rows:** {max_rows}")
    if meta_parts:
        lines.append(" · ".join(meta_parts))
        lines.append("")

    probe = get_dict(result.get("probe"))
    if probe:
        lines.extend(format_probe_position_markdown(probe, include_ui_toolkit_coords=True))
        lines.append("")

    adapters = result.get("adapters")
    if isinstance(adapters, list):
        for adapter in adapters:
            if not isinstance(adapter, dict):
                continue
            framework = adapter.get("framework") or adapter.get("frameworkId") or "unknown"
            title = "uGUI" if framework == "ugui" else "UI Toolkit" if framework == "uitoolkit" else str(framework)
            hits = adapter.get("hits")
            if not isinstance(hits, list):
                stack = adapter.get("stack")
                hits = stack if isinstance(stack, list) else []
            section = {
                "available": adapter.get("available"),
                "probed": adapter.get("probed"),
                "count": adapter.get("count", len(hits)),
                "hits": hits,
                "warnings": adapter.get("warnings"),
                "truncated": adapter.get("truncated"),
            }
            if framework == "uitoolkit":
                section["yInverted"] = probe.get("uiToolkitYInverted", True)
                section["panelScreen"] = probe.get("uiToolkitScreen")
            lines.extend(
                format_framework_probe_section_markdown(
                    title,
                    section,
                    include_panel_screen=framework == "uitoolkit",
                    legacy=True,
                )
            )
            lines.append("")

    warnings = result.get("warnings")
    if isinstance(warnings, list):
        warning_lines = [str(warning) for warning in warnings if isinstance(warning, str) and warning]
        if warning_lines:
            lines.append("### Warnings")
            lines.extend(f"- {warning}" for warning in warning_lines)

    return "\n".join(line for line in lines if line is not None).rstrip()


def format_runtime_ui_probe_markdown(result: dict[str, Any]) -> str:
    lines: list[str] = ["## Runtime UI probe", ""]

    page = result.get("page")
    total_pages = result.get("totalPages")
    if page is not None and total_pages is not None:
        lines.append(f"page:{page}/{total_pages}")
        lines.append("")

    runtime_available = result.get("runtimeAvailable")
    truncated = result.get("truncated")
    total_hits = result.get("totalHits")
    meta_parts: list[str] = []
    if runtime_available is not None:
        meta_parts.append(f"**Runtime available:** {'yes' if runtime_available else 'no'}")
    if total_hits is not None:
        meta_parts.append(f"**Total hits:** {total_hits}")
    if truncated is True:
        meta_parts.append("**Truncated:** yes")
    max_rows = result.get("maxRows")
    if max_rows is not None:
        meta_parts.append(f"**Max rows:** {max_rows}")
    if meta_parts:
        lines.append(" · ".join(meta_parts))
        lines.append("")

    probe = get_dict(result.get("probe"))
    if probe:
        lines.extend(format_probe_position_markdown(probe))
        lines.append("")

    ugui = get_dict(result.get("ugui"))
    if ugui:
        lines.extend(format_framework_probe_section_markdown("uGUI", ugui, include_panel_screen=False))
        lines.append("")

    uitoolkit = get_dict(result.get("uitoolkit"))
    if uitoolkit:
        lines.extend(format_framework_probe_section_markdown("UI Toolkit", uitoolkit, include_panel_screen=True))
        lines.append("")

    warnings = result.get("warnings")
    if isinstance(warnings, list):
        warning_lines = [str(warning) for warning in warnings if isinstance(warning, str) and warning]
        if warning_lines:
            lines.append("### Warnings")
            lines.extend(f"- {warning}" for warning in warning_lines)

    return "\n".join(line for line in lines if line is not None).rstrip()


def format_runtime_ui_probe_markdown_legacy(result: dict[str, Any]) -> str:
    lines: list[str] = ["## Runtime UI probe", ""]

    coord = get_dict(result.get("coordinateConvention"))
    if not coord and isinstance(result.get("input"), dict):
        input_row = get_dict(result.get("input"))
        coord = {
            "origin": input_row.get("origin", "bottom-left"),
            "normalizedPosition": input_row.get("normalizedPosition"),
            "screenPosition": input_row.get("screenPosition"),
            "screenSize": result.get("screenSize"),
        }
        if result.get("uiToolkitYInverted") is True:
            coord["uiToolkitYInverted"] = True
            coord["uiToolkitScreenPosition"] = result.get("uiToolkitScreenPosition")
    if coord:
        lines.extend(format_probe_position_markdown(coord, legacy=True))
        lines.append("")

    extension_id = result.get("extensionId")
    framework_name = "UI Toolkit" if extension_id == "chievfx.uitoolkit" else "uGUI"
    hits = result.get("stack") if isinstance(result.get("stack"), list) else []
    section = {
        "probed": result.get("runtimeAvailable"),
        "count": result.get("count", len(hits)),
        "hits": hits,
    }
    lines.extend(
        format_framework_probe_section_markdown(
            framework_name,
            section,
            include_panel_screen=extension_id == "chievfx.uitoolkit",
            legacy=True,
        )
    )
    lines.append("")

    warnings = result.get("warnings")
    if isinstance(warnings, list):
        warning_lines = [str(warning) for warning in warnings if isinstance(warning, str) and warning]
        if warning_lines:
            lines.append("### Warnings")
            lines.extend(f"- {warning}" for warning in warning_lines)

    return "\n".join(line for line in lines if line is not None).rstrip()


def format_probe_position_markdown(
    probe: dict[str, Any],
    *,
    legacy: bool = False,
    include_ui_toolkit_coords: bool = False,
) -> list[str]:
    parts: list[str] = []

    origin = probe.get("origin")
    if origin:
        parts.append(f"origin {origin}")

    normalized = probe.get("normalized") if not legacy else probe.get("normalizedPosition")
    normalized_text = format_vector2_fixed(normalized)
    if normalized_text:
        parts.append(f"normalized {normalized_text}")

    screen = probe.get("screen") if not legacy else probe.get("screenPosition")
    screen_text = format_vector2_fixed(screen)
    if screen_text:
        parts.append(f"screen {screen_text}")

    screen_size = probe.get("screenSize")
    screen_size_text = format_vector2_fixed(screen_size)
    if screen_size_text:
        parts.append(f"screen size {screen_size_text}")

    if include_ui_toolkit_coords or (legacy and probe.get("uiToolkitYInverted") is True):
        if probe.get("uiToolkitYInverted") is True:
            parts.append("UITK Y inverted yes")
        panel_text = format_vector2_fixed(probe.get("uiToolkitScreen"))
        if panel_text:
            parts.append(f"UITK screen {panel_text}")

    if not parts:
        return []

    return ["### Probe position", "", " · ".join(parts)]


def format_framework_probe_section_markdown(
    title: str,
    section: dict[str, Any],
    *,
    include_panel_screen: bool,
    legacy: bool = False,
) -> list[str]:
    lines = [f"### {title}", ""]

    header_parts: list[str] = []
    if "available" in section:
        header_parts.append(f"**Available:** {'yes' if section.get('available') else 'no'}")
    if "probed" in section:
        header_parts.append(f"**Probed:** {'yes' if section.get('probed') else 'no'}")
    if include_panel_screen and section.get("yInverted") is True:
        header_parts.append("**Y inverted:** yes")
        panel_text = format_vector2_fixed(section.get("panelScreen"))
        if panel_text:
            header_parts.append(f"**Panel screen:** {panel_text}")
    count = section.get("count")
    total_hits = section.get("totalHits")
    if total_hits is not None:
        header_parts.append(f"**Total hits:** {total_hits}")
    if count is not None:
        header_parts.append(f"**Hits on page:** {count}")
    if section.get("truncated") is True:
        header_parts.append("**Truncated:** yes")
    if header_parts:
        lines.append(" · ".join(header_parts))
        lines.append("")

    section_warnings = section.get("warnings")
    if isinstance(section_warnings, list):
        warning_lines = [str(warning) for warning in section_warnings if isinstance(warning, str) and warning]
        if warning_lines:
            lines.append("**Section warnings:** " + "; ".join(warning_lines))
            lines.append("")

    hits = section.get("hits")
    if not isinstance(hits, list) or not hits:
        lines.append("_No hits._")
        return lines

    rows: list[tuple[str, str, str, str]] = []
    for index, hit in enumerate(hits):
        if not isinstance(hit, dict):
            continue
        row_index = hit.get("i", index)
        path = str(hit.get("path") or "—")
        hit_type = format_probe_hit_type(hit)
        details = format_probe_hit_details(hit, legacy=legacy)
        rows.append((str(row_index), f"`{path}`", hit_type, details))

    lines.extend(format_markdown_table(["#", "Path", "Type", "Details"], rows))
    return lines


def format_probe_hit_type(hit: dict[str, Any]) -> str:
    hit_type = hit.get("type") or hit.get("typeName")
    if hit_type not in (None, ""):
        return str(hit_type)

    controls = hit.get("controls")
    if isinstance(controls, list) and controls:
        first_control = controls[0]
        if first_control not in (None, ""):
            return str(first_control)

    return "—"


def format_probe_hit_details(hit: dict[str, Any], *, legacy: bool = False) -> str:
    parts: list[str] = []

    text = hit.get("text")
    if text not in (None, ""):
        parts.append(f'text: "{text}"')

    value = hit.get("value")
    if value not in (None, ""):
        parts.append(f"value: {format_toon_atom(value)}")

    controls = hit.get("controls")
    if isinstance(controls, list) and controls:
        control_names = ", ".join(str(control) for control in controls if control)
        if control_names:
            parts.append(f"controls: {control_names}")

    flags: list[str] = []
    for key, label in (
        ("interactable", "interactable"),
        ("raycastTarget", "raycast"),
        ("enabled", "enabled"),
        ("focusable", "focusable"),
    ):
        value = hit.get(key)
        if value is False:
            flags.append(f"not {label}")
        elif value is True:
            flags.append(label)

    picking_mode = hit.get("pickingMode")
    if picking_mode not in (None, "", "Position"):
        flags.append(f"picking:{picking_mode}")

    handler_path = hit.get("handlerPath")
    if handler_path:
        flags.append(f"handler `{handler_path}`")

    sorting_order = hit.get("sortingOrder")
    if sorting_order not in (None, 0):
        flags.append(f"sort {sorting_order}")

    bound = get_dict(hit.get("bound"))
    if not bound and legacy:
        bound = get_dict(hit.get("worldBound"))
    bound_text = format_bound_fixed(bound)
    if bound_text:
        flags.append(f"bound {bound_text}")

    if flags:
        parts.extend(flags)

    return " · ".join(parts) if parts else "—"


def format_markdown_table(headers: list[str], rows: list[tuple[str, ...]]) -> list[str]:
    if not rows:
        return []

    lines = [
        "| " + " | ".join(headers) + " |",
        "| " + " | ".join("---" for _ in headers) + " |",
    ]
    for row in rows:
        lines.append("| " + " | ".join(str(cell) for cell in row) + " |")
    return lines


def format_vector2_fixed(value: Any) -> str:
    if not isinstance(value, dict):
        return ""
    x = value.get("x")
    y = value.get("y")
    if not isinstance(x, (int, float)) or not isinstance(y, (int, float)):
        return ""
    return f"{format_fixed2(float(x))}, {format_fixed2(float(y))}"


def format_bound_fixed(bound: dict[str, Any]) -> str:
    x = bound.get("x")
    y = bound.get("y")
    width = bound.get("width")
    height = bound.get("height")
    if not all(isinstance(value, (int, float)) for value in (x, y, width, height)):
        return ""
    return (
        f"{format_fixed2(float(x))},{format_fixed2(float(y))} "
        f"{format_fixed2(float(width))}x{format_fixed2(float(height))}"
    )


def format_fixed2(value: float) -> str:
    return f"{value:.2f}"


def format_uitoolkit_runtime_interact_text(result: dict[str, Any]) -> str:
    parts = [
        f"action:{format_toon_atom(result.get('action'))}",
        f"dryRun:{format_toon_atom(result.get('dryRun'))}",
    ]
    resolved_by = result.get("resolvedBy")
    if resolved_by:
        parts.append(f"resolved:{format_toon_atom(resolved_by)}")

    target = result.get("target")
    target_text = format_visual_element_brief(target)
    if target_text:
        parts.append(f"target:{target_text}")

    input_text = format_vector2_pair(get_dict(result.get("input")).get("screenPosition"))
    if input_text:
        parts.append(f"pos:{input_text}")

    panel_text = format_vector2_pair(result.get("panelPosition"))
    if panel_text:
        parts.append(f"panel:{panel_text}")

    lines = [" ".join(parts)]

    events = result.get("dispatchedEvents")
    if isinstance(events, list) and events:
        lines.append("events:" + ",".join(str(event) for event in events if event))

    plan = get_dict(result.get("plan"))
    delta_text = format_vector2_pair(plan.get("delta"))
    if delta_text:
        steps = plan.get("steps")
        lines.append(f"drag:{delta_text}" + (f" steps:{format_toon_atom(steps)}" if steps is not None else ""))

    value_text = format_interact_state_change(result)
    if value_text:
        lines.append(value_text)

    focus_text = format_focus_change(result)
    if focus_text:
        lines.append(focus_text)

    warnings = result.get("warnings")
    if isinstance(warnings, list):
        for warning in warnings:
            if isinstance(warning, str) and warning:
                lines.append(f"! {warning}")

    return "\n".join(lines)


def format_visual_element_brief(value: Any) -> str:
    if not isinstance(value, dict):
        return ""

    name = value.get("name")
    type_name = value.get("typeName") or value.get("type")
    text = value.get("text")
    label = f"#{name}" if isinstance(name, str) and name else str(type_name or "VisualElement")
    if type_name and type_name != label:
        label += f"[{type_name}]"
    if isinstance(text, str) and text:
        label += f' text:"{truncate_inline(text, 60)}"'
    return label


def format_interact_state_change(result: dict[str, Any]) -> str:
    before = get_dict(result.get("targetStateBefore"))
    after = get_dict(result.get("targetStateAfter"))
    before_value = before.get("value")
    after_value = after.get("value")
    if before_value is None and after_value is None:
        return ""
    if before_value == after_value:
        return f"value:{format_toon_atom(after_value)}"
    return f"value:{format_toon_atom(before_value)}->{format_toon_atom(after_value)}"


def format_focus_change(result: dict[str, Any]) -> str:
    before = get_dict(result.get("targetStateBefore")).get("focused")
    after = get_dict(result.get("targetStateAfter")).get("focused")
    if before is None and after is None:
        return ""
    if before == after:
        return f"focused:{format_toon_atom(after)}"
    return f"focused:{format_toon_atom(before)}->{format_toon_atom(after)}"


def get_dict(value: Any) -> dict[str, Any]:
    return value if isinstance(value, dict) else {}


def truncate_inline(value: str, limit: int) -> str:
    if len(value) <= limit:
        return value
    return value[: max(0, limit - 3)] + "..."


def format_vector2_pair(value: Any) -> str:
    if not isinstance(value, dict):
        return ""
    x = value.get("x")
    y = value.get("y")
    if not isinstance(x, (int, float)) or not isinstance(y, (int, float)):
        return ""
    return f"{format_number_compact(x)},{format_number_compact(y)}"


def format_number_compact(value: int | float) -> str:
    if isinstance(value, float) and value.is_integer():
        return str(int(value))
    return f"{value:g}"
