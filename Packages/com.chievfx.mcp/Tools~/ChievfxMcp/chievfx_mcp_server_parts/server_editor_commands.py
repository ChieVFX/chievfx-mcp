# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

from __future__ import annotations

class EditorCommandsMixin:
    """Tools the server drives itself across editor states: recompile, Play Mode, Frame Debugger captures."""

    def recompile(
        self,
        arguments: dict[str, Any],
        request_id: Any = None,
        progress_token: Any = None,
        notify: Any | None = None,
    ) -> dict[str, Any]:
        self.raise_if_editor_unresponsive()
        timeout_ms = clamp_int(arguments.get("timeoutMs"), int(RECOMPILE_WAIT_SECONDS * 1000), 1000, 30 * 60 * 1000)
        wait_seconds = timeout_ms / 1000
        status_before = self.get_bridge_status({})
        was_playing = self.read_playmode_state() is True

        if was_playing:
            # Do NOT wait for idle first here. Unity cannot compile during Play Mode, so a compile it
            # already parked keeps isCompiling pinned true until play ends: the pre-wait would burn the
            # whole timeout before the request that stops Play Mode was ever sent. That is the "recompile
            # timed out while isCompiling stayed true" case.
            ready_before = None
            self.emit_progress(
                progress_token, notify, 0.05, "Unity is in Play Mode; stopping it before recompile."
            )
        else:
            self.emit_progress(progress_token, notify, 0.05, "Waiting for Unity to become idle before recompile.")
            ready_before = self.wait_for_bridge_ready(max_wait_seconds=wait_seconds)

        try:
            bridge_result = self.call_unity_bridge("recompile", arguments, request_id, progress_token, notify)
            result = bridge_result.get("result")
            if not isinstance(result, dict):
                result = {}
        except EditorUnresponsiveError:
            raise
        except RuntimeError:
            # Leaving Play Mode domain-reloads Unity, which can swallow the response or briefly stale the
            # heartbeat mid-round-trip — the same recovery editor_playmode_set needs. The editor still
            # took the request, so confirm from the heartbeat below instead of failing the call.
            if not was_playing or not self.bridge_dir:
                raise
            result = {
                "requested": True,
                "wasPlaying": True,
                "exitedPlayMode": True,
                "compileRequestedAfterPlayModeExit": True,
                "bridgeRoundTripInterrupted": True,
            }

        # The compile is only issued once the editor is back in edit mode, so wait for the exit before
        # waiting on compilation itself.
        if result.get("exitedPlayMode"):
            self.emit_progress(progress_token, notify, 0.3, "Waiting for Play Mode to exit before compiling.")
            exit_wait_seconds = min(wait_seconds, PLAYMODE_EXIT_WAIT_TIMEOUT_SECONDS)
            exited, exit_waited_ms = self.wait_for_playmode(False, exit_wait_seconds, progress_token, notify)
            result["playModeExited"] = exited
            result["playModeExitWaitedMs"] = exit_waited_ms
            if not exited:
                result["warning"] = (
                    f"Play Mode did not exit within {int(exit_wait_seconds * 1000)}ms, so compilation "
                    "may not have started: recompile compiles only outside Play Mode."
                )

        # Compilation usually flips busy on the next editor tick. Give Unity a
        # short chance to enter compile/import state before waiting for idle.
        time.sleep(min(RECOMPILE_START_GRACE_SECONDS, max(wait_seconds, 0.0)))
        # An editor in the background can start the requested compile seconds later, after it already
        # looked idle: waiting for idle alone then reported "completed" on the old assemblies. The bridge
        # journals compile-start, so wait for this compile to actually start.
        since_event_id = status_before.get("lastEventId") if isinstance(status_before, dict) else None
        start_wait_seconds = min(wait_seconds, RECOMPILE_START_WAIT_SECONDS)
        compile_started: bool | None = None
        if isinstance(since_event_id, int) and self.bridge_dir and result.get("playModeExited") is not False:
            self.emit_progress(progress_token, notify, 0.4, "Waiting for Unity to start compiling.")
            compile_started = self.wait_for_compile_start(since_event_id, start_wait_seconds)
        self.emit_progress(progress_token, notify, 0.5, "Waiting for Unity compilation to finish.")
        ready_after = self.wait_for_bridge_ready(max_wait_seconds=wait_seconds)
        compile_finished: bool | None = None
        if compile_started and isinstance(since_event_id, int):
            compile_finished = self.wait_for_compile_event(
                since_event_id, min(wait_seconds, RECOMPILE_FINISH_WAIT_SECONDS), ("compile-finish",)
            )
            if compile_finished:
                # A clean compile reloads the domain after compile-finish: wait for the editor once more.
                ready_after = self.wait_for_bridge_ready(max_wait_seconds=wait_seconds)
        status_after = self.get_bridge_status({})
        if compile_finished is False:
            result["compileFinishJournaled"] = False
            finish_warning = (
                f"Unity's compile-finish event did not reach the journal within {int(min(wait_seconds, RECOMPILE_FINISH_WAIT_SECONDS))} s, "
                f"so the errors below may be incomplete: check console-get-logs sinceEventId:{since_event_id} levels:[Error]."
            )
            existing_warning = result.get("warning")
            result["warning"] = (
                f"{existing_warning} {finish_warning}" if isinstance(existing_warning, str) else finish_warning
            )
        if compile_started is False:
            result["compileStarted"] = False
            not_started_warning = (
                f"Unity did not start compiling within {int(start_wait_seconds)} s of the request (an editor "
                "in the background can stall), so the assemblies are unchanged and nothing below describes "
                "the current sources. Run recompile again."
            )
            existing_warning = result.get("warning")
            result["warning"] = (
                f"{existing_warning} {not_started_warning}" if isinstance(existing_warning, str) else not_started_warning
            )

        result.update(
            {
                "completed": ready_after,
                "readyBeforeRequest": ready_before,
                "readyAfterRequest": ready_after,
                "statusBefore": status_before,
                "statusAfter": status_after,
            }
        )
        if not ready_after:
            timeout_warning = "Timed out waiting for Unity compile/import busy state to clear."
            # Keep an earlier Play-Mode warning: it explains WHY nothing compiled, which the timeout
            # alone does not.
            existing_warning = result.get("warning")
            result["warning"] = (
                f"{existing_warning} {timeout_warning}" if isinstance(existing_warning, str) else timeout_warning
            )

        # Surface compile errors/warnings produced by this recompile directly, so callers do not need a
        # separate console-get-logs round-trip. Read from the event stream (which survives the domain
        # reload a successful compile triggers) using the pre-recompile cursor.
        if isinstance(since_event_id, int):
            result["compile"] = self.collect_recompile_issues(since_event_id)

        invalidate_extension_manifest_cache()
        self.emit_progress(progress_token, notify, 1.0, "recompile completed.")
        return result

    def settle_frame_debugger_result(
        self, name: str, arguments: dict[str, Any], result: dict[str, Any], request_id: Any
    ) -> dict[str, Any]:
        """Fill a Frame Debugger event's details that Unity had not produced yet, and say when the captured frame changed.

        Unity reports an event's shader, pass and render target only after the editor re-rendered with the event selected,
        a tick after the tool selected it: the reply said "GetFrameEventData unavailable for event N: " with no reason,
        and the details came only on a repeat call. In Play Mode the game can also render a different frame on some
        repaints (196 and 199 events alternating), so event, group and draw-call numbers from one reply pointed to other
        draw calls in the next without any sign of it.
        """
        diagnostics = [d for d in result.get("diagnostics") or [] if isinstance(d, str)]
        # Turning the Frame Debugger on or off starts a new capture: comparing with a frame of the previous session warned
        # about a "changed frame" on the very first list.
        if name == "frame-debugger-control" and "enabled" in arguments:
            self._frame_debugger_event_count = None
        event_count = frame_debugger_event_count(result)
        is_playing = frame_debugger_is_playing(result)
        frame_event = result.get("frameEvent")
        missing = [d for d in diagnostics if FRAME_EVENT_DATA_UNAVAILABLE in d]
        # Unity produces details only for the selected event; event-get select:false leaves the selection as it was.
        selects_event = arguments.get("select") is not False
        settled = False
        other_frame_count = None
        if (
            name in FRAME_DEBUGGER_DETAIL_TOOLS
            and selects_event
            and isinstance(frame_event, dict)
            and missing
            and isinstance(frame_event.get("index"), int)
        ):
            event_index = frame_event["index"]
            for _ in range(FRAME_DEBUGGER_DETAIL_RETRIES):
                time.sleep(FRAME_DEBUGGER_DETAIL_RETRY_SECONDS)
                try:
                    retry = self.call_unity_bridge(
                        "frame-debugger-event-get", {"eventIndex": event_index, "select": False}, request_id, None, None
                    ).get("result")
                except RuntimeError:
                    break
                if not isinstance(retry, dict) or not isinstance(retry.get("frameEvent"), dict):
                    break
                retry_count = frame_debugger_event_count(retry)
                # In Play Mode repaints alternate between frames: a later repaint captures this frame again.
                if isinstance(event_count, int) and isinstance(retry_count, int) and retry_count != event_count:
                    other_frame_count = retry_count
                    continue
                if any(FRAME_EVENT_DATA_UNAVAILABLE in d for d in retry.get("diagnostics") or [] if isinstance(d, str)):
                    continue
                merged = dict(retry["frameEvent"])
                for key in ("groupIndex", "drawCallIndex"):
                    if key in frame_event:
                        merged[key] = frame_event[key]
                result["frameEvent"] = merged
                settled = True
                break
        if missing and isinstance(frame_event, dict) and isinstance(frame_event.get("index"), int):
            event_index = frame_event["index"]
            diagnostics = [d for d in diagnostics if FRAME_EVENT_DATA_UNAVAILABLE not in d]
            if not settled and other_frame_count is not None:
                diagnostics.append(
                    f"Event {event_index} details were not read: repaints kept capturing another frame ({event_count} -> "
                    f"{other_frame_count} events) before Unity produced them; call again"
                    + (" or exit Play Mode." if is_playing else ".")
                )
            elif not settled:
                diagnostics.append(
                    f"Event {event_index} details (shader, pass, render target) are not ready: Unity produces them only for the "
                    f"selected event, after the editor re-renders with it - call frame-debugger-event-get eventIndex:{event_index} "
                    "(select:true, the default) again."
                )

        previous = getattr(self, "_frame_debugger_event_count", None)
        if isinstance(event_count, int) and event_count > 0:
            if isinstance(previous, int) and previous > 0 and previous != event_count:
                diagnostics.append(
                    f"The captured frame changed since the previous Frame Debugger reply ({previous} -> {event_count} events): "
                    "event, group and draw-call numbers from earlier replies may point to other draw calls now. "
                    + (
                        "In Play Mode repaints can capture different frames: list again before using a number, pass groupName "
                        "instead of groupIndex, or exit Play Mode."
                        if is_playing
                        else "List again before using a number."
                    )
                )
            self._frame_debugger_event_count = event_count
        result["diagnostics"] = diagnostics
        return result

    def call_frame_debugger_draw_call(
        self, name: str, arguments: dict[str, Any], request_id: Any, progress_token: Any, notify: Any | None
    ) -> dict[str, Any]:
        """A draw call by group index, read again while the capture is another frame.

        In Play Mode repaints alternate between frames with different groups (a second camera's group in front), so a
        group index from groups-list pointed to a one-event group in the next capture: "drawCallIndex 43 is outside
        group 9 draw calls 0..0", and a repeat of the same call worked. Right after the Frame Debugger is turned on the
        capture is not there yet: the call is repeated until it is.
        """
        attempt = 0
        not_ready_attempt = 0
        while True:
            try:
                return self.call_unity_bridge(name, arguments, request_id, progress_token, notify)
            except RuntimeError as exc:
                message = str(exc)
                if any(text in message for text in FRAME_DEBUGGER_NOT_READY) and not_ready_attempt < FRAME_DEBUGGER_NOT_READY_RETRIES:
                    not_ready_attempt += 1
                    time.sleep(FRAME_DEBUGGER_DETAIL_RETRY_SECONDS)
                    continue
                attempt += 1
                if FRAME_DEBUGGER_OUTSIDE_GROUP not in message or "groupName" in arguments or attempt > FRAME_DEBUGGER_GROUP_RETRIES:
                    raise
                time.sleep(FRAME_DEBUGGER_DETAIL_RETRY_SECONDS)

    def run_frame_debugger_drawcall_capture(
        self, arguments: dict[str, Any], request_id: Any, progress_token: Any, notify: Any | None
    ) -> dict[str, Any]:
        """frame-debugger-drawcall-screenshot over editor ticks: select an event in one bridge call, capture it in the next.

        Captured in one call, both halves were the Game view as it was before the selection (in Play Mode the finished
        frame with its UI) while the reply named the draw call. A Unity package without the steps answers the first call.
        """
        reply = self.call_frame_debugger_draw_call(
            "frame-debugger-drawcall-screenshot", {**arguments, "captureStep": "start"}, request_id, progress_token, notify
        )
        for _ in range(FRAME_DEBUGGER_CAPTURE_MAX_STEPS):
            result = reply.get("result")
            if not (isinstance(result, dict) and result.get("pending")):
                return reply
            time.sleep(FRAME_DEBUGGER_PICK_STEP_SECONDS)
            reply = self.call_unity_bridge(
                "frame-debugger-drawcall-screenshot", {"captureStep": "next"}, request_id, None, None
            )
        raise RuntimeError(
            f"frame-debugger-drawcall-screenshot did not finish in {FRAME_DEBUGGER_CAPTURE_MAX_STEPS} steps; call it again."
        )

    def run_frame_debugger_pixel_search(self, arguments: dict[str, Any], request_id: Any) -> dict[str, Any]:
        """frame-debugger-pick-pixel over several editor ticks: one capture per bridge call.

        The Frame Debugger applies a new event limit and re-renders the Game view only on a later editor tick, so a
        search inside one call compared copies of the same picture and always answered event 0 (seen in Play Mode).
        A Unity package without the stepped search returns its whole answer to the first call.
        """
        reply = self.call_unity_bridge(
            "frame-debugger-pick-pixel", {**arguments, "searchStep": "start"}, request_id, None, None
        )
        for _ in range(FRAME_DEBUGGER_PICK_MAX_STEPS):
            result = reply.get("result")
            if not (isinstance(result, dict) and result.get("pending")):
                return reply
            time.sleep(FRAME_DEBUGGER_PICK_STEP_SECONDS)
            reply = self.call_unity_bridge(
                "frame-debugger-pick-pixel", {"searchStep": "next"}, request_id, None, None
            )
        raise RuntimeError(
            f"frame-debugger-pick-pixel did not finish in {FRAME_DEBUGGER_PICK_MAX_STEPS} steps; call it again."
        )

    def wait_for_compile_start(self, since_event_id: int, timeout_seconds: float) -> bool:
        """True once the bridge journaled compile-start (or compile-finish) after since_event_id."""
        return self.wait_for_compile_event(since_event_id, timeout_seconds, ("compile-start", "compile-finish"))

    def wait_for_compile_event(self, since_event_id: int, timeout_seconds: float, types: tuple[str, ...]) -> bool:
        """True once the bridge journaled an editor event of one of types after since_event_id."""
        deadline = time.monotonic() + max(timeout_seconds, 0.0)
        while True:
            for event in self.read_event_stream().get("events", []):
                event_id = event.get("eventId")
                if (
                    isinstance(event_id, int)
                    and event_id > since_event_id
                    and event.get("source") == "editor"
                    and event.get("type") in types
                ):
                    return True
            if time.monotonic() >= deadline:
                return False
            time.sleep(RECOMPILE_START_POLL_SECONDS)

    @staticmethod
    def _requested_playmode(arguments: dict[str, Any]) -> bool | None:
        # Mirror the C# handler's accepted keys so we still know the target state if the bridge response
        # is lost to a domain reload. Boolean only (matches what the editor validates).
        for key in ("isPlaying", "play", "playing", "enabled"):
            value = arguments.get(key)
            if isinstance(value, bool):
                return value
        return None

    def editor_playmode_set(
        self,
        arguments: dict[str, Any],
        request_id: Any = None,
        progress_token: Any = None,
        notify: Any | None = None,
    ) -> dict[str, Any]:
        requested_arg = self._requested_playmode(arguments)
        try:
            bridge_result = self.call_unity_bridge("editor-playmode-set", arguments, request_id, progress_token, notify)
            result = bridge_result.get("result")
            if not isinstance(result, dict):
                result = {}
        except EditorUnresponsiveError:
            raise
        except RuntimeError:
            # Toggling Play Mode domain-reloads Unity, which can interrupt the bridge round-trip: the
            # response is lost across the reload, or the heartbeat goes briefly stale and call_unity_bridge
            # times out. That is NOT a failure — the editor still applied the toggle. Recover by confirming
            # the real state from the heartbeat below instead of surfacing a scary "bridge unavailable".
            # (Requires waitForReady so we actually go on to confirm; and a known requested state.)
            if requested_arg is None or not self.bridge_dir or not parse_bool(arguments.get("waitForReady"), True):
                raise
            result = {
                "ok": True,
                "status": "requested",
                "requestedIsPlaying": requested_arg,
                "bridgeRoundTripInterrupted": True,
            }

        # Entering/exiting Play Mode is async (and may domain-reload), so the bridge returns mid-transition.
        # When waitForReady is set, poll the heartbeat until isPlaying actually matches the request, then
        # settle briefly so initial frames render — removing the caller's guess-and-sleep.
        requested = result.get("requestedIsPlaying")
        if not isinstance(requested, bool):
            requested = requested_arg
        if result.get("ok") is False or not isinstance(requested, bool) or not self.bridge_dir:
            return result
        if not parse_bool(arguments.get("waitForReady"), True):
            return result

        default_wait_seconds = (
            PLAYMODE_ENTER_WAIT_TIMEOUT_SECONDS if requested else PLAYMODE_EXIT_WAIT_TIMEOUT_SECONDS
        )
        timeout_ms = clamp_int(
            arguments.get("timeoutMs"), int(default_wait_seconds * 1000), 100, 5 * 60 * 1000
        )
        settle_ms = clamp_int(arguments.get("settleMs"), PLAYMODE_SETTLE_MS_DEFAULT, 0, 60 * 1000)
        self.emit_progress(progress_token, notify, 0.3, f"Waiting for Play Mode to reach isPlaying={requested}.")
        reached, waited_ms = self.wait_for_playmode(requested, timeout_ms / 1000, progress_token, notify)

        if reached and settle_ms > 0:
            time.sleep(settle_ms / 1000)
        final_state = self.read_playmode_state()

        result["waitedForReady"] = True
        result["playmodeReady"] = reached
        result["waitedMs"] = waited_ms
        result["settleMs"] = settle_ms
        if isinstance(final_state, bool):
            result["isPlaying"] = final_state
            # The pause pressed before Play holds the game on its first frame: the reply says so, not just "ready".
            result["isPaused"] = final_state and self.read_editor_paused()
            if final_state:
                stale_since = self.read_editor_field("staleStaticStateSinceUtc")
                if isinstance(stale_since, str) and stale_since:
                    result["staleStaticStateSinceUtc"] = stale_since
        if not reached:
            result["warning"] = f"Play mode did not reach isPlaying={requested} within {timeout_ms}ms."
        self.emit_progress(progress_token, notify, 1.0, "editor-playmode-set completed.")
        return result

    def read_editor_paused(self) -> bool:
        return self.read_editor_field("isPaused") is True

    def read_editor_field(self, name: str) -> Any:
        heartbeat = read_json_file(self.state_path) or {}
        editor = heartbeat.get("editor") if isinstance(heartbeat.get("editor"), dict) else {}
        return editor.get(name)

    def read_playmode_state(self) -> bool | None:
        """Settled EditorApplication.isPlaying from the heartbeat, or None when it can't be trusted yet
        (bridge unreachable, or mid-transition). During a transition isPlaying and
        isPlayingOrWillChangePlaymode disagree (entering: false/true, exiting: true/false); returning
        None then makes the wait keep polling until the state is fully settled, so it never reports
        'ready' while play mode is still entering or exiting."""
        heartbeat = read_json_file(self.state_path) or {}
        heartbeat_age = file_age_seconds(self.state_path, time.time()) if self.state_path.exists() else None
        if heartbeat_age is None or heartbeat_age > HEARTBEAT_STALE_SECONDS:
            return None
        editor = heartbeat.get("editor") if isinstance(heartbeat.get("editor"), dict) else {}
        is_playing = editor.get("isPlaying")
        if not isinstance(is_playing, bool):
            return None
        will_change = editor.get("isPlayingOrWillChangePlaymode")
        if isinstance(will_change, bool) and will_change != is_playing:
            return None
        return is_playing

    def wait_for_playmode(
        self,
        requested: bool,
        timeout_seconds: float,
        progress_token: Any = None,
        notify: Any | None = None,
    ) -> tuple[bool, int]:
        started = time.monotonic()
        deadline = started + max(timeout_seconds, 0.0)
        next_progress_at = started + PROGRESS_INTERVAL_SECONDS
        while True:
            if self.read_playmode_state() == requested:
                return True, int((time.monotonic() - started) * 1000)
            now = time.monotonic()
            if now >= deadline:
                return False, int((now - started) * 1000)
            if progress_token is not None and notify is not None and now >= next_progress_at:
                self.emit_progress(progress_token, notify, 0.5, f"Waiting for Play Mode to reach isPlaying={requested}.")
                next_progress_at = now + PROGRESS_INTERVAL_SECONDS
            time.sleep(BRIDGE_READY_POLL_SECONDS)

    def collect_recompile_issues(self, since_event_id: int) -> dict[str, Any]:
        """Gather compile errors/warnings emitted after `since_event_id` from the event stream.

        Compiler messages are journaled as source=log events (with cursors), so this survives the
        domain reload a clean compile triggers, unlike the in-memory console buffer which is wiped.

        Only compiler diagnostics are reported as compile issues. The same window also catches
        ordinary runtime logs — stopping Play Mode to compile puts a project's own shutdown errors
        right inside it — and echoing one of those announced "1 error" for a compile that had in fact
        succeeded, with a game logger's inlined stack trace filling the response. Those are counted
        (so they are not silently dropped) and left for console-get-logs to show in full.
        """
        stream = self.read_event_stream()
        log_events = self.filter_events(
            stream.get("events", []),
            since_event_id=since_event_id,
            filters={"source": "log"},
            include_data=False,
        )
        # Compiler messages are journaled twice (the compilation callback and the log callback), so
        # dedupe by (level, message) keeping the earliest, to show each unique issue once.
        errors: list[dict[str, Any]] = []
        warnings: list[dict[str, Any]] = []
        other_errors = 0
        other_warnings = 0
        other_error_rows: list[dict[str, Any]] = []
        seen: set[tuple[str, str]] = set()
        for event in log_events:
            level = str(event.get("level", "")).lower()
            is_error = level in ("error", "exception", "assert")
            if not is_error and level != "warning":
                continue
            if not is_compiler_diagnostic(event):
                if is_error:
                    other_errors += 1
                    if len(other_error_rows) < RECOMPILE_MAX_OTHER_ERROR_LINES:
                        first_line = clean_console_message(event.get("message"), RECOMPILE_MAX_ISSUE_CHARS).split("\n", 1)[0]
                        other_error_rows.append(
                            {
                                "eventId": event.get("eventId"),
                                "message": clean_console_message(first_line, RECOMPILE_OTHER_ERROR_CHARS),
                            }
                        )
                else:
                    other_warnings += 1
                continue
            # Clean before deduping: the two journaled copies of one diagnostic can differ in markup.
            message = clean_console_message(event.get("message"), RECOMPILE_MAX_ISSUE_CHARS)
            key = (level, message)
            if key in seen:
                continue
            seen.add(key)
            row = {"eventId": event.get("eventId"), "message": message}
            if is_error:
                errors.append(row)
            else:
                warnings.append(row)

        issues: dict[str, Any] = {
            "errorCount": len(errors),
            "warningCount": len(warnings),
            "sinceEventId": since_event_id,
        }
        if errors:
            issues["errors"] = errors[:RECOMPILE_MAX_ISSUES]
            if len(errors) > RECOMPILE_MAX_ISSUES:
                issues["errorsTruncated"] = True
        if warnings:
            issues["warnings"] = warnings[:RECOMPILE_MAX_ISSUES]
            if len(warnings) > RECOMPILE_MAX_ISSUES:
                issues["warningsTruncated"] = True
        if other_errors:
            issues["otherErrorCount"] = other_errors
            issues["otherErrors"] = other_error_rows
        if other_warnings:
            issues["otherWarningCount"] = other_warnings
        return issues
