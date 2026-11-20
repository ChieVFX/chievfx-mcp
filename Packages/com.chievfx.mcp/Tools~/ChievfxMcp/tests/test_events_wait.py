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
        stale = self.marker_event(1, "stale-marker")
        stale["timestamp"] = "2026-01-01T00:00:00Z"
        self.write_events([stale])

        result = self.server.events_wait({"marker": "stale-marker", "timeoutMs": 20}, request_id="stale-wait")

        self.assertFalse(result["matched"])
        self.assertTrue(result["timedOut"])
        self.assertIsNone(result["event"])
        self.assertNotIn("firedBeforeWait", result)
        self.assertEqual(self.server.get_bridge_status({"verbose": True})["activeEventWaitCount"], 0)

    def test_bridge_state_comes_only_with_a_wait_that_came_back_empty(self) -> None:
        self.write_events([self.marker_event(1, "seen")])

        matched = self.server.events_wait({"marker": "seen", "sinceEventId": 0, "timeoutMs": 50}, request_id="m")
        timed_out = self.server.events_wait({"marker": "never", "timeoutMs": 20}, request_id="t")
        asked = self.server.events_wait({"marker": "seen", "sinceEventId": 0, "includeBridgeState": True}, request_id="a")

        self.assertTrue(matched["matched"])
        self.assertNotIn("bridgeState", matched)
        self.assertIn("bridgeState", timed_out)
        self.assertIn("bridgeState", asked)

    def test_match_fired_just_before_the_wait_returns_at_once_without_matching(self) -> None:
        # The end of a short action lands between its reply and the agent's events-wait; a bare wait
        # used to skip it and sit out the whole timeout.
        self.write_events([self.marker_event(1, "just-fired")])

        started = time.monotonic()
        result = self.server.events_wait({"marker": "just-fired", "timeoutMs": 5000}, request_id="fired-wait")

        self.assertLess(time.monotonic() - started, 1.0)
        self.assertFalse(result["matched"])
        self.assertFalse(result["timedOut"])
        self.assertIsNone(result["event"])
        self.assertEqual(result["firedBeforeWait"]["eventId"], 1)
        self.assertIn("sinceEventId:1", result["hint"])
        self.assertNotIn("bridgeState", result)

    def test_fired_before_wait_is_not_handed_back_to_the_next_bare_wait(self) -> None:
        # An "act, wait" loop without cursors must not get the previous answer again instantly.
        self.write_events([self.marker_event(1, "just-fired")])

        first = self.server.events_wait({"marker": "just-fired", "timeoutMs": 5000}, request_id="first-wait")
        second = self.server.events_wait({"marker": "just-fired", "timeoutMs": 20}, request_id="second-wait")

        self.assertEqual(first["firedBeforeWait"]["eventId"], 1)
        self.assertTrue(second["timedOut"])
        self.assertNotIn("firedBeforeWait", second)

    def test_matched_event_is_not_reported_as_fired_before_the_next_bare_wait(self) -> None:
        self.write_events([self.marker_event(1, "older"), self.marker_event(2, "done-marker")])

        matched = self.server.events_wait({"marker": "done-marker", "sinceEventId": 1, "timeoutMs": 200}, request_id="m")
        bare = self.server.events_wait({"marker": "done-marker", "timeoutMs": 20}, request_id="b")

        self.assertTrue(matched["matched"])
        self.assertTrue(bare["timedOut"])
        self.assertNotIn("firedBeforeWait", bare)

    def test_cursor_far_ahead_of_the_journal_is_called_out(self) -> None:
        # An epoch-millisecond id (once leaked by the verbose status) silently matched nothing.
        self.write_events([self.marker_event(1, "older")])

        waited = self.server.events_wait({"marker": "x", "sinceEventId": 1795130183483, "timeoutMs": 20}, request_id="ahead")
        checked = self.server.events_check_since({"sinceEventId": 1795130183483})

        self.assertTrue(waited["diagnostic"]["cursorAhead"])
        self.assertIn("ahead of the newest event 1", checked["warning"])

    def test_verbose_status_cursor_ignores_operation_records(self) -> None:
        self.write_events([self.marker_event(7, "older")])
        mcp.write_json_file_atomic(self.bridge_dir / "state.json", {"lastEventId": 7, "heartbeatUtc": mcp.utc_now_iso()})
        self.server.write_operation_record("op1", {"operationId": "op1", "toolName": "x", "state": "completed", "eventId": 1795130183483})

        verbose = self.server.get_bridge_status({"verbose": True})

        self.assertEqual(verbose["lastEventId"], 7)

    def test_explicit_cursor_skips_the_fired_before_check(self) -> None:
        self.write_events([self.marker_event(1, "just-fired")])

        result = self.server.events_wait(
            {"marker": "just-fired", "sinceEventId": 1, "timeoutMs": 20}, request_id="cursor-wait"
        )

        self.assertTrue(result["timedOut"])
        self.assertNotIn("firedBeforeWait", result)

    def test_contains_list_matches_any_term(self) -> None:
        failed = self.marker_event(2, "unused")
        failed["message"] = "[GameAI][task] Drive#3 failed:blocked"
        self.write_events([self.marker_event(1, "older"), failed])

        result = self.server.events_wait(
            {"contains": ["Drive#3 done", "Drive#3 failed"], "sinceEventId": 1, "timeoutMs": 200},
            request_id="any-of-wait",
        )

        self.assertTrue(result["matched"])
        self.assertEqual(result["event"]["eventId"], 2)

    def test_contains_list_rejects_non_strings(self) -> None:
        with self.assertRaises(ValueError):
            self.server.events_wait({"contains": ["ok", 3], "timeoutMs": 20}, request_id="bad-list")

    def test_fired_before_wait_ignores_events_older_than_the_last_action(self) -> None:
        # The previous run's end has the same text as the one just started; it must not answer the wait.
        previous_end = self.marker_event(5, "unused")
        previous_end["message"] = "[GameAI][skill-check] #39 done ok:1"
        self.write_events([previous_end])
        self.server._remember_async_action({"waitFor": "[GameAI][skill-check] #40 ", "sinceEventId": 5}, None)

        result = self.server.events_wait({"contains": "[GameAI][skill-check]", "timeoutMs": 20}, request_id="older-wait")

        self.assertTrue(result["timedOut"])
        self.assertNotIn("firedBeforeWait", result)

    def test_fired_before_wait_reports_the_end_of_the_last_action(self) -> None:
        end = self.marker_event(6, "unused")
        end["message"] = "[GameAI][skill-check] #40 done ok:1"
        self.write_events([self.marker_event(5, "older"), end])
        self.server._remember_async_action({"waitFor": "[GameAI][skill-check] #40 ", "sinceEventId": 5}, None)

        result = self.server.events_wait({"contains": "[GameAI][skill-check]", "timeoutMs": 2000}, request_id="end-wait")

        self.assertEqual(result["firedBeforeWait"]["eventId"], 6)

    def test_rich_text_tags_are_dropped_from_rows_and_ignored_by_contains(self) -> None:
        tagged = self.marker_event(2, "unused")
        tagged["message"] = "<b><color=#B06400FF>[Net] </color></b>[GameAI][task] Drive#3 done"
        self.write_events([self.marker_event(1, "older"), tagged])

        result = self.server.events_wait(
            {"contains": "[Net] [GameAI][task] Drive#3", "sinceEventId": 1, "timeoutMs": 200}, request_id="rich-wait"
        )

        self.assertTrue(result["matched"])
        self.assertEqual(result["event"]["message"], "[Net] [GameAI][task] Drive#3 done")

    def test_events_check_since_needs_only_an_event_id(self) -> None:
        self.write_events([self.marker_event(1, "a"), self.marker_event(2, "b")])

        result = self.server.events_check_since({"sinceEventId": 1})

        self.assertEqual([event["eventId"] for event in result["events"]], [2])
        self.assertIsNone(result["sinceTimestampUtc"])

    def test_events_check_since_names_the_cursor_of_the_next_page(self) -> None:
        self.write_events([self.marker_event(index, "e" + str(index)) for index in range(1, 6)])

        result = self.server.events_check_since({"sinceEventId": 1, "maxEntries": 2})

        self.assertTrue(result["hasMore"])
        self.assertEqual(result["nextSinceEventId"], 3)
        self.assertIn("pass sinceEventId:3", mcp.format_events_check_since_text(result))

    def test_events_check_since_without_any_cursor_is_an_error(self) -> None:
        with self.assertRaises(ValueError):
            self.server.events_check_since({"contains": "x"})

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

    def test_events_check_since_text_says_where_a_shortened_row_is_whole(self) -> None:
        long_message = "[demo][skill-end] boot#4 failed:connect_failed " + "detail=x " * 30 + "hint at the tail"
        short_result = {"matched": True, "count": 1, "events": [{"eventId": 2, "source": "log", "type": "message", "message": "short"}]}
        long_result = {"matched": True, "count": 1, "events": [{"eventId": 3, "source": "log", "type": "message", "message": long_message}]}

        self.assertNotIn("shortened", mcp.format_events_check_since_text(short_result))
        text = mcp.format_events_check_since_text(long_result)
        self.assertNotIn("hint at the tail", text)
        self.assertIn("Rows ending in ... are shortened: the whole line is in console-get-logs", text)

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

    def test_timeout_names_age_of_older_match_below_cursor(self) -> None:
        old_close = {
            "eventId": 10,
            "timestamp": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(time.time() - 26)),
            "source": "log",
            "type": "log",
            "level": "Log",
            "message": "[GameAI][tap] #134 window popup_closed UI Canvases/PolicePopupMessage(Clone)",
        }
        self.write_events([old_close])

        result = self.server.events_wait(
            {"source": "log", "contains": "PolicePopupMessage", "sinceEventId": 44, "timeoutMs": 20},
            request_id="older-below-cursor-wait",
        )

        diagnostic = result["diagnostic"]
        self.assertEqual(diagnostic["matchBelowCursorEventId"], 10)
        self.assertGreaterEqual(diagnostic["matchBelowCursorAgeS"], 25)
        self.assertIn("fired 2", diagnostic["hint"])
        self.assertIn("popup_closed", diagnostic["hint"])
        self.assertIn("has not fired", diagnostic["hint"])
        self.assertNotIn("likely fired during", diagnostic["hint"])

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

    def test_include_recent_matches_csharp_seven_digit_fraction_timestamp(self) -> None:
        # Regression: C# DateTime "o" timestamps carry 7 fractional digits, which
        # datetime.fromisoformat rejects before Python 3.11. If parse_utc_iso fails on them,
        # min_timestamp filtering (used by includeRecentMs / events-check-since) drops every
        # event and the marker is never found. Build the timestamp from a real recent time so
        # the includeRecentMs window covers it regardless of when the test runs.
        recent = time.gmtime(time.time() - 1)
        csharp_timestamp = time.strftime("%Y-%m-%dT%H:%M:%S", recent) + ".7105270Z"
        self.write_events([
            {
                "eventId": 7,
                "timestamp": csharp_timestamp,
                "source": "input",
                "type": "sequence-complete",
                "level": "info",
                "marker": "input-seq-1-mouse-gesture",
                "message": "Input sequence complete.",
            }
        ])

        self.assertIsNotNone(mcp.parse_utc_iso(csharp_timestamp))
        result = self.server.events_wait(
            {"marker": "input-seq-1-mouse-gesture", "includeRecentMs": 10000, "timeoutMs": 20},
            request_id="frac-wait",
        )
        self.assertTrue(result["matched"], result)
        self.assertEqual(result["event"]["marker"], "input-seq-1-mouse-gesture")

    def test_include_recent_reaches_below_an_explicit_cursor(self) -> None:
        # A skill's end logged before the cursor the agent passed (taken from a reply written after the end):
        # includeRecentMs is the look-back for exactly that race, but it used to be ANDed with the cursor.
        self.write_events([self.marker_event(5, "skill-end"), self.marker_event(6, "other")])

        started = time.monotonic()
        result = self.server.events_wait(
            {"marker": "skill-end", "sinceEventId": 6, "includeRecentMs": 10000, "timeoutMs": 3000}, request_id="look-back"
        )

        self.assertLess(time.monotonic() - started, 1.0)
        self.assertTrue(result["matched"], result)
        self.assertEqual(result["event"]["eventId"], 5)

    def test_include_recent_still_skips_old_events_below_the_cursor(self) -> None:
        old = self.marker_event(5, "skill-end")
        old["timestamp"] = "2026-01-01T00:00:00Z"
        self.write_events([old, self.marker_event(6, "other")])

        result = self.server.events_wait(
            {"marker": "skill-end", "sinceEventId": 6, "includeRecentMs": 2000, "timeoutMs": 20}, request_id="too-old"
        )

        self.assertFalse(result["matched"])

    def test_include_recent_without_cursor_does_not_match_old_events(self) -> None:
        old = self.marker_event(5, "skill-end")
        old["timestamp"] = "2026-01-01T00:00:00Z"
        self.write_events([old])

        result = self.server.events_wait({"marker": "skill-end", "includeRecentMs": 2000, "timeoutMs": 20}, request_id="no-cursor")

        self.assertFalse(result["matched"])


class ParseUtcIsoTests(unittest.TestCase):
    def test_handles_fraction_lengths_and_offsets(self) -> None:
        seven = mcp.parse_utc_iso("2026-07-20T20:03:23.7105270Z")
        six = mcp.parse_utc_iso("2026-07-20T20:03:23.710527Z")
        self.assertIsNotNone(seven)
        self.assertEqual(seven, six)
        for value in (
            "2020-01-01T00:00:00Z",
            "2026-07-20T20:03:23.123Z",
            "2026-07-20T20:03:23Z",
            "2026-07-20T20:03:23.7105270+00:00",
        ):
            self.assertIsNotNone(mcp.parse_utc_iso(value), value)
        self.assertIsNone(mcp.parse_utc_iso("garbage"))
        self.assertIsNone(mcp.parse_utc_iso(None))


if __name__ == "__main__":
    unittest.main()
