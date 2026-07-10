# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

CAMERA_RESOURCE_ROW_KEYS: dict[str, str] = {
    "cameras-cinemachine-cameras": "cameras",
    "cameras-cinemachine-brains": "brains",
    "cameras-cinemachine-sequencers": "sequencers",
    "cameras-cinemachine-splines-dolly": "splinesDollies",
    "cameras-cinemachine-input-axis-controllers": "inputAxisControllers",
    "cameras-cinemachine-blender-settings": "assets",
    "cameras-cinemachine-impulse": "sources",
    "cameras-cinemachine-confiner-2d": "confiners",
    "cameras-cinemachine-confiner-3d": "confiners",
    "cameras-timeline-directors": "directors",
    "cameras-timeline-assets": "assets",
}


CAMERA_TOOL_NAMES = {
    "brain-ensure",
    "cinemachine-blender-settings-set",
    "cinemachine-confiner-set",
    "cinemachine-create",
    "cinemachine-sequencer-create",
    "cinemachine-set",
    "cinemachine-spline-dolly-set",
    "timeline-director-create",
    "timeline-director-preview",
    "timeline-shot-sequence-create",
}


def format_cameras_extension_resource_text(resource_id: str | None, result: dict[str, Any]) -> str:
    if not isinstance(result, dict):
        return to_toon(result)
    if result.get("ok") is False or "error" in result:
        return format_camera_unavailable_text(result)
    if resource_id == "cameras-status":
        return format_camera_status_text(result)
    if resource_id in {"cameras-cinemachine-camera-detail", "cameras-cinemachine-brain-detail", "cameras-cinemachine-sequencer-detail", "cameras-timeline-director-detail"}:
        target = result.get("target")
        return format_camera_entity_row(target) if isinstance(target, dict) else to_toon(result)
    if resource_id == "cameras-timeline-asset-detail":
        asset = result.get("asset")
        return format_timeline_asset_detail_text(asset) if isinstance(asset, dict) else to_toon(result)

    row_key = CAMERA_RESOURCE_ROW_KEYS.get(resource_id or "")
    if row_key is None:
        return to_toon(result)
    lines = [format_camera_inventory_header(result, row_key)]
    rows = result.get(row_key)
    if isinstance(rows, list):
        for row in rows:
            if isinstance(row, dict):
                lines.extend(format_camera_inventory_row_lines(resource_id or "", row))
    lines.extend(format_camera_secondary_sections(resource_id or "", result, row_key))
    return "\n".join(line for line in lines if line)


def format_camera_tool_text(tool_name: str, result: dict[str, Any]) -> str:
    if result.get("ok") is False or "error" in result:
        return format_camera_unavailable_text(result)

    lines: list[str] = []
    header = [tool_name, f"ok:{format_toon_atom(result.get('ok', True))}"]
    if result.get("dryRun") is True:
        header.append("dryRun:true")
    for key in ("action", "wouldCreate", "wouldCreateCamera", "wouldCreateBrain", "wouldCreateAsset", "createdAsset", "addedConfiner", "addedSplineDolly"):
        value = result.get(key)
        if not should_omit_toon_value(value):
            header.append(f"{key}:{format_toon_atom(value)}")
    lines.append(" ".join(header))

    target_label = "director" if tool_name.startswith("timeline-") else "target"
    for label, key in (
        (target_label, "target"),
        ("camera", "camera"),
        ("brain", "brain"),
        ("asset", "asset"),
        ("confiner", "confiner"),
        ("splineDolly", "splineDolly"),
        ("spline", "splineContainer"),
    ):
        row = result.get(key)
        if isinstance(row, dict):
            lines.append(f"{label} {format_camera_tool_row(row)}")

    for key in ("changedFields", "safeSetFields"):
        values = result.get(key)
        if isinstance(values, list) and values:
            lines.append(f"{key}:{','.join(str(value) for value in values if not should_omit_toon_value(value))}")

    clips = result.get("clips")
    target = result.get("target")
    if not isinstance(clips, list) and isinstance(target, dict):
        clips = target.get("clips")
    if isinstance(clips, list) and clips:
        lines.append(f"clips[{len(clips)}]:")
        for clip in clips[:8]:
            if isinstance(clip, dict):
                parts = []
                for key in ("displayName", "start", "duration", "end", "assetType"):
                    value = clip.get(key)
                    if not should_omit_toon_value(value):
                        label = "name" if key == "displayName" else key
                        parts.append(f"{label}:{format_toon_atom(format_short_type(value) if key == 'assetType' else value)}")
                if parts:
                    lines.append("- " + " ".join(parts))

    tracks = result.get("tracks")
    if isinstance(tracks, list) and tracks and not clips:
        lines.append(f"tracks[{len(tracks)}]:")
        for track in tracks[:8]:
            if isinstance(track, dict):
                lines.append("- " + format_camera_tool_row(track))

    for key in ("warnings", "notes", "blendSafeNotes"):
        values = result.get(key)
        if isinstance(values, list) and values:
            prefix = "!" if key == "warnings" else "note"
            for value in values[:6]:
                if not should_omit_toon_value(value):
                    lines.append(f"{prefix} {value}")

    return "\n".join(line for line in lines if line)


