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
