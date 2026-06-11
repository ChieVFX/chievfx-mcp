import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path
from unittest import mock


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class EventsWaitTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)
        self.bridge_dir = Path(self.temp_dir.name)
        self.server = mcp.McpServer("http://127.0.0.1:1", str(self.bridge_dir), timeout_ms=1000)

    def write_events(self, events: list[dict[str, object]]) -> None:
        last_event_id = max(
            (event.get("eventId") for event in events if isinstance(event.get("eventId"), int)),
            default=0,
        )
        mcp.write_json_file_atomic(
            self.bridge_dir / "events.json",
            {
                "lastEventId": last_event_id,
                "truncatedBeforeEventId": 0,
                "events": events,
            },
        )

    def marker_event(self, event_id: int, marker: str) -> dict[str, object]:
        return {
            "eventId": event_id,
            "timestamp": mcp.utc_now_iso(),
            "source": "log",
            "type": "marker",
            "level": "Log",
            "message": f"MCPEventReachedLocation({marker})",
            "marker": marker,
        }

    def wait_for_active_wait_count(self, expected_count: int) -> dict[str, object]:
        for _ in range(100):
            status = self.server.get_bridge_status({"outputFormat": "json", "verbose": True})
            if status["activeEventWaitCount"] == expected_count:
                return status
            time.sleep(0.01)
        self.fail(f"Expected {expected_count} active event waits.")

    def run_wait_thread(
        self,
        request_id: str,
        arguments: dict[str, object],
        results: dict[str, object],
    ) -> threading.Thread:
        def run() -> None:
            try:
                results[request_id] = self.server.events_wait(arguments, request_id=request_id)
            except Exception as exc:  # noqa: BLE001 - tests assert failure payload explicitly.
                results[request_id] = exc

        thread = threading.Thread(target=run)
        thread.start()
        return thread

    def test_timeout_avoids_stale_marker_by_default(self) -> None:
        self.write_events([self.marker_event(1, "stale-marker")])

        result = self.server.events_wait({"marker": "stale-marker", "timeoutMs": 20}, request_id="stale-wait")

        self.assertFalse(result["matched"])
        self.assertTrue(result["timedOut"])
        self.assertIsNone(result["event"])
        self.assertEqual(self.server.get_bridge_status({"verbose": True})["activeEventWaitCount"], 0)

    def test_schema_accepts_1000_second_waits(self) -> None:
        wait_tool = next(tool for tool in mcp.TOOLS if tool["name"] == "events-wait")

        timeout_schema = wait_tool["inputSchema"]["properties"]["timeoutMs"]

        self.assertEqual(timeout_schema["maximum"], 1000000)

    def test_marker_match_survives_event_stream_rewrite(self) -> None:
        results: dict[str, object] = {}
        thread = self.run_wait_thread("domain-wait", {"marker": "after-domain-reload", "timeoutMs": 1000}, results)
        self.wait_for_active_wait_count(1)

        self.write_events(
            [
                {
                    "eventId": 1,
                    "timestamp": mcp.utc_now_iso(),
                    "source": "editor",
                    "type": "domain-reload-before",
                    "level": "Info",
                    "message": "Domain reload starting.",
                },
                {
                    "eventId": 2,
                    "timestamp": mcp.utc_now_iso(),
                    "source": "editor",
                    "type": "domain-reload-after",
                    "level": "Info",
                    "message": "Domain reload finished.",
                },
                self.marker_event(3, "after-domain-reload"),
            ]
        )

        thread.join(timeout=1)
        self.assertFalse(thread.is_alive())
        result = results["domain-wait"]
        self.assertIsInstance(result, dict)
        self.assertTrue(result["matched"])
        self.assertEqual(result["event"]["marker"], "after-domain-reload")

    def test_cancellation_returns_promptly_and_clears_status(self) -> None:
        results: dict[str, object] = {}
        thread = self.run_wait_thread("cancel-wait", {"marker": "never", "timeoutMs": 1000000}, results)
        status = self.wait_for_active_wait_count(1)
        active_wait = status["activeEventWaits"][0]
        self.assertEqual(active_wait["requestId"], "cancel-wait")
        self.assertEqual(active_wait["filters"], {"marker": "never"})
        self.assertEqual(active_wait["timeoutMs"], 1000000)
        self.assertFalse(active_wait["cancellationRequested"])

        self.server.handle_cancelled_notification({"requestId": "cancel-wait", "reason": "test cancel"})

        thread.join(timeout=1)
        self.assertFalse(thread.is_alive())
        result = results["cancel-wait"]
        self.assertIsInstance(result, dict)
        self.assertTrue(result["cancelled"])
        self.assertEqual(self.server.get_bridge_status({"verbose": True})["activeEventWaitCount"], 0)

    def test_three_concurrent_waits_resolve_independently(self) -> None:
        results: dict[str, object] = {}
        threads = [
            self.run_wait_thread("wait-a", {"marker": "marker-a", "timeoutMs": 1000}, results),
            self.run_wait_thread("wait-b", {"marker": "marker-b", "timeoutMs": 1000}, results),
            self.run_wait_thread("wait-c", {"marker": "marker-c", "timeoutMs": 1000}, results),
        ]
        self.wait_for_active_wait_count(3)

        self.write_events(
            [
                self.marker_event(1, "marker-b"),
                self.marker_event(2, "marker-a"),
                self.marker_event(3, "marker-c"),
            ]
        )

        for thread in threads:
            thread.join(timeout=1)
            self.assertFalse(thread.is_alive())

        matched_markers = {results[key]["event"]["marker"] for key in ("wait-a", "wait-b", "wait-c")}
        self.assertEqual(matched_markers, {"marker-a", "marker-b", "marker-c"})
        self.assertEqual(self.server.get_bridge_status({"verbose": True})["activeEventWaitCount"], 0)

    def test_event_waits_are_bounded(self) -> None:
        results: dict[str, object] = {}
        with mock.patch.object(mcp, "MAX_ACTIVE_EVENT_WAITS", 1):
            thread = self.run_wait_thread("first-wait", {"marker": "never", "timeoutMs": 1000}, results)
            self.wait_for_active_wait_count(1)

            with self.assertRaises(RuntimeError):
                self.server.events_wait({"marker": "second", "timeoutMs": 20}, request_id="second-wait")

            self.server.handle_cancelled_notification({"requestId": "first-wait"})
            thread.join(timeout=1)
            self.assertFalse(thread.is_alive())

    def test_events_wait_returns_window_cursor_for_follow_up_checks(self) -> None:
        result = self.server.events_wait({"marker": "never", "timeoutMs": 1}, request_id="window-wait")

        self.assertIn("sinceEventId", result)
        self.assertIn("startedAtUtc", result)
        self.assertIsNotNone(mcp.parse_utc_iso(result["startedAtUtc"]))

    def test_events_check_since_matches_only_requested_window(self) -> None:
        self.write_events(
            [
                {
                    "eventId": 1,
                    "timestamp": "2026-05-03T20:21:40Z",
                    "source": "log",
                    "type": "marker",
                    "level": "Log",
                    "message": "MCPEventReachedLocation(before)",
                    "marker": "target",
                },
                {
                    "eventId": 2,
                    "timestamp": "2026-05-03T20:21:42Z",
                    "source": "log",
                    "type": "marker",
                    "level": "Log",
                    "message": "MCPEventReachedLocation(after)",
                    "marker": "target",
                    "data": {"key": "value"},
                },
            ]
        )

        result = self.server.events_check_since(
            {
                "sinceEventId": 1,
                "sinceTimestampUtc": "2026-05-03T20:21:41Z",
                "marker": "target",
                "includeData": True,
            }
        )

        self.assertTrue(result["matched"])
        self.assertEqual(result["count"], 1)
        self.assertEqual(result["events"][0]["eventId"], 2)
        self.assertEqual(result["events"][0]["data"], {"key": "value"})

    def test_timeout_reports_match_below_cursor(self) -> None:
        boot_log = {
            "eventId": 10,
            "timestamp": mcp.utc_now_iso(),
            "source": "log",
            "type": "log",
            "level": "Log",
            "message": "[Turn] Turn 1 — Player Turn",
        }
        self.write_events([boot_log])

        # sinceEventId past the boot log mimics a cursor captured after the trigger op completed.
        result = self.server.events_wait(
            {"source": "log", "contains": "Turn 1", "sinceEventId": 44, "timeoutMs": 20},
            request_id="below-cursor-wait",
        )

        self.assertFalse(result["matched"])
        self.assertTrue(result["timedOut"])
        diagnostic = result["diagnostic"]
        self.assertEqual(diagnostic["matchBelowCursorEventId"], 10)
        self.assertEqual(diagnostic["matchBelowCursor"]["message"], "[Turn] Turn 1 — Player Turn")
        self.assertIn("sinceEventId", diagnostic["hint"])

    def test_timeout_reports_possible_truncation(self) -> None:
        mcp.write_json_file_atomic(
            self.bridge_dir / "events.json",
            {
                "lastEventId": 200,
                "truncatedBeforeEventId": 150,
                "events": [
                    {
                        "eventId": 200,
                        "timestamp": mcp.utc_now_iso(),
                        "source": "log",
                        "type": "log",
                        "level": "Log",
                        "message": "Turn 9 — Player Turn",
                    }
                ],
            },
        )

        result = self.server.events_wait(
            {"source": "log", "contains": "Turn 1", "sinceEventId": 120, "timeoutMs": 20},
            request_id="truncated-wait",
        )

        self.assertFalse(result["matched"])
        self.assertTrue(result["timedOut"])
        diagnostic = result["diagnostic"]
        self.assertTrue(diagnostic["possiblyTruncated"])
        self.assertEqual(diagnostic["truncatedBeforeEventId"], 150)

    def test_timeout_reports_non_ascii_contains(self) -> None:
        self.write_events(
            [
                {
                    "eventId": 10,
                    "timestamp": mcp.utc_now_iso(),
                    "source": "log",
                    "type": "log",
                    "level": "Log",
                    "message": "[Turn] Turn 1 — Player Turn",
                }
            ]
        )

        # Mojibake em dash never matches the real em dash in the log.
        result = self.server.events_wait(
            {"source": "log", "contains": "Turn 1 â€\" Player Turn", "sinceEventId": 5, "timeoutMs": 20},
            request_id="mojibake-wait",
        )

        self.assertFalse(result["matched"])
        self.assertTrue(result["timedOut"])
        diagnostic = result["diagnostic"]
        self.assertIn("nonAsciiContains", diagnostic)
        self.assertIn("ASCII", diagnostic["hint"])

    def test_timeout_without_filters_has_no_diagnostic(self) -> None:
        self.write_events(
            [
                {
                    "eventId": 5,
                    "timestamp": mcp.utc_now_iso(),
                    "source": "log",
                    "type": "log",
                    "level": "Log",
                    "message": "anything",
                }
            ]
        )

        result = self.server.events_wait({"sinceEventId": 10, "timeoutMs": 20}, request_id="bare-wait")

        self.assertTrue(result["timedOut"])
        self.assertNotIn("diagnostic", result)

    def test_events_get_tool_is_removed(self) -> None:
        self.assertNotIn("events-get", {tool["name"] for tool in mcp.TOOLS})
        self.assertNotIn("events-get", mcp.DEFAULT_REQUIRED_TOOL_IDS)


if __name__ == "__main__":
    unittest.main()
