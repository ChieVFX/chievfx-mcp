import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


UNAVAILABLE = "FrameDebuggerUtility.GetFrameEventData unavailable for event 112: "


def reply(event_count, frame_event, diagnostics=()):
    return {
        "frameDebugger": {"enabled": True, "eventCount": event_count},
        "frameEvent": frame_event,
        "diagnostics": list(diagnostics),
    }


class FrameDebuggerSettleTests(unittest.TestCase):
    """Unity fills an event's details only after the editor re-renders with it selected, a tick after the tool selected
    it; in Play Mode repaints can also capture different frames, so numbers from one reply pointed to other draw calls
    in the next without any sign of it."""

    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)
        self.server = mcp.McpServer("http://127.0.0.1:1", self.temp_dir.name, timeout_ms=1000)
        sleep_patch = mock.patch("chievfx_mcp_server.time.sleep", return_value=None)
        sleep_patch.start()
        self.addCleanup(sleep_patch.stop)

    def _settle(self, name, arguments, result, retries=()):
        responses = [{"result": retry} for retry in retries]
        with mock.patch.object(self.server, "call_unity_bridge", side_effect=responses) as bridge:
            settled = self.server.settle_frame_debugger_result(name, arguments, result, None)
        return settled, bridge

    def test_missing_details_are_read_again_and_keep_the_draw_call_position(self) -> None:
        first = reply(196, {"index": 112, "name": "UI", "groupIndex": 11, "drawCallIndex": 26}, [UNAVAILABLE])
        ready = reply(196, {"index": 112, "name": "UI", "shader": "UI/Default", "verts": 4})
        settled, bridge = self._settle("frame-debugger-drawcall-get", {"groupIndex": 11, "drawCallIndex": 26}, first, [ready])
        self.assertEqual(settled["frameEvent"]["shader"], "UI/Default")
        self.assertEqual(settled["frameEvent"]["groupIndex"], 11)
        self.assertEqual(settled["frameEvent"]["drawCallIndex"], 26)
        self.assertFalse(any("GetFrameEventData" in d for d in settled["diagnostics"]))
        bridge.assert_called_once_with("frame-debugger-event-get", {"eventIndex": 112, "select": False}, None, None, None)

    def test_frame_change_before_details_is_reported_instead_of_merging(self) -> None:
        first = reply(196, {"index": 112}, [UNAVAILABLE])
        other_frame = reply(199, {"index": 112, "shader": "Other"})
        retries = [other_frame] * mcp.FRAME_DEBUGGER_DETAIL_RETRIES
        settled, _ = self._settle("frame-debugger-event-get", {"eventIndex": 112}, first, retries)
        self.assertNotIn("shader", settled["frameEvent"])
        self.assertTrue(any("196 -> 199" in d for d in settled["diagnostics"]))

    def test_alternating_frames_are_read_again_until_the_same_frame_comes_back(self) -> None:
        # In Play Mode repaints alternate between two frames: the details come from a later repaint of the same frame.
        first = reply(196, {"index": 112}, [UNAVAILABLE])
        other_frame = reply(199, {"index": 112, "shader": "Other"})
        same_frame = reply(196, {"index": 112, "shader": "UI/Default"})
        settled, bridge = self._settle("frame-debugger-event-get", {"eventIndex": 112}, first, [other_frame, same_frame])
        self.assertEqual(settled["frameEvent"]["shader"], "UI/Default")
        self.assertEqual(bridge.call_count, 2)

    def test_turning_the_debugger_on_does_not_compare_with_the_previous_session(self) -> None:
        self.server._frame_debugger_event_count = 181
        self.server.settle_frame_debugger_result("frame-debugger-control", {"enabled": True}, reply(0, None), None)
        listed = self.server.settle_frame_debugger_result("frame-debugger-groups-list", {}, reply(218, None), None)
        self.assertFalse(any("changed" in d for d in listed["diagnostics"]))

    def test_frame_change_outside_play_mode_does_not_talk_about_play_mode(self) -> None:
        self.server._frame_debugger_event_count = 234
        listed = self.server.settle_frame_debugger_result("frame-debugger-events-list", {}, reply(4, None), None)
        self.assertTrue(any("234 -> 4" in d for d in listed["diagnostics"]))
        self.assertFalse(any("Play Mode" in d for d in listed["diagnostics"]))

    def test_select_false_is_not_retried_and_says_to_select(self) -> None:
        first = reply(196, {"index": 112}, [UNAVAILABLE])
        settled, bridge = self._settle("frame-debugger-event-get", {"eventIndex": 112, "select": False}, first)
        bridge.assert_not_called()
        self.assertTrue(any("select:true" in d for d in settled["diagnostics"]))
        self.assertFalse(any("GetFrameEventData" in d for d in settled["diagnostics"]))

    def test_details_still_missing_after_retries_say_why(self) -> None:
        first = reply(196, {"index": 112}, [UNAVAILABLE])
        retries = [reply(196, {"index": 112}, [UNAVAILABLE])] * mcp.FRAME_DEBUGGER_DETAIL_RETRIES
        settled, bridge = self._settle("frame-debugger-event-get", {"eventIndex": 112}, first, retries)
        self.assertEqual(bridge.call_count, mcp.FRAME_DEBUGGER_DETAIL_RETRIES)
        self.assertTrue(any("only for the selected event" in d for d in settled["diagnostics"]))

    def test_frame_change_between_replies_is_flagged(self) -> None:
        first, _ = self._settle("frame-debugger-groups-list", {}, reply(196, None))
        self.assertEqual(first["diagnostics"], [])
        second, _ = self._settle("frame-debugger-group-events-list", {"groupIndex": 11}, reply(199, None))
        self.assertTrue(any("196 -> 199" in d for d in second["diagnostics"]))
        third, _ = self._settle("frame-debugger-groups-list", {}, reply(199, None))
        self.assertEqual(third["diagnostics"], [])

    def test_enabling_from_zero_events_is_not_a_frame_change(self) -> None:
        self._settle("frame-debugger-control", {"enabled": False}, reply(0, None))
        settled, _ = self._settle("frame-debugger-events-list", {}, reply(196, None))
        self.assertEqual(settled["diagnostics"], [])

    def test_pick_pixel_count_is_read_from_its_top_level(self) -> None:
        self._settle("frame-debugger-groups-list", {}, reply(196, None))
        pick = {"eventCount": 199, "writerEventIndex": 115, "frameEvent": {"index": 115, "shader": "UI/Default"}, "diagnostics": []}
        settled, _ = self._settle("frame-debugger-pick-pixel", {"x": 0.1, "y": 0.1}, pick)
        self.assertTrue(any("196 -> 199" in d for d in settled["diagnostics"]))

    def test_play_pause_shows_in_the_state_line(self) -> None:
        text = mcp.format_frame_debugger_control_text(
            {"success": True, "frameDebugger": {"enabled": True, "eventCount": 196, "playPaused": True}, "diagnostics": []}
        )
        self.assertIn("playPaused:true", text)


if __name__ == "__main__":
    unittest.main()
