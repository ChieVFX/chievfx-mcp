import sys
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class UiControlFindTextsTests(unittest.TestCase):
    """ui-control-find texts:true prints one line per window after the controls."""

    def test_texts_print_one_line_per_window(self) -> None:
        text = mcp.format_ui_control_find_text(
            {
                "controls": [],
                "texts": [
                    {"window": "QuestsView", "texts": ["Daily quests", "Win 3 races (button)"]},
                    {"window": "Hud", "texts": ["1 250"]},
                ],
                "textsTotal": 3,
            }
        )
        self.assertIn("texts (3):\n  QuestsView: Daily quests | Win 3 races (button)\n  Hud: 1 250", text)

    def test_truncated_list_says_how_many_were_found(self) -> None:
        text = mcp.format_ui_control_find_text({"controls": [], "texts": [{"window": "Shop", "texts": ["a", "b"]}], "textsTotal": 75})
        self.assertIn("texts (2 of 75, narrow with wildcards):", text)

    def test_requested_but_none_visible(self) -> None:
        self.assertIn("texts: none visible", mcp.format_ui_control_find_text({"controls": [], "texts": [], "textsTotal": 0}))

    def test_later_page_points_to_the_first(self) -> None:
        self.assertIn("texts: listed with page 1", mcp.format_ui_control_find_text({"controls": [], "textsOnPage": 1}))

    def test_not_requested_adds_nothing(self) -> None:
        self.assertNotIn("texts", mcp.format_ui_control_find_text({"controls": []}))


if __name__ == "__main__":
    unittest.main()
