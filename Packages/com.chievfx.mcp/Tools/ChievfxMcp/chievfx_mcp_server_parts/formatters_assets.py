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

    lines = [" ".join(header_parts)]
    for asset in assets:
        if isinstance(asset, dict):
            lines.extend(format_asset_find_row_lines(asset))
    return "\n".join(line for line in lines if line)


def format_asset_find_row_lines(asset: dict[str, Any]) -> list[str]:
    path = format_asset_text_value(asset.get("path"))
    name = format_asset_text_value(asset.get("name"))
    guid = format_asset_text_value(asset.get("guid"))
    resource_uri = format_asset_text_value(asset.get("resourceUri"))
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
    lines = [f"- {path} name:{name} guid:{guid}{suffix}"]
    if not should_omit_toon_value(resource_uri):
        lines.append(f"  detail: {resource_uri}")
    return lines


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
        importer_parts = [format_toon_atom(importer.get("type"))]
        bundle = importer.get("assetBundleName")
        if not should_omit_toon_value(bundle):
            importer_parts.append(f"bundle:{format_toon_atom(bundle)}")
        lines.append("  importer: " + " ".join(part for part in importer_parts if part))

    subassets = asset.get("subassets")
    if isinstance(subassets, list) and subassets:
        lines.append(f"  subassets[{len(subassets)}]:")
        for subasset in subassets:
            if isinstance(subasset, dict):
                for row_line in format_asset_find_row_lines(subasset):
                    lines.append("  " + row_line)

    return "\n".join(line for line in lines if line)
