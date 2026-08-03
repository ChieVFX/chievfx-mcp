import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class RecompilePlaymodeTests(unittest.TestCase):
    """recompile must stop Play Mode first: Unity does not compile scripts on demand while playing,
    so a request issued during play either vanishes or parks as a pending compile that pins
    isCompiling true — which used to burn the whole timeout without ever compiling."""

    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)
        self.server = mcp.McpServer("http://127.0.0.1:1", self.temp_dir.name, timeout_ms=1000)
        # The 'let Unity enter compile state' grace is pure wall clock in a test.
        sleep_patch = mock.patch("chievfx_mcp_server.time.sleep", return_value=None)
        sleep_patch.start()
        self.addCleanup(sleep_patch.stop)

    def _run(self, arguments, *, playing, bridge_result=None, bridge_error=None, exited=True):
        ready_mock = mock.Mock(return_value=True)
        playmode_wait = mock.Mock(return_value=(exited, 120))
        bridge_kwargs = (
            {"side_effect": bridge_error}
            if bridge_error is not None
            else {"return_value": {"result": dict(bridge_result or {})}}
        )
        with mock.patch.object(self.server, "read_playmode_state", return_value=playing), mock.patch.object(
            self.server, "wait_for_bridge_ready", ready_mock
        ), mock.patch.object(self.server, "wait_for_playmode", playmode_wait), mock.patch.object(
            self.server, "collect_recompile_issues", return_value={"errorCount": 0, "warningCount": 0}
        ), mock.patch.object(
            self.server, "call_unity_bridge", **bridge_kwargs
        ) as bridge_mock:
            result = self.server.recompile(arguments)
        return result, ready_mock, playmode_wait, bridge_mock

    def test_edit_mode_still_waits_for_idle_before_requesting(self) -> None:
        result, ready_mock, playmode_wait, bridge_mock = self._run(
            {"timeoutMs": 1000}, playing=False, bridge_result={"requested": True}
        )
        # Pre-wait plus post-wait.
        self.assertEqual(ready_mock.call_count, 2)
        self.assertEqual(result["readyBeforeRequest"], True)
        playmode_wait.assert_not_called()
        bridge_mock.assert_called_once()
        self.assertNotIn("exitedPlayMode", result)

    def test_play_mode_skips_pre_idle_wait(self) -> None:
        result, ready_mock, _playmode_wait, _bridge = self._run(
            {"timeoutMs": 1000},
            playing=True,
            bridge_result={"requested": True, "exitedPlayMode": True, "wasPlaying": True},
        )
        # Only the post-request wait: pre-waiting on a compile Play Mode is holding never clears.
        self.assertEqual(ready_mock.call_count, 1)
        self.assertIsNone(result["readyBeforeRequest"])

    def test_play_mode_waits_for_exit_before_compile_wait(self) -> None:
        result, _ready, playmode_wait, _bridge = self._run(
            {"timeoutMs": 1000},
            playing=True,
            bridge_result={"requested": True, "exitedPlayMode": True, "wasPlaying": True},
        )
        playmode_wait.assert_called_once()
        self.assertIs(playmode_wait.call_args.args[0], False)
        self.assertTrue(result["playModeExited"])
        self.assertEqual(result["playModeExitWaitedMs"], 120)
        self.assertNotIn("warning", result)

    def test_exit_wait_is_capped_by_timeout_ms(self) -> None:
        _result, _ready, playmode_wait, _bridge = self._run(
            {"timeoutMs": 2000},
            playing=True,
            bridge_result={"exitedPlayMode": True},
        )
        # min(timeoutMs, PLAYMODE_EXIT_WAIT_TIMEOUT_SECONDS) — the caller's budget wins when smaller.
        self.assertEqual(playmode_wait.call_args.args[1], 2.0)

    def test_failed_exit_reports_why_nothing_compiled(self) -> None:
        result, _ready, _playmode_wait, _bridge = self._run(
            {"timeoutMs": 1000},
            playing=True,
            bridge_result={"exitedPlayMode": True},
            exited=False,
        )
        self.assertFalse(result["playModeExited"])
        self.assertIn("Play Mode did not exit", result["warning"])
        self.assertIn("does not compile while playing", result["warning"])

    def test_timeout_warning_keeps_play_mode_explanation(self) -> None:
        playmode_wait = mock.Mock(return_value=(False, 30))
        with mock.patch.object(self.server, "read_playmode_state", return_value=True), mock.patch.object(
            self.server, "wait_for_bridge_ready", return_value=False
        ), mock.patch.object(self.server, "wait_for_playmode", playmode_wait), mock.patch.object(
            self.server, "collect_recompile_issues", return_value={}
        ), mock.patch.object(
            self.server, "call_unity_bridge", return_value={"result": {"exitedPlayMode": True}}
        ):
            result = self.server.recompile({"timeoutMs": 1000})
        self.assertIn("Play Mode did not exit", result["warning"])
        self.assertIn("Timed out waiting for Unity compile/import busy state to clear.", result["warning"])
        self.assertFalse(result["completed"])

    def test_interrupted_round_trip_while_playing_is_recovered(self) -> None:
        result, _ready, playmode_wait, _bridge = self._run(
            {"timeoutMs": 1000},
            playing=True,
            bridge_error=RuntimeError("bridge unavailable"),
        )
        # Leaving Play Mode domain-reloads Unity, which can eat the response; the editor still took it.
        self.assertTrue(result["bridgeRoundTripInterrupted"])
        self.assertTrue(result["exitedPlayMode"])
        playmode_wait.assert_called_once()

    def test_interrupted_round_trip_in_edit_mode_still_raises(self) -> None:
        with mock.patch.object(self.server, "read_playmode_state", return_value=False), mock.patch.object(
            self.server, "wait_for_bridge_ready", return_value=True
        ), mock.patch.object(
            self.server, "call_unity_bridge", side_effect=RuntimeError("bridge unavailable")
        ):
            with self.assertRaises(RuntimeError):
                self.server.recompile({"timeoutMs": 1000})


class CompileBlockedStatusHintTests(unittest.TestCase):
    """bridge-get-status must not present a Play-Mode-held compile as work in progress."""

    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)
        self.server = mcp.McpServer("http://127.0.0.1:1", self.temp_dir.name, timeout_ms=1000)

    def test_hint_added_when_compile_waits_for_play_mode_exit(self) -> None:
        hints = self.server.get_status_hints(
            True, 0.1, [], 0, 0, compile_waiting_for_play_mode_exit=True
        )
        self.assertTrue(any("holding until Play Mode exits" in hint for hint in hints))
        self.assertTrue(any("recompile" in hint for hint in hints))

    def test_no_hint_when_compiling_normally(self) -> None:
        hints = self.server.get_status_hints(True, 0.1, [], 0, 0)
        self.assertEqual(hints, [])


if __name__ == "__main__":
    unittest.main()
