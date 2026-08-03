import sys
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class UiRuntimeDragFormattingTests(unittest.TestCase):
    def test_renders_drag_header_positions_and_framework_rows(self) -> None:
        result = {
            "playMode": True,
            "anyResolved": True,
            "anyDragged": True,
            "startCoordinateConvention": {
                "screenPosition": {"x": 100.0, "y": 200.0},
                "normalizedPosition": {"x": 0.1, "y": 0.4},
            },
            "endCoordinateConvention": {
                "screenPosition": {"x": 400.0, "y": 200.0},
                "normalizedPosition": {"x": 0.4, "y": 0.4},
            },
            "screenDelta": {"x": 300.0, "y": 0.0},
            "frameworks": [
                {
                    "framework": "ugui",
                    "available": True,
                    "resolved": True,
                    "dragged": True,
                    "target": {
                        "path": "Canvas/Screen/Slider",
                        "instanceId": 12345,
                    },
                }
            ],
        }

        text = mcp.format_ui_runtime_drag_text(result)

        self.assertIn("drag playMode:true dragged:true", text)
        self.assertIn("start px:100,200", text)
        self.assertIn("end px:400,200", text)
        self.assertIn("delta px:300,0", text)
        self.assertIn("- ugui path:Canvas/Screen/Slider id:12345 dragged", text)

    def test_format_tool_result_text_routes_ui_runtime_drag(self) -> None:
        result = {
            "playMode": False,
            "anyResolved": False,
            "frameworks": [],
        }

        text = mcp.format_tool_result_text("ui-runtime-drag", result, {})

        self.assertTrue(text.startswith("drag playMode:false"))


class UiRuntimeDragAdvertisedSchemaTests(unittest.TestCase):
    def test_advertised_schema_property_order(self) -> None:
        schema = mcp.advertised_input_schema({"name": "ui-runtime-drag", "inputSchema": {}})
        self.assertTrue(schema.get("additionalProperties"))
        self.assertEqual(
            list(schema["properties"].keys()),
            [
                "framework",
                "x",
                "y",
                "toX",
                "toY",
                "deltaX",
                "deltaY",
                "isNormalized",
                "space",
                "path",
                "instanceId",
            ],
        )


if __name__ == "__main__":
    unittest.main()
