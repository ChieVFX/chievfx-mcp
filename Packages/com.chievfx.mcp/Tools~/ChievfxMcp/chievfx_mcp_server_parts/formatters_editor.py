# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

def format_reflection_method_find_text(result: dict[str, Any]) -> str:
    methods = result.get("methods")
    if not isinstance(methods, list):
        methods = []

    header_parts = [
        f"count:{result.get('count', len(methods))}",
    ]
    for key in ("page", "pageSize", "hasMore", "nextPage", "truncated"):
        value = result.get(key)
        if not should_omit_toon_value(value):
            header_parts.append(f"{key}:{format_toon_atom(value)}")

    lines = [" ".join(header_parts)]
    if methods:
        lines.append(f"methods[{len(methods)}]:")
        for method in methods:
            if isinstance(method, dict):
                lines.append(format_reflection_method_row(method))
    return "\n".join(lines)


def format_reflection_method_row(method: dict[str, Any]) -> str:
    index = method.get("index")
    namespace = str(method.get("ns") or "")
    type_name = str(method.get("type") or "")
    full_type = ".".join(part for part in (namespace, type_name) if part)
    signature = method.get("signature")
    if not isinstance(signature, str) or not signature:
        signature = build_reflection_signature(method)
    target = ".".join(part for part in (full_type, signature) if part)
    return_type = compact_reflection_type_name(method.get("return"))
    qualifiers = " ".join(
        part
        for part in (
            str(method.get("visibility") or ""),
            "static" if method.get("static") else "instance",
        )
        if part
    )
    suffix = " ".join(part for part in (f"-> {return_type}" if return_type else "", qualifiers) if part)
    prefix = str(index) if isinstance(index, int) else "-"
    return f"{prefix} {target} {suffix}".rstrip()


def build_reflection_signature(method: dict[str, Any]) -> str:
    method_name = str(method.get("method") or "")
    params = method.get("params")
    if not isinstance(params, list):
        params = []
    param_text = ", ".join(format_reflection_parameter(param) for param in params if isinstance(param, dict))
    return f"{method_name}({param_text})"


def format_reflection_method_find_single_text(result: dict[str, Any]) -> str:
    method = result.get("method")
    if not isinstance(method, dict):
        return to_toon(result)

    lines = [
        f"index:{result.get('index', method.get('index', 0))} page:{result.get('page', 1)} pageSize:{result.get('pageSize', 1)}",
        format_reflection_method_row(method),
    ]
    scalar_keys = ("ns", "type", "method", "signature", "return", "static", "visibility")
    for key in scalar_keys:
        value = method.get(key)
        if not should_omit_toon_value(value):
            lines.append(f"{key}:{format_toon_atom(value)}")

    params = method.get("params")
    if isinstance(params, list):
        lines.append(f"params[{len(params)}]:")
        for param in params:
            if isinstance(param, dict):
                lines.append(f"- {format_reflection_parameter(param)}")

    call_filter = method.get("callFilter")
    if isinstance(call_filter, dict):
        lines.append("callFilter:")
        for key in ("namespace", "typeName", "methodName"):
            value = call_filter.get(key)
            if not should_omit_toon_value(value):
                lines.append(f"  {key}:{format_toon_atom(value)}")
        input_parameters = call_filter.get("inputParameters")
        if isinstance(input_parameters, list):
            lines.append(f"  inputParameters[{len(input_parameters)}]:")
            for parameter in input_parameters:
                if isinstance(parameter, dict):
                    type_name = parameter.get("typeName")
                    if not should_omit_toon_value(type_name):
                        lines.append(f"  - typeName:{format_toon_atom(type_name)}")

    return "\n".join(lines)


