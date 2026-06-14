# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

def format_asset_find_text(result: dict[str, Any]) -> str:
    assets = result.get("assets")
    if not isinstance(assets, list):
        assets = []

    header_parts = []
    count = result.get("count")
    total_guids = result.get("totalAssetGuids")
    if not should_omit_toon_value(count) and not should_omit_toon_value(total_guids):
        header_parts.append(f"({format_toon_atom(count)} shown, {format_toon_atom(total_guids)} asset guid{'' if total_guids == 1 else 's'})")
    elif not should_omit_toon_value(count):
        header_parts.append(f"({format_toon_atom(count)} shown)")
    if result.get("truncated") is True:
        max_results = result.get("maxResults")
        suffix = f" at maxResults {format_toon_atom(max_results)}" if not should_omit_toon_value(max_results) else ""
        header_parts.append(f"truncated{suffix}")
    asset_database_filter = result.get("assetDatabaseFilter")
    if not should_omit_toon_value(asset_database_filter):
        header_parts.append(f"filter:{asset_database_filter}")

    lines = [" ".join(header_parts), "detail: chievfx://asset/{guid} or chievfx://asset/{guid}/id/{localId}"]
    for asset in assets:
        if isinstance(asset, dict):
            lines.extend(format_asset_find_row_lines(asset))
    return "\n".join(line for line in lines if line)


def format_asset_find_row_lines(asset: dict[str, Any], include_detail: bool = False) -> list[str]:
    path = format_asset_text_value(asset.get("path"))
    name = format_asset_text_value(asset.get("name"))
    guid = format_asset_text_value(asset.get("guid"))
    asset_type = asset.get("mainType") if asset.get("isMainAsset") is True else asset.get("type")
    badges = []
    if not should_omit_toon_value(asset_type):
        badges.append(format_toon_atom(asset_type))
    if asset.get("isMainAsset") is False:
        badges.append(f"localId:{format_toon_atom(asset.get('localId'))}")
    labels = asset.get("labels")
    if isinstance(labels, list) and labels:
        badges.append("labels:" + ",".join(format_toon_atom(label) for label in labels))

    suffix = f" [{', '.join(badges)}]" if badges else ""
    return [f"- {path} name:{name} guid:{guid}{suffix}"]


def format_asset_text_value(value: Any) -> str:
    if should_omit_toon_value(value):
        return ""
    return str(value)


def format_asset_detail_text(result: dict[str, Any]) -> str:
    asset = result.get("asset")
    if not isinstance(asset, dict):
        return to_toon(result)

    lines = format_asset_find_row_lines(asset)
    full_type = asset.get("fullType")
    if not should_omit_toon_value(full_type):
        lines.append(f"  type: {format_toon_atom(full_type)}")

    importer = result.get("importer")
    if isinstance(importer, dict) and importer.get("available") is True:
        importer_type = importer.get("type")
        importer_parts = []
        if importer_type != "AssetImporter":
            importer_parts.append(format_toon_atom(importer_type))
        bundle = importer.get("assetBundleName")
        if not should_omit_toon_value(bundle):
            importer_parts.append(f"bundle:{format_toon_atom(bundle)}")
        if importer_parts:
            lines.append("  importer: " + " ".join(part for part in importer_parts if part))

    subassets = asset.get("subassets")
    if isinstance(subassets, list) and subassets:
        lines.append(f"  subassets[{len(subassets)}]:")
        for subasset in subassets:
            if isinstance(subasset, dict):
                for row_line in format_asset_find_row_lines(subasset, include_detail=True):
                    lines.append("  " + row_line)

    return "\n".join(line for line in lines if line)


def format_scene_usage_counts_text(result: dict[str, Any]) -> str:
    if not isinstance(result, dict) or "counts" not in result:
        return to_toon(result)
    lines = [
        " ".join(
            part
            for part in [
                f"assets:{format_toon_atom(result.get('totalAssets'))}",
                f"refs:{format_toon_atom(result.get('totalReferences'))}",
                f"objects:{format_toon_atom(result.get('totalObjects'))}",
            ]
            if "None" not in part
        )
    ]
    lines.append("usage: chievfx://scene/all/usage/assets/{assetType}")
    counts = result.get("counts")
    if isinstance(counts, list):
        for row in counts:
            if not isinstance(row, dict):
                continue
            lines.append(
                f"- {format_toon_atom(row.get('assetType'))}: assets:{format_toon_atom(row.get('assetCount'))} "
                f"refs:{format_toon_atom(row.get('referenceCount'))} objects:{format_toon_atom(row.get('gameObjectCount'))}"
            )
    return "\n".join(line for line in lines if line)


