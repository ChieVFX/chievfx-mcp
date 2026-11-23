# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

from __future__ import annotations

class BridgeStatusMixin:
    """bridge-get-status and bridge-get-operation: heartbeat, operation records, busy reasons and hints."""

    def get_bridge_status(self, arguments: dict[str, Any]) -> dict[str, Any]:
        verbose = parse_bool(arguments.get("verbose"), False)
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

    @staticmethod
    def _modified_or_zero(path: Path) -> float:
        # Unity rewrites operation records with File.Replace and prunes old ones: on Windows a record listed a moment
        # ago can be missing when stat() runs, and the WinError 2 failed whatever call was reading the bridge status.
        try:
            return path.stat().st_mtime
        except OSError:
            return 0.0

    def read_operation_rows(self, max_operations: int, now: float, verbose: bool = False) -> list[dict[str, Any]]:
        if not self.operation_dir.exists():
            return []

        rows: list[dict[str, Any]] = []
        for path in sorted(self.operation_dir.glob("*.json"), key=self._modified_or_zero, reverse=True):
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
