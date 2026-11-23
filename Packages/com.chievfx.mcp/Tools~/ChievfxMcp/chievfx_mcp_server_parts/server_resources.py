# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

from __future__ import annotations

class ResourceReadMixin:
    """resources/read: local catalog resources first, the rest from the Unity bridge."""

    def read_resource(self, params: dict[str, Any], request_id: Any = None) -> dict[str, Any]:
        uri = params.get("uri")
        if not isinstance(uri, str):
            raise ResourceNotFoundError("resources/read requires string param 'uri'.")

        resource_kind, resource_id = resolve_resource_uri(uri)
        ensure_resource_enabled(uri)
        if uri == CORE_DESCRIPTOR_INSTRUCTIONS_URI:
            self.core_descriptors_read = True
            self.write_core_descriptor_marker()
        mime_type = RESOURCE_MIME_TYPE

        def fetch_via_bridge() -> dict[str, Any]:
            self.wait_for_bridge_ready()
            try:
                return self.call_unity_bridge("resource-read", {"uri": uri}, request_id)
            except RuntimeError as exc:
                message = str(exc)
                if is_resource_not_found_error(message):
                    raise ResourceNotFoundError(message) from exc
                raise

        if resource_kind == "resource" and resource_id == INSTRUCTIONS_CORE_DESCRIPTORS_RESOURCE_ID:
            text = truncate_resource_text(build_core_descriptor_instructions_resource_body())
        elif (category_entry := get_category_resource_by_uri(uri)) is not None:
            text = truncate_resource_text(category_resource_body(category_entry))
        elif (extension_resource := get_extension_resource_by_uri(uri)) is not None:
            mime_type = extension_resource.get("mimeType") or RESOURCE_MIME_TYPE
            if isinstance(extension_resource.get("staticText"), str):
                text = truncate_resource_text(extension_resource["staticText"])
            else:
                bridge_result = fetch_via_bridge()
                text = truncate_resource_text(format_cameras_extension_resource_text(extension_resource.get("id"), bridge_result.get("result")))
        elif (extension_template := get_extension_resource_template_by_uri(uri)) is not None:
            mime_type = extension_template.get("mimeType") or RESOURCE_MIME_TYPE
            bridge_result = fetch_via_bridge()
            text = truncate_resource_text(format_cameras_extension_resource_text(extension_template.get("id"), bridge_result.get("result")))
        else:
            bridge_result = fetch_via_bridge()
            if uri == "chievfx://scene/opened":
                text = format_scene_opened_resource_text(bridge_result.get("result"))
            elif resource_kind == "resource" and resource_id == "scene-all-usage-counts":
                text = truncate_resource_text(format_scene_usage_counts_text(bridge_result.get("result")))
            elif resource_kind == "resource" and resource_id == "scene-all-material-profile-summary":
                text = truncate_resource_text(format_material_profile_summary_text(bridge_result.get("result")))
            elif resource_kind == "template" and resource_id in {"scene-go", "scene-all-go"}:
                text = truncate_resource_text(format_gameobject_get_text(bridge_result.get("result")))
            elif resource_kind == "template" and resource_id in {"scene-component", "scene-all-component"}:
                text = truncate_resource_text(format_gameobject_component_get_text(bridge_result.get("result")))
            elif resource_kind == "template" and resource_id in {"asset-detail", "asset-subasset-detail"}:
                text = truncate_resource_text(format_asset_detail_text(bridge_result.get("result")))
            elif resource_kind == "template" and resource_id == "scene-all-usage-assets":
                text = truncate_resource_text(format_scene_usage_assets_text(bridge_result.get("result")))
            elif resource_kind == "template" and resource_id in {"scene-all-usage-asset", "scene-all-usage-subasset"}:
                text = truncate_resource_text(format_scene_usage_asset_detail_text(bridge_result.get("result")))
            elif resource_kind == "template" and resource_id == "scene-all-material-profile-shader":
                text = truncate_resource_text(format_material_profile_shader_text(bridge_result.get("result")))
            elif resource_kind == "template" and resource_id == "scene-all-material-profile-material":
                text = truncate_resource_text(format_material_profile_material_text(bridge_result.get("result")))
            else:
                text = format_resource_text(bridge_result.get("result"))

        return {
            "contents": [
                {
                    "uri": uri,
                    "mimeType": mime_type,
                    "text": text,
                }
            ]
        }
