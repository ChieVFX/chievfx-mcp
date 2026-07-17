# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

from __future__ import annotations

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
