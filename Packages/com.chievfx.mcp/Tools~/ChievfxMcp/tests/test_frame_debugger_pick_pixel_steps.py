import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class FrameDebuggerPickPixelStepTests(unittest.TestCase):
    """The Frame Debugger re-renders the Game view with a newly selected event only on a later editor tick, so a pixel
    search inside one bridge call compared copies of the same picture and always answered event 0 in Play Mode. The
    server now runs it one capture per call."""

    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)
        self.server = mcp.McpServer("http://127.0.0.1:1", self.temp_dir.name, timeout_ms=1000)
        sleep_patch = mock.patch("chievfx_mcp_server.time.sleep", return_value=None)
        self.sleep = sleep_patch.start()
        self.addCleanup(sleep_patch.stop)

    def test_steps_until_the_answer_with_a_pause_between_calls(self) -> None:
        answer = {"success": True, "writerEventIndex": 112, "eventCount": 181, "capturesTaken": 9}
        responses = [
            {"result": {"success": True, "pending": True, "eventCount": 181}},
            {"result": {"success": True, "pending": True, "low": 0, "high": 180}},
            {"result": answer},
        ]
        with mock.patch.object(self.server, "call_unity_bridge", side_effect=responses) as bridge:
            reply = self.server.run_frame_debugger_pixel_search({"x": 0.5, "y": 0.6}, None)
        self.assertEqual(reply["result"], answer)
        calls = [call.args[1] for call in bridge.call_args_list]
        self.assertEqual(calls[0], {"x": 0.5, "y": 0.6, "searchStep": "start"})
        self.assertEqual(calls[1:], [{"searchStep": "next"}, {"searchStep": "next"}])
        self.assertEqual(self.sleep.call_count, 2)

    def test_a_package_without_steps_answers_the_first_call(self) -> None:
        answer = {"success": True, "writerEventIndex": 0, "eventCount": 181}
        with mock.patch.object(self.server, "call_unity_bridge", side_effect=[{"result": answer}]) as bridge:
            reply = self.server.run_frame_debugger_pixel_search({"x": 0.1, "y": 0.2}, None)
        self.assertEqual(reply["result"], answer)
        self.assertEqual(bridge.call_count, 1)

    def test_a_search_that_never_ends_is_an_error(self) -> None:
        pending = {"result": {"success": True, "pending": True}}
        with mock.patch.object(self.server, "call_unity_bridge", return_value=pending):
            with self.assertRaises(RuntimeError):
                self.server.run_frame_debugger_pixel_search({"x": 0.1, "y": 0.2}, None)


if __name__ == "__main__":
    unittest.main()
