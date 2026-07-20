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
        if readiness["isCompiling"]:
            return "Unity editor is compiling or reloading assemblies.", BRIDGE_RECOVERY_WAIT_SECONDS
        if readiness["isUpdating"]:
            return "Unity asset database is updating.", BRIDGE_RECOVERY_WAIT_SECONDS
        if heartbeat_age is not None and not readiness["bridgeReachable"] and heartbeat_age <= BRIDGE_STALE_RECOVERY_WAIT_SECONDS:
            return "Unity heartbeat is stale; waiting for possible domain reload recovery.", BRIDGE_STALE_RECOVERY_WAIT_SECONDS
        return None, 0.0

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
        timeout_ms = clamp_int(arguments.get("timeoutMs"), int(RECOMPILE_WAIT_SECONDS * 1000), 1000, 30 * 60 * 1000)
        wait_seconds = timeout_ms / 1000
        status_before = self.get_bridge_status({})
        self.emit_progress(progress_token, notify, 0.05, "Waiting for Unity to become idle before recompile.")
        ready_before = self.wait_for_bridge_ready(max_wait_seconds=wait_seconds)
        bridge_result = self.call_unity_bridge("recompile", arguments, request_id, progress_token, notify)

        # Compilation usually flips busy on the next editor tick. Give Unity a
        # short chance to enter compile/import state before waiting for idle.
        time.sleep(min(RECOMPILE_START_GRACE_SECONDS, max(wait_seconds, 0.0)))
        self.emit_progress(progress_token, notify, 0.5, "Waiting for Unity compilation to finish.")
        ready_after = self.wait_for_bridge_ready(max_wait_seconds=wait_seconds)
        status_after = self.get_bridge_status({})
        result = bridge_result.get("result")
        if not isinstance(result, dict):
            result = {}

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
            result["warning"] = "Timed out waiting for Unity compile/import busy state to clear."

        # Surface compile errors/warnings produced by this recompile directly, so callers do not need a
        # separate console-get-logs round-trip. Read from the event stream (which survives the domain
        # reload a successful compile triggers) using the pre-recompile cursor.
        since_event_id = status_before.get("lastEventId") if isinstance(status_before, dict) else None
        if isinstance(since_event_id, int):
            result["compile"] = self.collect_recompile_issues(since_event_id)

        invalidate_extension_manifest_cache()
        self.emit_progress(progress_token, notify, 1.0, "recompile completed.")
        return result

    def collect_recompile_issues(self, since_event_id: int) -> dict[str, Any]:
        """Gather compile errors/warnings emitted after `since_event_id` from the event stream.

        Compiler messages are journaled as source=log events (with cursors), so this survives the
        domain reload a clean compile triggers, unlike the in-memory console buffer which is wiped."""
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
        seen: set[tuple[str, str]] = set()
        for event in log_events:
            level = str(event.get("level", "")).lower()
            message = event.get("message", "")
            key = (level, message)
            if key in seen:
                continue
            seen.add(key)
            row = {"eventId": event.get("eventId"), "message": message}
            if level in ("error", "exception", "assert"):
                errors.append(row)
            elif level == "warning":
                warnings.append(row)

        issues: dict[str, Any] = {"errorCount": len(errors), "warningCount": len(warnings)}
        if errors:
            issues["errors"] = errors[:RECOMPILE_MAX_ISSUES]
            if len(errors) > RECOMPILE_MAX_ISSUES:
                issues["errorsTruncated"] = True
        if warnings:
            issues["warnings"] = warnings[:RECOMPILE_MAX_ISSUES]
            if len(warnings) > RECOMPILE_MAX_ISSUES:
                issues["warningsTruncated"] = True
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
        with self.bridge_call_lock:
            allow_recovery_extension = self.wait_for_bridge_recovery_if_needed(progress_token, notify)
            return self._call_unity_bridge_file_locked(name, arguments, jsonrpc_request_id, progress_token, notify, allow_recovery_extension)

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
                "eventId": int(time.time() * 1000),
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
            )

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
        payload["eventId"] = int(time.time() * 1000)
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
