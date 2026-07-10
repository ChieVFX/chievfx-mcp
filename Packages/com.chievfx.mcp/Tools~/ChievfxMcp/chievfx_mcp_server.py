#!/usr/bin/env python3
"""Local-only MCP server for unity-mcp-chievfx.

Cursor talks MCP over stdio or HTTP. Unity-specific work is forwarded to the
ChievFX Unity bridge that runs inside the editor.
"""

from __future__ import annotations

from chievfx_mcp_server_parts import load_parts

load_parts(globals())

if __name__ == "__main__":
    raise SystemExit(main())
