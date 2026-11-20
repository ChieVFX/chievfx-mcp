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

    text = value.strip()
    if text[-1:] in ("Z", "z"):
        text = text[:-1] + "+00:00"
    # datetime.fromisoformat before Python 3.11 only accepts 3- or 6-digit fractional seconds,
    # but C#'s round-trip ("o") timestamps emit 7 (e.g. "...:23.7105270Z"). Normalize the fraction
    # to exactly 6 digits so every runtime parses it — otherwise every event timestamp fails to
    # parse and min_timestamp filtering silently drops all events (breaking events-check-since and
    # events-wait includeRecentMs). See _normalize_iso_fraction.
    text = _normalize_iso_fraction(text)
    try:
        return datetime.fromisoformat(text).timestamp()
    except ValueError:
        return None


def _normalize_iso_fraction(text: str) -> str:
    dot = text.find(".")
    if dot == -1:
        return text
    end = dot + 1
    while end < len(text) and text[end].isdigit():
        end += 1
    fraction = text[dot + 1:end]
    if len(fraction) == 6:
        return text
    normalized = (fraction + "000000")[:6]
    return text[:dot + 1] + normalized + text[end:]


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


def process_is_alive(pid: Any) -> bool | None:
    """True/False when the OS can say whether this process exists, None when it cannot be told."""
    if isinstance(pid, bool) or not isinstance(pid, int) or pid <= 0:
        return None
    if os.name == "nt":
        try:
            import ctypes
            from ctypes import wintypes

            process_query_limited_information = 0x1000
            still_active = 259
            error_access_denied = 5
            kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
            handle = kernel32.OpenProcess(process_query_limited_information, False, pid)
            if not handle:
                # Access denied means the process exists but belongs to someone we may not query.
                return ctypes.get_last_error() == error_access_denied
            try:
                exit_code = wintypes.DWORD()
                if not kernel32.GetExitCodeProcess(handle, ctypes.byref(exit_code)):
                    return None
                return exit_code.value == still_active
            finally:
                kernel32.CloseHandle(handle)
        except Exception:  # noqa: BLE001 - a probe, never a reason to fail the call
            return None
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    except OSError:
        return None
    return True


def process_cpu_seconds(pid: Any) -> float | None:
    """CPU time the process has used so far (user + system, seconds), or None when the OS cannot tell."""
    if isinstance(pid, bool) or not isinstance(pid, int) or pid <= 0:
        return None
    try:
        if os.name == "nt":
            import ctypes
            from ctypes import wintypes

            process_query_limited_information = 0x1000
            kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
            handle = kernel32.OpenProcess(process_query_limited_information, False, pid)
            if not handle:
                return None
            try:
                creation, exited, kernel, user = (wintypes.FILETIME() for _ in range(4))
                if not kernel32.GetProcessTimes(handle, ctypes.byref(creation), ctypes.byref(exited), ctypes.byref(kernel), ctypes.byref(user)):
                    return None
                ticks = sum((time_value.dwHighDateTime << 32) | time_value.dwLowDateTime for time_value in (kernel, user))
                return ticks / 10_000_000
            finally:
                kernel32.CloseHandle(handle)
        stat_path = Path(f"/proc/{pid}/stat")
        if stat_path.exists():
            fields = stat_path.read_text(encoding="utf-8").rsplit(")", 1)[1].split()
            return (int(fields[11]) + int(fields[12])) / os.sysconf("SC_CLK_TCK")
        output = subprocess.run(["ps", "-o", "time=", "-p", str(pid)], capture_output=True, text=True, timeout=2).stdout.strip()
        if not output:
            return None
        # [[dd-]hh:]mm:ss[.ss]; macOS prints minutes past 60 (74:36.26).
        days, _, clock = output.rpartition("-")
        seconds = 0.0
        for part in clock.split(":"):
            seconds = seconds * 60 + float(part)
        return seconds + (int(days) * 86400 if days else 0)
    except Exception:  # noqa: BLE001 - a probe, never a reason to fail the call
        return None


def read_script_source_file(arguments: dict[str, Any]) -> dict[str, Any]:
    """script-execute csharpPath: the file's text becomes csharpCode, so quotes, backslashes and Cyrillic need no escaping."""
    raw_path = arguments.get("csharpPath")
    if not isinstance(raw_path, str) or not raw_path.strip():
        raise ToolCallError("csharpPath must be a non-empty path to a .cs file.")
    if isinstance(arguments.get("csharpCode"), str) and arguments["csharpCode"].strip():
        raise ToolCallError("Pass csharpCode or csharpPath, not both.")
    path = Path(raw_path.strip()).expanduser()
    if not path.is_absolute():
        path = PROJECT_ROOT / path
    try:
        code = path.read_text(encoding="utf-8-sig")
    except FileNotFoundError:
        raise ToolCallError(f"csharpPath: no file at {path}.") from None
    except (OSError, UnicodeDecodeError) as exc:
        raise ToolCallError(f"csharpPath: cannot read {path} as UTF-8 text: {exc}") from None
    rest = {key: value for key, value in arguments.items() if key not in ("csharpPath", "csharpCode")}
    return {**rest, "csharpCode": code}