def format_scene_usage_assets_text(result: dict[str, Any]) -> str:
    if not isinstance(result, dict) or "assets" not in result:
        return to_toon(result)
    lines = [
        f"{format_toon_atom(result.get('assetType'))} ({format_toon_atom(result.get('count'))} shown) "
        f"refs:{format_toon_atom(result.get('totalReferences'))} objects:{format_toon_atom(result.get('totalObjects'))}"
    ]
    lines.append("usage: chievfx://scene/all/usage/asset/{guid} or chievfx://scene/all/usage/asset/{guid}/id/{localId}")
    lines.append("asset: chievfx://asset/{guid} or chievfx://asset/{guid}/id/{localId}")
    assets = result.get("assets")
    if isinstance(assets, list):
        for asset in assets:
            if isinstance(asset, dict):
                lines.extend(format_scene_usage_asset_row_lines(asset))
    return "\n".join(line for line in lines if line)


def format_scene_usage_asset_detail_text(result: dict[str, Any]) -> str:
    if not isinstance(result, dict) or "asset" not in result:
        return to_toon(result)
    asset = result.get("asset")
    lines: list[str] = []
    if isinstance(asset, dict):
        lines.extend(format_asset_find_row_lines(asset))
    lines.append(
        f"usage refs:{format_toon_atom(result.get('referenceCount'))} "
        f"objects:{format_toon_atom(result.get('gameObjectCount'))} locations:{format_toon_atom(result.get('locationCount'))}"
    )
    locations = result.get("locations")
    if isinstance(locations, list):
        for location in locations:
            if not isinstance(location, dict):
                continue
            lines.append(
                f"- {format_asset_text_value(location.get('gameObjectPath'))} "
                f"{format_asset_text_value(location.get('componentKey'))}.{format_asset_text_value(location.get('propertyPath'))} "
                f"source:{format_toon_atom(location.get('source'))}"
            )
    return "\n".join(line for line in lines if line)


def format_scene_usage_asset_row_lines(asset: dict[str, Any]) -> list[str]:
    name = format_asset_text_value(asset.get("name"))
    path = format_asset_text_value(asset.get("path"))
    guid = format_asset_text_value(asset.get("guid"))
    asset_type = asset.get("type") or asset.get("assetType")
    badges = [format_toon_atom(asset_type)]
    if asset.get("isMainAsset") is False:
        badges.append(f"localId:{format_toon_atom(asset.get('localId'))}")
    if asset.get("dependencyOnly") is True:
        badges.append("dependencyOnly")
    suffix = f" [{', '.join(part for part in badges if part)}]" if badges else ""
    lines = [
        f"- {path} name:{name} guid:{guid}{suffix} refs:{format_toon_atom(asset.get('referenceCount'))} "
        f"objects:{format_toon_atom(asset.get('gameObjectCount'))}"
    ]
    return lines


def format_material_profile_summary_text(result: dict[str, Any]) -> str:
    if not isinstance(result, dict) or not any(key in result for key in ("materialCount", "shaderGroups", "materials")):
        return to_toon(result)
    lines = [
        f"materials:{format_toon_atom(result.get('materialCount'))} shaders:{format_toon_atom(result.get('shaderGroupCount'))} "
        f"textures:{format_toon_atom(result.get('textureCount'))} renderers:{format_toon_atom(result.get('rendererCount'))}"
    ]
    shader_groups = result.get("shaderGroups")
    if isinstance(shader_groups, list):
        for group in shader_groups:
            if not isinstance(group, dict):
                continue
            lines.append(
                f"- shader:{format_toon_atom(group.get('shaderName'))} materials:{format_toon_atom(group.get('materialCount'))} "
                f"refs:{format_toon_atom(group.get('rendererReferenceCount'))} detail:{format_asset_text_value(group.get('followUpUri'))}"
            )
    return "\n".join(line for line in lines if line)
