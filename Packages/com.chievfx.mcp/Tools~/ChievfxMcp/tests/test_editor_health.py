import sys
import tempfile
import unittest
from pathlib import Path
from typing import Any


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class EditorHealthStatusTests(unittest.TestCase):
    """bridge-get-status shows the editor's health problems from the heartbeat, and nothing when it is healthy."""

    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)
        self.bridge_dir = Path(self.temp_dir.name)
        self.server = mcp.McpServer("http://127.0.0.1:1", str(self.bridge_dir), timeout_ms=1000)

    def write_heartbeat(self, health: dict[str, Any]) -> None:
        payload = {"editor": {"isPlaying": True, "isPlayingOrWillChangePlaymode": True}, "busy": {}, "health": health}
        mcp.write_json_file_atomic(self.bridge_dir / "state.json", payload)

    def test_problems_and_hint_are_shown(self) -> None:
        self.write_heartbeat(
            {
                "reservedMb": 48900,
                "problems": ["high_memory 48900 MB reserved (over 12000 MB)"],
                "hint": "Unsubscribe and destroy what your scripts created, or restart the editor.",
            }
        )
        status = self.server.get_bridge_status({})
        self.assertEqual(status["editorHealth"], ["high_memory 48900 MB reserved (over 12000 MB)"])
        self.assertIn("Unsubscribe and destroy what your scripts created, or restart the editor.", status["hints"])

    def test_healthy_editor_adds_nothing_to_the_short_status(self) -> None:
        self.write_heartbeat({"reservedMb": 1500, "problems": [], "verdict": "ok: ..."})
        status = self.server.get_bridge_status({})
        self.assertNotIn("editorHealth", status)
        self.assertNotIn("ok: ...", " ".join(status.get("hints") or []))

    def test_verbose_status_carries_the_whole_block(self) -> None:
        self.write_heartbeat({"reservedMb": 1500, "bytesAssemblies": 7, "problems": [], "verdict": "ok: ..."})
        status = self.server.get_bridge_status({"verbose": True})
        self.assertEqual(status["health"]["bytesAssemblies"], 7)

    def test_heartbeat_from_an_older_bridge_has_no_health(self) -> None:
        mcp.write_json_file_atomic(self.bridge_dir / "state.json", {"editor": {}, "busy": {}})
        self.assertNotIn("editorHealth", self.server.get_bridge_status({}))
        self.assertIsNone(self.server.get_bridge_status({"verbose": True})["health"])


if __name__ == "__main__":
    unittest.main()
