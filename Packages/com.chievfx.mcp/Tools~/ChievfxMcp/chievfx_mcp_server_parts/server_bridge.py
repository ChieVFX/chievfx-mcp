# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

from __future__ import annotations

class BridgeTransportMixin:
    def call_unity_bridge(self, name: str, arguments: dict[str, Any], request_id: Any = None, progress_token: Any = None, notify: Any | None = None) -> dict[str, Any]:
        if self.bridge_dir:
            return self.call_unity_bridge_file(name, arguments, request_id, progress_token, notify)

        url = f"{self.unity_url}/tools/{urllib.parse.quote(name)}"
        body = json.dumps(arguments).encode("utf-8")
        timeout_seconds = self.get_tool_timeout_ms(name, arguments) / 1000
        request = urllib.request.Request(
            url,
            data=body,
            headers={"Content-Type": "application/json"},
            method="POST",
        )

        try:
            with urllib.request.urlopen(request, timeout=timeout_seconds) as response:
                payload = json.loads(response.read().decode("utf-8"))
        except urllib.error.URLError as exc:
            status = self.get_bridge_status({})
            raise RuntimeError(f"Unity bridge unavailable at {self.unity_url}: {exc}. {self.format_status_hint(status)}") from exc

        if not payload.get("ok"):
            status = self.get_bridge_status({})
            raise RuntimeError(f"{payload.get('error') or 'Unity bridge returned an error.'} {self.format_status_hint(status)}")

        return payload

    def read_bridge_readiness(self) -> dict[str, Any]:
        heartbeat = read_json_file(self.state_path) or {}
        heartbeat_age = file_age_seconds(self.state_path, time.time()) if self.state_path.exists() else None
        bridge_reachable = heartbeat_age is not None and heartbeat_age <= HEARTBEAT_STALE_SECONDS
        editor = heartbeat.get("editor") if isinstance(heartbeat.get("editor"), dict) else {}
        busy = heartbeat.get("busy") if isinstance(heartbeat.get("busy"), dict) else {}
        busy_reasons = heartbeat.get("busyReasons") if isinstance(heartbeat.get("busyReasons"), list) else []
        is_compiling = (
            bool(editor.get("isCompiling"))
            or bool(busy.get("isCompiling"))
            or "editor-compiling" in busy_reasons
        )
        is_updating = (
            bool(editor.get("isUpdating"))
            or bool(busy.get("isUpdating"))
            or "asset-database-updating" in busy_reasons
        )
        return {
            "heartbeat": heartbeat,
            "heartbeatAge": heartbeat_age,
            "bridgeReachable": bridge_reachable,
            "isCompiling": is_compiling,
            "isUpdating": is_updating,
            "ready": bridge_reachable and not is_compiling and not is_updating,
        }

    def bridge_recovery_wait_reason(self) -> tuple[str | None, float]:
        readiness = self.read_bridge_readiness()
        heartbeat_age = readiness["heartbeatAge"]
        # The flags of a heartbeat that stopped an hour ago say nothing about now: no recovery to wait for.
        if not readiness["bridgeReachable"] and self.describe_editor_down(self.assess_editor_liveness()) is not None:
            return None, 0.0
        if readiness["isCompiling"]:
            return "Unity editor is compiling or reloading assemblies.", BRIDGE_RECOVERY_WAIT_SECONDS
        if readiness["isUpdating"]:
            return "Unity asset database is updating.", BRIDGE_RECOVERY_WAIT_SECONDS
        if heartbeat_age is not None and not readiness["bridgeReachable"] and heartbeat_age <= BRIDGE_STALE_RECOVERY_WAIT_SECONDS:
            return "Unity heartbeat is stale; waiting for possible domain reload recovery.", BRIDGE_STALE_RECOVERY_WAIT_SECONDS
        if heartbeat_age is not None and not readiness["bridgeReachable"] and heartbeat_age < EDITOR_BUSY_UNRESPONSIVE_SECONDS:
            busy_sign = self.editor_busy_sign(readiness["heartbeat"], heartbeat_age)
            if busy_sign is not None:
                return f"Unity is {busy_sign}.", BRIDGE_RECOVERY_WAIT_SECONDS
        return None, 0.0

    def editor_busy_sign(self, heartbeat: dict[str, Any], heartbeat_age: float | None = None) -> str | None:
        """What the editor was doing that explains a stale heartbeat, or None when nothing does.

        The last heartbeat says compiling/importing/changing play mode; the journal says a compile or a
        domain reload started and has not finished (the reload itself stops the heartbeat). A flag that has
        stood for minutes is checked against the main thread: a dialog that opened during the work stops it too.
        """
        sign = self._editor_flagged_busy_sign(heartbeat)
        if sign is None:
            return None
        if sign.startswith("working on its main thread") or sign.startswith("resolving packages or importing (a script"):
            return sign
        if heartbeat_age is not None and heartbeat_age >= EDITOR_FLAGGED_MODAL_CHECK_AFTER_SECONDS:
            editor = heartbeat.get("editor") if isinstance(heartbeat.get("editor"), dict) else {}
            if self.editor_modal_loop(editor.get("processId")) in MODAL_SESSION_STACK_MARKERS:
                return None
        return sign

    def _editor_flagged_busy_sign(self, heartbeat: dict[str, Any]) -> str | None:
        editor = heartbeat.get("editor") if isinstance(heartbeat.get("editor"), dict) else {}
        busy = heartbeat.get("busy") if isinstance(heartbeat.get("busy"), dict) else {}
        if (bool(editor.get("isCompiling")) or bool(busy.get("isCompiling"))) and not editor.get("compileWaitingForPlayModeExit"):
            return "compiling scripts"
        if bool(editor.get("isUpdating")) or bool(busy.get("isUpdating")):
            return "importing assets"
        if "isPlayingOrWillChangePlaymode" in editor and bool(editor.get("isPlayingOrWillChangePlaymode")) != bool(editor.get("isPlaying")):
            return "changing play mode"
        if bool(busy.get("packageBusy")):
            return "resolving packages"
        if bool(busy.get("testBusy")):
            return "running tests"
        for event in reversed(self.read_event_stream().get("events", [])):
            if event.get("source") != "editor" or event.get("type") not in EDITOR_LIFECYCLE_EVENT_TYPES:
                continue
            if event.get("type") == "compile-start":
                return "compiling scripts"
            if event.get("type") == "domain-reload-before":
                return "reloading the script domain"
            break
        started_at = getattr(self, "_script_started_editor_work_at", None)
        is_script_work = started_at is not None and time.monotonic() - started_at < EDITOR_SCRIPT_STARTED_WORK_SECONDS
        cores = None if is_script_work else self.editor_cpu_cores(editor.get("processId"))
        if not is_script_work and (cores is None or cores < EDITOR_CPU_BUSY_CORES):
            return None
        # Neither sign tells a dialog from work: a modal loop keeps the CPU busy too, and the server waited 900 s on
        # "Missing Signature" after a package change. The main thread's stack does tell.
        if self.editor_modal_loop(editor.get("processId")) is not None:
            return None
        if is_script_work:
            return f"resolving packages or importing (a script-execute {int(time.monotonic() - started_at)} s ago started it)"
        return f"working on its main thread ({int((cores or 0) * 100)}% CPU: an import, a package resolution or a long computation)"

    def editor_modal_loop(self, process_id: Any) -> str | None:
        """The modal-loop frame the editor's main thread is in (sampled at most every EDITOR_MODAL_CHECK_SECONDS), or None.

        Unity's own progress window (entering Play Mode under load, "Hold on") spins the same modal loop for a few
        seconds, and one sample called that a dialog: a dialog is a modal loop the previous sample saw too.
        """
        cached = getattr(self, "_editor_modal_check", None)
        now_mono = time.monotonic()
        if cached is not None and cached[0] == process_id and now_mono - cached[1] < EDITOR_MODAL_CHECK_SECONDS:
            return cached[2]
        marker = process_modal_loop(process_id)
        seen_before = (
            cached is not None
            and cached[0] == process_id
            and now_mono - cached[1] < EDITOR_MODAL_CHECK_SECONDS * 3
            and (cached[3] if len(cached) > 3 else cached[2]) is not None
        )
        confirmed = marker if marker is not None and seen_before else None
        self._editor_modal_check = (process_id, time.monotonic(), confirmed, marker)
        return confirmed

    def recent_editor_modal_loop(self, process_id: Any) -> str | None:
        """The modal loop the last check found for this editor, if that check is recent."""
        cached = getattr(self, "_editor_modal_check", None)
        if cached is None or cached[0] != process_id or time.monotonic() - cached[1] >= EDITOR_MODAL_CHECK_SECONDS * 2:
            return None
        return cached[2]

    def editor_cpu_cores(self, process_id: Any) -> float | None:
        """How many cores the editor process has used lately, from two CPU-time readings (None when unknown)."""
        now_mono = time.monotonic()
        cpu_seconds = process_cpu_seconds(process_id)
        if cpu_seconds is None:
            return None
        previous = getattr(self, "_editor_cpu_sample", None)
        if (
            previous is None
            or previous[2] != process_id
            or not EDITOR_CPU_SAMPLE_SECONDS <= now_mono - previous[0] <= EDITOR_CPU_SAMPLE_MAX_AGE_SECONDS
        ):
            time.sleep(EDITOR_CPU_SAMPLE_SECONDS)
            previous = (now_mono, cpu_seconds, process_id)
            now_mono = time.monotonic()
            cpu_seconds = process_cpu_seconds(process_id)
            if cpu_seconds is None:
                return None
        self._editor_cpu_sample = (now_mono, cpu_seconds, process_id)
        return max(0.0, cpu_seconds - previous[1]) / max(now_mono - previous[0], 1e-3)

    def assess_editor_liveness(self) -> dict[str, Any]:
        """responsive | stale (briefly, as during a reload) | busy | unresponsive | gone | unknown (no heartbeat yet).

        Cached for EDITOR_LIVENESS_CHECK_SECONDS: wait loops poll it, and a stale heartbeat means reading the journal.
        """
        cached = getattr(self, "_editor_liveness_cache", None)
        now_mono = time.monotonic()
        if cached is not None and now_mono - cached[0] < EDITOR_LIVENESS_CHECK_SECONDS:
            return cached[1]
        liveness = self._read_editor_liveness()
        self._editor_liveness_cache = (now_mono, liveness)
        return liveness

    def _read_editor_liveness(self) -> dict[str, Any]:
        heartbeat = read_json_file(self.state_path) or {}
        heartbeat_age = file_age_seconds(self.state_path, time.time()) if self.state_path.exists() else None
        editor = heartbeat.get("editor") if isinstance(heartbeat.get("editor"), dict) else {}
        process_id = editor.get("processId") if isinstance(editor.get("processId"), int) and not isinstance(editor.get("processId"), bool) else None
        row: dict[str, Any] = {"heartbeatAgeSeconds": heartbeat_age, "processId": process_id}
        if heartbeat_age is None:
            return {**row, "state": "unknown"}
        if heartbeat_age <= HEARTBEAT_STALE_SECONDS:
            return {**row, "state": "responsive"}
        if process_is_alive(process_id) is False:
            return {**row, "state": "gone", "processAlive": False}
        busy_sign = self.editor_busy_sign(heartbeat, heartbeat_age)
        limit = EDITOR_BUSY_UNRESPONSIVE_SECONDS if busy_sign is not None else EDITOR_UNRESPONSIVE_SECONDS
        state = ("busy" if busy_sign is not None else "stale") if heartbeat_age < limit else "unresponsive"
        # A dialog waits for a click, not for time: no point in waiting out the limit.
        modal_loop = self.recent_editor_modal_loop(process_id) if busy_sign is None else None
        if modal_loop is not None:
            row["modalLoop"] = modal_loop
            state = "unresponsive"
        busy = heartbeat.get("busy") if isinstance(heartbeat.get("busy"), dict) else {}
        # The bridge's own work on the main thread (a long synchronous script) stops the heartbeat just as a hang does.
        # The flag is from the last heartbeat: a script that has since returned (and started an import) is not it.
        own_work = [label for flag, label in (("scriptBusy", "script-execute"), ("editorWindowScreenshotBusy", "an editor window screenshot")) if busy.get(flag)]
        if "script-execute" in own_work and not self.has_active_operation("script-execute"):
            own_work.remove("script-execute")
        return {**row, "state": state, "busySign": busy_sign, "unresponsiveAfterSeconds": limit, "runningWhenStopped": own_work}

    def has_active_operation(self, tool_name: str) -> bool:
        """True when a bridge operation of this tool has not ended (unknown counts as active).

        A running record that stopped updating reads as stale: that is just what a script blocking the main thread
        looks like, so it counts as active too.
        """
        try:
            rows = self.read_operation_rows(MAX_STATUS_OPERATIONS, time.time())
        except Exception:  # noqa: BLE001 - a probe, never a reason to fail the call
            return True
        return any(
            row.get("toolName") == tool_name and (row.get("state") not in TERMINAL_OPERATION_STATES or row.get("stale") is True)
            for row in rows
        )

    @staticmethod
    def describe_editor_down(liveness: dict[str, Any]) -> str | None:
        """The fail-fast message for a hung or gone editor; None while it may still answer."""
        state = liveness.get("state")
        if state not in ("unresponsive", "gone"):
            return None
        age = liveness.get("heartbeatAgeSeconds") or 0.0
        age_text = f"{int(age)} s" if age < 3600 else f"{age / 3600:.1f} h"
        process_id = liveness.get("processId")
        editor = f"Unity (PID {process_id})" if process_id else "Unity"
        if state == "gone":
            return (
                f"editor_not_running: {editor}, which wrote the last bridge heartbeat {age_text} ago, is not running "
                "(closed or crashed). Start the editor; calls fail at once until its bridge writes a heartbeat."
            )
        busy_sign = liveness.get("busySign")
        cause = (
            f"has been {busy_sign} for longer than {int(liveness.get('unresponsiveAfterSeconds') or 0)} s"
            if busy_sign
            else "shows no compile, domain reload, import or play-mode change under way"
        )
        modal_loop = liveness.get("modalLoop")
        if modal_loop:
            return (
                f"editor_unresponsive: {editor} has not written a bridge heartbeat for {age_text}: its main thread waits in a "
                f"modal dialog ({modal_loop} on its stack). After a package change it is usually 'Missing Signature' (press "
                "Close); a script's EditorUtility.DisplayDialog needs an answer. Calls work again once it is closed "
                "(unity-dialogs skill, or ask the user). bridge-get-status shows editorState."
            )
        own_work = liveness.get("runningWhenStopped") or []
        suspect = (
            f" The last heartbeat shows {' and '.join(own_work)} running: it may be what holds the main thread."
            if own_work
            else ""
        )
        return (
            f"editor_unresponsive: {editor} has not written a bridge heartbeat for {age_text} and {cause}: its main "
            "thread is stuck (hung, behind a modal dialog, or in the crash handler)." + suspect + " Calls fail at "
            "once until a heartbeat comes back; look at the editor or restart it. bridge-get-status shows editorState."
        )

    def raise_if_editor_unresponsive(self) -> None:
        if not self.bridge_dir:
            return
        message = self.describe_editor_down(self.assess_editor_liveness())
        if message is not None:
            raise EditorUnresponsiveError(message)

    def wait_for_bridge_recovery_if_needed(
        self,
        progress_token: Any = None,
        notify: Any | None = None,
    ) -> bool:
        reason, max_wait_seconds = self.bridge_recovery_wait_reason()
        if reason is None:
            return True

        started = time.monotonic()
        deadline = started + max_wait_seconds
        waited = False
        next_progress_at = 0.0
        while True:
            readiness = self.read_bridge_readiness()
            if readiness["ready"]:
                if waited:
                    time.sleep(BRIDGE_READY_POST_BUSY_GRACE_SECONDS)
                return True

            reason, max_wait_seconds = self.bridge_recovery_wait_reason()
            if reason is None:
                return False

            now = time.monotonic()
            deadline = max(deadline, started + max_wait_seconds)
            if now >= deadline:
                return False

            if progress_token is not None and notify is not None and now >= next_progress_at:
                self.emit_progress(progress_token, notify, 0.05, f"Waiting for Unity bridge recovery: {reason}")
                next_progress_at = now + PROGRESS_INTERVAL_SECONDS

            waited = True
            time.sleep(BRIDGE_READY_POLL_SECONDS)

    def wait_for_bridge_ready(
        self,
        max_wait_seconds: float = BRIDGE_READY_WAIT_SECONDS,
        post_busy_grace_seconds: float = BRIDGE_READY_POST_BUSY_GRACE_SECONDS,
    ) -> bool:
        """Block until the Unity bridge heartbeat reports a ready editor.

        Returns True once the bridge is reachable, not compiling, and not
        updating assets, with a small grace period after busy clears.
        Returns False on timeout; callers should still attempt the request so
        the existing per-request timeout path runs.
        """
        if not self.bridge_dir:
            return True

        deadline = time.monotonic() + max(max_wait_seconds, 0.0)
        last_busy_at: float | None = None
        while True:
            now_mono = time.monotonic()
            heartbeat = read_json_file(self.state_path) or {}
            heartbeat_age = file_age_seconds(self.state_path, time.time()) if self.state_path.exists() else None
            bridge_reachable = heartbeat_age is not None and heartbeat_age <= HEARTBEAT_STALE_SECONDS
            editor = heartbeat.get("editor") if isinstance(heartbeat.get("editor"), dict) else {}
            busy = bool(editor.get("isCompiling")) or bool(editor.get("isUpdating"))
            if not bridge_reachable or busy:
                last_busy_at = now_mono
                if not bridge_reachable and self.describe_editor_down(self.assess_editor_liveness()) is not None:
                    return False
            elif last_busy_at is None or (now_mono - last_busy_at) >= post_busy_grace_seconds:
                return True

            if now_mono >= deadline:
                return False

            time.sleep(BRIDGE_READY_POLL_SECONDS)

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
                    "may not have started. Unity does not compile while playing."
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
        group 9 draw calls 0..0", and a repeat of the same call worked.
        """
        attempt = 0
        while True:
            try:
                return self.call_unity_bridge(name, arguments, request_id, progress_token, notify)
            except RuntimeError as exc:
                attempt += 1
                if FRAME_DEBUGGER_OUTSIDE_GROUP not in str(exc) or "groupName" in arguments or attempt > FRAME_DEBUGGER_GROUP_RETRIES:
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
        if not reached:
            result["warning"] = f"Play mode did not reach isPlaying={requested} within {timeout_ms}ms."
        self.emit_progress(progress_token, notify, 1.0, "editor-playmode-set completed.")
        return result

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

    def discard_timed_out_request(self, request_id: str) -> None:
        """After the MCP server gives up waiting, stop Unity from later draining
        the orphaned request and re-running a tool nobody is listening for.

        Writes a cancel marker so Unity skips the request if it has not started
        it yet, and deletes the still-queued request file directly. A request
        already moved to ``.processing`` is left for Unity to finish; the stale
        ``.processing`` file is reaped by prune_stale_transport_files."""
        cancel_path = self.cancel_dir / f"{request_id}.json"
        try:
            self.cancel_dir.mkdir(parents=True, exist_ok=True)
            write_json_file_atomic(
                cancel_path,
                {
                    "operationId": request_id,
                    "requestedAtUtc": utc_now_iso(),
                    "reason": "MCP server timed out waiting for Unity bridge response.",
                },
            )
        except OSError:
            pass

        request_path = self.request_dir / f"{request_id}.json"
        processing_path = self.request_dir / f"{request_id}.json.processing"
        if request_path.exists() and not processing_path.exists():
            try:
                request_path.unlink(missing_ok=True)
            except OSError:
                pass

    def read_operation_state(self, operation_id: str) -> str | None:
        record = read_json_file(self.operation_dir / f"{operation_id}.json")
        if not isinstance(record, dict):
            return None
        state = record.get("state")
        return state if isinstance(state, str) else None

    def prune_stale_transport_files(self) -> None:
        """Remove leftover transport files that can survive crashes or domain
        reloads. Without this, pendingResponses can accumulate and make the
        bridge appear permanently busy to clients and to bridge-get-status."""
        now = time.time()
        active_ids = self.collect_active_operation_ids()
        for path in self._safe_glob(self.response_dir, "*.json"):
            stem = path.stem
            if stem in active_ids:
                continue
            age = file_age_seconds(path, now)
            if age is None or age <= ORPHAN_RESPONSE_STALE_SECONDS:
                continue
            try:
                path.unlink(missing_ok=True)
            except OSError:
                continue

        for path in self._safe_glob(self.request_dir, "*.processing"):
            age = file_age_seconds(path, now)
            if age is None or age <= PROCESSING_STALE_SECONDS:
                continue
            try:
                path.unlink(missing_ok=True)
            except OSError:
                continue

        # Orphan request files for operations that already reached a terminal
        # state (or have no live record) keep Unity draining backlog after the
        # MCP server gave up. Reap them so the bridge does not re-run abandoned
        # tools in a death-spiral loop.
        for path in self._safe_glob(self.request_dir, "*.json"):
            stem = path.stem
            if stem in active_ids:
                continue
            processing_path = self.request_dir / f"{stem}.json.processing"
            if processing_path.exists():
                continue
            age = file_age_seconds(path, now)
            if age is None or age <= ORPHAN_RESPONSE_STALE_SECONDS:
                continue
            state = self.read_operation_state(stem)
            if state is not None and state not in TERMINAL_OPERATION_STATES:
                continue
            try:
                path.unlink(missing_ok=True)
            except OSError:
                continue

        # Cancel markers whose operation is terminal/gone just accumulate; clear
        # the old ones so the cancel directory does not grow without bound.
        for path in self._safe_glob(self.cancel_dir, "*.json"):
            stem = path.stem
            if stem in active_ids:
                continue
            age = file_age_seconds(path, now)
            if age is None or age <= ORPHAN_RESPONSE_STALE_SECONDS:
                continue
            state = self.read_operation_state(stem)
            if state is not None and state not in TERMINAL_OPERATION_STATES:
                continue
            try:
                path.unlink(missing_ok=True)
            except OSError:
                continue

    def collect_active_operation_ids(self) -> set[str]:
        ids: set[str] = set()
        with self.request_operation_lock:
            ids.update(self.request_operation_ids.values())
        return ids

    @staticmethod
    def _safe_glob(directory: Path, pattern: str) -> list[Path]:
        if not directory.exists():
            return []
        try:
            return list(directory.glob(pattern))
        except OSError:
            return []

    def call_unity_bridge_file(self, name: str, arguments: dict[str, Any], jsonrpc_request_id: Any = None, progress_token: Any = None, notify: Any | None = None) -> dict[str, Any]:
        # Calls take turns; one stuck on a hung editor used to hold every later call for its full timeout.
        self.raise_if_editor_unresponsive()
        while not self.bridge_call_lock.acquire(timeout=EDITOR_LIVENESS_CHECK_SECONDS):
            self.raise_if_editor_unresponsive()
        try:
            allow_recovery_extension = self.wait_for_bridge_recovery_if_needed(progress_token, notify)
            self.raise_if_editor_unresponsive()
            return self._call_unity_bridge_file_locked(name, arguments, jsonrpc_request_id, progress_token, notify, allow_recovery_extension)
        finally:
            self.bridge_call_lock.release()

    def _call_unity_bridge_file_locked(
        self,
        name: str,
        arguments: dict[str, Any],
        jsonrpc_request_id: Any = None,
        progress_token: Any = None,
        notify: Any | None = None,
        allow_recovery_extension: bool = True,
    ) -> dict[str, Any]:
        request_id = uuid.uuid4().hex
        self.request_dir.mkdir(parents=True, exist_ok=True)
        self.response_dir.mkdir(parents=True, exist_ok=True)
        self.operation_dir.mkdir(parents=True, exist_ok=True)
        self.cancel_dir.mkdir(parents=True, exist_ok=True)
        self.prune_stale_transport_files()
        request_path = self.request_dir / f"{request_id}.json"
        response_path = self.response_dir / f"{request_id}.json"
        timeout_ms = self.get_tool_timeout_ms(name, arguments)
        timeout_seconds = timeout_ms / 1000
        jsonrpc_key = str(jsonrpc_request_id) if jsonrpc_request_id is not None else None
        self.write_operation_record(
            request_id,
            {
                "operationId": request_id,
                "jsonRpcRequestId": jsonrpc_key,
                "toolName": name,
                "state": "queued",
                "queuedAtUtc": utc_now_iso(),
                "updatedAtUtc": utc_now_iso(),
                "progressMessage": "Queued for Unity bridge.",
                "cancellable": True,
                "timeoutMs": timeout_ms,
            },
        )
        if jsonrpc_key is not None:
            with self.request_operation_lock:
                self.request_operation_ids[jsonrpc_key] = request_id

        write_json_file_atomic(
            request_path,
            {
                "id": request_id,
                "toolName": name,
                "arguments": arguments,
                "timeoutMs": timeout_ms,
                "createdAtUtc": utc_now_iso(),
                "jsonRpcRequestId": jsonrpc_key,
            },
        )

        try:
            wait_timeout_seconds = max(timeout_ms, 1000) / 1000
            started = time.monotonic()
            deadline = started + wait_timeout_seconds
            started_wall_time = time.time()
            next_progress_at = started
            progress_value = 0.0
            recovery_deadline = started + wait_timeout_seconds
            recovery_extended = False
            post_recovery_grace_started = False
            next_recovery_record_at = started
            next_liveness_check_at = started + EDITOR_LIVENESS_CHECK_SECONDS
            while True:
                if response_path.exists():
                    payload = json.loads(read_text_file(response_path))
                    response_path.unlink(missing_ok=True)
                    if not payload.get("ok"):
                        error_message = payload.get("error") or "Unity bridge returned an error."
                        final_state = "cancelled" if "cancel" in str(error_message).lower() else "failed"
                        self.update_operation_record(
                            request_id,
                            state=final_state,
                            completedAtUtc=utc_now_iso(),
                            progressMessage=error_message,
                        )
                        final_message = f"{name} cancelled." if final_state == "cancelled" else f"{name} failed."
                        self.emit_progress(progress_token, notify, 1.0, final_message)
                        raise RuntimeError(error_message)

                    self.update_operation_record(
                        request_id,
                        state="completed",
                        completedAtUtc=utc_now_iso(),
                        progressMessage=f"{name} completed.",
                    )
                    self.emit_progress(progress_token, notify, 1.0, f"{name} completed.")
                    return payload

                fallback_payload = self.try_read_tests_run_result_payload(name, arguments, timeout_ms, started_wall_time)
                if fallback_payload is not None:
                    self.update_operation_record(
                        request_id,
                        state="completed",
                        completedAtUtc=utc_now_iso(),
                        progressMessage=f"{name} completed from Unity TestResults.xml.",
                    )
                    self.emit_progress(progress_token, notify, 1.0, f"{name} completed.")
                    return fallback_payload

                now = time.monotonic()
                if now >= next_liveness_check_at:
                    next_liveness_check_at = now + EDITOR_LIVENESS_CHECK_SECONDS
                    # Only while the request still waits in the queue: once Unity picked it up, the stale
                    # heartbeat may be this very operation holding the main thread, and its timeout decides.
                    down = self.describe_editor_down(self.assess_editor_liveness()) if request_path.exists() else None
                    if down is not None:
                        self.update_operation_record(
                            request_id, state="failed", completedAtUtc=utc_now_iso(), progressMessage=down
                        )
                        self.discard_timed_out_request(request_id)
                        raise EditorUnresponsiveError(down)
                if progress_token is not None and notify is not None and now >= next_progress_at:
                    elapsed = max(0.0, now - started)
                    target_progress = min(0.95, elapsed / max(wait_timeout_seconds, 0.001))
                    progress_value = max(progress_value, target_progress)
                    self.emit_progress(
                        progress_token,
                        notify,
                        progress_value,
                        self.build_progress_message(request_id, name),
                    )
                    next_progress_at = now + PROGRESS_INTERVAL_SECONDS

                if now >= deadline:
                    reason, max_recovery_wait = self.bridge_recovery_wait_reason() if allow_recovery_extension else (None, 0.0)
                    if reason is not None:
                        recovery_extended = True
                        recovery_deadline = max(recovery_deadline, started + max_recovery_wait)
                        if now < recovery_deadline:
                            if now >= next_recovery_record_at:
                                self.update_operation_record(
                                    request_id,
                                    progressMessage=f"Waiting for Unity bridge recovery: {reason}",
                                )
                                next_recovery_record_at = now + PROGRESS_INTERVAL_SECONDS
                            deadline = min(recovery_deadline, now + BRIDGE_RECOVERY_RECHECK_SECONDS)
                            time.sleep(0.05)
                            continue
                        break

                    if recovery_extended and not post_recovery_grace_started and self.read_bridge_readiness()["ready"]:
                        post_recovery_grace_started = True
                        deadline = now + max(wait_timeout_seconds, BRIDGE_POST_RECOVERY_RESPONSE_GRACE_SECONDS)
                        self.update_operation_record(
                            request_id,
                            progressMessage="Unity bridge recovered; waiting for queued response.",
                        )
                        time.sleep(0.05)
                        continue

                    break

                time.sleep(0.05)

            self.update_operation_record(
                request_id,
                state="stale",
                progressMessage="MCP server timed out waiting for Unity bridge response.",
            )
            self.discard_timed_out_request(request_id)
            status = self.get_bridge_status({})
            raise RuntimeError(
                f"Unity bridge timed out waiting for operation {request_id} ({name}) at {response_path}. "
                f"{self.format_status_hint(status)}"
            )
        finally:
            if jsonrpc_key is not None:
                with self.request_operation_lock:
                    self.request_operation_ids.pop(jsonrpc_key, None)

    def get_tool_timeout_ms(self, name: str, arguments: dict[str, Any]) -> int:
        if name == "script-execute":
            return clamp_int(
                arguments.get("timeoutMs"),
                DEFAULT_SCRIPT_EXECUTE_TIMEOUT_MS,
                100,
                HARD_SCRIPT_EXECUTE_TIMEOUT_MS,
            ) + SCRIPT_EXECUTE_REPLY_ALLOWANCE_MS

        if name == "reflection-method-call":
            return clamp_int(
                arguments.get("timeoutMs"),
                DEFAULT_REFLECTION_METHOD_CALL_TIMEOUT_MS,
                100,
                HARD_REFLECTION_METHOD_CALL_TIMEOUT_MS,
            )

        if name == "tests-run":
            return clamp_int(
                arguments.get("timeoutMs"),
                DEFAULT_TESTS_RUN_TIMEOUT_MS,
                1000,
                HARD_TESTS_RUN_TIMEOUT_MS,
            )

        timeout_ms = arguments.get("timeoutMs")
        if isinstance(timeout_ms, int) and timeout_ms > 0:
            return timeout_ms

        return int(self.timeout_seconds * 1000)

    def try_read_tests_run_result_payload(
        self,
        name: str,
        arguments: dict[str, Any],
        timeout_ms: int,
        started_wall_time: float,
    ) -> dict[str, Any] | None:
        if name != "tests-run" or str(arguments.get("testMode", "")).lower() != "playmode":
            return None

        for path in unity_test_result_paths():
            try:
                if not path.exists() or path.stat().st_mtime < started_wall_time:
                    continue
                return {
                    "ok": True,
                    "contentType": "json",
                    "result": parse_unity_test_results_xml(path, arguments, timeout_ms, started_wall_time),
                }
            except (OSError, ET.ParseError, ValueError):
                continue

        return None

    def write_operation_record(self, operation_id: str, payload: dict[str, Any]) -> None:
        write_json_file_atomic(self.operation_dir / f"{operation_id}.json", payload)

    def update_operation_record(self, operation_id: str, **fields: Any) -> None:
        path = self.operation_dir / f"{operation_id}.json"
        payload = read_json_file(path) or {"operationId": operation_id}
        payload.update(fields)
        payload["updatedAtUtc"] = utc_now_iso()
        write_json_file_atomic(path, payload)

    def emit_progress(self, progress_token: Any, notify: Any | None, progress: float, message: str) -> None:
        if progress_token is None or notify is None:
            return

        if progress >= 1.0:
            # Cursor may tear down/recreate an MCP client while Unity finishes a
            # request. A late terminal progress notification can then reference
            # a token the new client does not know and fail the transport. The
            # final JSON-RPC response and operation record already carry
            # completion state, so only emit non-terminal progress updates.
            return

        notify(
            {
                "jsonrpc": "2.0",
                "method": "notifications/progress",
                "params": {
                    "progressToken": progress_token,
                    "progress": round(max(0.0, min(1.0, progress)), 4),
                    "total": 1.0,
                    "message": message,
                },
            }
        )

    def build_progress_message(self, operation_id: str, tool_name: str) -> str:
        operation = read_json_file(self.operation_dir / f"{operation_id}.json") or {}
        state = operation.get("state") if isinstance(operation.get("state"), str) else "waiting"
        message = operation.get("progressMessage") if isinstance(operation.get("progressMessage"), str) else ""
        status = self.get_bridge_status({})
        busy_reasons = status.get("busyReasons") if isinstance(status.get("busyReasons"), list) else []
        busy_text = ",".join(str(reason) for reason in busy_reasons[:3])
        parts = [f"{tool_name} operation {operation_id} {state}"]
        if message:
            parts.append(message)
        if busy_text:
            parts.append(f"busy:{busy_text}")
        return "; ".join(parts)
