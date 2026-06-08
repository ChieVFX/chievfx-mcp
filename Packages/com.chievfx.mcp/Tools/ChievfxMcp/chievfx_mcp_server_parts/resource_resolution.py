# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

def get_extension_resource_by_uri(uri: str) -> dict[str, Any] | None:
    for resource in collect_extension_capabilities()["resources"]:
        if resource["uri"] == uri:
            return resource
    return None


def extension_template_matches_uri(uri_template: str, uri: str) -> bool:
    pattern = re.sub(r"\\\{[A-Za-z0-9_]+\\\}", r"[^/?#]+", re.escape(uri_template))
    return re.fullmatch(pattern, uri) is not None


def get_extension_resource_template_by_uri(uri: str) -> dict[str, Any] | None:
    for template in collect_extension_capabilities()["resourceTemplates"]:
        if extension_template_matches_uri(template["uriTemplate"], uri):
            return template
    return None


def resolve_resource_uri(uri: str) -> tuple[str, str]:
    if not isinstance(uri, str) or not uri:
        raise ResourceNotFoundError("Resource URI is required.")

    if "?" in uri or "#" in uri:
        raise ResourceNotFoundError(f"ChievFX MCP resource URI not found: {uri}")

    category_slug = category_slug_from_uri(uri)
    if category_slug is not None:
        return "category", category_slug

    for resource in RESOURCES:
        if uri == resource["uri"]:
            return "resource", resource["id"]

    extension_resource = get_extension_resource_by_uri(uri)
    if extension_resource is not None:
        return "extension-resource", extension_resource["id"]

    extension_template = get_extension_resource_template_by_uri(uri)
    if extension_template is not None:
        return "extension-template", extension_template["id"]

    if uri.startswith("chievfx://scene/"):
        rest = uri[len("chievfx://scene/") :]
        parts = rest.split("/")
        if len(parts) == 4 and parts[0] == "current" and parts[1] == "usage" and parts[2] == "assets" and parts[3]:
            return "template", "scene-current-usage-assets"
        if len(parts) == 4 and parts[0] == "current" and parts[1] == "usage" and parts[2] == "asset" and parts[3]:
            return "template", "scene-current-usage-asset"
        if (
            len(parts) == 6
            and parts[0] == "current"
            and parts[1] == "usage"
            and parts[2] == "asset"
            and parts[3]
            and parts[4] == "id"
            and parts[5]
        ):
            return "template", "scene-current-usage-subasset"
        if (
            len(parts) == 4
            and parts[0] == "current"
            and parts[1] == "material-profile"
            and parts[2] == "shader"
            and parts[3]
        ):
            return "template", "scene-current-material-profile-shader"
        if (
            len(parts) == 4
            and parts[0] == "current"
            and parts[1] == "material-profile"
            and parts[2] == "material"
            and parts[3]
        ):
            return "template", "scene-current-material-profile-material"
        if len(parts) == 3 and parts[1] == "go" and parts[0] not in {"active", ""} and parts[2]:
            return "template", "scene-current-go" if parts[0] == "current" else "scene-go"
        if (
            len(parts) == 4
            and parts[0] == "current"
            and parts[1] == "go"
            and parts[2] == "name-contains"
            and parts[3]
        ):
            return "template", "scene-current-go-name-contains"
        if (
            len(parts) == 4
            and parts[0] == "current"
            and parts[1] == "go"
            and parts[2] == "name-pattern"
            and parts[3]
        ):
            return "template", "scene-current-go-name-pattern"
        if (
            len(parts) == 4
            and parts[0] == "current"
            and parts[1] == "go"
            and parts[2] == "component"
            and parts[3]
        ):
            return "template", "scene-current-go-component"
        if (
            len(parts) == 4
            and parts[0] == "current"
            and parts[1] == "go"
            and parts[2] == "filter"
            and parts[3]
        ):
            return "template", "scene-current-go-filter"
        if (
            len(parts) == 5
            and parts[1] == "go"
            and parts[3] == "component"
            and parts[0] not in {"active", ""}
            and parts[2]
            and parts[4]
        ):
            return "template", "scene-current-component" if parts[0] == "current" else "scene-component"

    if uri.startswith("chievfx://assets/"):
        rest = uri[len("chievfx://assets/") :]
        parts = rest.split("/")
        if len(parts) == 2 and parts[0] == "name-contains" and parts[1]:
            return "template", "assets-name-contains"
        if len(parts) == 2 and parts[0] == "type" and parts[1]:
            return "template", "assets-type"
        if len(parts) == 2 and parts[0] == "label" and parts[1]:
            return "template", "assets-label"
        if len(parts) == 2 and parts[0] == "filter" and parts[1]:
            return "template", "assets-filter"

    if uri.startswith("chievfx://asset/"):
        rest = uri[len("chievfx://asset/") :]
        parts = rest.split("/")
        if len(parts) == 1 and parts[0]:
            return "template", "asset-detail"
        if len(parts) == 3 and parts[0] and parts[1] == "id" and parts[2]:
            return "template", "asset-subasset-detail"

    raise ResourceNotFoundError(f"ChievFX MCP resource URI not found: {uri}")


def ensure_resource_enabled(uri: str) -> None:
    kind, resource_id = resolve_resource_uri(uri)
    if kind == "category":
        if get_category_resource_by_uri(uri) is None:
            raise ResourceNotFoundError(f"ChievFX MCP resource URI not found: {uri}")
        return
    enabled_resource_ids, enabled_template_ids = load_enabled_resource_ids()
    if kind == "resource" and resource_id not in enabled_resource_ids:
        raise ResourceNotFoundError(f"ChievFX MCP resource URI not found: {uri}")
    if kind == "extension-resource" and resource_id not in enabled_resource_ids:
        raise ResourceNotFoundError(f"ChievFX MCP resource URI not found: {uri}")
    if kind == "extension-template" and resource_id not in enabled_template_ids:
        raise ResourceNotFoundError(f"ChievFX MCP resource URI not found: {uri}")
    if kind == "template" and resource_id not in enabled_template_ids:
        raise ResourceNotFoundError(f"ChievFX MCP resource URI not found: {uri}")
