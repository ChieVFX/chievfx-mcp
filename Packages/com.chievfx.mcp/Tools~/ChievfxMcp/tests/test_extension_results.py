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
        self.assertTrue(text.startswith("Driving"), text)
        self.assertIn("awaited", text)
        self.assertIn("#2 [demo][task] Drive#3 done arrived", text)
        # The bridge (and the extension behind it) never sees awaitMs.
        self.assertEqual(self.bridge_calls, [("demo-act", {"target": "home"})])

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

    def test_batch_step_with_await_ms_reports_the_end(self) -> None:
        self.write_events([self.log_event(1, "[demo] boot")])
        self.fake_bridge(
            {"summary": "Driving", "waitFor": "[demo][task] Drive#3"},
            events_during_call=[self.log_event(1, "[demo] boot"), self.log_event(2, "[demo][task] Drive#3 done")],
        )

        result = self.call("tool-batch", {"steps": [{"tool": "demo-act", "args": {"awaitMs": 2000}}, {"waitMs": 1}]})

        text = self.text_of(result)
        self.assertTrue(text.startswith("tool-batch: 2 of 2 ok"), text)
        self.assertIn("\n1 demo-act: Driving", text)
        self.assertIn("#2 [demo][task] Drive#3 done", text)

    def test_batch_carries_step_images(self) -> None:
        def call(name: str, arguments: dict[str, Any], *args: Any, **kwargs: Any) -> dict[str, Any]:
            return {"contentType": "image", "base64": "iVBORw0KGgo=", "mimeType": "image/png", "metadata": {"pngWidth": 1}}

        self.server.call_unity_bridge = call  # type: ignore[method-assign]

        result = self.call("tool-batch", {"steps": [{"tool": "demo-act", "args": {}}]})

        kinds = [block["type"] for block in result["content"]]
        self.assertEqual(kinds, ["text", "text", "image"])
        self.assertEqual(result["content"][1]["text"], "step 1 image:")


if __name__ == "__main__":
    unittest.main()
