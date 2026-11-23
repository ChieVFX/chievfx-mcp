# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

from __future__ import annotations

class EventsStatusMixin:
    def register_event_wait(
        self,
        request_id: Any,
        filters: dict[str, Any],
        since_event_id: int,
        timeout_ms: int,
        include_recent_ms: Any,
        include_data: bool,
        started_at_utc: str,
    ) -> tuple[str, threading.Event]:
        request_key = str(request_id) if request_id is not None else f"local-{uuid.uuid4().hex}"
        cancel_event = threading.Event()
        now = time.time()
        record = {
            "requestId": request_key,
            "filters": filters.copy(),
            "sinceEventId": since_event_id,
            "startedAtUtc": started_at_utc,
            "startedAtUnix": now,
            "timeoutMs": timeout_ms,
            "includeRecentMs": include_recent_ms if isinstance(include_recent_ms, int) else None,
            "includeData": include_data,
            "state": "waiting",
            "cancellationRequested": False,
        }

        with self.wait_cancellation_lock:
            self.wait_cancellations[request_key] = cancel_event

        with self.active_event_wait_lock:
            if len(self.active_event_waits) >= MAX_ACTIVE_EVENT_WAITS:
                with self.wait_cancellation_lock:
                    self.wait_cancellations.pop(request_key, None)
                raise RuntimeError(
                    f"Too many active events-wait calls ({MAX_ACTIVE_EVENT_WAITS}). "
                    "Use events-check-since recovery or retry after existing waits complete."
                )
            self.active_event_waits[request_key] = record

        if cancel_event.is_set():
            self.mark_event_wait_cancellation_requested(request_key, "client cancelled request")

        return request_key, cancel_event

    def mark_event_wait_cancellation_requested(self, request_key: str, reason: Any) -> None:
        with self.active_event_wait_lock:
            record = self.active_event_waits.get(request_key)
            if record is None:
                return
            record["state"] = "cancelRequested"
            record["cancellationRequested"] = True
            record["cancellationRequestedAtUtc"] = utc_now_iso()
            if isinstance(reason, str) and reason:
                record["cancellationReason"] = reason

    def unregister_event_wait(self, request_key: str) -> None:
        with self.wait_cancellation_lock:
            self.wait_cancellations.pop(request_key, None)
        with self.active_event_wait_lock:
            self.active_event_waits.pop(request_key, None)

    def read_active_event_wait_rows(self, now: float) -> list[dict[str, Any]]:
        with self.active_event_wait_lock:
            records = [record.copy() for record in self.active_event_waits.values()]

        rows: list[dict[str, Any]] = []
        for record in records:
            started_at = record.get("startedAtUnix")
            started_age_ms = int(max(0.0, now - started_at) * 1000) if isinstance(started_at, (int, float)) else None
            timeout_ms = record.get("timeoutMs")
            remaining_ms = None
            if isinstance(started_age_ms, int) and isinstance(timeout_ms, int):
                remaining_ms = max(0, timeout_ms - started_age_ms)

            row = {
                "requestId": record.get("requestId"),
                "state": record.get("state"),
                "filters": record.get("filters"),
                "sinceEventId": record.get("sinceEventId"),
                "startedAtUtc": record.get("startedAtUtc"),
                "startedAgeMs": started_age_ms,
                "timeoutMs": timeout_ms,
                "remainingMs": remaining_ms,
                "cancellationRequested": record.get("cancellationRequested"),
                "cancellationRequestedAtUtc": record.get("cancellationRequestedAtUtc"),
                "cancellationReason": record.get("cancellationReason"),
                "includeRecentMs": record.get("includeRecentMs"),
                "includeData": record.get("includeData"),
            }
            rows.append({key: value for key, value in row.items() if value is not None})

        return sorted(rows, key=lambda row: row.get("startedAgeMs", 0), reverse=True)

    def events_check_since(self, arguments: dict[str, Any]) -> dict[str, Any]:
        max_entries = clamp_int(arguments.get("maxEntries"), DEFAULT_EVENTS_CHECK_MAX_ENTRIES, 1, HARD_EVENTS_MAX_ENTRIES)
        since_event_id = self.read_since_event_id(arguments, default=0)
        since_timestamp_utc, since_timestamp = self.read_since_timestamp(arguments)
        # Event ids only grow, so either cursor alone is a complete window; asking for both made a caller
        # holding just an id (from a tool reply or bridge-get-status) invent a timestamp.
        if since_timestamp is None and not isinstance(arguments.get("sinceEventId"), int):
            raise ValueError(
                "events-check-since requires 'sinceEventId' (from events-wait, a tool reply, or "
                "bridge-get-status lastEventId) or 'sinceTimestampUtc'; sinceEventId:0 searches all the journal keeps "
                "(its latest events; older log lines are only in this editor's Unity log file)."
            )
        filters = self.read_event_filters(arguments)
        stream = self.read_event_stream()
        matched = self.filter_events(
            stream.get("events", []),
            since_event_id=since_event_id,
            filters=filters,
            include_data=bool(arguments.get("includeData", False)),
            min_timestamp=since_timestamp,
        )
        limited = matched[:max_entries]
        result = {
            "matched": bool(matched),
            "events": limited,
            "count": len(limited),
            "hasMore": len(matched) > len(limited),
            "sinceEventId": since_event_id,
            "sinceTimestampUtc": since_timestamp_utc,
            "lastEventId": self.event_stream_last_id(stream),
            "truncatedBeforeEventId": stream.get("truncatedBeforeEventId", 0),
        }
        # The search reached the journal's start and it had dropped older events: "the whole journal" read as "all
        # history", and an agent answered "once, at 21:20" about an error seen three times that day.
        truncated_before = result["truncatedBeforeEventId"]
        stream_events = stream.get("events", [])
        if isinstance(truncated_before, int) and truncated_before > since_event_id and stream_events and isinstance(stream_events[0], dict):
            result["oldestEventUtc"] = stream_events[0].get("timestamp")
            # Every editor writes the same Editor.log: the bridge names the file that holds this editor's own log ("" = gone).
            heartbeat = read_json_file(self.state_path) or {}
            editor = heartbeat.get("editor") if isinstance(heartbeat.get("editor"), dict) else {}
            if isinstance(editor.get("logPath"), str):
                result["editorLogPath"] = editor["logPath"]
                # The journal outlives editor restarts, the log file does not: every launch starts a new one.
                log_started = self.log_file_started_unix(editor["logPath"])
                oldest_unix = parse_utc_iso(result["oldestEventUtc"])
                if log_started is not None and oldest_unix is not None and oldest_unix < log_started:
                    result["droppedBeforeEditorLog"] = True
        last_shown_id = limited[-1].get("eventId") if limited and isinstance(limited[-1], dict) else None
        if result["hasMore"] and isinstance(last_shown_id, int):
            result["nextSinceEventId"] = last_shown_id
        ahead = self.cursor_ahead_hint(since_event_id, stream)
        if ahead is not None:
            result["warning"] = ahead
        return result

    @staticmethod
    def log_file_started_unix(path: str) -> float | None:
        if not path:
            return None
        try:
            stat = os.stat(path)
        except OSError:
            return None
        # Creation time: st_birthtime on macOS, st_ctime on Windows; Linux keeps none here.
        born = getattr(stat, "st_birthtime", None)
        if born is None and os.name == "nt":
            born = stat.st_ctime
        return born

    def cursor_ahead_hint(self, since_event_id: int, stream: dict[str, Any]) -> str | None:
        """A cursor past the newest event (an id from another counter, or from before the journal was reset)
        can never match anything; say so instead of an empty result that reads as 'nothing happened'.

        A cursor from a reply may run a few ids ahead of events.json until the journal's next flush, hence the slack.
        """
        last_event_id = self.event_stream_last_id(stream)
        if since_event_id <= last_event_id + 1000:
            return None
        return (
            f"sinceEventId {since_event_id} is ahead of the newest event {last_event_id}, so nothing can match. "
            "Take the cursor from the action's reply (sinceEventId / eventCursorBefore) or bridge-get-status lastEventId."
        )

    def events_wait(self, arguments: dict[str, Any], request_id: Any = None) -> dict[str, Any]:
        timeout_ms = clamp_int(arguments.get("timeoutMs"), DEFAULT_EVENTS_WAIT_TIMEOUT_MS, 0, HARD_EVENTS_WAIT_TIMEOUT_MS)
        include_recent_ms = arguments.get("includeRecentMs")
        include_recent_cutoff = None
        if isinstance(include_recent_ms, int) and include_recent_ms > 0:
            include_recent_cutoff = time.time() - (include_recent_ms / 1000)

        initial_stream = self.read_event_stream()
        if isinstance(arguments.get("sinceEventId"), int):
            since_event_id = self.read_since_event_id(arguments, default=0)
        else:
            since_event_id = self.event_stream_last_id(initial_stream)

        filters = self.read_event_filters(arguments)
        include_data = bool(arguments.get("includeData", False))
        # A matched wait answers with the event; the bridge status only explains a wait that came back empty.
        include_bridge_state = parse_bool(arguments.get("includeBridgeState"), False)
        started = time.monotonic()
        started_wall = time.time()
        started_at_utc = utc_now_iso()
        deadline = started + (timeout_ms / 1000)
        explicit_cursor = isinstance(arguments.get("sinceEventId"), int) or include_recent_cutoff is not None
        if not explicit_cursor and filters:
            fired = self.find_fired_before_wait(initial_stream, since_event_id, filters, include_data)
            if fired is not None:
                self.remember_reported_wait_event(fired)
                result = self.build_fired_before_wait_result(fired, since_event_id, started_at_utc, initial_stream)
                if include_bridge_state:
                    result["bridgeState"] = self.get_bridge_status({})
                return result

        request_key, cancel_event = self.register_event_wait(
            request_id,
            filters,
            since_event_id,
            timeout_ms,
            include_recent_ms,
            include_data,
            started_at_utc,
        )

        try:
            while True:
                stream = self.read_event_stream()
                matched = self.filter_events(
                    stream.get("events", []),
                    since_event_id=since_event_id,
                    filters=filters,
                    include_data=include_data,
                    recent_floor=include_recent_cutoff,
                )
                if matched:
                    event = self.pick_wait_match(matched, since_event_id, include_recent_cutoff is not None)
                    self.remember_reported_wait_event(event)
                    result = {
                        "matched": True,
                        "timedOut": False,
                        "event": event,
                        "sinceEventId": since_event_id,
                        "startedAtUtc": started_at_utc,
                        "lastEventId": self.event_stream_last_id(stream),
                        "elapsedMs": int((time.monotonic() - started) * 1000),
                        **({"bridgeState": self.get_bridge_status({})} if include_bridge_state else {}),
                    }
                    fired_ago_ms = self.fired_ago_ms(event) if event.get("eventId", 0) <= since_event_id else None
                    if fired_ago_ms is not None:
                        result["firedAgoMs"] = fired_ago_ms
                    return result

                if cancel_event.is_set():
                    return {
                        "matched": False,
                        "timedOut": False,
                        "cancelled": True,
                        "event": None,
                        "sinceEventId": since_event_id,
                        "startedAtUtc": started_at_utc,
                        "lastEventId": self.event_stream_last_id(stream),
                        "elapsedMs": int((time.monotonic() - started) * 1000),
                        **({"bridgeState": self.get_bridge_status({})} if include_bridge_state else {}),
                    }

                now = time.monotonic()
                if now >= deadline:
                    result = {
                        "matched": False,
                        "timedOut": True,
                        "event": None,
                        "sinceEventId": since_event_id,
                        "startedAtUtc": started_at_utc,
                        "lastEventId": self.event_stream_last_id(stream),
                        "elapsedMs": int((now - started) * 1000),
                        "bridgeState": self.get_bridge_status({}),
                    }
                    diagnostic = self.build_wait_timeout_diagnostic(
                        stream, since_event_id, filters, started_wall,
                        include_recent_ms if isinstance(include_recent_ms, int) else None,
                    )
                    if diagnostic:
                        result["diagnostic"] = diagnostic
                    if result["bridgeState"].get("isPaused"):
                        result["editorPaused"] = (
                            "the editor is paused in Play Mode: no game frames run, so game events do not come until "
                            "it is unpaused (the Pause button, or EditorApplication.isPaused = false)"
                        )
                    return result

                time.sleep(min(EVENTS_WAIT_POLL_SECONDS, max(0.0, deadline - now)))
        finally:
            self.unregister_event_wait(request_key)

    @staticmethod
    def pick_wait_match(matched: list[dict[str, Any]], since_event_id: int, looks_back: bool) -> dict[str, Any]:
        """The next event after the cursor, or, for an includeRecentMs look-back, the newest one at or below it.

        The look-back is for an end logged just before the wait: when the window also holds the previous run's end
        (two transitions within it), the oldest match is that previous run, not the one the caller just started.
        """
        if looks_back:
            looked_back = [event for event in matched if isinstance(event.get("eventId"), int) and event["eventId"] <= since_event_id]
            if looked_back:
                return looked_back[-1]
        return matched[0]

    @staticmethod
    def fired_ago_ms(event: dict[str, Any]) -> int | None:
        timestamp = parse_utc_iso(event.get("timestamp"))
        return int(max(0.0, time.time() - timestamp) * 1000) if timestamp is not None else None

    def remember_reported_wait_event(self, event: dict[str, Any]) -> None:
        event_id = event.get("eventId")
        if not isinstance(event_id, int):
            return
        reported = getattr(self, "reported_wait_event_ids", None)
        if not isinstance(reported, set) or len(reported) >= 512:
            reported = set()
            self.reported_wait_event_ids = reported
        reported.add(event_id)

    def find_fired_before_wait(
        self,
        stream: dict[str, Any],
        since_event_id: int,
        filters: dict[str, Any],
        include_data: bool,
    ) -> dict[str, Any] | None:
        """The newest match at or below the default cursor that is younger than the look-back window.

        A bare wait starts after the last event, so the end of a short action — logged in the seconds
        between the action's reply and this call — sits below the cursor and the wait runs to its full
        timeout (minutes, when the caller asked for minutes). Reporting it up front costs a caller who
        wanted the next occurrence one more call with sinceEventId; missing it cost the whole timeout.
        """
        now = time.time()
        cutoff = now - EVENTS_WAIT_FIRED_BEFORE_WINDOW_MS / 1000
        # After a recent action that named its end, only events from that action on count: an older event with
        # the same text (the previous run's end) is history, not the result the caller is waiting for.
        lower_bound = 0
        action_cursor = getattr(self, "last_async_action_cursor", None)
        if isinstance(action_cursor, int) and getattr(self, "last_async_action_at", 0.0) >= cutoff:
            lower_bound = action_cursor
        # An event a wait already returned is an answer the caller has: handing it back to the next bare wait
        # turns an "act, wait" loop into one that never waits.
        reported = getattr(self, "reported_wait_event_ids", set())
        newest: dict[str, Any] | None = None
        for event in stream.get("events", []):
            if not isinstance(event, dict):
                continue
            event_id = event.get("eventId")
            if not isinstance(event_id, int) or event_id > since_event_id or event_id <= lower_bound or event_id in reported:
                continue
            timestamp = parse_utc_iso(event.get("timestamp"))
            if timestamp is None or timestamp < cutoff:
                continue
            if not self.event_matches_filters(event, filters):
                continue
            if newest is None or event_id > newest["eventId"]:
                newest = event
        return self.copy_event(newest, include_data) if newest is not None else None

    def build_fired_before_wait_result(
        self,
        fired: dict[str, Any],
        since_event_id: int,
        started_at_utc: str,
        stream: dict[str, Any],
    ) -> dict[str, Any]:
        fired_ago_ms = self.fired_ago_ms(fired)
        return {
            "matched": False,
            "timedOut": False,
            "event": None,
            "firedBeforeWait": fired,
            "firedAgoMs": fired_ago_ms,
            "hint": (
                f"A matching event fired {fired_ago_ms} ms before this wait started (eventId {fired.get('eventId')}): "
                "most likely the end of the action you just started. If you need a newer one, wait again with "
                f"sinceEventId:{since_event_id}. Next time pass the action's sinceEventId, or awaitMs to the action itself."
            ),
            "sinceEventId": since_event_id,
            "startedAtUtc": started_at_utc,
            "lastEventId": self.event_stream_last_id(stream),
            "elapsedMs": 0,
        }

    def read_event_stream(self) -> dict[str, Any]:
        payload = read_json_file(self.event_path) or {}
        events = payload.get("events")
        if not isinstance(events, list):
            events = []

        normalized_events = [event for event in events if isinstance(event, dict)]
        last_event_id = payload.get("lastEventId")
        if not isinstance(last_event_id, int):
            last_event_id = max(
                (event.get("eventId") for event in normalized_events if isinstance(event.get("eventId"), int)),
                default=0,
            )

        truncated_before_event_id = payload.get("truncatedBeforeEventId")
        if not isinstance(truncated_before_event_id, int):
            truncated_before_event_id = 0

        return {
            "lastEventId": last_event_id,
            "truncatedBeforeEventId": truncated_before_event_id,
            "events": normalized_events,
        }

    @staticmethod
    def event_stream_last_id(stream: dict[str, Any]) -> int:
        last_event_id = stream.get("lastEventId")
        return last_event_id if isinstance(last_event_id, int) else 0

    @staticmethod
    def read_since_event_id(arguments: dict[str, Any], default: int) -> int:
        since_event_id = arguments.get("sinceEventId")
        if isinstance(since_event_id, int) and since_event_id >= 0:
            return since_event_id

        return default

    @staticmethod
    def read_since_timestamp(arguments: dict[str, Any]) -> tuple[str | None, float | None]:
        value = arguments.get("sinceTimestampUtc", arguments.get("startedAtUtc"))
        if value is None or value == "":
            return None, None
        if not isinstance(value, str):
            raise ValueError("events-check-since 'sinceTimestampUtc' must be a UTC ISO timestamp string.")
        parsed = parse_utc_iso(value)
        if parsed is None:
            raise ValueError("events-check-since 'sinceTimestampUtc' must be a UTC ISO timestamp.")
        return value, parsed

    def read_event_filters(self, arguments: dict[str, Any]) -> dict[str, Any]:
        filters: dict[str, Any] = {}
        for key in ("source", "type", "level", "contains", "marker"):
            value = arguments.get(key)
            if value is None:
                continue
            # contains alone takes a list: the first of several outcomes ("done" or "failed") ends a wait.
            if key == "contains" and isinstance(value, list):
                if not value or not all(isinstance(item, str) for item in value):
                    raise ValueError("events filter 'contains' must be a string or a non-empty array of strings.")
                for item in value:
                    self.validate_event_filter_text(key, item)
                filters[key] = list(value)
                continue
            if not isinstance(value, str):
                raise ValueError(f"events filter '{key}' must be a string.")
            self.validate_event_filter_text(key, value)
            filters[key] = value
        return filters

    @staticmethod
    def validate_event_filter_text(key: str, value: str) -> None:
        if len(value) > MAX_EVENT_FILTER_TEXT or any(character in value for character in "\r\n\0"):
            raise ValueError(f"events filter '{key}' must be <= {MAX_EVENT_FILTER_TEXT} chars without newlines.")

    @staticmethod
    def contains_terms(filters: dict[str, Any]) -> list[str]:
        contains = filters.get("contains")
        if isinstance(contains, str):
            return [contains]
        return list(contains) if isinstance(contains, list) else []

    def filter_events(
        self,
        events: list[Any],
        since_event_id: int,
        filters: dict[str, Any],
        include_data: bool,
        min_timestamp: float | None = None,
        recent_floor: float | None = None,
    ) -> list[dict[str, Any]]:
        """Events after the cursor (and not older than min_timestamp, when given).

        recent_floor is events-wait includeRecentMs: an event at or below the cursor still counts when it is that
        recent. It is the look-back for an end logged before the cursor was taken, so it widens the cursor (OR);
        ANDed with it, as it was, it could never reach the event it exists for.
        """
        matched: list[dict[str, Any]] = []
        for event in events:
            if not isinstance(event, dict):
                continue
            event_id = event.get("eventId")
            if not isinstance(event_id, int):
                continue
            if event_id <= since_event_id:
                timestamp = parse_utc_iso(event.get("timestamp")) if recent_floor is not None else None
                if timestamp is None or timestamp < recent_floor:
                    continue
            if min_timestamp is not None:
                timestamp = parse_utc_iso(event.get("timestamp"))
                if timestamp is None or timestamp < min_timestamp:
                    continue
            if not self.event_matches_filters(event, filters):
                continue
            matched.append(self.copy_event(event, include_data))
        return matched

    def build_wait_timeout_diagnostic(
        self,
        stream: dict[str, Any],
        since_event_id: int,
        filters: dict[str, Any],
        wait_started_epoch: float | None = None,
        include_recent_ms: int | None = None,
    ) -> dict[str, Any] | None:
        """Explain a timeout when the wanted event likely fired below the cursor or was evicted.

        The cursor for a bare events-wait defaults to lastEventId, so a log emitted *during* the
        triggering operation (Play boot, recompile, script-execute) lands at an eventId <= sinceEventId
        and is silently skipped. Surface that here so callers can retry with an earlier cursor instead
        of assuming the event never fired. Never flips `matched` (keeps stale-event avoidance intact).
        """
        ahead = self.cursor_ahead_hint(since_event_id, stream)
        if ahead is not None:
            return {"cursorAhead": True, "hint": ahead}
        if not filters:
            return None

        events = stream.get("events", [])
        below_cursor = [
            self.copy_event(event, include_data=False)
            for event in events
            if isinstance(event, dict)
            and isinstance(event.get("eventId"), int)
            and event["eventId"] <= since_event_id
            and self.event_matches_filters(event, filters)
        ]

        truncated_before = stream.get("truncatedBeforeEventId")
        truncated_before = truncated_before if isinstance(truncated_before, int) else 0

        if below_cursor:
            latest = max(below_cursor, key=lambda event: event.get("eventId", 0))
            # The match below the cursor may be the wanted event (it fired during the trigger) or an older one
            # with the same text (a previous window of that name): its age before this wait tells them apart.
            fired_at = parse_utc_iso(latest.get("timestamp"))
            age_s = (
                round(max(0.0, wait_started_epoch - fired_at), 1)
                if wait_started_epoch is not None and fired_at is not None
                else None
            )
            age_text = f", fired {age_s:g} s before this wait" if age_s is not None else ""
            message = str(latest.get("message") or "")
            message = message if len(message) <= 120 else message[:120] + "…"
            # A look-back that was too short: the match is just older than includeRecentMs, so the direct fix is a
            # wider window, not a cursor taken before the trigger.
            if include_recent_ms and age_s is not None and age_s * 1000 > include_recent_ms:
                retry_text = (
                    f"includeRecentMs {include_recent_ms} looked back only {include_recent_ms / 1000:g} s: if that is the "
                    f"event you waited for, wait again with includeRecentMs >= {int(age_s + 1) * 1000}. "
                )
            else:
                # "Wait for X, it may already have fired" is answered by a look-back wide enough for that event.
                look_back_text = (
                    f"; to take an event that may already have fired, wait with includeRecentMs >= {int(age_s + 1) * 1000}"
                    if age_s is not None and not include_recent_ms
                    else ""
                )
                retry_text = (
                    "If that is the event you waited for, it fired during the triggering operation (e.g. Play-mode boot): "
                    "next time take sinceEventId from bridge-get-status BEFORE the trigger, or check it with "
                    f"events-check-since{look_back_text}. "
                )
            diagnostic: dict[str, Any] = {
                "matchBelowCursor": latest,
                "matchBelowCursorEventId": latest.get("eventId"),
                "hint": (
                    f"Nothing matching fired after sinceEventId {since_event_id}. The latest match at or below it is "
                    f"eventId {latest.get('eventId')}{age_text}: '{message}'. {retry_text}If it is an older one "
                    "(another window, an earlier step), the wanted event has not fired."
                ),
            }
            if age_s is not None:
                diagnostic["matchBelowCursorAgeS"] = age_s
            return diagnostic

        contains = next((term for term in self.contains_terms(filters) if not term.isascii()), None)
        if contains:
            return {
                "nonAsciiContains": contains,
                "hint": (
                    f"The contains filter '{contains}' has non-ASCII characters (e.g. em dash '—'). If the "
                    "JSON-RPC pipeline mangled the encoding, the substring match silently fails even though the "
                    "log fired. Retry with an ASCII-only substring (drop the punctuation, e.g. 'Turn 1') or use "
                    "a marker: filter instead of matching Unicode punctuation in log text."
                ),
            }

        if truncated_before > 0 and truncated_before >= since_event_id:
            return {
                "possiblyTruncated": True,
                "truncatedBeforeEventId": truncated_before,
                "hint": (
                    f"No buffered event matched, and the event ring buffer dropped events up to "
                    f"{truncated_before} (>= sinceEventId {since_event_id}). The wanted event may have been "
                    "evicted before this wait scanned it. For boot/early logs, set the cursor before the "
                    "trigger, or verify after the fact with console-get-logs (contains)."
                ),
            }

        return None

    @staticmethod
    def event_matches_filters(event: dict[str, Any], filters: dict[str, Any]) -> bool:
        for key in ("source", "type", "level"):
            expected = filters.get(key)
            if expected is not None and str(event.get(key, "")).lower() != expected.lower():
                return False

        contains = filters.get("contains")
        if contains is not None:
            raw_message = str(event.get("message", "")).lower()
            # Rows show the message without rich-text tags, so a term copied from them matches either form.
            message = strip_rich_text(raw_message)
            terms = [contains] if isinstance(contains, str) else contains
            if not any(term.lower() in message or term.lower() in raw_message for term in terms):
                return False

        marker = filters.get("marker")
        if marker is not None and str(event.get("marker", "")) != marker:
            return False

        return True

    @staticmethod
    def copy_event(event: dict[str, Any], include_data: bool) -> dict[str, Any]:
        copied = {
            key: event.get(key)
            for key in ("eventId", "timestamp", "source", "type", "level", "message", "marker", "operationId")
            if event.get(key) is not None
        }
        if isinstance(copied.get("message"), str):
            copied["message"] = strip_rich_text(copied["message"])
        if include_data and isinstance(event.get("data"), dict):
            copied["data"] = event["data"]
        return copied

    @staticmethod
    def result_response(request_id: Any, result: Any) -> dict[str, Any] | None:
        if request_id is None:
            return None

        return {"jsonrpc": "2.0", "id": request_id, "result": result}

    @staticmethod
    def error_response(request_id: Any, code: int, message: str) -> dict[str, Any] | None:
        if request_id is None:
            return None

        return {"jsonrpc": "2.0", "id": request_id, "error": {"code": code, "message": message}}