def format_editor_window_list_text(result: dict[str, Any]) -> str:
    windows = result.get("windows")
    if not isinstance(windows, list):
        windows = []

    header_parts = [
        f"count:{result.get('count', len(windows))}",
        f"matched:{result.get('matched', len(windows))}",
    ]
    for key in ("truncated", "focusedInstanceId", "mouseOverInstanceId"):
        value = result.get(key)
        if not should_omit_toon_value(value) and value != 0:
            header_parts.append(f"{key}:{format_toon_atom(value)}")

    lines = [" ".join(header_parts)]
    if windows:
        lines.append(f"windows[{len(windows)}]:")
        for window in windows:
            if isinstance(window, dict):
                lines.append(format_editor_window_row(window))

    diagnostics = result.get("diagnostics")
    if isinstance(diagnostics, list) and diagnostics:
        lines.append(f"diagnostics[{len(diagnostics)}]:")
        for diagnostic in diagnostics:
            lines.append(f"- {format_toon_atom(diagnostic)}")

    return "\n".join(lines)


def format_editor_window_row(window: dict[str, Any]) -> str:
    parts = [
        f"id:{format_toon_atom(window.get('instanceId'))}",
        f"title:{format_toon_atom(window.get('title'))}",
        f"type:{format_toon_atom(window.get('typeName'))}",
    ]
    full_type = window.get("fullTypeName")
    type_name = window.get("typeName")
    if isinstance(full_type, str) and full_type and full_type != type_name:
        parts.append(f"full:{format_toon_atom(full_type)}")
    for key in ("focused", "mouseOver", "selected", "docked", "floating"):
        if window.get(key) is True:
            parts.append(key)
    tab_index = window.get("tabIndex")
    tab_count = window.get("tabCount")
    selected_tab_index = window.get("selectedTabIndex")
    if isinstance(tab_index, int) and isinstance(tab_count, int) and tab_count > 1:
        parts.append(f"tab:{tab_index}/{tab_count}")
    if isinstance(selected_tab_index, int) and selected_tab_index >= 0 and selected_tab_index != tab_index:
        parts.append(f"selectedTab:{selected_tab_index}")
    host_id = window.get("hostViewInstanceId")
    if isinstance(host_id, int) and host_id != 0:
        parts.append(f"host:{host_id}")
    return "- " + " ".join(parts)


def format_editor_window_action_text(result: dict[str, Any]) -> str:
    parts = []
    action = result.get("action")
    if not should_omit_toon_value(action):
        parts.append(f"action:{format_toon_atom(action)}")
    success = result.get("success")
    if success is True:
        parts.append("success:true")
    elif success is False:
        parts.append("success:false")

    window = result.get("window")
    if isinstance(window, dict):
        parts.extend(format_editor_window_action_parts(window))

    lines = [" ".join(parts) if parts else to_toon(result)]
    diagnostics = result.get("diagnostics")
    if isinstance(diagnostics, list) and diagnostics:
        lines.append(f"diagnostics[{len(diagnostics)}]:")
        for diagnostic in diagnostics:
            lines.append(f"- {format_toon_atom(diagnostic)}")
    return "\n".join(lines)


def format_editor_window_action_parts(window: dict[str, Any]) -> list[str]:
    parts = []
    for key, label in (
        ("instanceId", "id"),
        ("title", "title"),
        ("typeName", "type"),
    ):
        value = window.get(key)
        if not should_omit_toon_value(value):
            parts.append(f"{label}:{format_toon_atom(value)}")
    for key in ("focused", "selected", "docked", "floating"):
        if window.get(key) is True:
            parts.append(key)
    tab_index = window.get("tabIndex")
    tab_count = window.get("tabCount")
    if isinstance(tab_index, int) and isinstance(tab_count, int) and tab_count > 1:
        parts.append(f"tab:{tab_index}/{tab_count}")
    selected_tab_index = window.get("selectedTabIndex")
    if isinstance(selected_tab_index, int) and isinstance(tab_index, int) and selected_tab_index != tab_index:
        parts.append(f"selectedTab:{selected_tab_index}")
    return parts


def format_package_list_text(result: dict[str, Any]) -> str:
    packages = result.get("packages")
    if not isinstance(packages, list):
        packages = []

    header_parts = [f"packages:{result.get('count', len(packages))}"]
    source_filter = result.get("sourceFilter")
    if not should_omit_toon_value(source_filter) and source_filter != "All":
        header_parts.append(f"source:{format_toon_atom(source_filter)}")
    if result.get("directDependenciesOnly") is True:
        header_parts.append("directOnly")
    if result.get("offlineMode") is True:
        header_parts.append("offline")

    lines = [" ".join(header_parts)]
    for package in packages:
        if isinstance(package, dict):
            lines.append(format_package_list_row(package))
    return "\n".join(lines)


