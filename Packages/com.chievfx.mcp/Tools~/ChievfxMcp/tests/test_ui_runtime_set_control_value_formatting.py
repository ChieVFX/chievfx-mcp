import sys
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class UiRuntimeSetControlValueFormattingTests(unittest.TestCase):
    def test_renders_set_value_header_target_and_states(self) -> None:
        result = {
            "playMode": True,
            "resolved": True,
            "framework": "ugui",
            "target": {
                "path": "Canvas/Screen/Slider",
                "instanceId": 12345,
            },
            "targetStateBefore": {
                "controls": [{"type": "Slider", "value": 0.1}],
            },
            "targetStateAfter": {
                "controls": [{"type": "Slider", "value": 0.5}],
            },
        }

        text = mcp.format_ui_runtime_set_control_value_text(result)

        self.assertIn("set-value playMode:true resolved:true framework:ugui", text)
        self.assertIn("target path:Canvas/Screen/Slider", text)
        self.assertIn("before 0.1", text)
        self.assertIn("after 0.5", text)

    def test_format_tool_result_text_routes_ui_runtime_set_control_value(self) -> None:
        result = {"playMode": False, "resolved": False, "attempts": []}

        text = mcp.format_tool_result_text("ui-runtime-set-control-value", result, {})

        self.assertTrue(text.startswith("set-value playMode:false resolved:false"))


class UiRuntimeSetControlValueAdvertisedSchemaTests(unittest.TestCase):
    def test_advertised_schema_property_order(self) -> None:
        schema = mcp.advertised_input_schema({"name": "ui-runtime-set-control-value", "inputSchema": {}})
        self.assertTrue(schema.get("additionalProperties"))
        self.assertEqual(schema.get("required"), ["value"])
        self.assertEqual(
            list(schema["properties"].keys()),
            ["framework", "x", "y", "isNormalized", "path", "instanceId", "value"],
        )


if __name__ == "__main__":
    unittest.main()
