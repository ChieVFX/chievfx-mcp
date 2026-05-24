# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

def resource_guide_text() -> str:
    return "\n".join(
        [
            "ChievFX MCP resources v2",
            "Guide covers v2 GameObject, AssetDatabase, and scene-usage resources.",
            "",
            "Static resources:",
            "- chievfx://resources/guide",
            "- chievfx://editor/context",
            "- chievfx://scene/opened",
            "- chievfx://scene/current/hierarchy",
            "- chievfx://scene/current/usage/counts",
            "- chievfx://scene/current/material-profile/summary",
            "",
            "Templates:",
            "- chievfx://scene/{scenePath}/go/{goPath}",
            "- chievfx://scene/{scenePath}/go/{goPath}/component/{componentKey}",
            "- chievfx://scene/current/go/{goPath}",
            "- chievfx://scene/current/go/{goPath}/component/{componentKey}",
        "- chievfx://scene/current/hierarchy/{hierarchyPath}",
            "- chievfx://scene/current/go/name-contains/{text}",
            "- chievfx://scene/current/go/name-pattern/{pattern}",
            "- chievfx://scene/current/go/component/{componentType}",
            "- chievfx://scene/current/go/filter/{filterSpec}",
            "- chievfx://assets/name-contains/{text}",
            "- chievfx://assets/type/{assetType}",
            "- chievfx://assets/label/{label}",
            "- chievfx://assets/filter/{filterSpec}",
            "- chievfx://asset/{guid}",
            "- chievfx://asset/{guid}/id/{localId}",
            "- chievfx://scene/current/material-profile/shader/{shaderKey}",
            "- chievfx://scene/current/material-profile/material/{materialKey}",
            "- chievfx://scene/current/usage/assets/{assetType}",
            "- chievfx://scene/current/usage/asset/{guid}",
            "- chievfx://scene/current/usage/asset/{guid}/id/{localId}",
            "",
            "Encode every scene path, GameObject hierarchy path, component key, and asset filterSpec as one URI segment.",
            "Use percent-encoding with no safe slash: quote(value, safe='').",
            "GameObject paths keep ChievFX grammar: / separator, \\/ literal slash, \\\\ literal backslash, [n] duplicate suffix.",
            "Component keys use simple class names. Duplicate simple names are suffixed 1-based, e.g. BoxCollider.1.",
            "Asset filterSpec uses semicolon key=value clauses: name, type, label, area, folder, limit, subassets.",
            "Asset resources cover persisted AssetDatabase project/package assets, not runtime-only objects.",
            "Current usage resources cover loaded current scene or prefab stage references; runtime-only and built-in objects have no asset GUID.",
            "Material profile resources report exact material/reference counts separately from optional Profiler.GetRuntimeMemorySizeLong estimates.",
            "",
            "Outputs are compact text/plain TOON with readAt metadata, drill-down URIs, truncation flags, and hard caps.",
        ]
    )


def format_scene_opened_resource_text(result: Any) -> str:
    if not isinstance(result, dict):
        return to_toon(result)

    lines = [f"count:{format_toon_atom(result.get('count', 0))}"]
    lines.append("scenes:")

    scenes = result.get("scenes")
    active_scene_path = result.get("activeScenePath")
    if isinstance(scenes, list):
        for scene in scenes:
            if not isinstance(scene, dict):
                continue

            path = scene.get("path")
            if not isinstance(path, str) or not path:
                continue

            encoded_path = urllib.parse.quote(path, safe="/")
            build_index = scene.get("buildIndex", 0)
            root_count = scene.get("rootCount", 0)
            is_loaded = scene.get("isLoaded")
            is_dirty = scene.get("isDirty")
            active_marker = (
                "active "
                if isinstance(active_scene_path, str) and active_scene_path == path
                else ""
            )
            lines.append(
                f"• {active_marker}{encoded_path} "
                f"[build:{format_toon_atom(build_index)} roots:{format_toon_atom(root_count)} "
                f"loaded:{format_toon_atom(is_loaded)} dirty:{format_toon_atom(is_dirty)}]"
            )

    return "\n".join(line for line in lines if line)