def format_camera_tool_row(row: dict[str, Any]) -> str:
    parts: list[str] = []
    for key, label in (
        ("path", "path"),
        ("name", "name"),
        ("instanceId", "id"),
        ("type", "type"),
        ("enabled", "enabled"),
        ("duration", "duration"),
        ("time", "time"),
        ("state", "state"),
        ("clipCount", "clips"),
        ("instructionCount", "instructions"),
        ("fieldOfView", "fov"),
    ):
        value = row.get(key)
        if should_omit_toon_value(value):
            continue
        if key == "type":
            value = format_short_type(value)
        parts.append(f"{label}:{format_toon_atom(value)}")
    for key, label in (("priority", "priority"), ("lens", "lens"), ("asset", "asset"), ("camera", "camera"), ("boundingVolume", "bounds")):
        value = row.get(key)
        if isinstance(value, dict):
            compact = format_camera_nested_value(value)
            if compact:
                parts.append(f"{label}:{compact}")
    return " ".join(parts)


def format_camera_nested_value(value: dict[str, Any]) -> str:
    if not isinstance(value, dict):
        return ""
    if not should_omit_toon_value(value.get("path")):
        return format_camera_text(value.get("path"))
    if not should_omit_toon_value(value.get("name")):
        return format_camera_text(value.get("name"))
    if "fieldOfView" in value:
        return "fov=" + format_toon_atom(value.get("fieldOfView"))
    if "Value" in value:
        enabled = value.get("Enabled")
        prefix = "" if enabled is True else "disabled:"
        return prefix + format_toon_atom(value.get("Value"))
    pairs = []
    for key, nested in value.items():
        if not should_omit_toon_value(nested):
            pairs.append(f"{key}={format_toon_atom(nested)}")
    return ",".join(pairs[:4])


def format_camera_unavailable_text(result: dict[str, Any]) -> str:
    reason = result.get("reason") or result.get("message") or result.get("error")
    status = result.get("status") or result.get("dependency") or {}
    package = status.get("packageName") if isinstance(status, dict) else None
    return " ".join(part for part in [
        "unavailable",
        f"package:{format_toon_atom(package)}" if not should_omit_toon_value(package) else "",
        f"reason:{format_toon_atom(reason)}" if not should_omit_toon_value(reason) else "",
    ] if part)


def format_camera_status_text(result: dict[str, Any]) -> str:
    lines = [
        f"cinemachine:{format_camera_gate(result.get('cinemachine'))} timeline:{format_camera_gate(result.get('timeline'))} "
        f"sequencer:{format_camera_gate(result.get('sequencerCamera'))}"
    ]
    resources = result.get("resources")
    templates = result.get("resourceTemplates")
    tools = result.get("tools")
    lines.append(
        f"resources:{format_toon_atom(len(resources) if isinstance(resources, list) else None)} "
        f"templates:{format_toon_atom(len(templates) if isinstance(templates, list) else None)} "
        f"tools:{format_toon_atom(len(tools) if isinstance(tools, list) else None)}"
    )
    return "\n".join(line for line in lines if line)


def format_camera_gate(value: Any) -> str:
    if not isinstance(value, dict):
        return "unknown"
    if value.get("available") is True:
        version = value.get("packageVersion")
        return "ok" if should_omit_toon_value(version) else f"ok({version})"
    reason = value.get("reason")
    return "no" if should_omit_toon_value(reason) else f"no:{reason}"


def format_camera_inventory_header(result: dict[str, Any], row_key: str) -> str:
    count = result.get("count")
    capped = " capped" if result.get("capped") is True else ""
    parts = [f"{row_key}:{format_toon_atom(count)}{capped}"]
    max_rows = result.get("maxRows")
    if not should_omit_toon_value(max_rows):
        parts.append(f"max:{format_toon_atom(max_rows)}")
    return " ".join(parts)


def format_camera_inventory_row_lines(resource_id: str, row: dict[str, Any]) -> list[str]:
    if resource_id in {"cameras-timeline-assets", "cameras-timeline-asset-detail"}:
        return format_timeline_asset_row_lines(row)
    return [format_camera_entity_row(row)]