def format_package_list_row(package: dict[str, Any]) -> str:
    name = str(package.get("name") or "<unknown>")
    version = package.get("version")
    parts = [f"{name}@{format_toon_atom(version)}" if not should_omit_toon_value(version) else name]
    display_name = package.get("displayName")
    if isinstance(display_name, str) and display_name and display_name != name:
        parts.append(f"name:{format_toon_atom(display_name)}")
    source = package.get("source")
    if not should_omit_toon_value(source):
        parts.append(f"src:{format_toon_atom(source)}")
    if package.get("isDirectDependency") is True:
        parts.append("direct")
    elif package.get("isDirectDependency") is False:
        parts.append("transitive")
    manifest_version = package.get("manifestVersion")
    if should_show_package_manifest_version(package, manifest_version):
        parts.append(f"manifest:{format_toon_atom(manifest_version)}")
    return "- " + " ".join(str(part) for part in parts)


def should_show_package_manifest_version(package: dict[str, Any], manifest_version: Any) -> bool:
    if should_omit_toon_value(manifest_version):
        return False
    version = package.get("version")
    source = str(package.get("source") or "")
    if source in {"Git", "Local", "Embedded", "LocalTarball"}:
        return True
    return manifest_version != version


def format_package_search_text(result: dict[str, Any]) -> str:
    results = result.get("results")
    if not isinstance(results, list):
        results = []

    header_parts = [
        f"query:{format_toon_atom(result.get('query'))}",
        f"results:{result.get('count', len(results))}",
    ]
    if result.get("truncated") is True:
        header_parts.append("truncated")
    if result.get("offlineMode") is True:
        header_parts.append("offline")
    registry_search_error = result.get("registrySearchError")
    if not should_omit_toon_value(registry_search_error):
        header_parts.append(f"registryError:{format_toon_atom(registry_search_error)}")

    lines = [" ".join(header_parts)]
    for package in results:
        if isinstance(package, dict):
            lines.append(format_package_search_row(package))
    return "\n".join(lines)


def format_package_search_row(package: dict[str, Any]) -> str:
    name = str(package.get("name") or "<unknown>")
    parts = [name]
    display_name = package.get("displayName")
    if isinstance(display_name, str) and display_name and display_name != name:
        parts.append(f"name:{format_toon_atom(display_name)}")
    latest_version = package.get("latestVersion")
    if not should_omit_toon_value(latest_version):
        parts.append(f"latest:{format_toon_atom(latest_version)}")
    if package.get("isInstalled") is True:
        installed_version = package.get("installedVersion")
        installed_text = format_toon_atom(installed_version) if not should_omit_toon_value(installed_version) else "true"
        installed_source = package.get("installedSource")
        if not should_omit_toon_value(installed_source):
            installed_text = f"{installed_text}/{format_toon_atom(installed_source)}"
        parts.append(f"installed:{installed_text}")
    else:
        parts.append("notInstalled")
    return "- " + " ".join(str(part) for part in parts)


def format_package_mutation_text(result: dict[str, Any]) -> str:
    operation = result.get("operation")
    package_id = result.get("packageId")
    header_parts = []
    if not should_omit_toon_value(operation):
        header_parts.append(f"operation:{format_toon_atom(operation)}")
    if not should_omit_toon_value(package_id):
        header_parts.append(f"packageId:{format_toon_atom(package_id)}")
    if result.get("completed") is True:
        header_parts.append("completed")
    elif result.get("completed") is False:
        header_parts.append("pending")
    if result.get("restoredAfterDomainReload") is True:
        header_parts.append("restoredAfterDomainReload")
    verification = result.get("verification")
    if not should_omit_toon_value(verification):
        header_parts.append(f"verification:{format_toon_atom(verification)}")

    lines = [" ".join(header_parts) if header_parts else "package-operation"]
    package = result.get("package")
    if isinstance(package, dict):
        lines.append("package: " + format_package_list_row(package)[2:])

    manifest_changes = result.get("manifestChanges")
    if isinstance(manifest_changes, list) and manifest_changes:
        lines.append(f"changes:{len(manifest_changes)}")
        for change in manifest_changes:
            if isinstance(change, dict):
                lines.append(format_package_manifest_change_row(change))
    return "\n".join(lines)


