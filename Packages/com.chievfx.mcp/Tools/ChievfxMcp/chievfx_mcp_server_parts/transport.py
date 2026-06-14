# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

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
            payload = {
                "name": SERVER_NAME,
                "version": SERVER_VERSION,
                "transport": "http",
                "bridgeDir": str(server.bridge_dir),
                "tools": [tool["name"] for tool in enabled_tools()],
            }
            self.send_json(payload)

        def do_POST(self) -> None:  # noqa: N802
            length = int(self.headers.get("Content-Length", "0"))
            raw_body = self.rfile.read(length).decode("utf-8")
            try:
                message = json.loads(raw_body)
                response = server.handle_message(message)
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
