# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

from __future__ import annotations

def is_resource_not_found_error(message: str) -> bool:
    lowered = message.lower()
    markers = [
        "not found",
        "no gameobject",
        "no component",
        "no opened scene",
        "resource uri",
        "unsupported chievfx resource",
        "ambiguous",
    ]
    return any(marker in lowered for marker in markers)


def utc_now_iso() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def parse_utc_iso(value: Any) -> float | None:
    if not isinstance(value, str) or not value:
        return None

    try:
        return datetime.fromisoformat(value.replace("Z", "+00:00")).timestamp()
    except ValueError:
        return None


def is_transient_file_lock_error(exc: BaseException) -> bool:
    if isinstance(exc, PermissionError):
        return True
    if isinstance(exc, OSError):
        winerror = getattr(exc, "winerror", None)
        if winerror in (5, 32):
            return True
        if exc.errno in (13, 16):
            return True
    return False


def file_io_retry_delay(attempt: int) -> float:
    return min(FILE_IO_RETRY_MAX_SECONDS, FILE_IO_RETRY_BASE_SECONDS * (2 ** attempt))


def read_text_file(path: Path, encoding: str = "utf-8") -> str:
    last_error: OSError | None = None
    for attempt in range(FILE_IO_RETRY_ATTEMPTS):
        try:
            return path.read_text(encoding=encoding)
        except OSError as exc:
            if not is_transient_file_lock_error(exc) or attempt >= FILE_IO_RETRY_ATTEMPTS - 1:
                raise
            last_error = exc
            time.sleep(file_io_retry_delay(attempt))
    if last_error is not None:
        raise last_error
    raise OSError(f"Could not read file: {path}")


def read_json_file(path: Path) -> dict[str, Any] | None:
    try:
        payload = json.loads(read_text_file(path))
    except (OSError, json.JSONDecodeError):
        return None

    return payload if isinstance(payload, dict) else None


def write_json_file_atomic(path: Path, payload: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temp_path = path.with_name(f".{path.name}.{uuid.uuid4().hex}.tmp")
    serialized = json.dumps(payload, separators=(",", ":"))
    for attempt in range(FILE_IO_RETRY_ATTEMPTS):
        try:
            if not temp_path.exists():
                temp_path.write_text(serialized, encoding="utf-8")
            temp_path.replace(path)
            return
        except OSError as exc:
            if not is_transient_file_lock_error(exc) or attempt >= FILE_IO_RETRY_ATTEMPTS - 1:
                try:
                    temp_path.unlink(missing_ok=True)
                except OSError:
                    pass
                raise
            time.sleep(file_io_retry_delay(attempt))


def file_age_seconds(path: Path, now: float | None = None) -> float | None:
    try:
        return max(0.0, (now or time.time()) - path.stat().st_mtime)
    except OSError:
        return None


def clamp_int(value: Any, default: int, minimum: int, maximum: int) -> int:
    if not isinstance(value, int):
        return default
    return max(minimum, min(maximum, value))


def parse_bool(value: Any, default: bool) -> bool:
    """Read a boolean argument, tolerating string-encoded forms ("true"/"false") from MCP clients
    that stringify arguments. Falls back to default for anything unrecognized."""
    if isinstance(value, bool):
        return value
    if isinstance(value, str):
        lowered = value.strip().lower()
        if lowered in ("true", "1", "yes", "on"):
            return True
        if lowered in ("false", "0", "no", "off"):
            return False
    return default


def unity_test_result_paths() -> list[Path]:
    company = read_unity_project_setting("companyName") or "DefaultCompany"
    product = read_unity_project_setting("productName") or PROJECT_ROOT.name
    return [
        Path.home() / "Library" / "Application Support" / company / product / "TestResults.xml",
        PROJECT_ROOT / "TestResults.xml",
    ]


def read_unity_project_setting(name: str) -> str | None:
    path = PROJECT_ROOT / "ProjectSettings" / "ProjectSettings.asset"
    try:
        for line in path.read_text(encoding="utf-8").splitlines():
            stripped = line.strip()
            prefix = f"{name}:"
            if stripped.startswith(prefix):
                value = stripped[len(prefix) :].strip()
                return value.strip('"') or None
    except OSError:
        return None

    return None


def parse_unity_test_results_xml(
    path: Path,
    arguments: dict[str, Any],
    timeout_ms: int,
    started_wall_time: float,
) -> dict[str, Any]:
    root = ET.parse(path).getroot()
    cases = list(root.iter("test-case"))
    failed = int_attr(root, "failed", sum(1 for case in cases if normalize_test_status(case.get("result")) == "Failed"))
    passed = int_attr(root, "passed", sum(1 for case in cases if normalize_test_status(case.get("result")) == "Passed"))
    skipped = int_attr(root, "skipped", sum(1 for case in cases if normalize_test_status(case.get("result")) == "Skipped"))
    total = int_attr(root, "total", len(cases))
    include_passing = bool(arguments.get("includePassingTests"))
    include_messages = bool(arguments.get("includeMessages", True))
    include_stack_trace = bool(arguments.get("includeStacktrace", arguments.get("includeStackTrace", False)))
    max_results = clamp_int(arguments.get("maxResults"), 200, 1, 1000)
    selected_cases = [case for case in cases if include_passing or normalize_test_status(case.get("result")) != "Passed"]
    truncated = len(selected_cases) > max_results

    return {
        "summary": {
            "status": normalize_test_status(root.get("result")) if total > 0 else "Unknown",
            "totalTests": total,
            "passedTests": passed,
            "failedTests": failed,
            "skippedTests": skipped,
            "duration": format_seconds(float_attr(root, "duration")),
            "noTests": total == 0,
        },
        "results": [
            unity_test_case_result(case, include_messages, include_stack_trace)
            for case in selected_cases[:max_results]
        ],
        "resultsTruncated": truncated,
        "logs": [],
        "durationMs": int(max(0.0, time.time() - started_wall_time) * 1000),
        "timeoutMs": timeout_ms,
        "source": "TestResults.xml",
    }


def unity_test_case_result(case: ET.Element, include_message: bool, include_stack_trace: bool) -> dict[str, Any]:
    result: dict[str, Any] = {
        "name": case.get("fullname") or case.get("name") or "",
        "status": normalize_test_status(case.get("result")),
        "duration": format_seconds(float_attr(case, "duration")),
    }
    if include_message:
        message = case.findtext("./failure/message") or case.findtext("./reason/message") or ""
        if message:
            result["message"] = message[:2000]
            if len(message) > 2000:
                result["messageTruncated"] = True
    if include_stack_trace:
        stack_trace = case.findtext("./failure/stack-trace") or ""
        if stack_trace:
            result["stackTrace"] = stack_trace[:6000]
            if len(stack_trace) > 6000:
                result["stackTraceTruncated"] = True
    return result


def normalize_test_status(status: str | None) -> str:
    value = status or ""
    if "Passed" in value:
        return "Passed"
    if "Skipped" in value or "Inconclusive" in value:
        return "Skipped"
    if "Failed" in value:
        return "Failed"
    return "Unknown"


def int_attr(element: ET.Element, name: str, default: int) -> int:
    try:
        return int(element.get(name) or default)
    except (TypeError, ValueError):
        return default


def float_attr(element: ET.Element, name: str) -> float:
    try:
        return float(element.get(name) or 0.0)
    except (TypeError, ValueError):
        return 0.0


def format_seconds(seconds: float) -> str:
    return f"{seconds:.3f}".rstrip("0").rstrip(".") + "s"
