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


class RecompileIssueCollectionTests(unittest.TestCase):
    """The compile summary must report compiler diagnostics only. recompile stops Play Mode first, so
    the event window it scans also contains the project's own shutdown logs; one of those used to be
    counted as "1 error" for a compile that succeeded, and its (logger-inlined) stack trace filled the
    whole response."""

    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)
        self.server = mcp.McpServer("http://127.0.0.1:1", self.temp_dir.name, timeout_ms=1000)

    def _collect(self, events, since_event_id=100):
        stream = {"events": events, "lastEventId": max((e["eventId"] for e in events), default=0)}
        with mock.patch.object(self.server, "read_event_stream", return_value=stream):
            return self.server.collect_recompile_issues(since_event_id)

    @staticmethod
    def _event(event_id, level, message, event_type="message"):
        return {
            "eventId": event_id,
            "source": "log",
            "type": event_type,
            "level": level,
            "message": message,
        }

    def test_runtime_error_is_counted_not_reported_as_compile_error(self) -> None:
        issues = self._collect(
            [
                self._event(
                    101,
                    "Error",
                    "<b><color=#E90C7BFF>[UnityLogs] </color></b>FileBlocks_Loader. Fail to stop thread\n"
                    "at Common.Logging.MadLogger.Error (Assets/Scripts/Common/Logging/MadLogger.cs:52)",
                ),
                self._event(102, "Warning", "Some runtime warning"),
            ]
        )
        self.assertEqual(issues["errorCount"], 0)
        self.assertEqual(issues["warningCount"], 0)
        self.assertNotIn("errors", issues)
        self.assertEqual(issues["otherErrorCount"], 1)
        self.assertEqual(issues["otherWarningCount"], 1)

    def test_compiler_diagnostics_are_reported(self) -> None:
        issues = self._collect(
            [
                self._event(
                    101,
                    "Error",
                    "Assets/Scripts/Foo.cs(12,34): error CS0103: The name 'x' does not exist",
                    event_type="compiler-message",
                ),
                self._event(102, "Warning", "Assets/Scripts/Foo.cs(9,5): warning CS0649: Field never assigned"),
            ]
        )
        self.assertEqual(issues["errorCount"], 1)
        self.assertEqual(issues["warningCount"], 1)
        self.assertIn("CS0103", issues["errors"][0]["message"])
        self.assertIn("CS0649", issues["warnings"][0]["message"])
        self.assertNotIn("otherErrorCount", issues)

    def test_compiler_message_type_is_trusted_without_a_diagnostic_id(self) -> None:
        # Not every compiler message carries a CSxxxx id (assembly resolution failures, for one).
        issues = self._collect(
            [self._event(101, "Error", "Unable to resolve reference 'Foo'.", event_type="compiler-message")]
        )
        self.assertEqual(issues["errorCount"], 1)

    def test_duplicate_journaling_of_one_diagnostic_is_deduped_after_cleanup(self) -> None:
        # The compilation callback and the log callback each journal the same diagnostic; the console
        # copy can carry markup the compiler copy does not.
        issues = self._collect(
            [
                self._event(101, "Error", "Assets/Foo.cs(1,1): error CS0246: Missing type", event_type="compiler-message"),
                self._event(102, "Error", "<b>Assets/Foo.cs(1,1): error CS0246: Missing type</b>"),
            ]
        )
        self.assertEqual(issues["errorCount"], 1)

    def test_shader_diagnostics_are_reported_without_their_boilerplate(self) -> None:
        # Captured verbatim from Unity 6 (macOS/metal). Shaders compile outside CompilationPipeline, so
        # there is no compiler-message event and no CSxxxx id — only the console line identifies them.
        # Unity appends the same ~800-char "Compiling Subshader / Platform defines / Disabled keywords"
        # block to every one of them, which would otherwise spend the whole per-message budget.
        message = (
            "Shader error in 'Hidden/Probe': undeclared identifier 'foo' at "
            "Assets/Probe.shader(17) (on metal)\n\n"
            "Compiling Subshader: 0, Pass: <Unnamed Pass 0>, Fragment program with <no keywords>\n"
            "Platform defines: SHADER_API_DESKTOP UNITY_ENABLE_DETAIL_NORMALMAP\n"
            "Disabled keywords: SHADER_API_GLES30 UNITY_COLORSPACE_GAMMA"
        )
        issues = self._collect(
            [
                self._event(101, "Error", message),
                self._event(102, "Warning", "Shader warning in 'Hidden/Probe': implicit truncation at line 12 (on metal)"),
            ]
        )
        self.assertEqual(issues["errorCount"], 1)
        self.assertEqual(issues["warningCount"], 1)
        self.assertEqual(
            issues["errors"][0]["message"],
            "Shader error in 'Hidden/Probe': undeclared identifier 'foo' at Assets/Probe.shader(17) (on metal)",
        )

    def test_shader_runtime_complaints_are_not_compile_output(self) -> None:
        issues = self._collect(
            [self._event(101, "Warning", "Shader 'Custom/Water': fallback shader 'Diffuse' not found")]
        )
        self.assertEqual(issues["warningCount"], 0)
        self.assertEqual(issues["otherWarningCount"], 1)

    def test_console_markup_is_stripped_from_reported_diagnostics(self) -> None:
        issues = self._collect(
            [
                self._event(
                    101,
                    "Error",
                    '<b><color=#E90C7BFF>Assets/Foo.cs(1,1)</color></b>: error CS0246: missing '
                    '<a href="Assets/Foo.cs" line="1">Assets/Foo.cs</a>',
                )
            ]
        )
        self.assertEqual(
            issues["errors"][0]["message"],
            "Assets/Foo.cs(1,1): error CS0246: missing Assets/Foo.cs",
        )

    def test_long_message_is_capped(self) -> None:
        issues = self._collect([self._event(101, "Error", "error CS0103: " + "x" * 5000)])
        message = issues["errors"][0]["message"]
        self.assertEqual(len(message), mcp.RECOMPILE_MAX_ISSUE_CHARS)
        self.assertTrue(message.endswith("..."))


class RecompileTextFormatTests(unittest.TestCase):
    def test_non_compile_logs_are_summarized_not_echoed(self) -> None:
        text = mcp.format_recompile_text(
            {
                "completed": True,
                "exitedPlayMode": True,
                "compile": {
                    "errorCount": 0,
                    "warningCount": 0,
                    "sinceEventId": 4200,
                    "otherErrorCount": 2,
                    "otherWarningCount": 1,
                },
            }
        )
        self.assertIn("recompile completed — 0 errors, 0 warnings", text)
        self.assertIn("Also logged, not compile output: 2 errors, 1 warning", text)
        self.assertIn("sinceEventId:4200", text)

    def test_clean_summary_has_no_extra_line(self) -> None:
        text = mcp.format_recompile_text(
            {"completed": True, "compile": {"errorCount": 0, "warningCount": 0, "sinceEventId": 1}}
        )
        self.assertEqual(text, "recompile completed — 0 errors, 0 warnings")


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