def format_resource_text(result: Any) -> str:
    if isinstance(result, str):
        text = result
    else:
        text = to_toon(result)
    return truncate_resource_text(text)


def format_scene_current_hierarchy_resource_text(result: Any) -> str:
    if not isinstance(result, dict):
        return to_toon(result)

    context = result.get("context")
    raw_source = context.get("source") if isinstance(context, dict) else None
    source = "openedPrefab" if raw_source == "prefabStage" else "activeScene"
    lines = [f"context source:{format_toon_atom(source)}"]

    lines.append("depthLimited:true")

    uri = result.get("uri")
    hierarchy_uri_prefix = "chievfx://scene/current/hierarchy/"
    if isinstance(uri, str) and uri.startswith(hierarchy_uri_prefix):
        encoded_root_path = uri[len(hierarchy_uri_prefix) :]
        if encoded_root_path:
            root_path = urllib.parse.unquote(encoded_root_path)
            encoded_path = urllib.parse.quote(root_path, safe="/")
            lines.append(f"root:{encoded_path}")
    else:
        roots = result.get("roots")
        if isinstance(roots, list) and roots:
            root = roots[0]
            if isinstance(root, dict):
                root_path = root.get("path")
                if isinstance(root_path, str) and root_path:
                    lines.append(f"root:{urllib.parse.quote(root_path, safe='/')}")

    roots = result.get("roots")
    is_subtree_request = isinstance(uri, str) and uri.startswith("chievfx://scene/current/hierarchy/")

    if not isinstance(roots, list):
        return truncate_resource_lines(lines, MAX_RESOURCE_TEXT_LINES)

    max_render_depth = 2 if is_subtree_request else 1

    if is_subtree_request:
        if len(roots) != 1 or not isinstance(roots[0], dict):
            return truncate_resource_lines(lines, MAX_RESOURCE_TEXT_LINES)
        first_root = roots[0]
        children = first_root.get("children")
        if not isinstance(children, list):
            return truncate_resource_lines(lines, MAX_RESOURCE_TEXT_LINES)
        for child in children:
            if isinstance(child, dict):
                lines.extend(format_scene_current_hierarchy_row(child, 0, max_render_depth))
        return truncate_resource_lines(lines, MAX_RESOURCE_TEXT_LINES)

    for root in roots:
        if isinstance(root, dict):
            lines.extend(format_scene_current_hierarchy_row(root, 0, max_render_depth))
    return truncate_resource_lines(lines, MAX_RESOURCE_TEXT_LINES)


def truncate_resource_lines(lines: list[str], max_lines: int) -> str:
    if max_lines <= 0:
        return ""
    if len(lines) <= max_lines:
        return "\n".join(line for line in lines if line)

    marker = f"truncatedByMcpServer:true\nmaxLines:{max_lines}"
    visible = lines[: max_lines - 1]
    if not visible:
        return marker

    visible_text = "\n".join(line for line in visible if line)
    return f"{visible_text}\n{marker}"


def format_scene_current_hierarchy_row(game_object: dict[str, Any], depth: int, max_depth: int) -> list[str]:
    indent = "  " * depth
    name = game_object.get("name")
    if should_omit_toon_value(name):
        return []
    else:
        name = str(name)
    safe_name = urllib.parse.quote(name, safe="")

    child_count = game_object.get("childCount")
    suffix = f" [{format_toon_atom(child_count)}]" if isinstance(child_count, int) and child_count > 0 else ""

    line_parts = [f"{indent}• {safe_name}{suffix}"]
    lines = [" ".join(line_parts)]

    if depth >= max_depth:
        return lines

    children = game_object.get("children")
    if isinstance(children, list):
        for child in children:
            if isinstance(child, dict):
                lines.extend(format_scene_current_hierarchy_row(child, depth + 1, max_depth))
    return lines


def truncate_resource_text(text: str) -> str:
    if len(text) <= MAX_RESOURCE_TEXT_CHARS:
        return text

    marker = f"\ntruncatedByMcpServer:true\nmaxChars:{MAX_RESOURCE_TEXT_CHARS}"
    keep = max(0, MAX_RESOURCE_TEXT_CHARS - len(marker))
    return text[:keep].rstrip() + marker
