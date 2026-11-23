# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

from __future__ import annotations

# Compact text of events-check-since.

def format_events_check_since_text(result: dict[str, Any]) -> str:
    events = result.get("events")
    if not isinstance(events, list):
        events = []

    shown_events = [event for event in events if isinstance(event, dict)]
    omitted_count = max(0, len(events) - len(shown_events))
    header_parts = [
        f"matched:{format_toon_atom(bool(result.get('matched')))}",
        f"count:{result.get('count', len(events))}",
        f"shown:{len(shown_events)}",
    ]
    if omitted_count:
        header_parts.append(f"omitted:{omitted_count}")
    for key in ("hasMore", "sinceEventId", "sinceTimestampUtc", "lastEventId", "truncatedBeforeEventId"):
        value = result.get(key)
        if not should_omit_toon_value(value):
            header_parts.append(f"{key}:{format_toon_atom(value)}")

    lines = [" ".join(header_parts)]
    if shown_events:
        lines.append("events:")
        for event in shown_events:
            lines.append(format_event_row(event))
    next_since = result.get("nextSinceEventId")
    if result.get("hasMore") and isinstance(next_since, int):
        more_per_call = (
            f", or maxEntries up to {HARD_EVENTS_MAX_ENTRIES} for more per call"
            if len(events) < HARD_EVENTS_MAX_ENTRIES
            else ""
        )
        lines.append(f"More match: pass sinceEventId:{next_since} with the same contains/marker for the next ones{more_per_call}.")
    if omitted_count:
        lines.append(
            f"... {omitted_count} non-object events omitted. Use outputFormat:\"json\" for raw detail."
        )
    if any(
        isinstance(event.get("message"), str)
        and len(" ".join(event["message"].split())) > DEFAULT_EVENTS_TEXT_MESSAGE_CHARS
        for event in shown_events
    ):
        # console-get-logs keeps a shorter window than the journal: for a line a few minutes old it found nothing.
        lines.append(
            "Rows ending in ... are shortened: the whole line is in outputFormat:\"json\" of this call "
            "(console-get-logs contains:<part of it> has it only while the log is still in its shorter window)."
        )
    oldest = result.get("oldestEventUtc")
    if isinstance(oldest, str) and oldest:
        log_path = result.get("editorLogPath")
        if isinstance(log_path, str) and log_path and result.get("droppedBeforeEditorLog"):
            # An editor restart keeps the journal but starts a new log: the dropped lines are in the previous run's file.
            where = (f"they were logged before this editor session started, so its log {log_path} does not have them; "
                     "the previous session's log is Editor-prev.log until another editor starts.")
        elif isinstance(log_path, str) and log_path:
            where = f"their log lines are in {log_path} (this editor session's log)."
        elif log_path == "":
            # A newer editor took over Editor.log and a later start deleted the renamed copy: the other project's log is no lead.
            where = ("this editor's log file is gone from disk (editors started after it took over Editor.log), so they are not "
                     "kept anywhere; Editor.log now holds another editor's log.")
        else:
            where = "their log lines are in the Unity Editor.log (this editor session) and Editor-prev.log (the previous one)."
        lines.append(
            f"The journal keeps only its latest events (the oldest kept is from {oldest}): earlier ones are gone from it; {where}"
        )
    return "\n".join(lines)


def format_event_row(event: dict[str, Any]) -> str:
    event_id = event.get("eventId")
    timestamp = compact_event_timestamp(event.get("timestamp"))
    source = event.get("source")
    event_type = event.get("type")
    level = event.get("level")
    marker = event.get("marker")
    operation_id = event.get("operationId")
    message = compact_event_message(event.get("message"))

    parts: list[str] = []
    if isinstance(event_id, int):
        parts.append(f"#{event_id}")
    if timestamp:
        parts.append(timestamp)
    source_type = "/".join(str(value) for value in (source, event_type) if value)
    if source_type:
        parts.append(source_type)
    if level:
        parts.append(str(level))
    if marker:
        parts.append(f"marker={marker}")
    if operation_id:
        parts.append(f"op={operation_id}")
    if message:
        parts.append(message)

    return "- " + " ".join(parts) if parts else "-"


def compact_event_timestamp(value: Any) -> str:
    if not isinstance(value, str) or not value:
        return ""
    timestamp = value
    if "." in timestamp:
        head, tail = timestamp.split(".", 1)
        suffix = "Z" if tail.endswith("Z") else ""
        timestamp = head + suffix
    return timestamp


def compact_event_message(value: Any) -> str:
    if not isinstance(value, str):
        return ""
    message = " ".join(value.split())
    if len(message) <= DEFAULT_EVENTS_TEXT_MESSAGE_CHARS:
        return message
    return message[: DEFAULT_EVENTS_TEXT_MESSAGE_CHARS - 3].rstrip() + "..."
