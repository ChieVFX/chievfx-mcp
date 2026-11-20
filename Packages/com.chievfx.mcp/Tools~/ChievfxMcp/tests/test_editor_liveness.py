import os
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path
from typing import Any
from unittest import mock


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class EditorLivenessTests(unittest.TestCase):
    """A hung or closed editor fails calls at once; compiles and domain reloads are still waited out."""

    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)
        self.bridge_dir = Path(self.temp_dir.name)
        self.server = mcp.McpServer("http://127.0.0.1:1", str(self.bridge_dir), timeout_ms=1000)
        patcher = mock.patch.object(mcp, "EDITOR_LIVENESS_CHECK_SECONDS", 0.05)
        patcher.start()
        self.addCleanup(patcher.stop)
        # The test process stands in for the editor: its own CPU use must not read as an editor at work.
        self.cpu_seconds: list[float] = []
        cpu_patcher = mock.patch.object(mcp, "process_cpu_seconds", lambda pid: self.cpu_seconds.pop(0) if self.cpu_seconds else None)
        cpu_patcher.start()
        self.addCleanup(cpu_patcher.stop)
        self.write_events([])

    def write_heartbeat(self, age_seconds: float, process_id: int | None = None, **editor: Any) -> None:
        payload: dict[str, Any] = {"editor": {"isPlaying": False, "isPlayingOrWillChangePlaymode": False, **editor}, "busy": {}}
        if process_id is not None:
            payload["editor"]["processId"] = process_id
        path = self.bridge_dir / "state.json"
        mcp.write_json_file_atomic(path, payload)
        stamp = time.time() - age_seconds
        os.utime(path, (stamp, stamp))
        self.server._editor_liveness_cache = None

    def write_events(self, events: list[dict[str, Any]]) -> None:
        mcp.write_json_file_atomic(
            self.bridge_dir / "events.json",
            {"lastEventId": len(events), "truncatedBeforeEventId": 0, "events": events},
        )
        self.server._editor_liveness_cache = None

    @staticmethod
    def editor_event(event_id: int, event_type: str) -> dict[str, Any]:
        return {"eventId": event_id, "timestamp": mcp.utc_now_iso(), "source": "editor", "type": event_type, "level": "info", "message": event_type}

    @staticmethod
    def dead_process_id() -> int:
        process = subprocess.Popen([sys.executable, "-c", "pass"])
        process.wait()
        return process.pid

    def test_hung_editor_fails_at_once_with_heartbeat_age_and_pid(self) -> None:
        self.write_heartbeat(age_seconds=3700, process_id=os.getpid())

        started = time.monotonic()
        with self.assertRaises(mcp.EditorUnresponsiveError) as caught:
            self.server.call_unity_bridge("console-get-logs", {}, request_id="hung")

        self.assertLess(time.monotonic() - started, 0.5)
        message = str(caught.exception)
        self.assertTrue(message.startswith("editor_unresponsive:"), message)
        self.assertIn(f"PID {os.getpid()}", message)
        self.assertIn("1.0 h", message)
        self.assertEqual(list((self.bridge_dir / "requests").glob("*.json")), [])

    def test_closed_editor_says_it_is_not_running(self) -> None:
        self.write_heartbeat(age_seconds=20, process_id=self.dead_process_id())

        with self.assertRaises(mcp.EditorUnresponsiveError) as caught:
            self.server.call_unity_bridge("console-get-logs", {}, request_id="gone")

        self.assertTrue(str(caught.exception).startswith("editor_not_running:"), str(caught.exception))

    def test_domain_reload_in_the_journal_keeps_the_wait(self) -> None:
        self.write_heartbeat(age_seconds=120, process_id=os.getpid())
        self.write_events([self.editor_event(1, "compile-start"), self.editor_event(2, "compile-finish"), self.editor_event(3, "domain-reload-before")])

        liveness = self.server.assess_editor_liveness()

        self.assertEqual(liveness["state"], "busy")
        self.assertEqual(liveness["busySign"], "reloading the script domain")
        self.server.raise_if_editor_unresponsive()
        reason, _ = self.server.bridge_recovery_wait_reason()
        self.assertEqual(reason, "Unity is reloading the script domain.")

    def test_finished_reload_is_no_excuse(self) -> None:
        self.write_heartbeat(age_seconds=120, process_id=os.getpid())
        self.write_events([self.editor_event(1, "domain-reload-before"), self.editor_event(2, "domain-reload-after")])

        self.assertEqual(self.server.assess_editor_liveness()["state"], "unresponsive")

    def test_compile_flag_waits_but_not_forever(self) -> None:
        self.write_heartbeat(age_seconds=120, process_id=os.getpid(), isCompiling=True)
        self.assertEqual(self.server.assess_editor_liveness()["state"], "busy")

        self.write_heartbeat(age_seconds=mcp.EDITOR_BUSY_UNRESPONSIVE_SECONDS + 5, process_id=os.getpid(), isCompiling=True)
        liveness = self.server.assess_editor_liveness()
        self.assertEqual(liveness["state"], "unresponsive")
        self.assertIn("has been compiling scripts for longer than", self.server.describe_editor_down(liveness))
        self.assertEqual(self.server.bridge_recovery_wait_reason(), (None, 0.0))

    def test_short_stale_heartbeat_is_not_a_verdict(self) -> None:
        self.write_heartbeat(age_seconds=10, process_id=os.getpid())

        self.assertEqual(self.server.assess_editor_liveness()["state"], "stale")
        self.server.raise_if_editor_unresponsive()

    def test_call_queued_behind_a_stuck_call_fails_when_the_editor_hangs(self) -> None:
        self.write_heartbeat(age_seconds=0, process_id=os.getpid())
        self.server.bridge_call_lock.acquire()
        self.addCleanup(self.server.bridge_call_lock.release)
        outcome: dict[str, Any] = {}

        def queued_call() -> None:
            try:
                self.server.call_unity_bridge("console-get-logs", {}, request_id="queued")
            except Exception as exc:  # noqa: BLE001 - the test inspects it
                outcome["error"] = exc

        thread = threading.Thread(target=queued_call)
        thread.start()
        time.sleep(0.2)
        self.assertTrue(thread.is_alive())
        self.write_heartbeat(age_seconds=300, process_id=os.getpid())
        thread.join(timeout=2)

        self.assertFalse(thread.is_alive())
        self.assertIsInstance(outcome.get("error"), mcp.EditorUnresponsiveError)

    def test_queued_request_is_withdrawn_when_the_editor_hangs_before_picking_it_up(self) -> None:
        self.write_heartbeat(age_seconds=0, process_id=os.getpid())
        outcome: dict[str, Any] = {}

        def call() -> None:
            try:
                self.server.call_unity_bridge("console-get-logs", {"timeoutMs": 30000}, request_id="waiting")
            except Exception as exc:  # noqa: BLE001 - the test inspects it
                outcome["error"] = exc

        thread = threading.Thread(target=call)
        thread.start()
        for _ in range(100):
            if list((self.bridge_dir / "requests").glob("*.json")):
                break
            time.sleep(0.01)
        self.write_heartbeat(age_seconds=300, process_id=os.getpid())
        thread.join(timeout=2)

        self.assertFalse(thread.is_alive())
        self.assertIsInstance(outcome.get("error"), mcp.EditorUnresponsiveError)
        self.assertEqual(list((self.bridge_dir / "requests").glob("*.json")), [])

    def test_bridge_status_names_the_hung_editor(self) -> None:
        self.write_heartbeat(age_seconds=600, process_id=os.getpid())

        status = self.server.get_bridge_status({})

        self.assertEqual(status["editorState"], "unresponsive")
        self.assertEqual(status["editorProcessId"], os.getpid())
        self.assertTrue(status["hints"][0].startswith("editor_unresponsive:"))

    def test_own_script_running_when_the_heartbeat_stopped_is_named(self) -> None:
        self.write_heartbeat(age_seconds=90, process_id=os.getpid())
        path = self.bridge_dir / "state.json"
        payload = mcp.read_json_file(path)
        payload["busy"] = {"scriptBusy": True}
        mcp.write_json_file_atomic(path, payload)
        stamp = time.time() - 90
        os.utime(path, (stamp, stamp))
        self.server._editor_liveness_cache = None
        self.write_operation("script-execute", "running", age_seconds=90)

        message = self.server.describe_editor_down(self.server.assess_editor_liveness())

        self.assertIn("The last heartbeat shows script-execute running", message)
        self.assertNotIn("Unity heartbeat is stale.", " ".join(self.server.get_bridge_status({})["hints"]))

    def test_script_that_already_returned_is_not_named(self) -> None:
        # Client.Resolve() from a script: the script returned, the import it started holds the main thread, and the
        # last heartbeat still carries the script flag.
        self.write_heartbeat(age_seconds=90, process_id=os.getpid())
        path = self.bridge_dir / "state.json"
        payload = mcp.read_json_file(path)
        payload["busy"] = {"scriptBusy": True}
        mcp.write_json_file_atomic(path, payload)
        stamp = time.time() - 90
        os.utime(path, (stamp, stamp))
        self.write_operation("script-execute", "completed", age_seconds=91)
        self.server._editor_liveness_cache = None

        message = self.server.describe_editor_down(self.server.assess_editor_liveness())

        self.assertNotIn("script-execute", message)

    def test_editor_using_cpu_is_waited_for_instead_of_failing(self) -> None:
        # An import or package resolution started from a script raises no flag in the last heartbeat, but the
        # process works; a hung, sleeping or modal-blocked editor stays nearly idle.
        self.write_heartbeat(age_seconds=90, process_id=os.getpid())
        self.cpu_seconds = [100.0, 100.0 + mcp.EDITOR_CPU_SAMPLE_SECONDS * 0.9]

        liveness = self.server.assess_editor_liveness()

        self.assertEqual(liveness["state"], "busy", liveness)
        self.assertIn("% CPU", liveness["busySign"])
        self.assertIsNone(self.server.describe_editor_down(liveness))

        self.cpu_seconds = [200.0, 200.0 + mcp.EDITOR_CPU_SAMPLE_SECONDS * 0.05]
        self.server._editor_cpu_sample = None
        self.server._editor_liveness_cache = None
        self.assertEqual(self.server.assess_editor_liveness()["state"], "unresponsive")

    def write_operation(self, tool_name: str, state: str, age_seconds: float) -> None:
        operations = self.bridge_dir / "operations"
        operations.mkdir(exist_ok=True)
        path = operations / f"{tool_name}-{state}.json"
        mcp.write_json_file_atomic(path, {"operationId": path.stem, "toolName": tool_name, "state": state})
        stamp = time.time() - age_seconds
        os.utime(path, (stamp, stamp))

    def test_responsive_editor_adds_nothing_to_status(self) -> None:
        self.write_heartbeat(age_seconds=0, process_id=os.getpid())

        self.assertNotIn("editorState", self.server.get_bridge_status({}))

    def test_playmode_set_does_not_mistake_a_hung_editor_for_an_interrupted_toggle(self) -> None:
        self.write_heartbeat(age_seconds=3700, process_id=os.getpid())

        with self.assertRaises(mcp.EditorUnresponsiveError):
            self.server.editor_playmode_set({"isPlaying": True})


if __name__ == "__main__":
    unittest.main()
