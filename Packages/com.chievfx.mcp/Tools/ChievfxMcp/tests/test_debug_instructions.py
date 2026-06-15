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
        self.original_resource_selection_path = mcp.RESOURCE_SELECTION_PATH
        mcp.configure_project_root(str(self.root))
        self.addCleanup(self.restore_paths)

    def restore_paths(self) -> None:
        mcp.PROJECT_ROOT = self.original_project_root
        mcp.DEBUG_INSTRUCTIONS_PATH = self.original_debug_path
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

    def test_dump_debug_instructions_writes_markdown(self) -> None:
        self.write_resource_selection(["editor-context"], ["scene-all-go"])

        path = mcp.dump_debug_instructions("test-trigger")

        self.assertEqual(mcp.DEBUG_INSTRUCTIONS_PATH, path)
        self.assertTrue(path.is_file())
        text = path.read_text(encoding="utf-8")
        self.assertIn("# ChievFX MCP debug instructions", text)
        self.assertIn("Trigger: test-trigger", text)
        self.assertIn("## initialize.instructions", text)
        self.assertIn("Enabled ChievFX MCP descriptors (compact instruction form):", text)
        self.assertIn("chievfx://editor/context", text)
        self.assertIn("chievfx://scene/opened", text)
        # The enabled scene-all-go template folds into the collapsed GameObject category
        # (10 default-enabled tools + 1 template), surfaced as its category resource link.
        self.assertIn("chievfx://categories/gameobject", text)


if __name__ == "__main__":
    unittest.main()
