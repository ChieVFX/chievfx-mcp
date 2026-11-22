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

    def get_bridge_status(self, arguments: dict[str, Any]) -> dict[str, Any]:
        verbose = bool(arguments.get("verbose", False))
        # In slim mode, default to active-only (no recent history) to keep the response tiny.
        default_max = MAX_STATUS_OPERATIONS if verbose else 0
        max_operations = clamp_int(arguments.get("maxOperations"), default_max, 0, 50)
        now = time.time()
        heartbeat = read_json_file(self.state_path) or {}
        heartbeat_age = file_age_seconds(self.state_path, now) if self.state_path.exists() else None
        bridge_reachable = heartbeat_age is not None and heartbeat_age <= HEARTBEAT_STALE_SECONDS
        # Always scan operations so active rows surface even when maxOperations == 0.
        recent_limit = max(max_operations, MAX_STATUS_OPERATIONS) if not verbose else max(max_operations, 1)
        operations = self.read_operation_rows(recent_limit, now, verbose=verbose)
        active_operations = [row for row in operations if row.get("state") not in TERMINAL_OPERATION_STATES]
        recent_operations = [row for row in operations if row.get("state") in TERMINAL_OPERATION_STATES]
        if max_operations > 0:
            recent_operations = recent_operations[:max_operations]
        else:
            recent_operations = []
        active_event_waits = self.read_active_event_wait_rows(now)
        stale_processing_files = self.get_stale_processing_files(now)
        busy_reasons = self.get_busy_reasons(heartbeat, active_operations, bridge_reachable)
        pending_request_count = self.count_files(self.request_dir, "*.json")
        processing_count = self.count_files(self.request_dir, "*.processing")
        pending_response_count = self.count_files(self.response_dir, "*.json")
        cancellation_count = self.count_files(self.cancel_dir, "*.json")
        # Only the journal's cursor: an operation record the server itself wrote carries no journal id.
        last_event_id = heartbeat.get("lastEventId") if isinstance(heartbeat.get("lastEventId"), int) else None

        editor = heartbeat.get("editor") if isinstance(heartbeat.get("editor"), dict) else {}
        busy = heartbeat.get("busy") if isinstance(heartbeat.get("busy"), dict) else {}
        health = heartbeat.get("health") if isinstance(heartbeat.get("health"), dict) else {}
        health_problems = [problem for problem in health.get("problems") or [] if isinstance(problem, str)]
        hints = self.get_status_hints(
            bridge_reachable,
            heartbeat_age,
            stale_processing_files,
            pending_response_count,
            len(active_event_waits),
            compile_waiting_for_play_mode_exit=bool(editor.get("compileWaitingForPlayModeExit")),
        )
        liveness = self.assess_editor_liveness() if self.bridge_dir else {"state": "unknown"}
        editor_down = self.describe_editor_down(liveness)
        if editor_down is not None:
            hints = [editor_down] + [hint for hint in hints if not hint.startswith("Unity heartbeat is stale.")]
        # A problem that names its own cure (profiler_recording) leaves the health hint empty: an empty row is noise.
        if health_problems and isinstance(health.get("hint"), str) and health["hint"].strip():
            hints.append(health["hint"])

        if verbose:
            return {
                "bridgeDir": str(self.bridge_dir),
                "bridgeReachable": bridge_reachable,
                "heartbeatAgeMs": None if heartbeat_age is None else int(heartbeat_age * 1000),
                "heartbeatStale": heartbeat_age is None or heartbeat_age > HEARTBEAT_STALE_SECONDS,
                "editorState": liveness.get("state"),
                "editorLiveness": liveness,
                "editor": editor,
                "busy": busy,
                "busyReasons": busy_reasons,
                "health": health or None,
                "activeOperationCount": len(active_operations),
                "activeOperations": active_operations,
                "activeEventWaitCount": len(active_event_waits),
                "activeEventWaits": active_event_waits,
                "eventWaits": {
                    "activeCount": len(active_event_waits),
                    "maxActive": MAX_ACTIVE_EVENT_WAITS,
                    "highWatermark": EVENT_WAIT_HIGH_WATERMARK,
                    "highWater": len(active_event_waits) >= EVENT_WAIT_HIGH_WATERMARK,
                    "atCapacity": len(active_event_waits) >= MAX_ACTIVE_EVENT_WAITS,
                },
                "operations": operations[:max_operations] if max_operations else operations,
                "counts": {
                    "pendingRequests": pending_request_count,
                    "processingRequests": processing_count,
                    "pendingResponses": pending_response_count,
                    "cancellationMarkers": cancellation_count,
                    "staleProcessingFiles": len(stale_processing_files),
                    "activeEventWaits": len(active_event_waits),
                },
                "staleProcessingFiles": stale_processing_files,
                "lastEventId": last_event_id,
                "hints": hints,
            }

        # Slim default: emit only the signals callers actually act on. Empty/zero blocks are dropped.
        is_compiling = bool(editor.get("isCompiling")) or bool(busy.get("isCompiling"))
        is_updating = bool(editor.get("isUpdating")) or bool(busy.get("isUpdating"))
        is_playing = bool(editor.get("isPlaying"))
        slim: dict[str, Any] = {
            "bridgeReachable": bridge_reachable,
            "heartbeatAgeMs": None if heartbeat_age is None else int(heartbeat_age * 1000),
            "isCompiling": is_compiling,
            "isUpdating": is_updating,
        }
        # responsive and unknown (no heartbeat file yet) are what bridgeReachable/hints already say.
        if liveness.get("state") not in ("responsive", "unknown"):
            slim["editorState"] = liveness.get("state")
            if liveness.get("busySign"):
                slim["editorBusy"] = liveness.get("busySign")
            if liveness.get("processId"):
                slim["editorProcessId"] = liveness.get("processId")
        # Printed both ways: a missing isPlaying read as "unknown" next to a printed isCompiling:false.
        slim["isPlaying"] = is_playing
        # A paused Play runs no game frames: waits for game events time out and input does nothing until it is unpaused.
        if is_playing and bool(editor.get("isPaused")):
            slim["isPaused"] = True
        if "isPlayingOrWillChangePlaymode" in editor and bool(editor.get("isPlayingOrWillChangePlaymode")) != is_playing:
            slim["playModeChanging"] = "exiting" if is_playing else "entering"
        # Surface only non-default busy flags to keep the row short.
        for flag in ("packageBusy", "testBusy", "editorWindowScreenshotBusy", "scriptBusy", "shaderCompiling"):
            if bool(busy.get(flag)):
                slim[flag] = True
        if busy_reasons:
            slim["busyReasons"] = busy_reasons
        # Only problems: a healthy editor adds nothing to the row agents poll most.
        if health_problems:
            slim["editorHealth"] = health_problems
        if active_operations:
            slim["activeOperations"] = active_operations
        if recent_operations:
            slim["recentOperations"] = recent_operations
        if active_event_waits:
            slim["eventWaitsActive"] = len(active_event_waits)
            if len(active_event_waits) >= EVENT_WAIT_HIGH_WATERMARK:
                slim["eventWaitsCapacity"] = MAX_ACTIVE_EVENT_WAITS
        if pending_request_count:
            slim["pendingRequests"] = pending_request_count
        if processing_count:
            slim["processingRequests"] = processing_count
        if pending_response_count:
            slim["pendingResponses"] = pending_response_count
        if cancellation_count:
            slim["cancellationMarkers"] = cancellation_count
        if stale_processing_files:
            slim["staleProcessingFiles"] = len(stale_processing_files)
        if last_event_id is not None:
            slim["lastEventId"] = last_event_id
        if hints:
            slim["hints"] = hints
        return slim

    def events_check_since(self, arguments: dict[str, Any]) -> dict[str, Any]:
        max_entries = clamp_int(arguments.get("maxEntries"), DEFAULT_EVENTS_CHECK_MAX_ENTRIES, 1, HARD_EVENTS_MAX_ENTRIES)
        since_event_id = self.read_since_event_id(arguments, default=0)
        since_timestamp_utc, since_timestamp = self.read_since_timestamp(arguments)
        # Event ids only grow, so either cursor alone is a complete window; asking for both made a caller
        # holding just an id (from a tool reply or bridge-get-status) invent a timestamp.
        if since_timestamp is None and not isinstance(arguments.get("sinceEventId"), int):
            raise ValueError(
                "events-check-since requires 'sinceEventId' (from events-wait, a tool reply, or "
                "bridge-get-status lastEventId) or 'sinceTimestampUtc'."
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
        last_shown_id = limited[-1].get("eventId") if limited and isinstance(limited[-1], dict) else None
        if result["hasMore"] and isinstance(last_shown_id, int):
            result["nextSinceEventId"] = last_shown_id
        ahead = self.cursor_ahead_hint(since_event_id, stream)
        if ahead is not None:
            result["warning"] = ahead
        return result

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

    def get_busy_reasons(self, heartbeat: dict[str, Any], active_operations: list[dict[str, Any]], bridge_reachable: bool) -> list[str]:
        reasons: list[str] = []
        if not bridge_reachable:
            reasons.append("heartbeat-stale-or-missing")

        heartbeat_reasons = heartbeat.get("busyReasons")
        if isinstance(heartbeat_reasons, list):
            reasons.extend(str(reason) for reason in heartbeat_reasons if isinstance(reason, str) and reason)

        for operation in active_operations:
            tool_name = operation.get("toolName")
            state = operation.get("state")
            if isinstance(tool_name, str) and isinstance(state, str):
                reasons.append(f"{tool_name}:{state}")

        seen: set[str] = set()
        unique: list[str] = []
        for reason in reasons:
            if reason not in seen:
                seen.add(reason)
                unique.append(reason)
        return unique

    def get_status_hints(
        self,
        bridge_reachable: bool,
        heartbeat_age: float | None,
        stale_processing_files: list[dict[str, Any]],
        pending_response_count: int,
        active_event_wait_count: int = 0,
        compile_waiting_for_play_mode_exit: bool = False,
    ) -> list[str]:
        hints: list[str] = []
        if compile_waiting_for_play_mode_exit:
            # Without this, "isCompiling: true" during Play Mode looks like a compile in flight and
            # callers keep waiting for an idle state that cannot arrive.
            hints.append(
                "isCompiling reflects a compile Unity is holding until Play Mode exits, not work in "
                "progress. Call recompile (it stops Play Mode first) or editor-playmode-set "
                "isPlaying=false."
            )
        if not bridge_reachable:
            if heartbeat_age is None:
                hints.append("No Unity heartbeat file. Open Unity or wait for bridge startup.")
            else:
                hints.append(
                    "Unity heartbeat is stale. Editor may be compiling, reloading, entering or exiting Play Mode "
                    "(under memory pressure that takes a minute), closed, or crashed."
                )
        if stale_processing_files:
            hints.append("Stale .processing files found. Previous Unity bridge work may have crashed mid-request.")
        if pending_response_count:
            hints.append("Pending response files exist. MCP client may have timed out before reading them.")
        if active_event_wait_count >= MAX_ACTIVE_EVENT_WAITS:
            hints.append("events-wait capacity reached. New event waits fail fast until existing waits finish.")
        elif active_event_wait_count >= EVENT_WAIT_HIGH_WATERMARK:
            hints.append("Active events-wait count is high. Prefer shared watchers or events-check-since recovery.")
        return hints

    def get_bridge_operation(self, arguments: dict[str, Any]) -> dict[str, Any]:
        operation_id = arguments.get("opId")
        if operation_id is None:
            operation_id = arguments.get("operationId")
        operation_id = self._coerce_operation_id(operation_id)
        path = self.operation_dir / f"{operation_id}.json"
        payload = read_json_file(path)
        if payload is None:
            raise ValueError(f"Bridge operation '{operation_id}' not found or could not be read.")
        now = time.time()
        updated_age = file_age_seconds(path, now)
        return self._normalize_operation_record(payload, operation_id, now, updated_age, include_full=True)

    def _coerce_operation_id(self, value: Any) -> str:
        if not isinstance(value, str):
            raise ValueError("bridge-get-operation requires string 'opId' (or 'operationId').")
        operation_id = value.strip()
        if not operation_id:
            raise ValueError("bridge-get-operation requires non-empty 'opId' (or 'operationId').")
        if re.fullmatch(r"[A-Za-z0-9._-]+", operation_id) is None:
            raise ValueError("bridge-get-operation operation id contains invalid characters.")
        return operation_id

    @staticmethod
    def _resolve_operation_state(payload: dict[str, Any], updated_age: float | None) -> tuple[str, bool]:
        raw_state = payload.get("state")
        state = raw_state if isinstance(raw_state, str) else "unknown"
        stale = state not in TERMINAL_OPERATION_STATES and updated_age is not None and updated_age > OPERATION_STALE_SECONDS
        return ("stale" if stale else state, stale)

    @staticmethod
    def _estimate_operation_duration_ms(payload: dict[str, Any], now: float) -> int | None:
        queued_at = parse_utc_iso(payload.get("queuedAtUtc"))
        started_at = parse_utc_iso(payload.get("startedAtUtc"))
        completed_at = parse_utc_iso(payload.get("completedAtUtc"))

        if queued_at is not None and completed_at is not None:
            return int(max(0.0, completed_at - queued_at) * 1000)
        if started_at is not None and completed_at is None:
            return int(max(0.0, now - started_at) * 1000)
        if queued_at is not None and started_at is None:
            return int(max(0.0, now - queued_at) * 1000)
        return None

    def _normalize_operation_record(
        self,
        payload: dict[str, Any],
        operation_id: str,
        now: float,
        updated_age: float | None,
        include_full: bool,
    ) -> dict[str, Any]:
        resolved_state, stale = self._resolve_operation_state(payload, updated_age)
        compact_row = {
            "operationId": payload.get("operationId") or operation_id,
            "toolName": payload.get("toolName"),
            "state": resolved_state,
            "updatedAgeMs": None if updated_age is None else int(updated_age * 1000),
            "durationMs": self._estimate_operation_duration_ms(payload, now),
        }
        if include_full:
            compact_row.update(payload)
            compact_row["state"] = resolved_state
            compact_row["operationId"] = compact_row.get("operationId") or operation_id
            compact_row["durationMs"] = self._estimate_operation_duration_ms(payload, now)
            compact_row["updatedAgeMs"] = None if updated_age is None else int(updated_age * 1000)
            if "stale" not in compact_row and stale:
                compact_row["stale"] = True
            return {key: value for key, value in compact_row.items() if value is not None}

        is_terminal = resolved_state in TERMINAL_OPERATION_STATES
        if not is_terminal:
            compact_row["cancellable"] = payload.get("cancellable")
            compact_row["timeoutMs"] = payload.get("timeoutMs")
        if stale:
            compact_row["stale"] = True
        return {key: value for key, value in compact_row.items() if value is not None}

    def read_operation_rows(self, max_operations: int, now: float, verbose: bool = False) -> list[dict[str, Any]]:
        if not self.operation_dir.exists():
            return []

        rows: list[dict[str, Any]] = []
        for path in sorted(self.operation_dir.glob("*.json"), key=lambda candidate: candidate.stat().st_mtime, reverse=True):
            payload = read_json_file(path)
            if payload is None:
                continue

            updated_age = file_age_seconds(path, now)
            normalized = self._normalize_operation_record(payload, path.stem, now, updated_age, include_full=verbose)
            rows.append(normalized)

            if len(rows) >= max_operations:
                break

        return rows

    def get_stale_processing_files(self, now: float) -> list[dict[str, Any]]:
        if not self.request_dir.exists():
            return []

        stale_files: list[dict[str, Any]] = []
        for path in self.request_dir.glob("*.processing"):
            age = file_age_seconds(path, now)
            if age is None or age <= PROCESSING_STALE_SECONDS:
                continue

            stale_files.append({"file": path.name, "ageMs": int(age * 1000)})
        return sorted(stale_files, key=lambda row: row["ageMs"], reverse=True)

    @staticmethod
    def count_files(directory: Path, pattern: str) -> int:
        if not directory.exists():
            return 0
        return sum(1 for _ in directory.glob(pattern))

    @staticmethod
    def format_status_hint(status: dict[str, Any]) -> str:
        heartbeat_age = status.get("heartbeatAgeMs")
        busy_reasons = status.get("busyReasons") if isinstance(status.get("busyReasons"), list) else []
        hints = status.get("hints") if isinstance(status.get("hints"), list) else []
        # Read counts from either the slim flat keys or the verbose `counts` block.
        counts = status.get("counts") if isinstance(status.get("counts"), dict) else {}
        pending_requests = counts.get("pendingRequests", status.get("pendingRequests", 0))
        processing_requests = counts.get("processingRequests", status.get("processingRequests", 0))
        pending_responses = counts.get("pendingResponses", status.get("pendingResponses", 0))
        active_event_waits = counts.get("activeEventWaits", status.get("eventWaitsActive", 0))
        return (
            f"operationStatus bridgeReachable:{status.get('bridgeReachable')} "
            f"heartbeatAgeMs:{heartbeat_age} busyReasons:{','.join(str(reason) for reason in busy_reasons[:4]) or 'none'} "
            f"pendingRequests:{pending_requests} processing:{processing_requests} "
            f"pendingResponses:{pending_responses} eventWaits:{active_event_waits}/{MAX_ACTIVE_EVENT_WAITS} "
            f"hints:{' | '.join(str(hint) for hint in hints) or 'none'}"
        )

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
