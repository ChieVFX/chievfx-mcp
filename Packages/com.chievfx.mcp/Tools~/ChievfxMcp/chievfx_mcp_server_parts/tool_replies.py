# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

from __future__ import annotations

# Reply text shared by tool calls: tool-batch steps, failures a tool reported, the outcome of an awaitMs wait.

def format_tool_batch_text(result: dict[str, Any]) -> str:
    """One headline, then each step numbered from 1 with its own output indented under it.

    A step's first line is its tool's headline (an extension's summary), so the list reads like a log
    of what was done: "1 gameai-ui-tap: tapped Play", "2 waited 1000 ms", "3 gameai-read: ...".
    """
    total = result.get("totalCount", 0)
    headline = f"tool-batch{' ' + str(result['tool']) if result.get('tool') else ''}: {result.get('okCount', 0)} of {total} ok"
    if result.get("failedCount"):
        headline += f", {result['failedCount']} failed"
    if result.get("notRunCount"):
        headline += f", {result['notRunCount']} not run (stopped at the first failure; stopOnError:false runs the rest)"
    lines = [headline]
    for row in result.get("steps") or []:
        if not isinstance(row, dict):
            continue
        number = int(row.get("index", 0)) + 1
        if "waitMs" in row:
            lines.append(f"{number} waited {row['waitMs']} ms")
            continue
        if not row.get("ok"):
            lines.append(f"{number} {row.get('tool')} FAILED: {row.get('error')}")
            # A tool that replied with its failure: the reply itself says what went wrong.
            lines.extend("  " + line for line in str(row.get("text") or "").splitlines())
            continue
        text_lines = str(row.get("text") or "").splitlines() or ["(no output)"]
        lines.append(f"{number} {row.get('tool')}: {text_lines[0]}")
        lines.extend("  " + line for line in text_lines[1:])
    return "\n".join(lines)


# Runtime UI actions answer a miss with resolved:false (no target) or clicked:false (nothing took the click).
UI_RUNTIME_ACTION_TOOL_IDS = frozenset(
    {"ui-runtime-click", "ui-runtime-drag", "ui-runtime-focus", "ui-runtime-set-control-value", "ui-runtime-type-text"}
)


def describe_reported_failure(raw_result: Any, tool_name: str = "") -> str | None:
    """Why a tool's reply counts as a failed step, or None when it does not.

    Most tools throw on failure, but some answer with it: script-execute success:false (compile or runtime
    error), input and extension tools ok:false, runtime UI actions resolved:false / clicked:false. A batch that
    reads those as done runs its next steps on a state the failed step never produced.
    """
    if not isinstance(raw_result, dict):
        return None
    if raw_result.get("success") is False:
        flag = "success:false"
    elif raw_result.get("ok") is False:
        flag = "ok:false"
    elif tool_name in UI_RUNTIME_ACTION_TOOL_IDS and raw_result.get("resolved") is False:
        flag = "resolved:false"
    elif tool_name in UI_RUNTIME_ACTION_TOOL_IDS and raw_result.get("clicked") is False:
        flag = "clicked:false"
    else:
        return None
    stage = raw_result.get("stage")
    detail = f"replied {flag}" + (f" (stage:{stage})" if isinstance(stage, str) and stage else "")
    errors = raw_result.get("errors")
    reason = next((item for item in errors if isinstance(item, str) and item), None) if isinstance(errors, list) else None
    warnings = raw_result.get("warnings")
    if reason is None and isinstance(warnings, list):
        reason = next((item for item in warnings if isinstance(item, str) and item), None)
    for key in ("error", "message"):
        if reason is None and isinstance(raw_result.get(key), str) and raw_result.get(key):
            reason = raw_result[key]
    return f"{detail}: {compact_event_message(reason)}" if reason else detail


