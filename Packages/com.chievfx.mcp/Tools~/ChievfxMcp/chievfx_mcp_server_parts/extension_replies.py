# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Reply fields the server derives for tools that cannot ship a formatter of their own (extensions).

from __future__ import annotations

# How long the ready events-wait call in `next` waits when the reply does not say (waitTimeoutMs).
DEFAULT_NEXT_WAIT_TIMEOUT_MS = 30000


def with_wait_next(raw_result: Any) -> Any:
    """The reply plus `next`: the ready events-wait call for the end it names (waitFor / completionMarker).

    Used when the caller did not pass awaitMs. Extensions used to spell the wait out in a text hint each, with their own
    cursor; the server builds it from the reply instead: the cursor is the extension's sinceEventId, or the one the bridge
    took before the tool ran (eventCursorBefore, folded into `next`). An extension sets waitTimeoutMs when its action
    lasts longer than the default (a drive across the map), and keeps its own `next` if it builds one.
    """
    if not isinstance(raw_result, dict) or "next" in raw_result:
        return raw_result
    wait_for = raw_result.get("waitFor")
    marker = raw_result.get("completionMarker")
    wait_arguments: dict[str, Any] = {}
    if (isinstance(wait_for, str) and wait_for) or (
        isinstance(wait_for, list) and wait_for and all(isinstance(item, str) for item in wait_for)
    ):
        wait_arguments["contains"] = wait_for
    elif isinstance(marker, str) and marker:
        wait_arguments["marker"] = marker
    else:
        return raw_result
    cursor = next(
        (
            raw_result[key]
            for key in ("sinceEventId", "eventCursorBefore")
            if isinstance(raw_result.get(key), int) and not isinstance(raw_result.get(key), bool)
        ),
        None,
    )
    if cursor is not None:
        wait_arguments["sinceEventId"] = cursor
    else:
        wait_arguments["includeRecentMs"] = 60000
    timeout_ms = raw_result.get("waitTimeoutMs")
    if isinstance(timeout_ms, bool) or not isinstance(timeout_ms, int) or timeout_ms <= 0:
        timeout_ms = DEFAULT_NEXT_WAIT_TIMEOUT_MS
    wait_arguments["timeoutMs"] = min(timeout_ms, HARD_EVENTS_WAIT_TIMEOUT_MS)
    shaped = {key: value for key, value in raw_result.items() if key not in ("eventCursorBefore", "waitTimeoutMs")}
    shaped["next"] = (
        "events-wait " + json.dumps(wait_arguments, ensure_ascii=False, separators=(",", ":"))
        + " (or pass awaitMs to this call to get the end in its reply)"
    )
    return shaped