def format_package_manifest_change_row(change: dict[str, Any]) -> str:
    name = str(change.get("name") or "<unknown>")
    parts = [name]
    change_kind = change.get("change")
    if not should_omit_toon_value(change_kind):
        parts.append(f"change:{format_toon_atom(change_kind)}")
    version = change.get("version")
    if not should_omit_toon_value(version):
        parts.append(f"version:{format_toon_atom(version)}")
    return "- " + " ".join(str(part) for part in parts)


def format_frame_debugger_control_text(result: dict[str, Any]) -> str:
    parts = ["success:true" if result.get("success") is True else "success:false"]
    append_frame_debugger_state_parts(parts, result.get("frameDebugger"))
    append_diagnostics_lines = format_diagnostics_lines(result.get("diagnostics"))
    lines = [" ".join(parts)]
    lines.extend(append_diagnostics_lines)
    return "\n".join(lines)


def format_frame_debugger_events_list_text(result: dict[str, Any]) -> str:
    events = result.get("events")
    if not isinstance(events, list):
        events = []

    header_parts = [f"events:{result.get('count', len(events))}"]
    total_events = result.get("totalEvents")
    if not should_omit_toon_value(total_events):
        header_parts.append(f"total:{format_toon_atom(total_events)}")
    start_index = result.get("startIndex")
    if start_index not in (None, 0):
        header_parts.append(f"start:{format_toon_atom(start_index)}")
    if result.get("truncated") is True:
        header_parts.append("truncated")
    append_frame_debugger_state_parts(header_parts, result.get("frameDebugger"))

    lines = [" ".join(header_parts)]
    for frame_event in events:
        if isinstance(frame_event, dict):
            lines.append(format_frame_debugger_event_row(frame_event))
    lines.extend(format_diagnostics_lines(result.get("diagnostics")))
    return "\n".join(lines)


def format_frame_debugger_groups_list_text(result: dict[str, Any]) -> str:
    groups = result.get("groups")
    if not isinstance(groups, list):
        groups = []

    header_parts = [f"groups:{result.get('count', len(groups))}"]
    total_events = result.get("totalEvents")
    if not should_omit_toon_value(total_events):
        header_parts.append(f"events:{format_toon_atom(total_events)}")
    append_frame_debugger_state_parts(header_parts, result.get("frameDebugger"))

    lines = [" ".join(header_parts)]
    for group in groups:
        if isinstance(group, dict):
            lines.append(format_frame_debugger_group_row(group))
    lines.extend(format_diagnostics_lines(result.get("diagnostics")))
    return "\n".join(lines)


def format_frame_debugger_group_events_list_text(result: dict[str, Any]) -> str:
    events = result.get("events")
    if not isinstance(events, list):
        events = []

    group = result.get("group")
    group_text = format_frame_debugger_group_ref(group)
    header_parts = [group_text, f"drawcalls:{result.get('count', len(events))}"]
    total_events = result.get("totalEvents")
    if not should_omit_toon_value(total_events):
        header_parts.append(f"total:{format_toon_atom(total_events)}")
    start_index = result.get("startIndex")
    if start_index not in (None, 0):
        header_parts.append(f"start:{format_toon_atom(start_index)}")
    if result.get("truncated") is True:
        header_parts.append("truncated")

    lines = [" ".join(header_parts)]
    for frame_event in events:
        if isinstance(frame_event, dict):
            lines.append(format_frame_debugger_event_row(frame_event, use_drawcall_index=True))
    lines.extend(format_diagnostics_lines(result.get("diagnostics")))
    return "\n".join(lines)


