"""Loader for the split ChievFX MCP server implementation."""

from __future__ import annotations

from pathlib import Path
from typing import Any

_PART_FILES = [
    'common.py',
    'catalog_records.py',
    'initialize_instructions.py',
    'tool_descriptors.py',
    'static_catalog.py',
    'extension_capabilities.py',
    'exceptions.py',
    'tool_metadata.py',
    'resource_metadata.py',
    'tool_selection.py',
    'prompt_metadata.py',
    'resource_resolution.py',
    'resource_text.py',
    'category_resources.py',
    'debug_instructions.py',
    'bridge_utils.py',
    'server_core.py',
    'server_bridge.py',
    'server_events.py',
    'server.py',
    'transport.py',
    'formatters_ugui.py',
    'formatters_editor.py',
    'formatters_gameobjects.py',
    'toon.py',
    'cli.py',
]


def load_parts(namespace: dict[str, Any]) -> None:
    """Execute server parts in one namespace to preserve the legacy module API."""
    base_dir = Path(__file__).resolve().parent
    for file_name in _PART_FILES:
        path = base_dir / file_name
        source = path.read_text(encoding="utf-8")
        code = compile(source, str(path), "exec")
        exec(code, namespace)


__all__ = ["load_parts"]
