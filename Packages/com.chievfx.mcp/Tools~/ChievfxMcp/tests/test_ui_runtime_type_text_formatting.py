import sys
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class UiRuntimeTypeTextFormattingTests(unittest.TestCase):
    def test_renders_compact_header_target_and_text(self) -> None:
        result = {
            "playMode": True,
            "resolved": True,
            "framework": "ugui",
            "controlType": "TMP_InputField",
            "target": {"path": "Canvas/Search/Field", "instanceId": 4242},
            "textBefore": "",
            "textAfter": "dark arrow",
        }

        text = mcp.format_ui_runtime_type_text_text(result)

        self.assertIn("type-text playMode:true resolved:true framework:ugui control:TMP_InputField", text)
        self.assertIn("target path:Canvas/Search/Field id:4242", text)
        self.assertIn('after "dark arrow"', text)

    def test_successful_call_omits_hit_stack_and_target_states(self) -> None:
        # The generic dump used to emit the whole hit stack and both target states on every call —
        # several KB per call in a real UI, none of it needed once the field was found.
        result = {
            "playMode": True,
            "resolved": True,
            "framework": "ugui",
            "target": {"path": "Canvas/Search/Field"},
            "textAfter": "abc",
            "stack": [{"path": "Canvas/Search/Field/Text Area/Text"} for _ in range(40)],
            "targetStateBefore": {"controls": [{"type": "TMP_InputField", "text": ""}]},
            "targetStateAfter": {"controls": [{"type": "TMP_InputField", "text": "abc"}]},
        }

        text = mcp.format_ui_runtime_type_text_text(result)

        self.assertNotIn("Text Area", text)
        self.assertNotIn("targetStateBefore", text)
        self.assertLess(len(text), 200)

    def test_unresolved_call_surfaces_the_hit_stack(self) -> None:
        result = {
            "playMode": True,
            "resolved": False,
            "framework": None,
            "stack": [
                {"path": "Canvas/Overlay/Blocker"},
                {"path": "Canvas/Search/Background"},
            ],
            "warnings": ["No uGUI or UI Toolkit text field resolved from the supplied screen position."],
        }

        text = mcp.format_ui_runtime_type_text_text(result)

        self.assertIn("resolved:false", text)
        self.assertIn("hit Canvas/Overlay/Blocker", text)
        self.assertIn("! No uGUI or UI Toolkit text field resolved", text)

    def test_per_canvas_warning_flood_is_aggregated(self) -> None:
        result = {
            "playMode": True,
            "resolved": True,
            "framework": "ugui",
            "target": {"path": "Canvas/Search/Field"},
            "textAfter": "abc",
            "warnings": [
                "No EventSystem.current found; uGUI selection and raycast routing may be unavailable.",
            ]
            + [f"Canvas 'UI/Pooled{index}' has no GraphicRaycaster." for index in range(30)],
        }

        text = mcp.format_ui_runtime_type_text_text(result)

        self.assertIn("No EventSystem.current found", text)
        self.assertIn("30 canvases no GraphicRaycaster", text)
        self.assertNotIn("has no GraphicRaycaster.", text)

    def test_tool_result_text_routes_type_text_through_the_compact_formatter(self) -> None:
        result = {
            "playMode": True,
            "resolved": True,
            "framework": "ugui",
            "target": {"path": "Canvas/Search/Field"},
            "textAfter": "abc",
        }

        text = mcp.format_tool_result_text("ui-runtime-type-text", result, {})

        self.assertTrue(text.startswith("type-text "))


if __name__ == "__main__":
    unittest.main()
