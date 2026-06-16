import sys
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class UiControlFindFormattingTests(unittest.TestCase):
    def test_renders_compact_rows_with_control_type(self) -> None:
        result = {
            "count": 2,
            "totalMatches": 2,
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

        self.assertIn("(2 shown, 2 matches)", text)
        self.assertIn("- Canvas/Screen/BtnTest (id: 57736) : button; zone:439.0,199.0..739.0,399.0 center:589,299", text)
        self.assertIn(
            "- VisualElement#Root[0]/TextField#FocusableUiToolkitTextField[1] (ve:12345678) : inputfield; zone:100.0,200.0..300.0,250.0 center:200,225",
            text,
        )

    def test_omits_control_type_when_filter_present(self) -> None:
        result = {
            "count": 1,
            "totalMatches": 1,
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

        self.assertIn("- Canvas/Screen/BtnTest (id: 57736); zone:439.0,199.0..739.0,399.0 center:589,299", text)
        self.assertNotIn(": button", text)


if __name__ == "__main__":
    unittest.main()
