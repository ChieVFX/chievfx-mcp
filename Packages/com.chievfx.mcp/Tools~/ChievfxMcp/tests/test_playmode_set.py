import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class PlaymodeSetTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)
        self.server = mcp.McpServer("http://127.0.0.1:1", self.temp_dir.name, timeout_ms=1000)

    def _run(self, arguments):
        return self.server.editor_playmode_set(arguments)

    def test_normal_enter_confirms_via_heartbeat(self) -> None:
        with mock.patch.object(
            self.server,
            "call_unity_bridge",
            return_value={"result": {"ok": True, "status": "requested", "requestedIsPlaying": True, "isPlaying": False}},
        ), mock.patch.object(self.server, "read_playmode_state", return_value=True):
            result = self._run({"isPlaying": True, "settleMs": 0})
        self.assertTrue(result["ok"])
        self.assertTrue(result["isPlaying"])
        self.assertTrue(result["playmodeReady"])
        self.assertNotIn("bridgeRoundTripInterrupted", result)

    def test_domain_reload_interrupts_round_trip_recovers_as_success(self) -> None:
        # Entering Play Mode domain-reloads Unity; the bridge round-trip can be lost/time out. That must
        # NOT surface as a failure — confirm the real state from the heartbeat and report success.
        with mock.patch.object(
            self.server,
            "call_unity_bridge",
            side_effect=RuntimeError("Unity bridge unavailable at http://x: <urlopen error>. hint"),
        ), mock.patch.object(self.server, "read_playmode_state", return_value=True):
            result = self._run({"isPlaying": True, "settleMs": 0})
        self.assertTrue(result["ok"])
        self.assertTrue(result["isPlaying"])
        self.assertTrue(result["playmodeReady"])
        self.assertTrue(result["bridgeRoundTripInterrupted"])

    def test_enter_default_timeout_is_larger_than_exit(self) -> None:
        # Entering domain-reloads Unity and can run tens of seconds on a large/slow project, so its
        # default wait must be much bigger than exit's. Capture the timeout handed to wait_for_playmode.
        captured: dict[str, float] = {}

        def fake_wait(requested, timeout_seconds, *a, **k):
            captured["enter" if requested else "exit"] = timeout_seconds
            return True, 0

        for requested in (True, False):
            with mock.patch.object(
                self.server,
                "call_unity_bridge",
                return_value={"result": {"ok": True, "requestedIsPlaying": requested, "isPlaying": requested}},
            ), mock.patch.object(self.server, "wait_for_playmode", side_effect=fake_wait), mock.patch.object(
                self.server, "read_playmode_state", return_value=requested
            ):
                self._run({"isPlaying": requested, "settleMs": 0})

        self.assertGreater(captured["enter"], captured["exit"])
        self.assertGreaterEqual(captured["enter"], 120.0)

    def test_explicit_timeout_ms_overrides_default(self) -> None:
        captured: dict[str, float] = {}

        def fake_wait(requested, timeout_seconds, *a, **k):
            captured["t"] = timeout_seconds
            return True, 0

        with mock.patch.object(
            self.server,
            "call_unity_bridge",
            return_value={"result": {"ok": True, "requestedIsPlaying": True, "isPlaying": False}},
        ), mock.patch.object(self.server, "wait_for_playmode", side_effect=fake_wait), mock.patch.object(
            self.server, "read_playmode_state", return_value=True
        ):
            self._run({"isPlaying": True, "settleMs": 0, "timeoutMs": 5000})
        self.assertEqual(captured["t"], 5.0)

    def test_interrupted_without_wait_for_ready_reraises(self) -> None:
        # With waitForReady off we cannot confirm the state, so a genuine bridge error must propagate
        # rather than be faked into a success.
        with mock.patch.object(
            self.server,
            "call_unity_bridge",
            side_effect=RuntimeError("Unity bridge unavailable."),
        ), mock.patch.object(self.server, "read_playmode_state", return_value=True):
            with self.assertRaises(RuntimeError):
                self._run({"isPlaying": True, "waitForReady": False})


if __name__ == "__main__":
    unittest.main()
