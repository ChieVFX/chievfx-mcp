# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

from __future__ import annotations

class McpServer(McpServerCore, BridgeTransportMixin, EventsStatusMixin):
    pass
