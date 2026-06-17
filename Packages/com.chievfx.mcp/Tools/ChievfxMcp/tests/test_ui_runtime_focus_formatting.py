import sys
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class UiRuntimeFocusFormattingTests(unittest.TestCase):
    def test_renders_focus_header_and_target(self) -> None:
        result = {
            "playMode": True,
            "resolved": True,
            "framework": "ugui",
            "target": {"path": "Canvas/Screen/Toggle", "instanceId": 42},
            "selectedObjectAfter": {"path": "Canvas/Screen/Toggle"},
        }

        text = mcp.format_ui_runtime_focus_text(result)

        self.assertIn("focus playMode:true focused:true framework:ugui", text)
        self.assertIn("target path:Canvas/Screen/Toggle", text)
        self.assertIn("selected:Canvas/Screen/Toggle", text)

    def test_format_tool_result_text_routes_ui_runtime_focus(self) -> None:
        result = {"playMode": False, "resolved": False, "attempts": []}
        text = mcp.format_tool_result_text("ui-runtime-focus", result, {})
        self.assertTrue(text.startswith("focus playMode:false resolved:false"))


class UiRuntimeClearFocusFormattingTests(unittest.TestCase):
    def test_renders_clear_focus_framework_rows(self) -> None:
        result = {
            "playMode": True,
            "anyCleared": True,
            "frameworks": [
                {"framework": "ugui", "available": True, "cleared": True},
                {"framework": "uitoolkit", "available": True, "cleared": False},
            ],
        }

        text = mcp.format_ui_runtime_clear_focus_text(result)

        self.assertIn("clear-focus playMode:true cleared:true", text)
        self.assertIn("- ugui cleared", text)
        self.assertIn("- uitoolkit noop", text)


class UiRuntimeFocusAdvertisedSchemaTests(unittest.TestCase):
    def test_focus_schema_property_order(self) -> None:
        schema = mcp.advertised_input_schema({"name": "ui-runtime-focus", "inputSchema": {}})
        self.assertEqual(
            list(schema["properties"].keys()),
            ["framework", "x", "y", "isNormalized", "path", "instanceId"],
        )

    def test_clear_focus_schema_property_order(self) -> None:
        schema = mcp.advertised_input_schema({"name": "ui-runtime-clear-focus", "inputSchema": {}})
        self.assertEqual(list(schema["properties"].keys()), ["framework"])


if __name__ == "__main__":
    unittest.main()
