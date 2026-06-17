import sys
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class UiRuntimeClickFormattingTests(unittest.TestCase):
    def test_renders_compact_header_position_and_framework_rows(self) -> None:
        result = {
            "playMode": True,
            "anyResolved": True,
            "anyClicked": False,
            "coordinateConvention": {
                "screenPosition": {"x": 589.0, "y": 299.0},
                "normalizedPosition": {"x": 0.5, "y": 0.5},
            },
            "frameworks": [
                {
                    "framework": "ugui",
                    "available": True,
                    "resolved": True,
                    "clicked": False,
                    "target": {
                        "path": "Canvas/Screen/BtnTest",
                        "instanceId": 57736,
                    },
                },
                {
                    "framework": "uitoolkit",
                    "available": True,
                    "resolved": False,
                    "clicked": False,
                },
            ],
        }

        text = mcp.format_ui_runtime_click_text(result)

        self.assertIn("click playMode:true resolved:true", text)
        self.assertIn("pos px:589,299 norm:0.50,0.50", text)
        self.assertIn("- ugui path:Canvas/Screen/BtnTest id:57736 resolved", text)
        self.assertIn("- uitoolkit miss", text)
        self.assertNotIn("uri:", text)
        self.assertNotIn("frameworks[", text)

    def test_renders_clicked_events_and_warnings(self) -> None:
        result = {
            "playMode": True,
            "anyResolved": True,
            "anyClicked": True,
            "coordinateConvention": {
                "screenPosition": {"x": 200.0, "y": 225.0},
                "normalizedPosition": {"x": 0.25, "y": 0.75},
            },
            "frameworks": [
                {
                    "framework": "uitoolkit",
                    "available": True,
                    "resolved": True,
                    "clicked": True,
                    "target": {
                        "path": "VisualElement#Root[0]/Button#Play[2]",
                        "visualElementRef": "ve:12345678",
                    },
                    "events": ["PointerDownEvent", "PointerUpEvent", "ClickEvent"],
                }
            ],
            "warnings": ["Coordinate is outside current screen/game-view bounds."],
        }

        text = mcp.format_ui_runtime_click_text(result)

        self.assertIn("click playMode:true clicked:true", text)
        self.assertIn("- uitoolkit path:", text)
        self.assertIn("VisualElement#Root[0]/Button#Play[2]", text)
        self.assertIn("ref:", text)
        self.assertIn("ve:12345678", text)
        self.assertIn("clicked", text)
        self.assertIn("events:PointerDownEvent,PointerUpEvent,ClickEvent", text)
        self.assertIn("! Coordinate is outside current screen/game-view bounds.", text)

    def test_format_tool_result_text_routes_ui_runtime_click(self) -> None:
        result = {
            "playMode": False,
            "anyResolved": False,
            "frameworks": [],
        }

        text = mcp.format_tool_result_text("ui-runtime-click", result, {})

        self.assertTrue(text.startswith("click playMode:false"))


class UiRuntimeClickAdvertisedSchemaTests(unittest.TestCase):
    def test_advertised_schema_property_order(self) -> None:
        schema = mcp.advertised_input_schema({"name": "ui-runtime-click", "inputSchema": {}})
        self.assertTrue(schema.get("additionalProperties"))
        self.assertEqual(
            list(schema["properties"].keys()),
            ["framework", "x", "y", "isNormalized", "path", "instanceId", "handler"],
        )


if __name__ == "__main__":
    unittest.main()
