# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

from __future__ import annotations

def force_utf8_stdio() -> None:
    """JSON-RPC is UTF-8, but Windows stdio defaults to cp1252 and mangles multibyte chars.

    Without this, an em dash (—, UTF-8 E2 80 94) arrives as mojibake (â€") and substring
    filters like contains silently fail to match. errors="replace" keeps the read loop alive
    on malformed bytes instead of crashing the server.
    """
    for stream in (sys.stdin, sys.stdout):
        reconfigure = getattr(stream, "reconfigure", None)
        if callable(reconfigure):
            try:
                reconfigure(encoding="utf-8", errors="replace")
            except (ValueError, OSError):
                pass


SELECTION_WATCH_POLL_SECONDS = 1.0

# Delay before the post-initialized list_changed nudge. Gives Cursor a moment to finish
# wiring up the connection after it sends notifications/initialized, so the list_changed
# (which makes it commit the just-handshaked instructions) is not dropped as too-early.
POST_INITIALIZE_LIST_CHANGED_DELAY_SECONDS = 0.75

LIST_CHANGED_METHOD_BY_KIND = {
    "tools": "notifications/tools/list_changed",
    "resources": "notifications/resources/list_changed",
    "prompts": "notifications/prompts/list_changed",
}


def selection_watch_targets() -> dict[str, tuple[str, ...]]:
    """Maps a watched selection/snapshot file to the capability kinds it affects.

    Paths are read fresh each call because configure_project_root() reassigns the
    module-level *_SELECTION_PATH globals after the watcher thread starts.
    """
    return {
        str(TOOL_SELECTION_PATH): ("tools",),
        str(RESOURCE_SELECTION_PATH): ("resources",),
        str(PROMPT_SELECTION_PATH): ("prompts",),
        # Category selection and live extension capabilities can shift all three lists.
        str(CATEGORY_SELECTION_PATH): ("tools", "resources", "prompts"),
        str(EXTENSION_CAPABILITY_MANIFEST_PATH): ("tools", "resources", "prompts"),
        # Arming or disarming remote-cmd adds or removes a tool, and the client caches tools/list.
        str(REMOTE_CMD_SETTINGS_PATH): ("tools",),
    }


def selection_file_signature(path_str: str) -> tuple[float, int] | None:
    try:
        stat_result = os.stat(path_str)
    except OSError:
        return None
    return (stat_result.st_mtime, stat_result.st_size)


def handle_selection_target_changed(path_str: str) -> None:
    try:
        changed_path = Path(path_str).expanduser().resolve()
        manifest_path = EXTENSION_CAPABILITY_MANIFEST_PATH.expanduser().resolve()
    except OSError:
        changed_path = Path(path_str).expanduser().absolute()
        manifest_path = EXTENSION_CAPABILITY_MANIFEST_PATH.expanduser().absolute()

    if changed_path == manifest_path:
        invalidate_extension_manifest_cache()


def watch_selection_files(send: Any, poll_seconds: float = SELECTION_WATCH_POLL_SECONDS) -> None:
    """Polls selection files and pushes list_changed notifications when they change.

    Cursor caches tools/resources/prompts lists and does not re-query on a plain reload,
    so a selection edit in Unity otherwise stays invisible until the server is removed and
    re-added. Emitting list_changed is the spec-compliant nudge for the client to re-fetch.
    """
    last_signatures = {path_str: selection_file_signature(path_str) for path_str in selection_watch_targets()}

    while True:
        time.sleep(poll_seconds)
        changed_kinds: set[str] = set()
        for path_str, kinds in selection_watch_targets().items():
            current = selection_file_signature(path_str)
            previous = last_signatures.get(path_str, "__unset__")
            last_signatures[path_str] = current
            if previous == "__unset__":
                continue
            if current != previous:
                handle_selection_target_changed(path_str)
                changed_kinds.update(kinds)

        for kind in ("tools", "resources", "prompts"):
            if kind in changed_kinds:
                send({"jsonrpc": "2.0", "method": LIST_CHANGED_METHOD_BY_KIND[kind]})

        if "tools" in changed_kinds:
            refresh_debug_artifacts_on_tools_list_changed()


