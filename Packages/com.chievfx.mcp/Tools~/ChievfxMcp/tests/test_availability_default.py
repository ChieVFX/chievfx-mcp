import json
import sys
import tempfile
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402

HIDDEN_CATEGORIES = {"autonomous", "obsolete"}


class AvailabilityDefaultTests(unittest.TestCase):
    """The master 'expose all non-hidden' default (Connection tab toggle OFF) must advertise every
    non-hidden tool/resource regardless of any saved selection, and turning manual mode ON must
    restore that saved selection."""

    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)
        root = Path(self.temp_dir.name)
        self._originals = {
            "TOOL_SELECTION_PATH": mcp.TOOL_SELECTION_PATH,
            "RESOURCE_SELECTION_PATH": mcp.RESOURCE_SELECTION_PATH,
            "PROMPT_SELECTION_PATH": mcp.PROMPT_SELECTION_PATH,
            "AVAILABILITY_SETTINGS_PATH": mcp.AVAILABILITY_SETTINGS_PATH,
            "EXTENSION_CAPABILITY_MANIFEST_PATH": mcp.EXTENSION_CAPABILITY_MANIFEST_PATH,
            "PROJECT_ROOT": mcp.PROJECT_ROOT,
        }
        self.tool_selection_path = root / "UserSettings" / "ChievfxMcpToolSelection.json"
        self.availability_path = root / "UserSettings" / "ChievfxMcpAvailability.json"
        mcp.TOOL_SELECTION_PATH = self.tool_selection_path
        mcp.RESOURCE_SELECTION_PATH = root / "UserSettings" / "ChievfxMcpResourceSelection.json"
        mcp.PROMPT_SELECTION_PATH = root / "UserSettings" / "ChievfxMcpPromptSelection.json"
        mcp.AVAILABILITY_SETTINGS_PATH = self.availability_path
        mcp.EXTENSION_CAPABILITY_MANIFEST_PATH = root / "Library" / "ChievfxMcpBridge" / "extension-capabilities.snapshot.json"
        mcp.PROJECT_ROOT = root
        mcp.configure_extension_manifest_bridge_fetcher(None)
        # A deliberately restrictive saved selection: only one tool enabled.
        self.tool_selection_path.parent.mkdir(parents=True, exist_ok=True)
        self.tool_selection_path.write_text(
            json.dumps({"schemaVersion": mcp.TOOL_SELECTION_SCHEMA_VERSION, "enabledToolIds": ["console-get-logs"]}),
            encoding="utf-8",
        )
        self.addCleanup(self.restore)

    def restore(self) -> None:
        for name, value in self._originals.items():
            setattr(mcp, name, value)

    def set_manual(self, manual: bool) -> None:
        self.availability_path.parent.mkdir(parents=True, exist_ok=True)
        self.availability_path.write_text(json.dumps({"manualSelection": manual}), encoding="utf-8")

    def all_non_hidden_tool_names(self) -> set[str]:
        return {t["name"] for t in mcp.all_tools() if mcp._tool_category(t) not in HIDDEN_CATEGORIES}

    def an_optional_tool(self) -> str:
        # A non-hidden, non-required tool other than the one in the restrictive selection, so it should
        # be OFF in manual mode but ON under expose-all.
        tools = mcp.all_tools()
        required = mcp.required_tool_ids_for_tools(tools)
        for tool in tools:
            name = tool["name"]
            if name != "console-get-logs" and name not in required and mcp._tool_category(tool) not in HIDDEN_CATEGORIES:
                return name
        raise AssertionError("no optional non-hidden tool available for the test")

    def test_default_exposes_all_non_hidden_ignoring_saved_selection(self) -> None:
        # No availability file at all -> default expose-all.
        enabled = mcp.load_enabled_tool_ids()
        self.assertEqual(enabled, self.all_non_hidden_tool_names())
        self.assertIn("console-get-logs", enabled)
        # An optional tool NOT in the restrictive saved list is still exposed.
        self.assertIn(self.an_optional_tool(), enabled)
        # Hidden categories stay off.
        self.assertNotIn("tools-set-role", enabled)  # autonomous

    def test_manual_mode_honors_saved_selection(self) -> None:
        self.set_manual(True)
        enabled = mcp.load_enabled_tool_ids()
        self.assertIn("console-get-logs", enabled)
        self.assertNotIn(self.an_optional_tool(), enabled)

    def test_toggle_round_trip_restores_selection(self) -> None:
        self.set_manual(False)
        self.assertEqual(mcp.load_enabled_tool_ids(), self.all_non_hidden_tool_names())
        self.set_manual(True)
        self.assertNotIn(self.an_optional_tool(), mcp.load_enabled_tool_ids())

    def test_default_exposes_all_resources(self) -> None:
        mcp.RESOURCE_SELECTION_PATH.write_text(
            json.dumps({"enabledResourceIds": [], "enabledResourceTemplateIds": []}), encoding="utf-8"
        )
        resource_ids, template_ids = mcp.load_enabled_resource_ids()
        self.assertEqual(resource_ids, {r["id"] for r in mcp.all_resources()})
        self.assertEqual(template_ids, {t["id"] for t in mcp.all_resource_templates()})

    def test_default_hides_all_prompts(self) -> None:
        self.assertEqual(mcp.load_enabled_prompt_names(), set())


if __name__ == "__main__":
    unittest.main()
