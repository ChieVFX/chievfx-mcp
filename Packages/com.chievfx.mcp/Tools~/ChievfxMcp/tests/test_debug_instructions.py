import sys
import tempfile
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class DebugInstructionsTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)
        self.root = Path(self.temp_dir.name)
        self.original_project_root = mcp.PROJECT_ROOT
        self.original_debug_path = mcp.DEBUG_INSTRUCTIONS_PATH
        self.original_debug_settings_path = mcp.DEBUG_SETTINGS_PATH
        self.original_debug_descriptors_dir = mcp.DEBUG_DESCRIPTORS_DIR
        self.original_resource_selection_path = mcp.RESOURCE_SELECTION_PATH
        mcp.configure_project_root(str(self.root))
        # This suite disables tools/resources, which is opt-in now that the default is expose-all.
        mcp.AVAILABILITY_SETTINGS_PATH.parent.mkdir(parents=True, exist_ok=True)
        mcp.AVAILABILITY_SETTINGS_PATH.write_text(mcp.json.dumps({"manualSelection": True}), encoding="utf-8")
        self.addCleanup(self.restore_paths)

    def restore_paths(self) -> None:
        mcp.PROJECT_ROOT = self.original_project_root
        mcp.DEBUG_INSTRUCTIONS_PATH = self.original_debug_path
        mcp.DEBUG_SETTINGS_PATH = self.original_debug_settings_path
        mcp.DEBUG_DESCRIPTORS_DIR = self.original_debug_descriptors_dir
        mcp.RESOURCE_SELECTION_PATH = self.original_resource_selection_path
        mcp.configure_project_root(str(self.original_project_root))

    def write_resource_selection(self, enabled_resources: list[str], enabled_templates: list[str]) -> None:
        mcp.RESOURCE_SELECTION_PATH.parent.mkdir(parents=True, exist_ok=True)
        mcp.RESOURCE_SELECTION_PATH.write_text(
            mcp.json.dumps(
                {
                    "schemaVersion": mcp.RESOURCE_SELECTION_SCHEMA_VERSION,
                    "enabledResourceIds": enabled_resources,
                    "enabledResourceTemplateIds": enabled_templates,
                }
            ),
            encoding="utf-8",
        )

    def write_debug_settings(self, debug_mode: bool) -> None:
        mcp.DEBUG_SETTINGS_PATH.parent.mkdir(parents=True, exist_ok=True)
        mcp.DEBUG_SETTINGS_PATH.write_text(
            mcp.json.dumps(
                {
                    "schemaVersion": mcp.DEBUG_SETTINGS_SCHEMA_VERSION,
                    "debugMode": debug_mode,
                }
            ),
            encoding="utf-8",
        )

    def test_dump_debug_instructions_skipped_when_debug_mode_off(self) -> None:
        self.write_debug_settings(False)
        self.write_resource_selection(["editor-context"], ["scene-all-go"])

        path = mcp.dump_debug_instructions("test-trigger")

        self.assertIsNone(path)
        self.assertFalse(mcp.DEBUG_INSTRUCTIONS_PATH.is_file())
        self.assertFalse(mcp.DEBUG_DESCRIPTORS_DIR.exists())

    def test_refresh_debug_artifacts_removes_disabled_tool_descriptors(self) -> None:
        self.write_debug_settings(True)
        self.write_resource_selection(["editor-context"], ["scene-all-go"])

        path = mcp.dump_debug_instructions("initial-dump")
        self.assertIsNotNone(path)

        tools = mcp.json.loads((mcp.DEBUG_DESCRIPTORS_DIR / "tools-list.json").read_text(encoding="utf-8"))["tools"]
        self.assertGreater(len(tools), 1)
        disabled_tool = tools[-1]["name"]
        remaining_tool_ids = [tool["name"] for tool in tools[:-1]]
        self.write_tool_selection(remaining_tool_ids)

        refreshed = mcp.refresh_debug_artifacts_on_tools_list_changed("test-disable-tool")
        self.assertIsNotNone(refreshed)
        self.assertFalse((mcp.DEBUG_DESCRIPTORS_DIR / f"{disabled_tool}.json").exists())

        refreshed_tools = mcp.json.loads((mcp.DEBUG_DESCRIPTORS_DIR / "tools-list.json").read_text(encoding="utf-8"))["tools"]
        self.assertTrue(all(tool["name"] != disabled_tool for tool in refreshed_tools))
        self.assertEqual(len(refreshed_tools), len(remaining_tool_ids))

    def write_tool_selection(self, enabled_tool_ids: list[str]) -> None:
        mcp.TOOL_SELECTION_PATH.parent.mkdir(parents=True, exist_ok=True)
        mcp.TOOL_SELECTION_PATH.write_text(
            mcp.json.dumps(
                {
                    "schemaVersion": mcp.TOOL_SELECTION_SCHEMA_VERSION,
                    "enabledToolIds": enabled_tool_ids,
                }
            ),
            encoding="utf-8",
        )

    def test_dump_debug_instructions_writes_markdown_and_tool_descriptors(self) -> None:
        self.write_debug_settings(True)
        self.write_resource_selection(["editor-context"], ["scene-all-go"])

        path = mcp.dump_debug_instructions("test-trigger")

        self.assertEqual(mcp.DEBUG_INSTRUCTIONS_PATH, path)
        self.assertTrue(path.is_file())
        text = path.read_text(encoding="utf-8")
        self.assertIn("# ChievFX MCP debug instructions", text)
        self.assertIn("Trigger: test-trigger", text)
        self.assertIn("## Tool descriptors (tools/list)", text)
        self.assertIn("## initialize.instructions", text)
        self.assertIn("Core descriptors (if list cut, read chievfx://instructions/core-descriptors):", text)
        self.assertIn("chievfx://editor/context", text)
        self.assertIn("chievfx://scene/opened", text)
        self.assertIn("chievfx://categories/gameobject", text)

        self.assertTrue(mcp.DEBUG_DESCRIPTORS_DIR.is_dir())
        tools_list_path = mcp.DEBUG_DESCRIPTORS_DIR / "tools-list.json"
        self.assertTrue(tools_list_path.is_file())
        tools_list = mcp.json.loads(tools_list_path.read_text(encoding="utf-8"))
        self.assertIn("tools", tools_list)
        self.assertGreater(len(tools_list["tools"]), 0)

        for tool in tools_list["tools"]:
            name = tool["name"]
            tool_path = mcp.DEBUG_DESCRIPTORS_DIR / f"{name}.json"
            self.assertTrue(tool_path.is_file(), msg=f"missing descriptor file for {name}")
            payload = mcp.json.loads(tool_path.read_text(encoding="utf-8"))
            self.assertEqual(payload, tool)
            self.assertIn("inputSchema", payload)
            self.assertNotIn("arguments", payload)


if __name__ == "__main__":
    unittest.main()