def parse_script_parameters(arguments: dict[str, Any]) -> dict[str, Any]:
    """script-execute parameters that arrived as JSON text become a list.

    A client that does not know the property's type sends the array as a string, and the editor read it as no
    parameters at all ("no overload with 0 supplied parameters"). A bare value list is wrapped as [{value}].
    """
    raw = arguments.get("parameters")
    if not isinstance(raw, str):
        return arguments
    try:
        parsed = json.loads(raw)
    except ValueError:
        raise ToolCallError('parameters must be a JSON array, e.g. [{"value": "text"}, {"value": 2}].') from None
    if not isinstance(parsed, list):
        raise ToolCallError('parameters must be a JSON array, e.g. [{"value": "text"}, {"value": 2}].')
    return {**arguments, "parameters": parsed}


def client_session_key() -> str | None:
    """The MCP client's own session id when it passes one to this server process, else None."""
    for name in CLIENT_SESSION_ENV_VARS:
        value = os.environ.get(name, "").strip()
        if value:
            return value[:128]
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


# A compiler diagnostic always names its severity next to a diagnostic id ("error CS0246",
# "warning CS0649") or carries the file(line,col) prefix Unity's compiler callback writes. Ordinary
# console logs match neither, which is what keeps them out of the recompile compile summary: stopping
# Play Mode to compile lands a project's own shutdown errors inside the same event window, and
# reporting one of those as "1 error" claimed a failure for a compile that actually succeeded.
COMPILER_DIAGNOSTIC_RE = re.compile(
    # "error CS0246", "warning CS0649", "error BC1051" (Burst) — severity next to a diagnostic id.
    r"\b(?:error|warning)\s+[A-Za-z]{1,10}\d{3,5}\b"
    # "Assets/Foo.cs(12,34): error ..." — the file(line,col) prefix, for diagnostics without an id.
    r"|\(\d+,\d+\):\s*(?:error|warning)\b"
    # Shaders compile through a pipeline of their own: no CompilerMessage callback, no diagnostic id,
    # just "Shader error in 'Foo': ... at line 23 (on metal)". recompile refreshes assets, so an edited
    # shader is reimported inside its window and its diagnostics belong in the same summary.
    r"|\bshader\s+(?:error|warning)\s+in\b",
    re.IGNORECASE,
)

# Unity marks up console text: style tags plus the <a href=... line=N> link it wraps around file
# paths. Useful in the Console window, pure noise in a tool response. Zero-width characters ride
# along in messages from loggers that use them as separators and survive every other cleanup.
CONSOLE_RICH_TEXT_TAG_RE = re.compile(r"</?(?:b|i|u|a|align|color|size)(?:[=\s][^>]*)?>", re.IGNORECASE)
CONSOLE_ZERO_WIDTH_RE = re.compile("[\u200b-\u200f\u2060\ufeff]")

# Unity appends the same compilation-context block to every shader diagnostic ("Compiling Subshader:
# ... / Platform defines: <every define> / Disabled keywords: <every keyword>"). It is identical for
# every error, says nothing about the error, and runs to ~800 characters — so it is cut rather than
# allowed to spend the whole per-message budget the actual diagnostic line needs.
SHADER_DIAGNOSTIC_CONTEXT_RE = re.compile(r"\n\s*Compiling Subshader:.*", re.DOTALL)


def is_compiler_diagnostic(event: dict[str, Any]) -> bool:
    """True for events the C# compiler callback journaled (type=compiler-message), and — so this also
    works against a project still running an older bridge build — for anything whose text is shaped
    like a compiler diagnostic."""
    if str(event.get("type", "")) == "compiler-message":
        return True
    return bool(COMPILER_DIAGNOSTIC_RE.search(str(event.get("message", ""))))


def clean_console_message(message: Any, max_chars: int) -> str:
    """Strip Unity's console markup and cap one message, so a single pathological entry cannot crowd
    out the rest of the summary."""
    if not isinstance(message, str):
        return ""
    text = CONSOLE_ZERO_WIDTH_RE.sub("", CONSOLE_RICH_TEXT_TAG_RE.sub("", message))
    text = SHADER_DIAGNOSTIC_CONTEXT_RE.sub("", text).strip()
    if len(text) <= max_chars:
        return text
    return text[: max(0, max_chars - 3)].rstrip() + "..."
