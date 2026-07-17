# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

from __future__ import annotations

class ResourceNotFoundError(ValueError):
    """Raised when an MCP resource URI is unknown, disabled, or no longer resolvable."""


class PromptNotFoundError(ValueError):
    """Raised when an MCP prompt is unknown or disabled."""


class PromptArgumentError(ValueError):
    """Raised when prompt/get arguments are malformed or incomplete."""


class ToolCallError(ValueError):
    """Raised when tools/call arguments are invalid or the target tool is unavailable."""