def format_camera_entity_row(row: dict[str, Any]) -> str:
    path = format_camera_text(row.get("path") or row.get("name"))
    badges = []
    instance_id = row.get("instanceId")
    if not should_omit_toon_value(instance_id):
        badges.append(f"id:{format_toon_atom(instance_id)}")
    enabled = row.get("enabled")
    if enabled is False:
        badges.append("disabled")
    suffix = f" ({', '.join(badges)})" if badges else ""
    detail = format_camera_detail_bits(row)
    return f"- {path}{suffix}{detail}"


def format_camera_detail_bits(row: dict[str, Any]) -> str:
    bits = []
    for key, label in (
        ("priority", "priority"),
        ("lens", "lens"),
        ("fieldOfView", "fov"),
        ("time", "time"),
        ("duration", "duration"),
        ("state", "state"),
        ("shotCount", "shots"),
        ("instructionCount", "instructions"),
        ("clipCount", "clips"),
        ("trackCount", "tracks"),
        ("defaultBlend", "blend"),
    ):
        value = row.get(key)
        if not should_omit_toon_value(value):
            if isinstance(value, dict):
                compact = format_camera_nested_value(value)
                if compact:
                    if key == "lens":
                        bits.append(compact)
                    else:
                        bits.append(f"{label}:{compact}")
            else:
                bits.append(f"{label}:{format_toon_atom(value)}")
    asset = row.get("asset") or row.get("customBlendsAsset")
    if isinstance(asset, dict):
        asset_path = asset.get("path") or asset.get("name")
        if not should_omit_toon_value(asset_path):
            bits.append(f"asset:{format_camera_text(asset_path)}")
    camera = row.get("camera")
    if isinstance(camera, dict) and not should_omit_toon_value(camera.get("path")):
        bits.append(f"camera:{format_camera_text(camera.get('path'))}")
    detail_uri = row.get("detailUri")
    if not should_omit_toon_value(detail_uri):
        bits.append(f"detail:{format_camera_text(detail_uri)}")
    return "" if not bits else " " + " ".join(bits)


def format_timeline_asset_detail_text(asset: dict[str, Any]) -> str:
    lines = format_timeline_asset_row_lines(asset)
    tracks = asset.get("tracks")
    if isinstance(tracks, list):
        for track in tracks:
            if not isinstance(track, dict):
                continue
            lines.append(
                f"  - track:{format_toon_atom(track.get('name'))} type:{format_short_type(track.get('type'))} "
                f"clips:{format_toon_atom(track.get('clipCount'))}"
            )
    return "\n".join(lines)


def format_timeline_asset_row_lines(asset: dict[str, Any]) -> list[str]:
    path = format_camera_text(asset.get("path") or asset.get("name"))
    guid = format_camera_text(asset.get("guid"))
    clips = asset.get("clips")
    tracks = asset.get("tracks")
    clip_count = len(clips) if isinstance(clips, list) else asset.get("clipCount")
    track_count = len(tracks) if isinstance(tracks, list) else asset.get("trackCount")
    bits = [
        f"guid:{guid}" if guid else "",
        f"tracks:{format_toon_atom(track_count)}" if not should_omit_toon_value(track_count) else "",
        f"clips:{format_toon_atom(clip_count)}" if not should_omit_toon_value(clip_count) else "",
    ]
    detail_uri = asset.get("detailUri")
    if not should_omit_toon_value(detail_uri):
        bits.append(f"detail:{format_camera_text(detail_uri)}")
    return [f"- {path} " + " ".join(bit for bit in bits if bit)]


def format_camera_secondary_sections(resource_id: str, result: dict[str, Any], primary_key: str) -> list[str]:
    lines: list[str] = []
    for key in ("brainCustomBlends", "cameras", "directors", "listeners", "axisOwners", "splineContainers", "obsoleteConfiners"):
        if key == primary_key:
            continue
        rows = result.get(key)
        if not isinstance(rows, list) or not rows:
            continue
        lines.append(f"{key}[{len(rows)}]:")
        for row in rows:
            if isinstance(row, dict):
                lines.append(format_camera_entity_row(row))
    warnings = result.get("warnings")
    if isinstance(warnings, list) and warnings:
        lines.append("warnings:")
        lines.extend(f"- {format_toon_atom(item)}" for item in warnings if not should_omit_toon_value(item))
    return lines


def format_short_type(value: Any) -> str:
    if should_omit_toon_value(value):
        return ""
    return str(value).split(".")[-1]


def format_camera_text(value: Any) -> str:
    if should_omit_toon_value(value):
        return ""
    return str(value)
