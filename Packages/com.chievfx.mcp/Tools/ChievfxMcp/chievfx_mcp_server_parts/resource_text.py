# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

def resource_guide_text() -> str:
    lines = [
        "ChievFX MCP resources v2",
        "Guide covers enabled v2 GameObject, AssetDatabase, and scene-usage resources for this project.",
        "Static resource and template lists match resources/list and resources/templates/list for the current selection.",
        "",
        "Static resources:",
    ]

    resource_descriptors = sorted(enabled_resources(), key=lambda item: item.get("uri", ""))
    if resource_descriptors:
        lines.extend(format_resource_for_initialize_instructions(descriptor) for descriptor in resource_descriptors)
    else:
        lines.append("- (none enabled)")

    lines.extend(["", "Templates:"])

    template_descriptors = sorted(enabled_resource_templates(), key=lambda item: item.get("uriTemplate", ""))
    if template_descriptors:
        lines.extend(
            format_resource_template_for_initialize_instructions(descriptor) for descriptor in template_descriptors
        )
    else:
        lines.append("- (none enabled)")

    lines.extend(
        [
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
    return "\n".join(lines)


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


def truncate_resource_text(text: str) -> str:
    if len(text) <= MAX_RESOURCE_TEXT_CHARS:
        return text

    marker = f"\ntruncatedByMcpServer:true\nmaxChars:{MAX_RESOURCE_TEXT_CHARS}"
    keep = max(0, MAX_RESOURCE_TEXT_CHARS - len(marker))
    return text[:keep].rstrip() + marker
