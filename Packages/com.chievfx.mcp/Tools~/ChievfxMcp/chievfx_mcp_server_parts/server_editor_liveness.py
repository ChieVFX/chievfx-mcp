# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

from __future__ import annotations

class EditorLivenessMixin:
    """Is the editor alive, busy, in a modal dialog or recovering: what a bridge call checks before it queues and while it waits."""

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
