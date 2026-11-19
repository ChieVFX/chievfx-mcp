# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

from __future__ import annotations

REMOTE_CMD_TOOL_ID = "remote-cmd"

# Matches the executable, not the argument string. Unity's CLI binary is "Unity" on every platform
# ("Unity.exe" on Windows, Unity.app/Contents/MacOS/Unity on macOS); adb is "adb"/"adb.exe".
DEFAULT_REMOTE_CMD_ALLOW_PATTERN = r"^(?:unity|unityhub|adb)$"

REMOTE_CMD_DEFAULT_TIMEOUT_MS = 120000
REMOTE_CMD_MAX_TIMEOUT_MS = 3600000
REMOTE_CMD_DEFAULT_MAX_OUTPUT_BYTES = 200000
REMOTE_CMD_MAX_OUTPUT_BYTES_CEILING = 4000000

# Programs whose entire job is to run code handed to them as an argument. While a filter is on, an
# allowlist that admits any of these admits everything (`bash -c "<anything>"`), so the filter is
# not consulted for them at all — they are refused before the pattern is applied.
REMOTE_CMD_INTERPRETER_DENY_LIST = frozenset(
    {
        "sh", "bash", "zsh", "dash", "ksh", "csh", "tcsh", "fish", "ash",
        "cmd", "command", "powershell", "pwsh", "wsl", "wscript", "cscript",
        "python", "python2", "python3", "py", "node", "ruby", "perl", "php", "lua",
        "osascript", "applescript", "env", "xargs", "eval", "nohup", "setsid",
        "sudo", "doas", "su", "runas", "start", "open", "ssh", "scp", "telnet",
    }
)

# Shell grammar that would chain, redirect, or substitute a second command. With shell=False these
# would reach the program as literal argv text instead of running, but an allowed program that
# forwards its arguments to another shell (`adb shell "..."`) makes that distinction thin, so a
# filtered call is refused outright and the operator is told to turn the filter off if they meant it.
REMOTE_CMD_SHELL_OPERATORS = (";", "|", "&", "`", "$(", ">", "<", "\n", "\r")

# Windows has no argv array: the OS hands each process one command-line string that the process
# re-parses however it likes. shlex's POSIX mode also eats backslashes, which are path separators
# there, so parsing is split by platform.
REMOTE_CMD_IS_WINDOWS = os.name == "nt"


class RemoteCmdUnavailableError(ToolCallError):
    """Raised when remote-cmd is called but not armed for this server instance."""


class RemoteCmdRejectedError(ToolCallError):
    """Raised when an armed remote-cmd refuses the specific command it was handed."""


# Per-request transport facts that tools/call itself does not carry: which peer sent the JSON-RPC
# message and whether it presented the shared key. Thread-local because the HTTP server handles each
# request on its own thread, and a tool must never read another request's credentials.
_REMOTE_CMD_REQUEST = threading.local()


def set_remote_cmd_request_context(peer_ip: str | None, key_presented: str | None) -> None:
    _REMOTE_CMD_REQUEST.peer_ip = peer_ip
    _REMOTE_CMD_REQUEST.key_presented = key_presented


def clear_remote_cmd_request_context() -> None:
    _REMOTE_CMD_REQUEST.peer_ip = None
    _REMOTE_CMD_REQUEST.key_presented = None


def remote_cmd_request_context() -> tuple[str | None, str | None]:
    return (getattr(_REMOTE_CMD_REQUEST, "peer_ip", None), getattr(_REMOTE_CMD_REQUEST, "key_presented", None))


