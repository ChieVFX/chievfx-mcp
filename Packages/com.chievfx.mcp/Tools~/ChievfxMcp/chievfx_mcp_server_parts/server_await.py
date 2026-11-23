# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

from __future__ import annotations

class AwaitCompletionMixin:
    """awaitMs: the server waits for the end an action names (waitFor / completionMarker) and puts it first in the reply."""

    @contextlib.contextmanager
    def _await_progress_ticker(self, progress_token: Any, notify: Any | None, name: str, await_ms: int):
        """Progress notifications while the server waits for an action's end under awaitMs.

        Clients that cut a call at a fixed time (60 s in some) keep it alive while progress arrives; without a
        progressToken from the client nothing is sent.
        """
        if progress_token is None or notify is None:
            yield
            return
        stop = threading.Event()
        started = time.monotonic()

        def tick() -> None:
            while not stop.wait(AWAIT_PROGRESS_TICK_SECONDS):
                elapsed_s = time.monotonic() - started
                fraction = min(0.95, elapsed_s * 1000 / max(await_ms, 1))
                self.emit_progress(progress_token, notify, fraction, f"Waiting for {name} to end: {round(elapsed_s)} s of {round(await_ms / 1000)} s.")

        ticker = threading.Thread(target=tick, name="await-progress", daemon=True)
        ticker.start()
        try:
            yield
        finally:
            stop.set()
            ticker.join(timeout=1)

    def _remember_async_action(self, raw_result: Any, cursor_before: int | None) -> None:
        """Keeps where the last action that ends later (waitFor / completionMarker) started in the event stream.

        events-wait uses it to tell the end of that action, logged before the wait was armed, from an older
        event with the same text: only matches after this cursor count as firedBeforeWait.
        """
        if not isinstance(raw_result, dict) or not (raw_result.get("waitFor") or raw_result.get("completionMarker")):
            return
        cursor = cursor_before
        for key in ("eventCursorBefore", "sinceEventId"):
            value = raw_result.get(key)
            if cursor is None and isinstance(value, int) and not isinstance(value, bool):
                cursor = value
        self.last_async_action_cursor = cursor
        self.last_async_action_at = time.time()

    def _take_await_argument(self, params: dict[str, Any]) -> tuple[int | None, dict[str, Any]]:
        """Pops awaitMs off the call so neither the bridge nor an extension ever sees it."""
        arguments = params.get("arguments")
        if not isinstance(arguments, dict) or "awaitMs" not in arguments:
            return None, params
        if params.get("name") in AWAIT_IGNORING_TOOL_IDS:
            raise ToolCallError(
                f"awaitMs does not apply to {params.get('name')}: pass it to the action tool "
                "(inside a tool-batch step's args), or use events-wait timeoutMs."
            )
        value = arguments.get("awaitMs")
        # Clients send an argument their copy of the schema lacks as a string ("3000").
        if isinstance(value, str) and value.strip().isdigit():
            value = int(value.strip())
        if isinstance(value, bool) or not isinstance(value, int) or value < 0:
            raise ToolCallError("awaitMs must be a non-negative integer (milliseconds).")
        stripped = {key: item for key, item in arguments.items() if key != "awaitMs"}
        return min(value, HARD_EVENTS_WAIT_TIMEOUT_MS), {**params, "arguments": stripped}

    def _await_tool_completion(
        self,
        result: dict[str, Any],
        name: str,
        raw_result: Any,
        await_ms: int,
        cursor: int,
        request_id: Any,
    ) -> dict[str, Any]:
        """Waits for the end event a tool named in its reply (waitFor, or completionMarker); its outcome becomes the first line."""
        wait_for = raw_result.get("waitFor") if isinstance(raw_result, dict) else None
        marker = raw_result.get("completionMarker") if isinstance(raw_result, dict) else None
        has_wait_for = (isinstance(wait_for, str) and bool(wait_for)) or (
            isinstance(wait_for, list) and bool(wait_for) and all(isinstance(item, str) for item in wait_for)
        )
        if not has_wait_for and not (isinstance(marker, str) and marker):
            attach_result_notice(
                result,
                f"! awaitMs ignored: {name} returned nothing to wait for (no waitFor or completionMarker in its reply).",
            )
            return result

        wait_arguments: dict[str, Any] = {"sinceEventId": cursor, "timeoutMs": await_ms}
        if has_wait_for:
            wait_arguments["contains"] = wait_for
        else:
            wait_arguments["marker"] = marker
        outcome = self.events_wait(wait_arguments, request_id)
        timed_out = not outcome.get("matched") and not outcome.get("cancelled")
        # A tool whose end may simply never come (a tap that opens no window) says what silence means.
        timeout_means = raw_result.get("waitTimeoutMeans") if isinstance(raw_result, dict) else None
        if not (isinstance(timeout_means, str) and timeout_means):
            timeout_means = None
        headline = (
            f"awaited {outcome.get('elapsedMs')} ms: no end event. {timeout_means}"
            if timed_out and timeout_means
            else format_await_outcome_text(
                outcome, wait_arguments, raw_result.get("waitTimeoutMs") if isinstance(raw_result, dict) else None
            )
        )
        # The reply below the outcome is the state when the action started ("GoTo#3 running"): say so.
        prepend_result_headline(result, headline, "at start: ")
        failure = describe_awaited_failure(raw_result, outcome)
        if failure is None and timed_out:
            # A batch step's next steps rely on its end (walk up, then hit): one still running would be cut short
            # by the next action, so a step whose end did not come within awaitMs stops the batch too.
            failure = (
                f"no end event within {await_ms} ms: {timeout_means}"
                if timeout_means
                else f"the awaited end did not come within {await_ms} ms (the action may still be running)"
            )
        if failure is not None and isinstance(raw_result, dict):
            # The reply was written when the action started (ok:true); a tool-batch step judges the action by its end.
            raw_result["ok"] = False
            raw_result["error"] = failure
        elif outcome.get("matched") and isinstance(raw_result, dict):
            # The other way round too: script-execute that outran its timeoutMs replied success:false stage:timeout,
            # and its awaited "done" end is what the step did.
            raw_result["awaitedEndOk"] = True
        return result
