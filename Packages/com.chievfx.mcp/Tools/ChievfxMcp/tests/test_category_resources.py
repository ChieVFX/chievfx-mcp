import json
import sys
import tempfile
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


FRAME_DEBUGGER_TOOLS = [
    "frame-debugger-control",
    "frame-debugger-groups-list",
    "frame-debugger-group-events-list",
    "frame-debugger-drawcall-get",
    "frame-debugger-drawcall-screenshot",
    "frame-debugger-events-list",
    "frame-debugger-event-get",
]


class CategoryResourceTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)
        root = Path(self.temp_dir.name)
        self.tool_selection_path = root / "UserSettings" / "ChievfxMcpToolSelection.json"
        self.resource_selection_path = root / "UserSettings" / "ChievfxMcpResourceSelection.json"
        self.category_selection_path = root / "UserSettings" / "ChievfxMcpCategorySelection.json"
        self.extension_manifest_path = root / "Library" / "ChievfxMcpBridge" / "extension-capabilities.snapshot.json"

        self._originals = {
            "TOOL_SELECTION_PATH": mcp.TOOL_SELECTION_PATH,
            "RESOURCE_SELECTION_PATH": mcp.RESOURCE_SELECTION_PATH,
            "CATEGORY_SELECTION_PATH": mcp.CATEGORY_SELECTION_PATH,
            "EXTENSION_CAPABILITY_MANIFEST_PATH": mcp.EXTENSION_CAPABILITY_MANIFEST_PATH,
        }
        mcp.TOOL_SELECTION_PATH = self.tool_selection_path
        mcp.RESOURCE_SELECTION_PATH = self.resource_selection_path
        mcp.CATEGORY_SELECTION_PATH = self.category_selection_path
        mcp.EXTENSION_CAPABILITY_MANIFEST_PATH = self.extension_manifest_path
        mcp.configure_extension_manifest_bridge_fetcher(None)
        mcp.invalidate_extension_manifest_cache()
        self.addCleanup(self.restore_paths)

    def restore_paths(self) -> None:
        for name, value in self._originals.items():
            setattr(mcp, name, value)

    def write_tool_selection(self, enabled_tool_ids: list[str]) -> None:
        self.tool_selection_path.parent.mkdir(parents=True, exist_ok=True)
        self.tool_selection_path.write_text(
            json.dumps(
                {
                    "schemaVersion": mcp.TOOL_SELECTION_SCHEMA_VERSION,
                    "enabledToolIds": enabled_tool_ids,
                }
            ),
            encoding="utf-8",
        )

    def write_resource_selection(self, resources: list[str], templates: list[str]) -> None:
        self.resource_selection_path.parent.mkdir(parents=True, exist_ok=True)
        self.resource_selection_path.write_text(
            json.dumps(
                {
                    "schemaVersion": mcp.RESOURCE_SELECTION_SCHEMA_VERSION,
                    "enabledResourceIds": resources,
                    "enabledResourceTemplateIds": templates,
                }
            ),
            encoding="utf-8",
        )

    def write_category_settings(self, force_all: bool, always_supplied: list[str]) -> None:
        self.category_selection_path.parent.mkdir(parents=True, exist_ok=True)
        self.category_selection_path.write_text(
            json.dumps(
                {
                    "schemaVersion": mcp.CATEGORY_SELECTION_SCHEMA_VERSION,
                    "forceAllCategoriesAlwaysSupplied": force_all,
                    "alwaysSuppliedCategories": always_supplied,
                }
            ),
            encoding="utf-8",
        )

    def test_large_category_collapses(self) -> None:
        self.write_tool_selection(FRAME_DEBUGGER_TOOLS)
        self.write_resource_selection(["editor-context"], [])

        instructions = mcp.build_initialize_instructions()

        self.assertIn("Extra API capabilities", instructions)
        self.assertIn("chievfx://categories/frame-debugger", instructions)
        self.assertNotIn("frame-debugger-control:", instructions)
        # The batched section must sit below the Tools/Resources descriptor block.
        self.assertGreater(
            instructions.index("Extra API capabilities"),
            instructions.index("Enabled ChievFX MCP descriptors"),
        )

    def test_collapsed_category_advertised_and_readable(self) -> None:
        self.write_tool_selection(FRAME_DEBUGGER_TOOLS)
        self.write_resource_selection(["editor-context"], [])

        listed = {resource["uri"] for resource in mcp.dynamic_category_resources()}
        self.assertIn("chievfx://categories/frame-debugger", listed)

        entry = mcp.get_category_resource_by_uri("chievfx://categories/frame-debugger")
        self.assertIsNotNone(entry)
        body = mcp.category_resource_body(entry)
        self.assertIn("frame-debugger-control:", body)
        self.assertIn("Tools:", body)

    def test_small_category_stays_inline(self) -> None:
        self.write_tool_selection(FRAME_DEBUGGER_TOOLS[:3])
        self.write_resource_selection(["editor-context"], [])

        instructions = mcp.build_initialize_instructions()

        self.assertIn("frame-debugger-control:", instructions)
        self.assertNotIn("chievfx://categories/frame-debugger", instructions)

    def test_always_supplied_category_not_collapsed(self) -> None:
        self.write_tool_selection(FRAME_DEBUGGER_TOOLS)
        self.write_resource_selection(["editor-context"], [])
        self.write_category_settings(False, ["Frame Debugger"])

        instructions = mcp.build_initialize_instructions()

        self.assertIn("frame-debugger-control:", instructions)
        self.assertNotIn("chievfx://categories/frame-debugger", instructions)

    def test_force_all_collapses_nothing(self) -> None:
        self.write_tool_selection(FRAME_DEBUGGER_TOOLS)
        self.write_resource_selection(["editor-context"], [])
        self.write_category_settings(True, [])

        instructions = mcp.build_initialize_instructions()

        self.assertNotIn("Extra API capabilities", instructions)
        self.assertIn("frame-debugger-control:", instructions)
        self.assertEqual(mcp.dynamic_category_resources(), [])

    def test_server_lists_and_reads_category_resource(self) -> None:
        self.write_tool_selection(FRAME_DEBUGGER_TOOLS)
        self.write_resource_selection(["editor-context"], [])

        server = mcp.McpServer("http://127.0.0.1:1", str(self.temp_dir.name) + "/bridge", timeout_ms=1000)

        listed = server.handle_message(
            {"jsonrpc": "2.0", "id": 1, "method": "resources/list", "params": {}}
        )
        uris = {resource["uri"] for resource in listed["result"]["resources"]}
        self.assertIn("chievfx://categories/frame-debugger", uris)

        read = server.handle_message(
            {
                "jsonrpc": "2.0",
                "id": 2,
                "method": "resources/read",
                "params": {"uri": "chievfx://categories/frame-debugger"},
            }
        )
        text = read["result"]["contents"][0]["text"]
        self.assertIn("frame-debugger-control:", text)

        missing = server.handle_message(
            {
                "jsonrpc": "2.0",
                "id": 3,
                "method": "resources/read",
                "params": {"uri": "chievfx://categories/does-not-exist"},
            }
        )
        self.assertEqual(missing["error"]["code"], -32002)

    def test_templates_fold_into_resources_count(self) -> None:
        self.write_tool_selection([])
        self.write_resource_selection(
            ["editor-context"],
            ["assets-name-contains", "assets-type", "assets-label", "assets-filter"],
        )

        instructions = mcp.build_initialize_instructions()

        self.assertIn("chievfx://categories/asset", instructions)
        asset_line = next(line for line in instructions.splitlines() if "chievfx://categories/asset" in line)
        self.assertIn("4 resources", asset_line)
        self.assertNotIn("templates", asset_line)

    def test_essentials_never_collapses_by_default(self) -> None:
        self.write_tool_selection([])
        self.write_resource_selection(["editor-context"], [])

        instructions = mcp.build_initialize_instructions()

        self.assertNotIn("chievfx://categories/essentials", instructions)


if __name__ == "__main__":
    unittest.main()