def load_remote_cmd_settings() -> dict[str, Any]:
    """Read the Connection tab's mirror file, normalizing every field.

    A missing or unreadable file means the operator never armed the feature, so it reports disabled
    rather than falling back to defaults: the defaults would be a working configuration.
    """
    disabled = {
        "enabled": False,
        "allowPattern": DEFAULT_REMOTE_CMD_ALLOW_PATTERN,
        "unrestrictedAck": False,
        "securityKey": "",
        "allowedIps": [],
        "bindHost": "127.0.0.1",
        "timeoutMs": REMOTE_CMD_DEFAULT_TIMEOUT_MS,
        "maxOutputBytes": REMOTE_CMD_DEFAULT_MAX_OUTPUT_BYTES,
        "workingDirectory": "",
    }

    try:
        payload = json.loads(REMOTE_CMD_SETTINGS_PATH.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return disabled
    if not isinstance(payload, dict):
        return disabled

    allowed_ips = payload.get("allowedIps")
    settings = dict(disabled)
    settings["enabled"] = payload.get("enabled") is True
    settings["allowPattern"] = (payload.get("allowPattern") or "").strip() if isinstance(payload.get("allowPattern"), str) else ""
    settings["unrestrictedAck"] = payload.get("unrestrictedAck") is True
    settings["securityKey"] = payload.get("securityKey").strip() if isinstance(payload.get("securityKey"), str) else ""
    settings["allowedIps"] = [item.strip() for item in allowed_ips if isinstance(item, str) and item.strip()] if isinstance(allowed_ips, list) else []
    if isinstance(payload.get("bindHost"), str) and payload["bindHost"].strip():
        settings["bindHost"] = payload["bindHost"].strip()
    settings["timeoutMs"] = _clamp_int(payload.get("timeoutMs"), REMOTE_CMD_DEFAULT_TIMEOUT_MS, 1000, REMOTE_CMD_MAX_TIMEOUT_MS)
    settings["maxOutputBytes"] = _clamp_int(
        payload.get("maxOutputBytes"), REMOTE_CMD_DEFAULT_MAX_OUTPUT_BYTES, 1024, REMOTE_CMD_MAX_OUTPUT_BYTES_CEILING
    )
    if isinstance(payload.get("workingDirectory"), str):
        settings["workingDirectory"] = payload["workingDirectory"].strip()
    return settings


def _clamp_int(value: Any, fallback: int, minimum: int, maximum: int) -> int:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return fallback
    return max(minimum, min(maximum, int(value)))


def remote_cmd_gate() -> dict[str, Any]:
    """Every precondition for the tool to exist, with the reason it does not.

    Deliberately not expressed as a tool category or a role preset. Both of those are bulk-enable
    surfaces ("enable all", "expose every non-hidden tool"), and a tool that runs arbitrary
    commands must never be reachable by a bulk action — only by this one explicit switch.
    """
    settings = load_remote_cmd_settings()
    if not settings["enabled"]:
        return {
            "armed": False,
            "settings": settings,
            "reason": "remote-cmd is off. Enable it in Window > ChievFX > MCP (Connection tab, HTTP transport only).",
        }
    if ACTIVE_TRANSPORT != "http":
        return {
            "armed": False,
            "settings": settings,
            "reason": (
                "remote-cmd is HTTP-only and this server runs on stdio. A stdio client already runs on this "
                "machine and has its own shell, so the tool would add reach without adding capability."
            ),
        }
    if not settings["securityKey"]:
        return {
            "armed": False,
            "settings": settings,
            "reason": "remote-cmd needs a non-empty security key. Set one in Window > ChievFX > MCP (Connection tab).",
        }
    if not settings["allowPattern"] and not settings["unrestrictedAck"]:
        return {
            "armed": False,
            "settings": settings,
            "reason": (
                "remote-cmd has an empty program filter, which means unrestricted shell access. Confirm that "
                "in Window > ChievFX > MCP (Connection tab) or set a filter pattern."
            ),
        }
    return {"armed": True, "settings": settings, "reason": ""}


def remote_cmd_armed() -> bool:
    return remote_cmd_gate()["armed"] is True


def remote_cmd_ip_allowed(peer_ip: str | None, allowed_ips: list[str]) -> bool:
    """Empty allowlist means any peer, which the Unity window warns about in as many words."""
    if not allowed_ips:
        return True
    if not peer_ip:
        return False
    try:
        peer = ipaddress.ip_address(peer_ip)
    except ValueError:
        return False
    for entry in allowed_ips:
        try:
            if "/" in entry:
                if peer in ipaddress.ip_network(entry, strict=False):
                    return True
            elif peer == ipaddress.ip_address(entry):
                return True
        except ValueError:
            continue
    return False


def split_command_line(command: str) -> list[str]:
    """Parse one command line into argv the same way the target platform's launcher would."""
    if REMOTE_CMD_IS_WINDOWS:
        lexer = shlex.shlex(command, posix=False)
        lexer.whitespace_split = True
        lexer.commenters = ""
        tokens = list(lexer)
        return [token[1:-1] if len(token) >= 2 and token[0] == token[-1] and token[0] in "\"'" else token for token in tokens]
    return shlex.split(command, comments=False, posix=True)


def normalize_program_name(argv0: str) -> str:
    """The identity a filter pattern is matched against: basename, no extension, lowercased.

    Matching the raw argv[0] string would let `adb`, `./adb`, `/usr/local/bin/adb` and `ADB.exe`
    behave differently, and would pass `../../bin/sh` to a pattern that only looks for "adb".
    """
    name = os.path.basename(argv0.replace("\\", "/").rstrip("/"))
    stem, extension = os.path.splitext(name)
    if extension.lower() in (".exe", ".bat", ".cmd", ".com", ".ps1", ".sh"):
        name = stem
    return name.lower()


def resolve_program_path(argv0: str, cwd: Path) -> str | None:
    if os.path.sep in argv0 or (os.path.altsep and os.path.altsep in argv0):
        candidate = Path(argv0)
        if not candidate.is_absolute():
            candidate = cwd / candidate
        return str(candidate) if candidate.exists() else None
    return shutil.which(argv0)


def validate_filtered_command(command: str, allow_pattern: str, cwd: Path) -> list[str]:
    """Turn one single-line command into argv, or refuse and say which rule refused it."""
    if "\n" in command or "\r" in command:
        raise RemoteCmdRejectedError(
            "remote-cmd rejected: multi-line commands are not allowed while the program filter is on. "
            f'In this mode only single-line commands whose program matches "{allow_pattern}" are accepted. '
            "Turn the filter off in Window > ChievFX > MCP to allow multi-line shell input."
        )

    for operator in REMOTE_CMD_SHELL_OPERATORS:
        if operator in command:
            raise RemoteCmdRejectedError(
                f"remote-cmd rejected: shell operator '{operator.strip()}' is not allowed while the program filter is on, "
                "because it can chain a second command past the filter. Send one program invocation per call, or turn "
                "the filter off in Window > ChievFX > MCP."
            )

    try:
        argv = split_command_line(command)
    except ValueError as exc:
        raise RemoteCmdRejectedError(f"remote-cmd rejected: could not parse the command line ({exc}).") from exc
    if not argv:
        raise RemoteCmdRejectedError("remote-cmd rejected: the command is empty.")

    program = normalize_program_name(argv[0])
    if program in REMOTE_CMD_INTERPRETER_DENY_LIST:
        raise RemoteCmdRejectedError(
            f"remote-cmd rejected: '{program}' runs whatever code it is given, so allowing it would bypass the "
            'program filter entirely. Call the target program directly, or turn the filter off in '
            "Window > ChievFX > MCP to accept arbitrary shell input."
        )

    resolved = resolve_program_path(argv[0], cwd)
    # A pattern without a path separator is about the program's name, so it is matched against the
    # name alone. Matching it against the resolved path as well would let a directory carry the
    # match: /usr/local/adb-tools/anything would satisfy a pattern of "adb". A pattern that does
    # contain a separator is pinning a location on purpose, and gets the full path to match.
    pattern_is_path_anchored = "/" in allow_pattern or "\\" in allow_pattern
    candidates = [program]
    if pattern_is_path_anchored and resolved:
        candidates.append(resolved)
    try:
        pattern = re.compile(allow_pattern, re.IGNORECASE)
    except re.error as exc:
        raise RemoteCmdRejectedError(
            f'remote-cmd rejected: the configured program filter "{allow_pattern}" is not a valid regex ({exc}). '
            "Fix it in Window > ChievFX > MCP."
        ) from exc
    if not any(pattern.search(candidate) for candidate in candidates):
        raise RemoteCmdRejectedError(
            f"remote-cmd rejected: program '{program}' does not match the configured filter \"{allow_pattern}\". "
            "The filter matches the executable name or its resolved path, not the arguments."
        )

    if resolved is None:
        raise RemoteCmdRejectedError(
            f"remote-cmd rejected: program '{argv[0]}' was not found on PATH of the host machine."
        )
    argv[0] = resolved
    return argv


def remote_cmd_shell_argv(shell: str, command: str) -> tuple[list[str], str]:
    """The interpreter used when no filter is set and arbitrary (including multi-line) input is allowed."""
    requested = (shell or "default").strip().lower()
    if REMOTE_CMD_IS_WINDOWS:
        if requested in ("default", "powershell", "pwsh"):
            executable = shutil.which("pwsh") or shutil.which("powershell")
            if executable:
                return ([executable, "-NoProfile", "-NonInteractive", "-Command", command], os.path.basename(executable))
            if requested != "default":
                raise RemoteCmdRejectedError(f"remote-cmd rejected: shell '{requested}' is not installed on the host machine.")
        comspec = os.environ.get("COMSPEC") or "cmd.exe"
        return ([comspec, "/d", "/s", "/c", command], "cmd")

    if requested == "default":
        executable = os.environ.get("SHELL") or shutil.which("bash") or "/bin/sh"
    else:
        executable = shutil.which(requested)
        if not executable:
            raise RemoteCmdRejectedError(f"remote-cmd rejected: shell '{requested}' is not installed on the host machine.")
    return ([executable, "-c", command], os.path.basename(executable))


def resolve_remote_cmd_cwd(requested: str, settings: dict[str, Any]) -> Path:
    """Default to the Unity project root; anything outside it must be named explicitly by the caller."""
    configured = settings.get("workingDirectory") or ""
    base = Path(configured).expanduser() if configured else PROJECT_ROOT
    if not requested:
        candidate = base
    else:
        candidate = Path(requested).expanduser()
        if not candidate.is_absolute():
            candidate = base / candidate
    try:
        candidate = candidate.resolve()
    except OSError as exc:
        raise RemoteCmdRejectedError(f"remote-cmd rejected: working directory '{requested}' is not resolvable ({exc}).") from exc
    if not candidate.is_dir():
        raise RemoteCmdRejectedError(f"remote-cmd rejected: working directory '{candidate}' does not exist.")
    return candidate


def _truncate_output(text: str, max_bytes: int) -> tuple[str, bool]:
    encoded = text.encode("utf-8", errors="replace")
    if len(encoded) <= max_bytes:
        return (text, False)
    return (encoded[:max_bytes].decode("utf-8", errors="ignore"), True)


def audit_remote_cmd(entry: dict[str, Any]) -> None:
    """Append-only record of what ran, what was refused, and who asked.

    A shell tool that leaves no trace cannot be reviewed after the fact, so this is written before
    the result is formatted and never suppressed by a failure in the command itself.
    """
    line = json.dumps(dict(entry, at=datetime.now().astimezone().isoformat()), ensure_ascii=False, separators=(",", ":"))
    print(f"[remote-cmd] {line}", file=sys.stderr)
    try:
        path = REMOTE_CMD_AUDIT_PATH
        path.parent.mkdir(parents=True, exist_ok=True)
        # Keep one rotation so a long adb-heavy session cannot grow the log without bound.
        if path.exists() and path.stat().st_size > 1_000_000:
            path.replace(path.with_suffix(path.suffix + ".1"))
        with path.open("a", encoding="utf-8") as handle:
            handle.write(line + "\n")
    except OSError:
        pass


def run_remote_cmd(arguments: dict[str, Any]) -> dict[str, Any]:
    gate = remote_cmd_gate()
    settings = gate["settings"]
    peer_ip, key_presented = remote_cmd_request_context()

    if not gate["armed"]:
        raise RemoteCmdUnavailableError(gate["reason"])

    command = arguments.get("command")
    if not isinstance(command, str) or not command.strip():
        raise ToolCallError("remote-cmd requires a non-empty string argument 'command'.")
    command = command.strip()

    # Key and peer are checked per call, not only at connection time: the settings file can change
    # between requests, and a long-lived HTTP connection must not carry an old authorization.
    if not key_presented or not hmac.compare_digest(key_presented, settings["securityKey"]):
        audit_remote_cmd({"outcome": "rejected", "reason": "bad-key", "peerIp": peer_ip, "command": command})
        raise RemoteCmdUnavailableError(
            "remote-cmd rejected: missing or wrong security key. The MCP client must send it as the "
            "X-Chievfx-Key header (or Authorization: Bearer <key>); the value is in Window > ChievFX > MCP."
        )
    if not remote_cmd_ip_allowed(peer_ip, settings["allowedIps"]):
        audit_remote_cmd({"outcome": "rejected", "reason": "ip-not-allowed", "peerIp": peer_ip, "command": command})
        raise RemoteCmdUnavailableError(
            f"remote-cmd rejected: client address {peer_ip or 'unknown'} is not in the configured allowed-IP list."
        )

    cwd = resolve_remote_cmd_cwd(arguments.get("workingDirectory") or "", settings)
    # The configured values are ceilings, not just defaults: a caller may ask for a shorter timeout or
    # a smaller output cap, never a larger one. Otherwise the operator's limits would be advisory.
    timeout_ms = _clamp_int(arguments.get("timeoutMs"), settings["timeoutMs"], 1000, settings["timeoutMs"])
    max_output_bytes = _clamp_int(arguments.get("maxOutputBytes"), settings["maxOutputBytes"], 1024, settings["maxOutputBytes"])
    allow_pattern = settings["allowPattern"]

    try:
        if allow_pattern:
            argv = validate_filtered_command(command, allow_pattern, cwd)
            shell_mode = "none"
            use_shell = False
        else:
            argv, shell_mode = remote_cmd_shell_argv(arguments.get("shell") or "default", command)
            use_shell = True
    except RemoteCmdRejectedError as exc:
        audit_remote_cmd(
            {"outcome": "rejected", "reason": "filter", "peerIp": peer_ip, "command": command, "detail": str(exc)}
        )
        raise

    started = time.time()
    result = _spawn_remote_cmd(argv, cwd, timeout_ms)
    duration_ms = int((time.time() - started) * 1000)

    stdout, stdout_truncated = _truncate_output(result["stdout"], max_output_bytes)
    stderr, stderr_truncated = _truncate_output(result["stderr"], max_output_bytes)
    payload = {
        "command": command,
        "program": argv[0],
        "argv": argv,
        "shell": shell_mode,
        "filtered": bool(allow_pattern),
        "allowPattern": allow_pattern,
        "workingDirectory": str(cwd),
        "exitCode": result["exit_code"],
        "timedOut": result["timed_out"],
        "timeoutMs": timeout_ms,
        "durationMs": duration_ms,
        "stdout": stdout,
        "stderr": stderr,
        "stdoutTruncated": stdout_truncated,
        "stderrTruncated": stderr_truncated,
        "peerIp": peer_ip,
    }
    audit_remote_cmd(
        {
            "outcome": "ran",
            "peerIp": peer_ip,
            "command": command,
            "argv": argv,
            "shell": shell_mode,
            "cwd": str(cwd),
            "exitCode": result["exit_code"],
            "timedOut": result["timed_out"],
            "durationMs": duration_ms,
        }
    )
    return payload


def _spawn_remote_cmd(argv: list[str], cwd: Path, timeout_ms: int) -> dict[str, Any]:
    """Run argv with no inherited stdin, and kill the whole process tree on timeout.

    subprocess.run's own timeout only kills the direct child, which leaves an `adb logcat` or a
    Unity batchmode build orphaned and still holding the project lock. A new session/process group
    makes the timeout reach every descendant.
    """
    popen_kwargs: dict[str, Any] = {
        "cwd": str(cwd),
        "stdin": subprocess.DEVNULL,
        "stdout": subprocess.PIPE,
        "stderr": subprocess.PIPE,
        "text": True,
        "encoding": "utf-8",
        "errors": "replace",
    }
    if REMOTE_CMD_IS_WINDOWS:
        popen_kwargs["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
    else:
        popen_kwargs["start_new_session"] = True

    try:
        process = subprocess.Popen(argv, **popen_kwargs)
    except OSError as exc:
        raise RemoteCmdRejectedError(f"remote-cmd could not start '{argv[0]}': {exc}") from exc

    timed_out = False
    try:
        stdout, stderr = process.communicate(timeout=timeout_ms / 1000)
    except subprocess.TimeoutExpired:
        timed_out = True
        _kill_process_tree(process)
        stdout, stderr = process.communicate()

    return {
        "stdout": stdout or "",
        "stderr": stderr or "",
        "exit_code": process.returncode,
        "timed_out": timed_out,
    }


def _kill_process_tree(process: Any) -> None:
    try:
        if REMOTE_CMD_IS_WINDOWS:
            subprocess.run(
                ["taskkill", "/F", "/T", "/PID", str(process.pid)],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                check=False,
            )
        else:
            os.killpg(os.getpgid(process.pid), signal.SIGKILL)
    except (OSError, ProcessLookupError, PermissionError):
        pass
    try:
        process.kill()
    except (OSError, ProcessLookupError):
        pass


def format_remote_cmd_text(result: dict[str, Any]) -> str:
    header = f"exit {result['exitCode']}" if result["exitCode"] is not None else "exit unknown"
    if result["timedOut"]:
        header = f"TIMED OUT after {result['timeoutMs']} ms (killed)"
    lines = [f"{header} | {result['durationMs']} ms | cwd {result['workingDirectory']}", f"$ {result['command']}"]
    for label, key in (("stdout", "stdout"), ("stderr", "stderr")):
        body = result.get(key) or ""
        if not body.strip():
            continue
        suffix = " (truncated)" if result.get(f"{key}Truncated") else ""
        lines.append(f"--- {label}{suffix} ---")
        lines.append(body.rstrip())
    if not (result.get("stdout") or "").strip() and not (result.get("stderr") or "").strip():
        lines.append("(no output)")
    return "\n".join(lines)


def remote_cmd_instruction_banner() -> str:
    """Advertised high in initialize.instructions, but only while the tool is actually armed.

    The point of the banner is that the agent should reach for the host shell instead of giving up
    on anything Unity's own tools cannot do (adb, git, a batchmode build). Absent when disarmed, so
    a disabled feature costs no instruction budget.
    """
    gate = remote_cmd_gate()
    if not gate["armed"]:
        return ""

    settings = gate["settings"]
    if settings["allowPattern"]:
        scope = (
            f'Only programs matching "{settings["allowPattern"]}" (the executable, not its arguments), one '
            "invocation per call: no pipes, redirects, chaining, or multi-line input."
        )
    else:
        scope = "Any command, including multi-line shell input, through the host's default shell."
    return (
        f"HOST SHELL AVAILABLE: `{REMOTE_CMD_TOOL_ID}` runs commands on the machine hosting this MCP server "
        "(the one running Unity), not on your own. Use it for CLI work Unity's tools do not cover — adb, "
        f"Unity batchmode, build scripts. {scope}"
    )
