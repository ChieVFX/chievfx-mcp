# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="unity-mcp-chievfx local MCP server")
    parser.add_argument("--transport", choices=["stdio", "http"], default="stdio")
    parser.add_argument("--port", type=int, default=27247)
    parser.add_argument("--unity-url", default="http://127.0.0.1:27248")
    parser.add_argument("--project-root", default=os.getcwd())
    parser.add_argument("--bridge-dir", default=str(Path(os.getcwd()) / "Library" / "ChievfxMcpBridge"))
    parser.add_argument("--timeout", type=int, default=10000)
    parser.add_argument("--tool-metadata", action="store_true", help="Print ChievFX MCP tool metadata and exit.")
    parser.add_argument("--resource-metadata", action="store_true", help="Print ChievFX MCP resource metadata and exit.")
    parser.add_argument("--prompt-metadata", action="store_true", help="Print ChievFX MCP prompt metadata and exit.")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    configure_project_root(args.project_root)
    if args.tool_metadata:
        sys.stdout.write(json.dumps(build_tool_metadata(), ensure_ascii=False, separators=(",", ":")) + "\n")
        return 0
    if args.resource_metadata:
        sys.stdout.write(json.dumps(build_resource_metadata(), ensure_ascii=False, separators=(",", ":")) + "\n")
        return 0
    if args.prompt_metadata:
        sys.stdout.write(json.dumps(build_prompt_metadata(), ensure_ascii=False, separators=(",", ":")) + "\n")
        return 0

    server = McpServer(args.unity_url, args.bridge_dir, args.timeout)

    if args.transport == "stdio":
        run_stdio(server)
        return 0

    httpd = ThreadingHTTPServer(("127.0.0.1", args.port), make_http_handler(server))
    print(f"{SERVER_NAME} listening at http://127.0.0.1:{args.port}", file=sys.stderr)
    httpd.serve_forever()
    return 0
