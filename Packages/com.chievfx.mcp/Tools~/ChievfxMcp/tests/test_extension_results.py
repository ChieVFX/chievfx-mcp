import json
import sys
import tempfile
import unittest
from pathlib import Path
from typing import Any


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


DEMO_EXTENSION = {
    "id": "demo.ext",
    "displayName": "Demo",
    "version": "1.0.0",
    "tools": [
        {
            "name": "demo-act",
            "description": "Starts an action that ends later.",
            "category": "demo",
            "inputSchema": {"type": "object", "properties": {"target": {"type": "string"}}, "additionalProperties": True},
        },
        {
            "name": "demo-record",
            "description": "Writes its own file.",
            "category": "demo",
            "inputSchema": {"type": "object", "properties": {"savePath": {"type": "string"}}, "additionalProperties": True},
        },
        {
            "name": "demo-closed",
            "description": "Closed schema.",
            "category": "demo",
            "inputSchema": {"type": "object", "properties": {"target": {"type": "string"}}},
        },
    ],
}


class ExtensionResultTests(unittest.TestCase):
    """Extension replies: a summary headline, open schemas judged by the extension, and awaitMs."""

    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)
        root = Path(self.temp_dir.name)
        self.bridge_dir = root / "Library" / "ChievfxMcpBridge"
        self.bridge_dir.mkdir(parents=True)
        self._originals = {
            name: getattr(mcp, name)
            for name in (
                "TOOL_SELECTION_PATH",
                "RESOURCE_SELECTION_PATH",
                "CATEGORY_SELECTION_PATH",
                "AVAILABILITY_SETTINGS_PATH",
                "EXTENSION_CAPABILITY_MANIFEST_PATH",
                "PROJECT_ROOT",
            )
        }
        mcp.TOOL_SELECTION_PATH = root / "UserSettings" / "ChievfxMcpToolSelection.json"
        mcp.RESOURCE_SELECTION_PATH = root / "UserSettings" / "ChievfxMcpResourceSelection.json"
        mcp.CATEGORY_SELECTION_PATH = root / "UserSettings" / "ChievfxMcpCategorySelection.json"
        mcp.AVAILABILITY_SETTINGS_PATH = root / "UserSettings" / "ChievfxMcpAvailability.json"
        mcp.EXTENSION_CAPABILITY_MANIFEST_PATH = self.bridge_dir / "extension-capabilities.snapshot.json"
        mcp.PROJECT_ROOT = root
        self.addCleanup(self.restore)
        self.server = mcp.McpServer("http://127.0.0.1:1", str(self.bridge_dir), timeout_ms=1000)
        # After the server: its constructor installs the live-bridge fetcher.
        mcp.configure_extension_manifest_bridge_fetcher(
            lambda: {"schemaVersion": mcp.EXTENSION_CAPABILITY_MANIFEST_SCHEMA_VERSION, "extensions": [json.loads(json.dumps(DEMO_EXTENSION))]}
        )
        mcp.invalidate_extension_manifest_cache()
        self.server.core_descriptors_read = True
        self.bridge_calls: list[tuple[str, dict[str, Any]]] = []
        self.write_events([])

    def restore(self) -> None:
        for name, value in self._originals.items():
            setattr(mcp, name, value)
        mcp.configure_extension_manifest_bridge_fetcher(None)
        mcp.invalidate_extension_manifest_cache()

    def write_events(self, events: list[dict[str, Any]]) -> None:
        mcp.write_json_file_atomic(
            self.bridge_dir / "events.json",
            {
                "lastEventId": max((event["eventId"] for event in events), default=0),
                "truncatedBeforeEventId": 0,
                "events": events,
            },
        )

    def log_event(self, event_id: int, message: str) -> dict[str, Any]:
        return {"eventId": event_id, "timestamp": mcp.utc_now_iso(), "source": "log", "type": "log", "level": "Log", "message": message}

    def fake_bridge(self, reply: Any, events_during_call: list[dict[str, Any]] | None = None):
        def call(name: str, arguments: dict[str, Any], *args: Any, **kwargs: Any) -> dict[str, Any]:
            self.bridge_calls.append((name, dict(arguments)))
            if events_during_call is not None:
                self.write_events(events_during_call)
            return {"result": reply}

        self.server.call_unity_bridge = call  # type: ignore[method-assign]

    def call(self, name: str, arguments: dict[str, Any]) -> dict[str, Any]:
        return self.server.call_tool({"name": name, "arguments": arguments})

    @staticmethod
    def text_of(result: dict[str, Any]) -> str:
        return "\n".join(block["text"] for block in result["content"] if block.get("type") == "text")

    def test_summary_becomes_the_headline(self) -> None:
        self.fake_bridge({"summary": "Tapped Play at (0.5, 0.8)", "control": "Play", "ok": True})

        text = self.text_of(self.call("demo-act", {"target": "Play"}))

        self.assertTrue(text.startswith("Tapped Play at (0.5, 0.8)\n"), text)
        self.assertNotIn("summary", text)
        self.assertIn("control", text)

    def test_summary_alone_is_just_the_line(self) -> None:
        self.fake_bridge({"summary": "Waited 1 s"})

        self.assertEqual(self.text_of(self.call("demo-act", {})), "Waited 1 s")

    def test_non_string_summary_stays_a_field(self) -> None:
        self.fake_bridge({"summary": {"passed": 3}, "ok": True})

        text = self.text_of(self.call("demo-act", {}))

        self.assertIn("summary", text)

    def test_json_output_keeps_the_summary_field(self) -> None:
        self.fake_bridge({"summary": "Tapped", "ok": True})

        payload = json.loads(self.call("demo-act", {"outputFormat": "json"})["content"][0]["text"])

        self.assertEqual(payload["summary"], "Tapped")

    def test_open_extension_schema_gets_no_unrecognized_warning(self) -> None:
        self.assertEqual(mcp.unknown_tool_arguments("demo-act", {"targetName": "Play"}), [])

    def test_closed_extension_schema_still_warns(self) -> None:
        self.assertEqual(mcp.unknown_tool_arguments("demo-closed", {"targetName": "Play"}), ["targetName"])

    def test_await_ms_waits_for_the_end_the_reply_names(self) -> None:
        # The end is logged while the tool is still replying, which is exactly what a separate
        # events-wait started afterwards would skip.
        self.write_events([self.log_event(1, "[demo] boot")])
        self.fake_bridge(
            {"summary": "Driving", "waitFor": "[demo][task] Drive#3", "sinceEventId": 1},
            events_during_call=[self.log_event(1, "[demo] boot"), self.log_event(2, "[demo][task] Drive#3 done arrived")],
        )

        result = self.call("demo-act", {"target": "home", "awaitMs": 2000})

        text = self.text_of(result)
        # The end comes first: the summary was written when the action started.
        lines = text.splitlines()
        self.assertTrue(lines[0].startswith("awaited"), text)
        self.assertIn("#2 [demo][task] Drive#3 done arrived", lines[0])
        self.assertEqual(lines[1], "at start: Driving")
        # One cursor only: the reply's own is for waiting by hand.
        self.assertNotIn("sinceEventId", text)
        # The bridge (and the extension behind it) never sees awaitMs.
        self.assertEqual(self.bridge_calls, [("demo-act", {"target": "home"})])

    def test_await_ms_keeps_a_long_end_whole(self) -> None:
        # The end line is the action's result: a hint at its tail must not be cut like a row in an event list.
        end = "[demo][skill-end] boot#4 failed:connect_failed " + "detail=x " * 25 + "hint: pick a server directly"
        self.write_events([self.log_event(1, "[demo] boot")])
        self.fake_bridge(
            {"summary": "Boot started", "ok": True, "waitFor": "[demo][skill-end] boot#4 ", "waitForFailed": "[demo][skill-end] boot#4 failed"},
            events_during_call=[self.log_event(1, "[demo] boot"), self.log_event(2, end)],
        )

        text = self.text_of(self.call("demo-act", {"awaitMs": 2000}))
        self.assertIn("hint: pick a server directly", text.splitlines()[0])

        batch = self.text_of(self.call("tool-batch", {"steps": [{"tool": "demo-act", "args": {"awaitMs": 2000}}]}))
        self.assertIn("the awaited end is a failure: #2 " + end, batch)

    def test_await_ms_timeout_says_how_to_keep_waiting(self) -> None:
        self.write_events([self.log_event(5, "[demo] idle")])
        self.fake_bridge({"waitFor": "[demo][task] Drive#4"})

        text = self.text_of(self.call("demo-act", {"awaitMs": 30}))

        self.assertIn("not ended yet", text)
        self.assertIn('events-wait contains:"[demo][task] Drive#4" sinceEventId:5', text)

    def test_await_ms_without_wait_for_says_it_was_ignored(self) -> None:
        self.fake_bridge({"summary": "Tapped"})

        text = self.text_of(self.call("demo-act", {"awaitMs": 1000}))

        self.assertIn("awaitMs ignored", text)

    def test_await_ms_follows_a_completion_marker(self) -> None:
        self.fake_bridge(
            {"completionMarker": "tap-42"},
            events_during_call=[{**self.log_event(3, "MCPEventReachedLocation(tap-42)"), "marker": "tap-42"}],
        )

        text = self.text_of(self.call("demo-act", {"awaitMs": 2000}))

        self.assertIn("awaited", text)
        self.assertIn("#3", text)

    def test_await_ms_on_events_wait_is_an_error(self) -> None:
        with self.assertRaises(mcp.ToolCallError):
            self.call("events-wait", {"contains": "x", "awaitMs": 10})

    def test_await_ms_as_a_numeric_string_is_accepted(self) -> None:
        # A client whose schema lacks awaitMs sends it as a string.
        self.fake_bridge({"completionMarker": "tap-7"}, events_during_call=[{**self.log_event(3, "tap done"), "marker": "tap-7"}])

        text = self.text_of(self.call("demo-act", {"awaitMs": "2000"}))

        self.assertIn("awaited", text)
        self.assertEqual(self.bridge_calls, [("demo-act", {})])

    def test_await_ms_drops_the_replys_own_wait_advice(self) -> None:
        reply = {
            "summary": "Driving",
            "waitFor": "[demo][task] Drive#5",
            "wait": "pass awaitMs (e.g. 20000) to get the end in the reply",
            "hint": "Await: pass awaitMs to this call next time, or events-wait marker=x",
            "target": "home",
        }
        self.fake_bridge(dict(reply), events_during_call=[self.log_event(4, "[demo][task] Drive#5 done")])

        awaited = self.text_of(self.call("demo-act", {"awaitMs": 2000}))
        self.fake_bridge(dict(reply))
        plain = self.text_of(self.call("demo-act", {}))

        self.assertNotIn("pass awaitMs", awaited)
        self.assertIn("home", awaited)
        self.assertIn("pass awaitMs", plain)

    def test_reply_with_wait_for_gets_the_ready_wait_call(self) -> None:
        self.fake_bridge({"summary": "Driving", "waitFor": "[demo][task] Drive#5 ", "eventCursorBefore": 41})

        text = self.text_of(self.call("demo-act", {}))

        self.assertIn('events-wait {"contains":"[demo][task] Drive#5 ","sinceEventId":41,"timeoutMs":30000}', text)
        self.assertIn("or pass awaitMs to this call", text)
        # The cursor is inside the ready call: a separate row would be the same number twice.
        self.assertNotIn("eventCursorBefore", text)

    def test_extension_cursor_and_timeout_win(self) -> None:
        self.fake_bridge(
            {"waitFor": ["[demo] #7 window", "[demo] #7 skipped"], "sinceEventId": 12, "eventCursorBefore": 41, "waitTimeoutMs": 90000}
        )

        result = self.call("demo-act", {"outputFormat": "json"})
        reply = json.loads(self.text_of(result))

        self.assertEqual(
            reply["next"],
            'events-wait {"contains":["[demo] #7 window","[demo] #7 skipped"],"sinceEventId":12,"timeoutMs":90000}'
            " (or pass awaitMs to this call to get the end in its reply)",
        )
        self.assertNotIn("waitTimeoutMs", reply)

    def test_completion_marker_without_cursor_waits_back_a_minute(self) -> None:
        self.fake_bridge({"completionMarker": "beacon-3"})

        text = self.text_of(self.call("demo-act", {}))

        self.assertIn('events-wait {"marker":"beacon-3","includeRecentMs":60000,"timeoutMs":30000}', text)

    def test_extension_own_next_is_kept(self) -> None:
        self.fake_bridge({"waitFor": "[demo] #1 ", "next": "gameai-wait task:1"})

        self.assertIn("gameai-wait task:1", self.text_of(self.call("demo-act", {})))

    def test_reply_without_an_end_gets_no_next(self) -> None:
        self.fake_bridge({"summary": "Tapped", "ok": True})

        self.assertNotIn("events-wait", self.text_of(self.call("demo-act", {})))

    def test_awaited_call_gets_no_next(self) -> None:
        self.fake_bridge({"waitFor": "[demo][task] Drive#6 "}, events_during_call=[self.log_event(4, "[demo][task] Drive#6 done")])

        self.assertNotIn("or pass awaitMs", self.text_of(self.call("demo-act", {"awaitMs": 2000})))

    def test_batch_stops_at_a_step_that_replies_with_its_failure(self) -> None:
        self.fake_bridge({"success": False, "stage": "runtime", "error": "boom-in-step"})

        result = self.call("tool-batch", {"steps": [{"tool": "demo-act", "args": {}}, {"waitMs": 1}]})

        text = self.text_of(result)
        self.assertTrue(text.startswith("tool-batch: 0 of 2 ok, 1 failed, 1 not run"), text)
        self.assertIn("1 demo-act FAILED: replied success:false (stage:runtime): boom-in-step", text)

    def test_batch_ok_false_reply_counts_as_failed_but_runs_on_when_asked(self) -> None:
        self.fake_bridge({"ok": False, "status": "failed", "errors": ["Play Mode required."]})

        result = self.call("tool-batch", {"steps": [{"tool": "demo-act", "args": {}}, {"waitMs": 1}], "stopOnError": False})

        text = self.text_of(result)
        self.assertTrue(text.startswith("tool-batch: 1 of 2 ok, 1 failed"), text)
        self.assertIn("FAILED: replied ok:false: Play Mode required.", text)

    def test_batch_step_with_both_tool_and_wait_ms_is_rejected_before_running(self) -> None:
        self.fake_bridge({"summary": "Driving"})

        with self.assertRaises(mcp.ToolCallError) as caught:
            self.call("tool-batch", {"steps": [{"tool": "demo-act", "args": {}, "waitMs": 500}]})

        self.assertIn("a pause is a step of its own", str(caught.exception))
        self.assertEqual(self.bridge_calls, [])

    def test_batch_step_with_await_ms_reports_the_end(self) -> None:
        self.write_events([self.log_event(1, "[demo] boot")])
        self.fake_bridge(
            {"summary": "Driving", "waitFor": "[demo][task] Drive#3"},
            events_during_call=[self.log_event(1, "[demo] boot"), self.log_event(2, "[demo][task] Drive#3 done")],
        )

        result = self.call("tool-batch", {"steps": [{"tool": "demo-act", "args": {"awaitMs": 2000}}, {"waitMs": 1}]})

        text = self.text_of(result)
        self.assertTrue(text.startswith("tool-batch: 2 of 2 ok"), text)
        self.assertIn("\n1 demo-act: awaited", text)
        self.assertIn("\n  at start: Driving", text)
        self.assertIn("#2 [demo][task] Drive#3 done", text)

    def test_batch_stops_at_a_step_whose_awaited_end_is_a_failure(self) -> None:
        self.write_events([self.log_event(1, "[demo] boot")])
        self.fake_bridge(
            {"summary": "Wait #7 started", "ok": True, "waitFor": "[demo][wait] #7 ", "waitForFailed": "[demo][wait] #7 failed"},
            events_during_call=[self.log_event(1, "[demo] boot"), self.log_event(2, "[demo][wait] #7 failed:timeout ui not active")],
        )

        result = self.call("tool-batch", {"steps": [{"tool": "demo-act", "args": {"awaitMs": 2000}}, {"waitMs": 1}]})

        text = self.text_of(result)
        self.assertTrue(text.startswith("tool-batch: 0 of 2 ok, 1 failed, 1 not run"), text)
        self.assertIn("1 demo-act FAILED: replied ok:false: the awaited end is a failure: #2 [demo][wait] #7 failed:timeout", text)

    def test_batch_stops_at_a_runtime_ui_step_that_resolved_nothing(self) -> None:
        self.fake_bridge({"playMode": True, "resolved": False, "attempts": [], "warnings": ["No runtime UI control found at path:'Nope/Missing'."]})

        result = self.call("tool-batch", {"steps": [{"tool": "ui-runtime-set-control-value", "args": {"path": "Nope/Missing", "value": 1}}, {"waitMs": 1}]})

        text = self.text_of(result)
        self.assertTrue(text.startswith("tool-batch: 0 of 2 ok, 1 failed, 1 not run"), text)
        self.assertIn("FAILED: replied resolved:false: No runtime UI control found at path:'Nope/Missing'.", text)

    def test_batch_stops_at_a_step_whose_awaited_end_did_not_come(self) -> None:
        # Walk up, then hit: a walk still running would be cancelled by the hit, so the batch stops instead.
        self.write_events([self.log_event(1, "[demo] boot")])
        self.fake_bridge(
            {"summary": "GoTo#9 running", "ok": True, "waitFor": "[demo][task] GoTo#9 ", "waitForFailed": "[demo][task] GoTo#9 failed"},
            events_during_call=[self.log_event(1, "[demo] boot")],
        )

        result = self.call("tool-batch", {"steps": [{"tool": "demo-act", "args": {"awaitMs": 50}}, {"waitMs": 1}]})

        text = self.text_of(result)
        self.assertTrue(text.startswith("tool-batch: 0 of 2 ok, 1 failed, 1 not run"), text)
        self.assertIn("FAILED: replied ok:false: the awaited end did not come within 50 ms", text)

    def test_await_ms_timeout_uses_what_the_tool_says_silence_means(self) -> None:
        # A tap that opens no window has no end event at all: "keep waiting" would send the agent after nothing.
        self.write_events([self.log_event(1, "[demo] boot")])
        self.fake_bridge(
            {"summary": "Tap #3", "ok": True, "waitFor": "[demo][tap] #3 window", "waitTimeoutMeans": "the tap opened or closed no window."},
            events_during_call=[self.log_event(1, "[demo] boot")],
        )

        text = self.text_of(self.call("demo-act", {"awaitMs": 30}))
        self.assertTrue(text.startswith("awaited "), text)
        self.assertIn("no end event. the tap opened or closed no window.", text.splitlines()[0])
        self.assertNotIn("Keep waiting", text)

        batch = self.text_of(self.call("tool-batch", {"steps": [{"tool": "demo-act", "args": {"awaitMs": 30}}]}))
        self.assertIn("FAILED: replied ok:false: no end event within 30 ms: the tap opened or closed no window.", batch)

    def test_awaited_end_without_the_failed_marker_stays_a_success(self) -> None:
        self.write_events([self.log_event(1, "[demo] boot")])
        self.fake_bridge(
            {"summary": "Wait #8 started", "ok": True, "waitFor": "[demo][wait] #8 ", "waitForFailed": "[demo][wait] #8 failed"},
            events_during_call=[self.log_event(1, "[demo] boot"), self.log_event(2, "[demo][wait] #8 done 1503")],
        )

        result = self.call("tool-batch", {"steps": [{"tool": "demo-act", "args": {"awaitMs": 2000}}]})

        self.assertTrue(self.text_of(result).startswith("tool-batch: 1 of 1 ok"))

    def test_batch_step_that_timed_out_but_whose_awaited_end_is_done_passes(self) -> None:
        # script-execute that outran timeoutMs replies success:false stage:timeout; its awaited "done" is what the step did.
        self.write_events([self.log_event(1, "[demo] boot")])
        self.fake_bridge(
            {"success": False, "stage": "timeout", "stillRunning": True, "waitFor": "[demo] async end R8B.Main ", "waitForFailed": " ms failed: "},
            events_during_call=[self.log_event(1, "[demo] boot"), self.log_event(2, "[demo] async end R8B.Main after 2990 ms done: b-done")],
        )

        result = self.call("tool-batch", {"steps": [{"tool": "demo-act", "args": {"awaitMs": 2000}}, {"waitMs": 1}]})

        self.assertTrue(self.text_of(result).startswith("tool-batch: 2 of 2 ok"), self.text_of(result))

    def test_batch_step_whose_awaited_script_end_failed_stops(self) -> None:
        self.write_events([self.log_event(1, "[demo] boot")])
        self.fake_bridge(
            {"success": False, "stage": "timeout", "stillRunning": True, "waitFor": "[demo] async end R8C.Main ", "waitForFailed": " ms failed: "},
            events_during_call=[self.log_event(1, "[demo] boot"), self.log_event(2, "[demo] async end R8C.Main after 120 ms failed: Exception: boom")],
        )

        result = self.call("tool-batch", {"steps": [{"tool": "demo-act", "args": {"awaitMs": 2000}}, {"waitMs": 1}]})

        text = self.text_of(result)
        self.assertTrue(text.startswith("tool-batch: 0 of 2 ok, 1 failed, 1 not run"), text)
        self.assertIn("the awaited end is a failure", text)

    def test_extension_tool_keeps_argument_descriptions_in_tools_list(self) -> None:
        extension = json.loads(json.dumps(DEMO_EXTENSION))
        extension["tools"][0]["inputSchema"]["properties"]["target"]["description"] = "Where to go."
        mcp.configure_extension_manifest_bridge_fetcher(
            lambda: {"schemaVersion": mcp.EXTENSION_CAPABILITY_MANIFEST_SCHEMA_VERSION, "extensions": [extension]}
        )
        mcp.invalidate_extension_manifest_cache()

        tool = next(item for item in mcp.enabled_tools() if item["name"] == "demo-act")

        self.assertEqual(tool["inputSchema"]["properties"]["target"]["description"], "Where to go.")

    def test_batch_carries_step_images(self) -> None:
        def call(name: str, arguments: dict[str, Any], *args: Any, **kwargs: Any) -> dict[str, Any]:
            return {"contentType": "image", "base64": "iVBORw0KGgo=", "mimeType": "image/png", "metadata": {"pngWidth": 1}}

        self.server.call_unity_bridge = call  # type: ignore[method-assign]

        result = self.call("tool-batch", {"steps": [{"tool": "demo-act", "args": {}}]})

        kinds = [block["type"] for block in result["content"]]
        self.assertEqual(kinds, ["text", "text", "image"])
        self.assertEqual(result["content"][1]["text"], "step 1 image:")

    def test_extension_that_declares_save_path_receives_it(self) -> None:
        self.fake_bridge({"summary": "Recorded", "ok": True})

        text = self.text_of(self.call("demo-record", {"savePath": "/tmp/clip.mp4"}))

        self.assertEqual(self.bridge_calls, [("demo-record", {"savePath": "/tmp/clip.mp4"})])
        self.assertNotIn("savePathError", text)

    def test_server_keeps_save_path_of_an_image_from_an_extension_that_does_not_declare_it(self) -> None:
        def call(name: str, arguments: dict[str, Any], *args: Any, **kwargs: Any) -> dict[str, Any]:
            self.bridge_calls.append((name, dict(arguments)))
            return {"contentType": "image", "base64": "iVBORw0KGgo=", "mimeType": "image/png", "metadata": {"pngWidth": 1}}

        self.server.call_unity_bridge = call  # type: ignore[method-assign]
        target = Path(self.temp_dir.name) / "shot.png"

        result = self.call("demo-act", {"savePath": str(target)})

        self.assertEqual(self.bridge_calls, [("demo-act", {})])
        self.assertTrue(target.exists())
        self.assertEqual(result["structuredContent"]["savedPath"], str(target.resolve()))


if __name__ == "__main__":
    unittest.main()
