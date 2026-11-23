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

    def discard_timed_out_request(self, request_id: str) -> bool:
        """After the MCP server gives up waiting, stop Unity from later draining
        the orphaned request and re-running a tool nobody is listening for.

        Writes a cancel marker so Unity skips the request if it has not started
        it yet, and deletes the still-queued request file directly. A request
        already moved to ``.processing`` is left for Unity to finish; the stale
        ``.processing`` file is reaped by prune_stale_transport_files.

        Returns True when the queued request was dropped before Unity took it."""
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
                return True
            except OSError:
                pass
        return False

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
                "queuedAtUtc": utc_now_iso_ms(),
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
            unreadable_response_reads = 0
            while True:
                if response_path.exists():
                    try:
                        payload = json.loads(read_text_file(response_path))
                    except (FileNotFoundError, json.JSONDecodeError):
                        # Windows: a scanner or indexer can hold the new file so it reads missing or short for a moment
                        # (WinError 2), though Unity wrote it whole. The action already ran; reading again beats failing.
                        unreadable_response_reads += 1
                        if unreadable_response_reads >= RESPONSE_READ_ATTEMPTS:
                            raise
                        time.sleep(file_io_retry_delay(unreadable_response_reads))
                        continue
                    response_path.unlink(missing_ok=True)
                    if not payload.get("ok"):
                        error_message = payload.get("error") or "Unity bridge returned an error."
                        final_state = "cancelled" if "cancel" in str(error_message).lower() else "failed"
                        self.update_operation_record(
                            request_id,
                            state=final_state,
                            completedAtUtc=utc_now_iso_ms(),
                            progressMessage=error_message,
                        )
                        final_message = f"{name} cancelled." if final_state == "cancelled" else f"{name} failed."
                        self.emit_progress(progress_token, notify, 1.0, final_message)
                        raise RuntimeError(error_message)

                    self.update_operation_record(
                        request_id,
                        state="completed",
                        completedAtUtc=utc_now_iso_ms(),
                        progressMessage=f"{name} completed.",
                    )
                    self.emit_progress(progress_token, notify, 1.0, f"{name} completed.")
                    return payload

                fallback_payload = self.try_read_tests_run_result_payload(name, arguments, timeout_ms, started_wall_time)
                if fallback_payload is not None:
                    self.update_operation_record(
                        request_id,
                        state="completed",
                        completedAtUtc=utc_now_iso_ms(),
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
                            request_id, state="failed", completedAtUtc=utc_now_iso_ms(), progressMessage=down
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

            # Unity may still be running a request it already took: a caller that repeats a changing call then does it twice.
            dropped = self.discard_timed_out_request(request_id)
            fate = (
                "Unity had not started it, so it was dropped and will not run."
                if dropped
                else f"Unity may still be running it: bridge-get-operation opId:{request_id} shows when it ends "
                "(the call's own result is not kept); do not repeat a call that changes the project or the game before that."
            )
            self.update_operation_record(
                request_id,
                state="stale",
                progressMessage="MCP server stopped waiting for the reply. " + fate,
            )
            status = self.get_bridge_status({})
            raise RuntimeError(
                f"Unity bridge timed out waiting for operation {request_id} ({name}) at {response_path}. {fate} "
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

        if name == "recompile" and self.read_playmode_state() is not True:
            # The request lands while Unity imports the very edits it is for: its Refresh, plus a slow
            # background editor taking the request seconds late, outlast the default reply wait. In Play
            # the short wait stays: leaving Play can swallow the reply, and recompile() recovers from that.
            return clamp_int(arguments.get("timeoutMs"), int(RECOMPILE_WAIT_SECONDS * 1000), 1000, 30 * 60 * 1000)

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