def format_frame_debugger_drawcall_get_text(result: dict[str, Any]) -> str:
    lines = []
    group = result.get("group")
    if isinstance(group, dict):
        lines.append(format_frame_debugger_group_row(group))
    frame_event = result.get("frameEvent")
    if isinstance(frame_event, dict):
        lines.append(format_frame_debugger_event_row(frame_event, use_drawcall_index=True))
    else:
        lines.append("drawcall:<missing>")
    lines.extend(format_diagnostics_lines(result.get("diagnostics")))
    return "\n".join(lines)


def format_frame_debugger_event_get_text(result: dict[str, Any]) -> str:
    frame_event = result.get("frameEvent")
    lines = []
    if isinstance(frame_event, dict):
        lines.append(format_frame_debugger_event_row(frame_event))
    else:
        lines.append("event:<missing>")
    diagnostics = format_diagnostics_lines(result.get("diagnostics"))
    lines.extend(diagnostics)
    return "\n".join(lines)


def format_frame_debugger_group_row(group: dict[str, Any]) -> str:
    parts = [f"g#{format_toon_atom(group.get('index'))}"]
    name = group.get("name")
    if not should_omit_toon_value(name):
        parts.append(f"name:{format_toon_atom(name)}")
    event_count = group.get("eventCount")
    if not should_omit_toon_value(event_count):
        parts.append(f"events:{format_toon_atom(event_count)}")
    first_event = group.get("firstEventIndex")
    last_event = group.get("lastEventIndex")
    if not should_omit_toon_value(first_event) and not should_omit_toon_value(last_event):
        parts.append(f"range:{format_toon_atom(first_event)}-{format_toon_atom(last_event)}")
    return "- " + " ".join(str(part) for part in parts)


def format_frame_debugger_group_ref(group: Any) -> str:
    if not isinstance(group, dict):
        return "group:<missing>"
    index = group.get("index")
    name = group.get("name")
    if should_omit_toon_value(name):
        return f"group:{format_toon_atom(index)}"
    return f"group:{format_toon_atom(index)} name:{format_toon_atom(name)}"


def append_frame_debugger_state_parts(parts: list[str], state: Any) -> None:
    if not isinstance(state, dict):
        return
    enabled = state.get("enabled")
    if enabled is True:
        parts.append("enabled:true")
    elif enabled is False:
        parts.append("enabled:false")
    for key in ("eventCount", "currentEventLimit", "selectedEventIndex"):
        value = state.get(key)
        if not should_omit_toon_value(value):
            parts.append(f"{key}:{format_toon_atom(value)}")


def format_frame_debugger_event_row(frame_event: dict[str, Any], use_drawcall_index: bool = False) -> str:
    if use_drawcall_index and not should_omit_toon_value(frame_event.get("drawCallIndex")):
        parts = [
            f"g#{format_toon_atom(frame_event.get('groupIndex'))}",
            f"d#{format_toon_atom(frame_event.get('drawCallIndex'))}",
            f"event:{format_toon_atom(frame_event.get('index'))}",
        ]
    else:
        parts = [f"#{format_toon_atom(frame_event.get('index'))}"]
    event_type = frame_event.get("type")
    if not should_omit_toon_value(event_type):
        parts.append(f"type:{format_toon_atom(event_type)}")
    name = frame_event.get("name")
    if not should_omit_toon_value(name):
        parts.append(f"name:{format_toon_atom(name)}")
    object_name = frame_event.get("objectName")
    if not should_omit_toon_value(object_name):
        object_text = format_toon_atom(object_name)
        object_type = frame_event.get("objectType")
        if not should_omit_toon_value(object_type):
            object_text = f"{object_text}/{format_toon_atom(object_type)}"
        parts.append(f"obj:{object_text}")
    for key, label in (
        ("shader", "shader"),
        ("pass", "pass"),
        ("passLightMode", "lightMode"),
        ("meshName", "mesh"),
        ("drawCalls", "draws"),
        ("vertices", "verts"),
        ("indices", "indices"),
        ("instances", "instances"),
        ("renderTarget", "rt"),
        ("batchBreakReason", "batch"),
    ):
        value = frame_event.get(key)
        if not should_omit_toon_value(value):
            parts.append(f"{label}:{format_toon_atom(value)}")
    return "- " + " ".join(str(part) for part in parts)
