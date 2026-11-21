import sys
import unittest
from pathlib import Path
from typing import Any
from unittest import mock


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


TOOL = {
    "name": "demo-drive",
    "source": "extension",
    "inputSchema": {
        "type": "object",
        "additionalProperties": True,
        "properties": {
            "targetId": {"type": "string", "x-aliases": ["target", "id"]},
            "carName": {"type": "string", "x-aliases": ["car", "vehicle"]},
            "grep": {"type": "string", "x-aliases": ["Text"]},
            "maxSpeedKmh": {"type": "number"},
            "speedKmh": {"type": "number"},
        },
    },
}


class ArgumentAliasTests(unittest.TestCase):
    """Guessed argument names are renamed to the tool's own: another case or separators, or a declared alias."""

    def setUp(self) -> None:
        patcher = mock.patch.object(mcp, "_find_tool", lambda name: TOOL if name == "demo-drive" else None)
        patcher.start()
        self.addCleanup(patcher.stop)

    def resolve(self, arguments: dict[str, Any]) -> tuple[Any, list[str]]:
        return mcp.resolve_argument_aliases("demo-drive", arguments)

    def test_case_and_separators_match_one_property(self) -> None:
        resolved, renamed = self.resolve({"car_name": "Vaz", "TARGET-ID": "home"})
        self.assertEqual(resolved, {"carName": "Vaz", "targetId": "home"})
        self.assertEqual(renamed, ["car_name→carName", "TARGET-ID→targetId"])

    def test_declared_alias_any_case(self) -> None:
        resolved, renamed = self.resolve({"vehicle": "Vaz", "text": "Taxi"})
        self.assertEqual(resolved, {"carName": "Vaz", "grep": "Taxi"})
        self.assertEqual(renamed, ["vehicle→carName", "text→grep"])

    def test_target_already_set_keeps_both(self) -> None:
        resolved, renamed = self.resolve({"carName": "Vaz", "car": "Lada"})
        self.assertEqual(resolved, {"carName": "Vaz", "car": "Lada"})
        self.assertEqual(renamed, [])

    def test_ambiguous_or_unknown_name_is_left_for_the_warning(self) -> None:
        resolved, renamed = self.resolve({"speed": 60, "colour": "red"})
        self.assertEqual(resolved, {"speed": 60, "colour": "red"})
        self.assertEqual(renamed, [])

    def test_known_and_universal_names_are_untouched(self) -> None:
        arguments = {"targetId": "home", "outputFormat": "json", "awaitMs": 1000}
        self.assertEqual(self.resolve(arguments), (arguments, []))

    def test_tool_without_schema_is_untouched(self) -> None:
        self.assertEqual(mcp.resolve_argument_aliases("other-tool", {"car_name": "x"}), ({"car_name": "x"}, []))


if __name__ == "__main__":
    unittest.main()


class CoreToolAliasTests(unittest.TestCase):
    """Synonyms declared in a core tool's own descriptor."""

    def test_game_view_record_takes_duration_and_frame_rate(self) -> None:
        resolved, renamed = mcp.resolve_argument_aliases("game-view-record", {"duration": 3, "frame_rate": 10, "save_path": "Temp/a.mp4"})

        self.assertEqual(resolved, {"seconds": 3, "fps": 10, "savePath": "Temp/a.mp4"})
        self.assertEqual(renamed, ["duration→seconds", "frame_rate→fps", "save_path→savePath"])

    def test_game_view_record_advertises_await_ms(self) -> None:
        tool = next(item for item in mcp.all_tools() if item.get("name") == "game-view-record")

        self.assertIn("awaitMs", mcp.advertised_input_schema(tool)["properties"])

