import json
import re
import sys
import tempfile
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class ResourceServer(mcp.McpServer):
    def __init__(self, bridge_dir: Path) -> None:
        super().__init__("http://127.0.0.1:1", str(bridge_dir), timeout_ms=1000)
        self.calls: list[tuple[str, dict[str, object]]] = []

    def wait_for_bridge_ready(self, *args: object, **kwargs: object) -> bool:
        # No real bridge heartbeat file exists in tests; skip the 30s poll so
        # resource reads forward to the stubbed bridge immediately.
        return True

    def call_unity_bridge(
        self,
        name: str,
        arguments: dict[str, object],
        request_id: object = None,
        progress_token: object = None,
        notify: object = None,
    ) -> dict[str, object]:
        self.calls.append((name, arguments))
        return {
            "ok": True,
            "result": {
                "readAt": "2026-04-27T00:00:00Z",
                "uri": arguments.get("uri"),
                "context": {"source": "test"},
            },
        }


class ResourceTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)
        self.bridge_dir = Path(self.temp_dir.name) / "bridge"
        self.selection_path = Path(self.temp_dir.name) / "UserSettings" / "ChievfxMcpResourceSelection.json"
        self.extension_manifest_path = Path(self.temp_dir.name) / "Library" / "ChievfxMcpBridge" / "extension-capabilities.snapshot.json"
        self.original_selection_path = mcp.RESOURCE_SELECTION_PATH
        self.original_extension_manifest_path = mcp.EXTENSION_CAPABILITY_MANIFEST_PATH
        mcp.RESOURCE_SELECTION_PATH = self.selection_path
        mcp.EXTENSION_CAPABILITY_MANIFEST_PATH = self.extension_manifest_path
        self.addCleanup(self.restore_selection_path)
        self.server = ResourceServer(self.bridge_dir)
        mcp.configure_extension_manifest_bridge_fetcher(None)

    def restore_selection_path(self) -> None:
        mcp.RESOURCE_SELECTION_PATH = self.original_selection_path
        mcp.EXTENSION_CAPABILITY_MANIFEST_PATH = self.original_extension_manifest_path

    def write_extension_manifest(self, extensions: list[dict[str, object]]) -> None:
        self.extension_manifest_path.parent.mkdir(parents=True, exist_ok=True)
        self.extension_manifest_path.write_text(
            json.dumps(
                {
                    "schemaVersion": mcp.EXTENSION_CAPABILITY_MANIFEST_SCHEMA_VERSION,
                    "extensions": extensions,
                }
            ),
            encoding="utf-8",
        )
        mcp.invalidate_extension_manifest_cache()

    def overwrite_extension_manifest_without_invalidating(self, extensions: list[dict[str, object]]) -> None:
        self.extension_manifest_path.parent.mkdir(parents=True, exist_ok=True)
        self.extension_manifest_path.write_text(
            json.dumps(
                {
                    "schemaVersion": mcp.EXTENSION_CAPABILITY_MANIFEST_SCHEMA_VERSION,
                    "extensions": extensions,
                }
            ),
            encoding="utf-8",
        )

    def write_selection(
        self,
        enabled_resources: list[str],
        enabled_templates: list[str],
    ) -> None:
        self.selection_path.parent.mkdir(parents=True, exist_ok=True)
        self.selection_path.write_text(
            json.dumps(
                {
                    "schemaVersion": mcp.RESOURCE_SELECTION_SCHEMA_VERSION,
                    "enabledResourceIds": enabled_resources,
                    "enabledResourceTemplateIds": enabled_templates,
                }
            ),
            encoding="utf-8",
        )

    def request(self, method: str, params: dict[str, object] | None = None) -> dict[str, object]:
        response = self.server.handle_message({"jsonrpc": "2.0", "id": 1, "method": method, "params": params or {}})
        self.assertIsInstance(response, dict)
        return response

    def test_initialize_advertises_resource_capability(self) -> None:
        response = self.request("initialize", {"protocolVersion": "2024-11-05"})

        capabilities = response["result"]["capabilities"]
        self.assertEqual(capabilities["resources"], {"listChanged": True})
        self.assertIn("tools", capabilities)

    def test_lists_enabled_resources_and_templates_by_default(self) -> None:
        resources = self.request("resources/list")["result"]["resources"]
        templates = self.request("resources/templates/list")["result"]["resourceTemplates"]

        self.assertIn("chievfx://editor/context", {resource["uri"] for resource in resources})
        self.assertIn(
            "chievfx://scene/{scenePath}/go/{goPath}/component/{componentKey}",
            {template["uriTemplate"] for template in templates},
        )
        self.assertIn(
            "chievfx://scene/all/go/{goPath}",
            {template["uriTemplate"] for template in templates},
        )
        self.assertIn("chievfx://scene/current/usage/counts", {resource["uri"] for resource in resources})
        self.assertIn(
            "chievfx://scene/current/usage/assets/{assetType}",
            {template["uriTemplate"] for template in templates},
        )
        self.assertNotIn(
            "chievfx://scene/current/go/name-contains/Door",
            {resource["uri"] for resource in resources},
        )

    def test_extension_manifest_resources_merge_with_metadata_and_reads_static_text(self) -> None:
        self.write_selection(["editor-context"], [])
        self.write_extension_manifest(
            [
                {
                    "id": "test.extension",
                    "displayName": "Test Extension",
                    "version": "1.0.0",
                    "sourceAssembly": "Test.Mcp.Extension",
                    "resources": [
                        {
                            "id": "test-extension-static",
                            "uri": "chievfx://extensions/test.extension/static",
                            "name": "Test extension static resource",
                            "description": "Registry static resource test.",
                            "mimeType": "text/plain",
                            "category": "Extensions",
                            "required": True,
                            "staticText": "extension registry active",
                        }
                    ],
                }
            ]
        )

        metadata = mcp.build_resource_metadata()
        resources_by_id = {resource["id"]: resource for resource in metadata["resources"]}
        static_resource = resources_by_id["test-extension-static"]
        listed_uris = {resource["uri"] for resource in self.request("resources/list")["result"]["resources"]}
        content = self.request(
            "resources/read",
            {"uri": "chievfx://extensions/test.extension/static"},
        )["result"]["contents"][0]

        self.assertIn("test-extension-static", metadata["requiredResourceIds"])
        self.assertIn("chievfx://extensions/test.extension/static", listed_uris)
        self.assertEqual(static_resource["category"], "Extensions")
        self.assertEqual(static_resource["sourceExtensionId"], "test.extension")
        self.assertEqual(static_resource["sourceAssembly"], "Test.Mcp.Extension")
        self.assertRegex(static_resource["descriptorHash"], r"^[0-9a-f]{64}$")
        self.assertGreater(static_resource["estimatedTokens"], 0)
        self.assertEqual(content["text"], "extension registry active")
        self.assertEqual(self.server.calls, [])

    def test_extension_manifest_watcher_invalidation_refreshes_cached_manifest(self) -> None:
        self.write_selection(["editor-context"], [])
        self.write_extension_manifest(
            [
                {
                    "id": "test.one",
                    "displayName": "Test One",
                    "resources": [
                        {
                            "id": "test-one-resource",
                            "uri": "chievfx://extensions/test.one/resource",
                            "name": "Test one resource",
                            "description": "Cached manifest resource.",
                            "category": "Extensions",
                        }
                    ],
                }
            ]
        )
        self.assertIn("test-one-resource", {resource["id"] for resource in mcp.all_resources()})

        self.overwrite_extension_manifest_without_invalidating(
            [
                {
                    "id": "test.two",
                    "displayName": "Test Two",
                    "resources": [
                        {
                            "id": "test-two-resource",
                            "uri": "chievfx://extensions/test.two/resource",
                            "name": "Test two resource",
                            "description": "Fresh manifest resource.",
                            "category": "Extensions",
                        }
                    ],
                }
            ]
        )
        self.assertNotIn("test-two-resource", {resource["id"] for resource in mcp.all_resources()})

        mcp.handle_selection_target_changed(str(self.extension_manifest_path))

        resource_ids = {resource["id"] for resource in mcp.all_resources()}
        self.assertIn("test-two-resource", resource_ids)
        self.assertNotIn("test-one-resource", resource_ids)

    def test_dynamic_extension_resource_reads_forward_to_unity_bridge(self) -> None:
        uri = "chievfx://extensions/chievfx.ecs/worlds"
        self.write_selection(["editor-context", "ecs-worlds-list"], [])
        self.write_extension_manifest(
            [
                {
                    "id": "chievfx.ecs",
                    "displayName": "ChievFX MCP ECS",
                    "version": "0.1.0",
                    "sourceAssembly": "Chievfx.Mcp.Extensions.Ecs",
                    "resources": [
                        {
                            "id": "ecs-worlds-list",
                            "uri": uri,
                            "name": "ECS worlds list",
                            "description": "Dynamic ECS worlds list.",
                            "mimeType": "application/json",
                            "category": "ECS",
                        }
                    ],
                }
            ]
        )

        content = self.request("resources/read", {"uri": uri})["result"]["contents"][0]

        self.assertEqual(content["mimeType"], "application/json")
        self.assertEqual(self.server.calls, [("resource-read", {"uri": uri})])
        self.assertIn(uri, content["text"])

    def test_dynamic_extension_resource_templates_forward_to_unity_bridge(self) -> None:
        uri = "chievfx://extensions/chievfx.ecs/subscene/0123456789abcdef0123456789abcdef"
        self.write_selection(["editor-context"], ["ecs-subscene-detail"])
        self.write_extension_manifest(
            [
                {
                    "id": "chievfx.ecs",
                    "displayName": "ChievFX MCP ECS",
                    "version": "0.1.0",
                    "sourceAssembly": "Chievfx.Mcp.Extensions.Ecs",
                    "resourceTemplates": [
                        {
                            "id": "ecs-subscene-detail",
                            "uriTemplate": "chievfx://extensions/chievfx.ecs/subscene/{guidOrPath}",
                            "name": "SubScene detail",
                            "description": "Dynamic SubScene detail by GUID or URL-encoded path.",
                            "mimeType": "application/json",
                            "category": "ECS",
                        }
                    ],
                }
            ]
        )

        content = self.request("resources/read", {"uri": uri})["result"]["contents"][0]

        self.assertEqual(content["mimeType"], "application/json")
        self.assertEqual(self.server.calls, [("resource-read", {"uri": uri})])
        self.assertIn(uri, content["text"])

    def test_disabled_extension_resource_template_is_not_found(self) -> None:
        uri = "chievfx://extensions/chievfx.ecs/entities/query/Position"
        self.write_selection(["editor-context"], [])
        self.write_extension_manifest(
            [
                {
                    "id": "chievfx.ecs",
                    "displayName": "ChievFX MCP ECS",
                    "resourceTemplates": [
                        {
                            "id": "ecs-entities-query",
                            "uriTemplate": "chievfx://extensions/chievfx.ecs/entities/query/{querySpec}",
                            "name": "ECS entities query summary",
                            "description": "Dynamic entity query summary.",
                        }
                    ],
                }
            ]
        )

        response = self.request("resources/read", {"uri": uri})

        self.assertEqual(response["error"]["code"], -32002)
        self.assertEqual(self.server.calls, [])

    def test_extension_manifest_rejects_core_resource_id_collision(self) -> None:
        self.write_extension_manifest(
            [
                {
                    "id": "bad.extension",
                    "displayName": "Bad",
                    "resources": [
                        {
                            "id": "editor-context",
                            "uri": "chievfx://extensions/bad.extension/context",
                            "name": "Bad",
                            "description": "Should be rejected.",
                        }
                    ],
                }
            ]
        )

        metadata = mcp.build_resource_metadata()

        self.assertEqual(1, sum(resource["id"] == "editor-context" for resource in metadata["resources"]))
        self.assertTrue(any("id collision editor-context" in error for error in metadata["extensionErrors"]))

    def test_essentials_resources_are_required_and_always_enabled(self) -> None:
        self.write_extension_manifest(
            [
                {
                    "id": "chievfx.control",
                    "displayName": "ChievFX MCP Control",
                    "resources": [
                        {
                            "id": "control-status",
                            "uri": "chievfx://extensions/chievfx.control/status",
                            "name": "Control extension status",
                            "description": "Reports control status.",
                            "category": "Control",
                        }
                    ],
                },
                {
                    "id": "chievfx.runtime-ui",
                    "displayName": "ChievFX MCP Runtime UI",
                    "resources": [
                        {
                            "id": "runtime-ui-status",
                            "uri": "chievfx://extensions/chievfx.runtime-ui/status",
                            "name": "Runtime UI adapter status",
                            "description": "Reports runtime UI status.",
                            "category": "Runtime UI",
                        }
                    ],
                },
            ]
        )
        self.write_selection([], [])

        metadata = mcp.build_resource_metadata()
        resources = {resource["id"]: resource for resource in metadata["resources"]}
        enabled_resource_ids, _ = mcp.load_enabled_resource_ids()

        for resource_id in ["editor-context", "scenes-opened", "runtime-ui-status", "control-status"]:
            self.assertTrue(resources[resource_id]["required"])
            self.assertEqual(resources[resource_id]["category"], "Essentials")
            self.assertIn(resource_id, enabled_resource_ids)

    def test_gameobject_search_resource_templates_are_removed(self) -> None:
        metadata = mcp.build_resource_metadata()
        templates = {template["id"]: template for template in metadata["resourceTemplates"]}

        for template_id in [
            "scene-current-go-name-contains",
            "scene-current-go-name-pattern",
            "scene-current-go-component",
            "scene-current-go-filter",
            "scene-go-name-contains",
            "scene-go-name-pattern",
            "scene-go-component",
            "scene-go-filter",
        ]:
            self.assertNotIn(template_id, templates)

    def test_asset_filter_templates_are_categorized_and_not_listed_as_resources(self) -> None:
        metadata = mcp.build_resource_metadata()
        templates = {template["id"]: template for template in metadata["resourceTemplates"]}
        resources = self.request("resources/list")["result"]["resources"]

        for template_id in [
            "assets-name-contains",
            "assets-type",
            "assets-label",
            "assets-filter",
            "asset-detail",
            "asset-subasset-detail",
            "scene-current-material-profile-shader",
            "scene-current-material-profile-material",
            "scene-current-usage-assets",
            "scene-current-usage-asset",
            "scene-current-usage-subasset",
        ]:
            self.assertEqual(templates[template_id]["category"], "Asset")
            self.assertLess(templates[template_id]["estimatedTokens"], 100)

        resources_by_id = {resource["id"]: resource for resource in metadata["resources"]}
        self.assertEqual(resources_by_id["scene-current-usage-counts"]["category"], "Asset")
        self.assertEqual(resources_by_id["scene-current-material-profile-summary"]["category"], "Asset")

        self.assertNotIn(
            "chievfx://assets/type/Material",
            {resource["uri"] for resource in resources},
        )

    def test_core_resource_metadata_matches_csharp_registry_catalog(self) -> None:
        package_root = Path(__file__).resolve().parents[3]
        catalog = (
            package_root
            / "Editor"
            / "ChievfxMcp"
            / "Core"
            / "Metadata"
            / "ChievfxMcpCoreMetadata.cs"
        ).read_text(encoding="utf-8")

        csharp_resources = {
            resource_id: uri
            for resource_id, uri in re.findall(r'Resource\("([^"]+)",\s*"([^"]+)"', catalog)
        }
        csharp_templates = {
            template_id: uri_template
            for template_id, uri_template in re.findall(r'Template\("([^"]+)",\s*"([^"]+)"', catalog)
        }
        csharp_prompts = set(re.findall(r'Prompt\("([^"]+)"', catalog))

        self.assertEqual({resource["id"]: resource["uri"] for resource in mcp.RESOURCES}, csharp_resources)
        self.assertEqual(
            {template["id"]: template["uriTemplate"] for template in mcp.RESOURCE_TEMPLATES},
            csharp_templates,
        )
        self.assertEqual({prompt["name"] for prompt in mcp.PROMPTS}, csharp_prompts)

    def test_large_resource_response_guidance_uses_resource_copy(self) -> None:
        metadata = mcp.build_resource_metadata()
        templates = {template["id"]: template for template in metadata["resourceTemplates"]}

        component_estimate = templates["scene-component"]["responseEstimate"]
        current_component_estimate = templates["scene-all-component"]["responseEstimate"]

        for estimate in [component_estimate, current_component_estimate]:
            label = estimate["label"].lower()
            self.assertIn("resource payload", label)
            self.assertNotIn("script", label)
            self.assertNotIn("test output", label)
            self.assertNotIn("logs", label)
            self.assertEqual("100-300 typical; 500-2000+ on larger resource payloads", estimate["typicalTokens"])
            self.assertIn("serialized component data", estimate["label"])

    def test_dynamic_resource_read_forwards_hidden_bridge_command(self) -> None:
        uri = "chievfx://scene/Assets%2FScenes%2FSample.unity/go/Root%2FChild/component/BoxCollider.1"

        result = self.request("resources/read", {"uri": uri})["result"]

        self.assertEqual(self.server.calls, [("resource-read", {"uri": uri})])
        self.assertIn(uri, result["contents"][0]["text"])

    def test_gameobject_search_resource_reads_are_not_found(self) -> None:
        uris = [
            "chievfx://scene/current/go/name-contains/Door",
            "chievfx://scene/current/go/name-pattern/%2ADoor%3F",
            "chievfx://scene/current/go/component/MeshRenderer",
            "chievfx://scene/current/go/filter/name%3D%2ADoor%2A%3Bcomponent%3DMeshRenderer",
            "chievfx://scene/all/go/name-contains/Door",
            "chievfx://scene/all/go/name-pattern/%2ADoor%3F",
            "chievfx://scene/all/go/component/MeshRenderer",
            "chievfx://scene/all/go/filter/name%3D%2ADoor%2A%3Bcomponent%3DMeshRenderer",
        ]

        for uri in uris:
            with self.subTest(uri=uri):
                self.server.calls.clear()
                response = self.request("resources/read", {"uri": uri})

                self.assertEqual(response["error"]["code"], -32002)
                self.assertEqual(self.server.calls, [])

    def test_asset_filter_resource_reads_are_enabled(self) -> None:
        uris = [
            "chievfx://assets/name-contains/Wood",
            "chievfx://assets/type/material",
            "chievfx://assets/label/ui",
            "chievfx://assets/filter/name%3Dwood%3Btype%3DMaterial%2CTexture2D%3Blabel%3Dui%3Barea%3Dassets%3Bfolder%3DAssets%2FArt%3Blimit%3D80%3Bsubassets%3D0",
            "chievfx://asset/0123456789abcdef0123456789abcdef",
            "chievfx://asset/0123456789abcdef0123456789abcdef/id/123456789",
        ]

        for uri in uris:
            with self.subTest(uri=uri):
                self.server.calls.clear()
                result = self.request("resources/read", {"uri": uri})["result"]

                self.assertEqual(self.server.calls, [("resource-read", {"uri": uri})])
                self.assertIn(uri, result["contents"][0]["text"])

    def test_current_scene_usage_resource_reads_are_enabled(self) -> None:
        uris = [
            "chievfx://scene/current/usage/counts",
            "chievfx://scene/current/usage/assets/material",
            "chievfx://scene/current/usage/assets/renderTexture",
            "chievfx://scene/current/usage/assets/all",
            "chievfx://scene/current/usage/asset/0123456789abcdef0123456789abcdef",
            "chievfx://scene/current/usage/asset/0123456789abcdef0123456789abcdef/id/123456789",
        ]

        for uri in uris:
            with self.subTest(uri=uri):
                self.server.calls.clear()
                result = self.request("resources/read", {"uri": uri})["result"]

                self.assertEqual(self.server.calls, [("resource-read", {"uri": uri})])
                self.assertIn(uri, result["contents"][0]["text"])

    def test_current_scene_material_profile_resource_reads_are_enabled(self) -> None:
        uris = [
            "chievfx://scene/current/material-profile/summary",
            "chievfx://scene/current/material-profile/shader/Universal%20Render%20Pipeline%2FLit",
            "chievfx://scene/current/material-profile/material/0123456789abcdef0123456789abcdef%3A2100000",
        ]

        for uri in uris:
            with self.subTest(uri=uri):
                self.server.calls.clear()
                result = self.request("resources/read", {"uri": uri})["result"]

                self.assertEqual(self.server.calls, [("resource-read", {"uri": uri})])
                self.assertIn(uri, result["contents"][0]["text"])

    def test_disabled_static_resource_is_not_listed_or_readable(self) -> None:
        self.write_selection(["editor-context"], [])

        resources = self.request("resources/list")["result"]["resources"]
        response = self.request("resources/read", {"uri": "chievfx://scene/current/usage/counts"})

        self.assertNotIn("chievfx://scene/current/usage/counts", {resource["uri"] for resource in resources})
        self.assertEqual(response["error"]["code"], -32002)

    def test_disabled_matching_template_is_not_found(self) -> None:
        self.write_selection(["editor-context"], [])

        response = self.request("resources/read", {"uri": "chievfx://scene/Assets%2FMain.unity/hierarchy"})

        self.assertEqual(response["error"]["code"], -32002)
        self.assertEqual(self.server.calls, [])

    def test_disabled_current_filter_template_is_not_found(self) -> None:
        self.write_selection(["editor-context"], ["scene-all-go"])

        response = self.request("resources/read", {"uri": "chievfx://scene/current/go/name-contains/Door"})

        self.assertEqual(response["error"]["code"], -32002)
        self.assertEqual(self.server.calls, [])

    def test_disabled_asset_filter_template_is_not_found(self) -> None:
        self.write_selection(["editor-context"], ["assets-type"])

        response = self.request("resources/read", {"uri": "chievfx://assets/label/ui"})

        self.assertEqual(response["error"]["code"], -32002)
        self.assertEqual(self.server.calls, [])

    def test_disabled_current_scene_usage_template_is_not_found(self) -> None:
        self.write_selection(["editor-context"], ["scene-current-usage-assets"])

        response = self.request("resources/read", {"uri": "chievfx://scene/current/usage/asset/0123456789abcdef0123456789abcdef"})

        self.assertEqual(response["error"]["code"], -32002)
        self.assertEqual(self.server.calls, [])

    def test_disabled_current_scene_material_profile_template_is_not_found(self) -> None:
        self.write_selection(["editor-context"], ["scene-current-material-profile-shader"])

        response = self.request("resources/read", {"uri": "chievfx://scene/current/material-profile/material/abc%3A123"})

        self.assertEqual(response["error"]["code"], -32002)
        self.assertEqual(self.server.calls, [])

    def test_resource_read_rejects_query_strings(self) -> None:
        response = self.request("resources/read", {"uri": "chievfx://scene/current/go/name-contains/Door?limit=1"})

        self.assertEqual(response["error"]["code"], -32002)
        self.assertEqual(self.server.calls, [])

    def test_unknown_resource_returns_json_rpc_not_found(self) -> None:
        response = self.request("resources/read", {"uri": "chievfx://missing"})

        self.assertEqual(response["error"]["code"], -32002)


if __name__ == "__main__":
    unittest.main()
