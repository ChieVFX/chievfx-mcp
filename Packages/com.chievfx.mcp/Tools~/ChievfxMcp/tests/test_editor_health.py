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

    def test_a_problem_that_names_its_own_cure_adds_no_empty_hint(self) -> None:
        problem = "profiler_recording: the Profiler records every frame and keeps them in memory; stop it with profiler-stop-recording unless you profile now"
        self.write_heartbeat({"reservedMb": 1500, "problems": [problem], "hint": ""})
        status = self.server.get_bridge_status({})
        self.assertEqual(status["editorHealth"], [problem])
        self.assertNotIn("", status.get("hints") or [])

    def test_healthy_editor_adds_nothing_to_the_short_status(self) -> None:
        self.write_heartbeat({"reservedMb": 1500, "problems": [], "verdict": "ok: ..."})
        status = self.server.get_bridge_status({})
        self.assertNotIn("editorHealth", status)
        self.assertNotIn("ok: ...", " ".join(status.get("hints") or []))

    def test_verbose_status_carries_the_whole_block(self) -> None:
        self.write_heartbeat({"reservedMb": 1500, "bytesAssemblies": 7, "problems": [], "verdict": "ok: ..."})
        status = self.server.get_bridge_status({"verbose": True})
        self.assertEqual(status["health"]["bytesAssemblies"], 7)

    def test_string_arguments_from_clients_are_read_as_their_values(self) -> None:
        # Clients send arguments the advertised schema leaves out as strings: "false" must not switch verbose on.
        self.write_heartbeat({"reservedMb": 1500, "problems": [], "verdict": "ok: ..."})
        self.assertNotIn("health", self.server.get_bridge_status({"verbose": "false"}))
        self.assertIn("health", self.server.get_bridge_status({"verbose": "true"}))
        self.assertEqual(mcp.clamp_int("3", 12, 0, 50), 3)
        self.assertEqual(mcp.clamp_int("x", 12, 0, 50), 12)

    def test_heartbeat_from_an_older_bridge_has_no_health(self) -> None:
        mcp.write_json_file_atomic(self.bridge_dir / "state.json", {"editor": {}, "busy": {}})
        self.assertNotIn("editorHealth", self.server.get_bridge_status({}))
        self.assertIsNone(self.server.get_bridge_status({"verbose": True})["health"])


if __name__ == "__main__":
    unittest.main()


class BridgeOperationRecordTests(unittest.TestCase):
    """bridge-get-operation: the start of an id finds the record, and the record says how long Unity took to pick the call up."""

    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)
        self.bridge_dir = Path(self.temp_dir.name)
        self.server = mcp.McpServer("http://127.0.0.1:1", str(self.bridge_dir), timeout_ms=1000)
        (self.bridge_dir / "operations").mkdir()
        self.operation_id = "8a2f6ecfb4504e0b95931690f6d2a94d"
        mcp.write_json_file_atomic(
            self.bridge_dir / "operations" / f"{self.operation_id}.json",
            {
                "operationId": self.operation_id,
                "toolName": "gameai-wait",
                "state": "completed",
                "queuedAtUtc": "2026-11-23T12:42:00.300Z",
                "startedAtUtc": "2026-11-23T12:42:39.6819670Z",
                "completedAtUtc": "2026-11-23T12:42:39.900Z",
            },
        )

    def test_a_shortened_id_reads_the_one_record_it_starts(self) -> None:
        record = self.server.get_bridge_operation({"opId": "8a2f6ecf"})
        self.assertEqual(record["operationId"], self.operation_id)
        self.assertEqual(record["queueWaitMs"], 39381)

    def test_an_unknown_id_says_where_to_find_the_full_one(self) -> None:
        with self.assertRaisesRegex(ValueError, "maxOperations:50"):
            self.server.get_bridge_operation({"opId": "ffffffff"})