def run_stdio(server: McpServer) -> None:
    force_utf8_stdio()
    output_lock = threading.Lock()
    workers: list[threading.Thread] = []

    def send(payload: Any) -> None:
        if payload is None:
            return

        with output_lock:
            sys.stdout.write(json.dumps(payload, separators=(",", ":")) + "\n")
            sys.stdout.flush()

    watcher = threading.Thread(target=watch_selection_files, args=(send,), daemon=True)
    watcher.start()

    def run_worker(message: Any) -> None:
        try:
            send(server.handle_message(message, send))
        except Exception as exc:  # noqa: BLE001 - stdio server must stay alive.
            print(traceback.format_exc(), file=sys.stderr)
            send(server.error_response(message.get("id") if isinstance(message, dict) else None, -32700, str(exc)))

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue

        try:
            message = json.loads(line)
            if isinstance(message, dict) and message.get("method") == "tools/call" and "id" in message:
                worker = threading.Thread(target=run_worker, args=(message,))
                worker.start()
                workers.append(worker)
                continue

            response = server.handle_message(message, send)
        except Exception as exc:  # noqa: BLE001 - stdio server must stay alive.
            print(traceback.format_exc(), file=sys.stderr)
            response = server.error_response(None, -32700, str(exc))

        send(response)

    for worker in workers:
        worker.join()


class ExclusiveThreadingHTTPServer(ThreadingHTTPServer):
    """An HTTP server that refuses to share its port.

    http.server sets SO_REUSEADDR, which on Windows lets a second process bind a port that is already listening:
    a restarted server and the stale one it was meant to replace both accepted connections, and requests went to
    either. SO_EXCLUSIVEADDRUSE makes that bind fail instead. Elsewhere SO_REUSEADDR only reuses a port in TIME_WAIT.
    """

    allow_reuse_address = os.name != "nt"

    def server_bind(self) -> None:
        exclusive = getattr(socket, "SO_EXCLUSIVEADDRUSE", None)
        if os.name == "nt" and exclusive is not None:
            self.socket.setsockopt(socket.SOL_SOCKET, exclusive, 1)
        super().server_bind()


# EADDRINUSE on macOS/Linux, WSAEADDRINUSE and WSAEACCES (an exclusive holder) on Windows.
PORT_IN_USE_ERRNOS = frozenset({48, 98, 10048, 10013})


def describe_port_holder(host: str, port: int) -> str:
    """Who answers on the port, when it is a ChievFX MCP server (its GET reply names it and its bridge)."""
    probe_host = "127.0.0.1" if host in ("0.0.0.0", "", "::") else host
    try:
        with urllib.request.urlopen(f"http://{probe_host}:{port}/", timeout=1.0) as response:
            payload = json.loads(response.read().decode("utf-8"))
    except Exception:  # noqa: BLE001 - only a hint for the error message
        return "another process"
    if isinstance(payload, dict) and payload.get("name") == SERVER_NAME:
        return f"another {SERVER_NAME} server (bridge {payload.get('bridgeDir')})"
    return "another process"


def bind_http_server(host: str, port: int, handler: type[BaseHTTPRequestHandler]) -> ThreadingHTTPServer:
    try:
        return ExclusiveThreadingHTTPServer((host, port), handler)
    except OSError as exc:
        if exc.errno not in PORT_IN_USE_ERRNOS and getattr(exc, "winerror", None) not in PORT_IN_USE_ERRNOS:
            raise
        raise PortInUseError(
            f"{SERVER_NAME}: port {port} on {host} is already in use by {describe_port_holder(host, port)}. "
            "A server left over from a restart keeps answering there with its old tools; stop it (or the process "
            "holding the port) and start again, or set another port in Window > ChievFX MCP."
        ) from exc


LOOPBACK_HOST_NAMES = frozenset({"localhost", "127.0.0.1", "::1", "[::1]"})


def http_requires_security_key() -> tuple[bool, str]:
    """When the shared key must be presented on every request, and what it must equal.

    Two independent reasons: the port is reachable from off the machine, or remote-cmd is armed and a
    request to this port can run commands. Loopback-only setups without remote-cmd keep working with
    no header, so enabling this feature is what tightens the endpoint rather than a silent break.
    """
    settings = load_remote_cmd_settings()
    try:
        bind_is_loopback = ipaddress.ip_address(ACTIVE_BIND_HOST).is_loopback
    except ValueError:
        bind_is_loopback = ACTIVE_BIND_HOST in LOOPBACK_HOST_NAMES
    if not bind_is_loopback or settings["enabled"]:
        return (True, settings["securityKey"])
    return (False, settings["securityKey"])