def without_wait_advice(raw_result: Any) -> Any:
    """The reply minus what it offers for waiting by hand: a hint naming awaitMs or events-wait, and its cursor.

    Used when the caller passed awaitMs: the server waits and puts the outcome first, with its own cursor
    when the end has not come yet, so advice to pass awaitMs next time and a second cursor are noise.
    """
    if not isinstance(raw_result, dict):
        return raw_result
    advice_keys = [
        key
        for key in ("hint", "wait")
        if isinstance(raw_result.get(key), str) and ("awaitMs" in raw_result[key] or "events-wait" in raw_result[key])
    ]
    # waitTimeoutMs only sizes the ready events-wait call in `next`, which an awaited reply does not get.
    advice_keys += [key for key in ("sinceEventId", "eventCursorBefore", "waitTimeoutMs") if key in raw_result]
    if not advice_keys:
        return raw_result
    return {key: value for key, value in raw_result.items() if key not in advice_keys}


def awaited_event_message(value: Any) -> str:
    """The awaited end in full: it is the action's result (reason, hint), unlike a row in an event list."""
    if not isinstance(value, str):
        return ""
    message = " ".join(value.split())
    if len(message) <= AWAITED_EVENT_MESSAGE_CHARS:
        return message
    return message[: AWAITED_EVENT_MESSAGE_CHARS - 3].rstrip() + "..."


def describe_awaited_failure(raw_result: Any, outcome: dict[str, Any]) -> str | None:
    """Why the awaited end counts as a failure, or None.

    The reply names its failed ends in waitForFailed (a log substring, or a list: any matches); an end event
    containing one means the action failed, though the reply that started it said ok.
    """
    failed_when = raw_result.get("waitForFailed") if isinstance(raw_result, dict) else None
    markers = [failed_when] if isinstance(failed_when, str) else failed_when if isinstance(failed_when, list) else []
    event = outcome.get("event") if outcome.get("matched") and isinstance(outcome.get("event"), dict) else None
    message = event.get("message") if event is not None else None
    if not isinstance(message, str):
        return None
    if not any(isinstance(marker, str) and marker and marker.lower() in message.lower() for marker in markers):
        return None
    return f"the awaited end is a failure: #{event.get('eventId')} {awaited_event_message(message)}"


def format_await_outcome_text(
    outcome: dict[str, Any], wait_arguments: dict[str, Any], expected_ms: Any = None
) -> str:
    """The line awaitMs appends to a tool's reply: how its end event came out.

    Not ended yet: the ready events-wait call, as `next` gives it without awaitMs, waiting for what is left of the
    time the tool expects its action to take (expected_ms, its waitTimeoutMs), at least the default.
    """
    target = wait_arguments.get("contains") or f"marker {wait_arguments.get('marker')}"
    target_text = " | ".join(target) if isinstance(target, list) else str(target)
    if outcome.get("matched"):
        event = outcome.get("event") if isinstance(outcome.get("event"), dict) else {}
        return f"awaited {outcome.get('elapsedMs')} ms: #{event.get('eventId')} {awaited_event_message(event.get('message'))}"
    if outcome.get("cancelled"):
        return f"await cancelled after {outcome.get('elapsedMs')} ms (waiting for {target_text})."
    filter_key = "contains" if wait_arguments.get("contains") else "marker"
    elapsed_ms = outcome.get("elapsedMs") if isinstance(outcome.get("elapsedMs"), int) else 0
    timeout_ms = DEFAULT_NEXT_WAIT_TIMEOUT_MS
    if isinstance(expected_ms, int) and not isinstance(expected_ms, bool):
        timeout_ms = max(timeout_ms, expected_ms - elapsed_ms)
    next_arguments = {
        filter_key: wait_arguments.get(filter_key),
        "sinceEventId": wait_arguments.get("sinceEventId"),
        "timeoutMs": min(timeout_ms, HARD_EVENTS_WAIT_TIMEOUT_MS),
    }
    return (
        f"awaited {outcome.get('elapsedMs')} ms, not ended yet (waiting for {target_text}). Keep waiting: "
        "events-wait " + json.dumps(next_arguments, ensure_ascii=False, separators=(",", ":"))
    )
