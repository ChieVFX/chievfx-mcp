import sys
import unittest
from pathlib import Path
from unittest.mock import patch


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class UiControlFindFormattingTests(unittest.TestCase):
    def test_renders_page_header_and_compact_rows(self) -> None:
        result = {
            "page": 1,
            "totalPages": 1,
            "total": 2,
            "controls": [
                {
                    "framework": "ugui",
                    "path": "Canvas/Screen/BtnTest",
                    "instanceId": 57736,
                    "controlType": "button",
                    "zone": {
                        "xMin": 439.0,
                        "yMin": 199.0,
                        "xMax": 739.0,
                        "yMax": 399.0,
                        "center": {"x": 589, "y": 299},
                    },
                },
                {
                    "framework": "uitoolkit",
                    "path": "VisualElement#Root[0]/TextField#FocusableUiToolkitTextField[1]",
                    "visualElementRef": "ve:12345678",
                    "controlType": "inputfield",
                    "zone": {
                        "xMin": 100.0,
                        "yMin": 200.0,
                        "xMax": 300.0,
                        "yMax": 250.0,
                        "center": {"x": 200, "y": 225},
                    },
                },
            ],
        }

        text = mcp.format_ui_control_find_text(result)

        self.assertTrue(text.startswith("page:1/1"))
        self.assertNotIn("uri:", text)
        self.assertNotIn("controls[", text)
        self.assertNotIn("truncated", text)
        self.assertNotIn("maxResults", text)
        self.assertNotIn("center:", text)
        self.assertIn("- Canvas/Screen/BtnTest (id: 57736) : button; zone:439.0,199.0..739.0,399.0", text)
        self.assertIn(
            "- VisualElement#Root[0]/TextField#FocusableUiToolkitTextField[1] (ve:12345678) : inputfield; zone:100.0,200.0..300.0,250.0",
            text,
        )

    def test_normalize_coords_ceil_min_floor_max(self) -> None:
        result = {
            "page": 1,
            "totalPages": 1,
            "total": 1,
            "normalizeCoords": True,
            "controls": [
                {
                    "framework": "ugui",
                    "path": "Canvas/Scroll View/Scrollbar Vertical",
                    "instanceId": 57812,
                    "controlType": "scrollbar",
                    "zone": {
                        "xMin": 333.4,
                        "yMin": 0.0,
                        "xMax": 353.4,
                        "yMax": 598.0,
                        "center": {"x": 343.4, "y": 299.0},
                    },
                }
            ],
        }

        text = mcp.format_ui_control_find_text(result)

        self.assertIn(
            "- Canvas/Scroll View/Scrollbar Vertical (id: 57812) : scrollbar; zone:334,0..353,598",
            text,
        )
        self.assertNotIn("center:", text)

    def test_omits_control_type_when_filter_present(self) -> None:
        result = {
            "page": 1,
            "totalPages": 2,
            "total": 11,
            "controlTypeFilter": "button",
            "controls": [
                {
                    "framework": "ugui",
                    "path": "Canvas/Screen/BtnTest",
                    "instanceId": 57736,
                    "controlType": "button",
                    "zone": {
                        "xMin": 439.0,
                        "yMin": 199.0,
                        "xMax": 739.0,
                        "yMax": 399.0,
                        "center": {"x": 589, "y": 299},
                    },
                }
            ],
        }

        text = mcp.format_ui_control_find_text(result)

        self.assertEqual("page:1/2", text.splitlines()[0])
        self.assertIn("- Canvas/Screen/BtnTest (id: 57736); zone:439.0,199.0..739.0,399.0", text)
        self.assertNotIn(": button", text)
        self.assertNotIn("center:", text)

    def test_call_tool_uses_custom_formatter_not_toon(self) -> None:
        payload = {
            "page": 1,
            "totalPages": 1,
            "total": 4,
            "controls": [
                {
                    "framework": "ugui",
                    "path": "Canvas/Screen/BtnTest",
                    "instanceId": 57736,
                    "controlType": "button",
                    "zone": {
                        "xMin": 439.0,
                        "yMin": 199.0,
                        "xMax": 739.0,
                        "yMax": 399.0,
                        "center": {"x": 589, "y": 299},
                    },
                }
            ],
        }
        server = mcp.McpServer("http://127.0.0.1:1", "", timeout_ms=1000)
        enabled = mcp.load_enabled_tool_ids() | {"ui-control-find"}
        with patch.object(mcp, "load_enabled_tool_ids", return_value=enabled):
            with patch.object(server, "call_unity_bridge", return_value={"ok": True, "result": payload}):
                response = server.call_tool({"name": "ui-control-find", "arguments": {}})

        text = response["content"][0]["text"]
        self.assertEqual("page:1/1", text.splitlines()[0])
        self.assertNotIn("totalPages:", text)
        self.assertNotIn("controls[", text)
        self.assertNotIn("center:", text)

    def test_call_tool_passes_through_preformatted_string(self) -> None:
        formatted = "page:1/1\n- Canvas/Screen/BtnTest (id: 57736) : button; zone:439.0,199.0..739.0,399.0"
        server = mcp.McpServer("http://127.0.0.1:1", "", timeout_ms=1000)
        enabled = mcp.load_enabled_tool_ids() | {"ui-control-find"}
        with patch.object(mcp, "load_enabled_tool_ids", return_value=enabled):
            with patch.object(server, "call_unity_bridge", return_value={"ok": True, "result": formatted}):
                response = server.call_tool({"name": "ui-control-find", "arguments": {}})

        self.assertEqual(formatted, response["content"][0]["text"])


if __name__ == "__main__":
    unittest.main()