def http_presented_key(headers: Any) -> str:
    key = (headers.get("X-Chievfx-Key") or "").strip()
    if key:
        return key
    authorization = (headers.get("Authorization") or "").strip()
    if authorization.lower().startswith("bearer "):
        return authorization[7:].strip()
    return ""


def http_host_header_is_allowed(host_header: str) -> bool:
    """Reject a Host this server was not reached at, which is what DNS rebinding needs to work.

    A browser cannot be stopped from resolving an attacker-controlled name to 127.0.0.1 and POSTing
    here, but it always sends that name in Host. IP literals and localhost are accepted; any other
    hostname is not, because nothing legitimately reaches this port by name.
    """
    host = (host_header or "").strip()
    if not host:
        return False
    if host.startswith("["):
        closing = host.find("]")
        host = host[: closing + 1] if closing != -1 else host
    elif ":" in host:
        host = host.rsplit(":", 1)[0]
    host = host.strip().lower()
    if host in LOOPBACK_HOST_NAMES:
        return True
    try:
        ipaddress.ip_address(host.strip("[]"))
        return True
    except ValueError:
        return False


def make_http_handler(server: McpServer) -> type[BaseHTTPRequestHandler]:
    def log_stderr(text: str) -> None:
        try:
            print(text, file=sys.stderr)
        except OSError:
            pass

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, format: str, *args: Any) -> None:  # noqa: A002
            log_stderr(format % args)

        def do_GET(self) -> None:  # noqa: N802
            if self.reject_untrusted_request():
                return

            payload = {
                "name": SERVER_NAME,
                "version": SERVER_VERSION,
                "transport": "http",
                "bridgeDir": str(server.bridge_dir),
                "tools": [tool["name"] for tool in enabled_tools()],
            }
            self.send_json(payload)

        def reject_untrusted_request(self) -> bool:
            """Refuse anything that is not an MCP client on this port, before parsing a body.

            No MCP client sends Origin; a web page always does, and until this check existed any page
            the user had open could POST JSON-RPC to the loopback port and drive the editor.
            """
            if self.headers.get("Origin"):
                log_stderr(f"rejected request carrying Origin {self.headers.get('Origin')!r}")
                self.send_json({"error": "Cross-origin requests are not accepted."}, status=403)
                return True
            if not http_host_header_is_allowed(self.headers.get("Host", "")):
                log_stderr(f"rejected request with Host {self.headers.get('Host')!r}")
                self.send_json({"error": "Unexpected Host header."}, status=403)
                return True

            required, expected_key = http_requires_security_key()
            if not required:
                return False
            presented = http_presented_key(self.headers)
            if not expected_key or not presented or not hmac.compare_digest(presented, expected_key):
                log_stderr(f"rejected unauthenticated request from {self.client_address[0]}")
                self.send_json(
                    {"error": "Missing or invalid X-Chievfx-Key header."},
                    status=401,
                )
                return True
            return False

        def do_POST(self) -> None:  # noqa: N802
            if self.reject_untrusted_request():
                return

            length = int(self.headers.get("Content-Length", "0"))
            raw_body = self.rfile.read(length).decode("utf-8")
            try:
                message = json.loads(raw_body)
                # The peer and the key it presented are not part of the JSON-RPC message, and
                # remote-cmd re-checks both per call, so they are handed down per request thread.
                set_remote_cmd_request_context(self.client_address[0], http_presented_key(self.headers))
                try:
                    response = server.handle_message(message)
                finally:
                    clear_remote_cmd_request_context()
                self.send_json(response if response is not None else {})
            except Exception as exc:  # noqa: BLE001
                log_stderr(traceback.format_exc())
                self.send_json(server.error_response(None, -32700, str(exc)), status=400)

        def send_json(self, payload: Any, status: int = 200) -> None:
            body = json.dumps(payload, separators=(",", ":")).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

    return Handler
