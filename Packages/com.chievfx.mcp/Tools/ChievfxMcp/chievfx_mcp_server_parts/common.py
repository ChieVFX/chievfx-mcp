# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

#!/usr/bin/env python3
"""Local-only MCP server for unity-mcp-chievfx.

Cursor talks MCP over stdio or HTTP. Unity-specific work is forwarded to the
ChievFX Unity bridge that runs inside the editor.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import re
import sys
import threading
import time
import traceback
import urllib.error
import urllib.parse
import urllib.request
import uuid
import xml.etree.ElementTree as ET
from datetime import datetime
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any


SERVER_NAME = "unity-mcp-chievfx"
SERVER_VERSION = "0.1.0"
PACKAGE_ROOT = Path(__file__).resolve().parents[2]
PROJECT_ROOT = Path(os.getcwd()).resolve()
TOOL_POLICY_PATH = PACKAGE_ROOT / "Tools" / "ChievfxMcp" / "chievfx_mcp_tool_policy.json"
TOOL_ROLE_PRESETS_PATH = PACKAGE_ROOT / "Tools" / "ChievfxMcp" / "chievfx_mcp_role_presets.json"
TOOL_SELECTION_PATH = PROJECT_ROOT / "UserSettings" / "ChievfxMcpToolSelection.json"
RESOURCE_SELECTION_PATH = PROJECT_ROOT / "UserSettings" / "ChievfxMcpResourceSelection.json"
PROMPT_SELECTION_PATH = PROJECT_ROOT / "UserSettings" / "ChievfxMcpPromptSelection.json"
CATALOGS_MD_PATH = PACKAGE_ROOT / "Tools" / "ChievfxMcp" / "chievfx_mcp_text_prompts_resources.md"
INITIALIZE_INSTRUCTIONS_MD_PATH = PACKAGE_ROOT / "Tools" / "ChievfxMcp" / "chievfx_mcp_initialize_instructions.md"
EXTENSION_CAPABILITY_MANIFEST_PATH = PROJECT_ROOT / "Library" / "ChievfxMcpBridge" / "extension-capabilities.json"
DEBUG_INSTRUCTIONS_PATH = PROJECT_ROOT / ".temp" / "debug_instructions.md"
TOOL_SELECTION_SCHEMA_VERSION = 1
RESOURCE_SELECTION_SCHEMA_VERSION = 1
PROMPT_SELECTION_SCHEMA_VERSION = 1
EXTENSION_CAPABILITY_MANIFEST_SCHEMA_VERSION = 1
EXTENSION_URI_PREFIX = "chievfx://extensions/"
RESOURCE_MIME_TYPE = "text/plain"
MAX_RESOURCE_TEXT_CHARS = 40000
HEARTBEAT_STALE_SECONDS = 5.0
PROCESSING_STALE_SECONDS = 30.0
OPERATION_STALE_SECONDS = 60.0
# How long resource reads may wait for the Unity bridge to become ready
# (heartbeat fresh, not compiling, not updating) before falling through anyway.
BRIDGE_READY_WAIT_SECONDS = 30.0
BRIDGE_READY_POLL_SECONDS = 0.1
# Small grace after compile/update flips false so log buffers and resource
# state can settle before we hand resources back to the client.
BRIDGE_READY_POST_BUSY_GRACE_SECONDS = 1.0
RECOMPILE_WAIT_SECONDS = 300.0
RECOMPILE_START_GRACE_SECONDS = 1.5
BRIDGE_RECOVERY_WAIT_SECONDS = 300.0
BRIDGE_STALE_RECOVERY_WAIT_SECONDS = 60.0
BRIDGE_RECOVERY_RECHECK_SECONDS = 1.0
BRIDGE_POST_RECOVERY_RESPONSE_GRACE_SECONDS = 10.0
# Orphan response/processing files older than this are removed before queueing
# a new request to keep the file transport from looking permanently "busy".
ORPHAN_RESPONSE_STALE_SECONDS = 30.0
PROGRESS_INTERVAL_SECONDS = 1.0
MAX_STATUS_OPERATIONS = 12
HARD_EVENTS_MAX_ENTRIES = 200
DEFAULT_EVENTS_CHECK_MAX_ENTRIES = 12
DEFAULT_EVENTS_TEXT_MESSAGE_CHARS = 180
DEFAULT_EVENTS_WAIT_TIMEOUT_MS = 10000
HARD_EVENTS_WAIT_TIMEOUT_MS = 1000000
DEFAULT_SCRIPT_EXECUTE_TIMEOUT_MS = 60000
DEFAULT_REFLECTION_METHOD_CALL_TIMEOUT_MS = 60000
DEFAULT_TESTS_RUN_TIMEOUT_MS = 60000
HARD_SCRIPT_EXECUTE_TIMEOUT_MS = 300000
HARD_REFLECTION_METHOD_CALL_TIMEOUT_MS = 300000
HARD_TESTS_RUN_TIMEOUT_MS = 300000
EVENTS_WAIT_POLL_SECONDS = 0.05

MAX_ACTIVE_EVENT_WAITS = 32
EVENT_WAIT_HIGH_WATERMARK = 24
MAX_EVENT_FILTER_TEXT = 256
TERMINAL_OPERATION_STATES = {"completed", "failed", "cancelled", "stale"}


def configure_project_root(project_root: str | os.PathLike[str] | None) -> None:
    """Point runtime state paths at Unity project root, not package root."""
    global PROJECT_ROOT
    global TOOL_SELECTION_PATH
    global RESOURCE_SELECTION_PATH
    global PROMPT_SELECTION_PATH
    global EXTENSION_CAPABILITY_MANIFEST_PATH
    global DEBUG_INSTRUCTIONS_PATH

    if project_root is None:
        return

    PROJECT_ROOT = Path(project_root).expanduser().resolve()
    TOOL_SELECTION_PATH = PROJECT_ROOT / "UserSettings" / "ChievfxMcpToolSelection.json"
    RESOURCE_SELECTION_PATH = PROJECT_ROOT / "UserSettings" / "ChievfxMcpResourceSelection.json"
    PROMPT_SELECTION_PATH = PROJECT_ROOT / "UserSettings" / "ChievfxMcpPromptSelection.json"
    EXTENSION_CAPABILITY_MANIFEST_PATH = PROJECT_ROOT / "Library" / "ChievfxMcpBridge" / "extension-capabilities.json"
    DEBUG_INSTRUCTIONS_PATH = PROJECT_ROOT / ".temp" / "debug_instructions.md"


DEFAULT_REQUIRED_TOOL_IDS = {
    "screenshot-game-view",
    "screenshot-camera",
    "screenshot-editor-window",
    "tool-batch",
    "editor-playmode-set",
    "bridge-get-operation",
    "bridge-get-status",
    "events-check-since",
    "events-wait",
    "asset-create",
    "asset-delete",
    "assets-refresh",
    "folder-ensure",
    "recompile",
    "console-clear-logs",
    "console-get-logs",
    "console-get-logs-single",
    "reflection-method-find",
    "reflection-method-find-single",
    "reflection-method-call",
    "editor-window-list",
    "editor-window-open",
    "editor-window-focus",
}
DEFAULT_ENABLED_TOOL_IDS = {
    "tools-list-categories",
    "tools-list-category",
    "tools-set-enabled-state",
    "tools-get-roles",
    "tools-get-role",
    "tools-set-role",
}
TOOL_CATEGORIES = {
    "screenshot-game-view": "Essentials",
    "screenshot-camera": "Essentials",
    "screenshot-editor-window": "Editor Window",
    "tool-batch": "Essentials",
    "editor-playmode-set": "Essentials",
    "bridge-get-operation": "Essentials",
    "bridge-get-status": "Essentials",
    "events-check-since": "Essentials",
    "events-wait": "Essentials",
    "asset-create": "Essentials",
    "asset-delete": "Essentials",
    "assets-refresh": "Essentials",
    "folder-ensure": "Essentials",
    "recompile": "Essentials",
    "console-clear-logs": "Essentials",
    "console-get-logs": "Essentials",
    "console-get-logs-single": "Essentials",
    "reflection-method-find": "Essentials",
    "reflection-method-find-single": "Essentials",
    "reflection-method-call": "Essentials",
    "tools-list-categories": "Autonomous",
    "tools-list-category": "Autonomous",
    "tools-set-enabled-state": "Autonomous",
    "tools-get-roles": "Autonomous",
    "tools-get-role": "Autonomous",
    "tools-set-role": "Autonomous",
    "editor-window-list": "Editor Window",
    "editor-window-open": "Editor Window",
    "editor-window-focus": "Editor Window",
    "scene-list-opened": "Scene",
    "scene-list-available": "Scene",
    "scene-create": "Scene",
    "scene-open": "Scene",
    "scene-save": "Scene",
    "gameobject-create": "GameObject",
    "gameobject-hierarchy": "GameObject",
    "gameobject-find": "GameObject",
    "gameobject-component-get": "GameObject",
    "gameobject-update": "GameObject",
    "gameobject-component-update-or-create": "GameObject",
    "gameobject-transform-get": "GameObject",
    "gameobject-transform-update": "GameObject",
    "gameobject-set-parent": "GameObject",
    "gameobject-duplicate": "GameObject",
    "prefab-open": "Prefab",
    "prefab-close": "Prefab",
    "prefab-save": "Prefab",
    "prefab-create": "Prefab",
    "prefab-instantiate": "Prefab",
    "package-list": "Package Manager",
    "package-search": "Package Manager",
    "package-add": "Package Manager",
    "package-remove": "Package Manager",
    "script-execute": "Script Execution / Tests",
    "tests-run": "Script Execution / Tests",
    "profiler-get-state": "Profiler",
    "profiler-start-recording": "Profiler",
    "profiler-stop-recording": "Profiler",
    "profiler-counters-get": "Profiler",
    "profiler-window-control": "Profiler",
    "frame-debugger-control": "Frame Debugger",
    "frame-debugger-groups-list": "Frame Debugger",
    "frame-debugger-group-events-list": "Frame Debugger",
    "frame-debugger-drawcall-get": "Frame Debugger",
    "frame-debugger-drawcall-screenshot": "Frame Debugger",
    "frame-debugger-events-list": "Frame Debugger",
    "frame-debugger-event-get": "Frame Debugger",
}
TOOL_CATEGORY_DESCRIPTIONS = {
    "Essentials": "Always-on safe basics for screenshots, console inspection, asset refresh, and reflected C# calls.",
    "Autonomous": "Optional discovery and enablement helpers for agents to inspect and change optional MCP tool exposure.",
    "Editor Window": "Always-on Unity Editor window discovery, opening, tab selection, and render-backed capture workflows.",
    "Scene": "Optional scene inventory and open/save control.",
    "GameObject": "Optional GameObject hierarchy, lookup, creation, metadata/component mutation, transform, parenting, and duplication tools.",
    "Prefab": "Optional prefab-stage and prefab asset workflows.",
    "Package Manager": "Optional Unity Package Manager inventory, search, add, and remove operations.",
    "Script Execution / Tests": "Optional high-risk local script execution and Unity test running tools.",
    "Profiler": "Optional Unity profiler state, recording, counter, and focused window navigation helpers.",
    "Frame Debugger": "Optional Unity Frame Debugger window state and event navigation helpers.",
    "cinemachine-and-timeline": "Optional camera and cutscene authoring helpers for Cinemachine, Timeline, shots, and camera QA.",
    "Control": "Optional Play Mode keyboard and mouse input helpers for New Input System-driven control.",
    "Particles": "Optional built-in ParticleSystem authoring, playback, preview, and inspection helpers.",
    "Runtime UI": "Optional runtime UI screen-position probing across registered UI adapters.",
    "UI Toolkit": "Optional runtime UI Toolkit panel inspection and screen-position probing helpers.",
    "ugui-design": "Optional editor-time uGUI authoring helpers for Canvas, RectTransform, images, layout, TMP, and sprites.",
    "ugui-runtime-control": "Optional Play Mode uGUI probing and control helpers: hit stacks, clicks, drags, selection, and control values.",
    "OBSOLETE": "Deprecated compatibility tools. Only enable explicitly; bulk enable skips this category.",
}
DEFAULT_REQUIRED_RESOURCE_IDS = {"resources-guide"}
DEFAULT_REQUIRED_RESOURCE_TEMPLATE_IDS: set[str] = set()
DEFAULT_REQUIRED_PROMPT_NAMES: set[str] = set()
RESOURCE_CATEGORIES = {
    "resources-guide": "Guide",
    "editor-context": "Editor",
    "scene-opened": "Scene",
    "scene-current-material-profile-summary": "Asset",
    "scene-current-usage-counts": "Asset",
}
RESOURCE_TEMPLATE_CATEGORIES = {
    "scene-go": "GameObject",
    "scene-component": "GameObject",
    "scene-current-go": "GameObject",
    "scene-current-component": "GameObject",
    "scene-current-go-name-contains": "GameObject",
    "scene-current-go-name-pattern": "GameObject",
    "scene-current-go-component": "GameObject",
    "scene-current-go-filter": "GameObject",
    "assets-name-contains": "Asset",
    "assets-type": "Asset",
    "assets-label": "Asset",
    "assets-filter": "Asset",
    "asset-detail": "Asset",
    "asset-subasset-detail": "Asset",
    "scene-current-material-profile-shader": "Asset",
    "scene-current-material-profile-material": "Asset",
    "scene-current-usage-assets": "Asset",
    "scene-current-usage-asset": "Asset",
    "scene-current-usage-subasset": "Asset",
}
RESOURCE_CATEGORY_DESCRIPTIONS = {
    "Guide": "Static usage notes for ChievFX MCP resources and URI encoding.",
    "Editor": "Compact current Unity editor context.",
    "Scene": "Opened scenes and scene context resources.",
    "GameObject": "GameObject and component drill-down resources.",
    "Asset": "Persisted AssetDatabase search and asset drill-down resources.",
    "cinemachine-and-timeline": "Cinemachine and Timeline extension resources for camera/cutscene inspection.",
}
PROMPT_CATEGORIES = {
    "unity-scene-review": "Scene",
    "unity-editor-context": "Editor",
    "unity-shader-built-in-draft": "Shader",
    "unity-shader-urp-draft": "Shader",
    "unity-shader-hdrp-draft": "Shader",
    "unity-shader-graph-plan": "Shader",
    "unity-material-profile-review": "Shader",
}
PROMPT_CATEGORY_DESCRIPTIONS = {
    "Editor": "Prompt templates grounded in current Unity Editor context.",
    "Scene": "Prompt templates for reviewing, debugging, and planning scene work.",
    "Shader": "Prompt templates for Unity shader, Shader Graph, material, and render pipeline work.",
    "Diagnostics": "Extension and MCP diagnostic prompts.",
    "cinemachine-and-timeline": "Prompt templates for Cinemachine and Timeline camera/cutscene workflows.",
}
TOOL_SELECTION_NOTE = "Token counts estimate compact JSON MCP descriptors only; not exact billable request tokens."
RESOURCE_SELECTION_NOTE = (
    "Token counts estimate compact MCP resource and resource template descriptors only; not exact billable request tokens."
)
PROMPT_SELECTION_NOTE = "Token counts estimate compact MCP prompt descriptors only; not exact billable request tokens."
TOOL_RELOAD_GUIDANCE = (
    "After changing enabled tools, reload MCP tools or restart Cursor. Running MCP server processes read "
    "selection at runtime, but Cursor may cache descriptors."
)
RESOURCE_RELOAD_GUIDANCE = (
    "After changing enabled resources, reload MCP resources or restart Cursor. Running MCP server processes "
    "read selection at runtime, but Cursor may cache descriptors."
)
PROMPT_RELOAD_GUIDANCE = (
    "After changing enabled prompts, reload MCP prompts or restart Cursor. Running MCP server processes read "
    "selection at runtime, but Cursor may cache descriptors."
)
DESCRIPTOR_ESTIMATE_BASIS = (
    'json.dumps({name,description,inputSchema:advertised_input_schema(tool)}, ensure_ascii=False, '
    'separators=(",",":"))'
)
TOOL_DESCRIPTION_ESTIMATE_BASIS = 'json.dumps({name,description}, ensure_ascii=False, separators=(",",":"))'
RESOURCE_DESCRIPTOR_ESTIMATE_BASIS = (
    'json.dumps({uri,name,description,mimeType}, ensure_ascii=False, separators=(",",":"))'
)
RESOURCE_TEMPLATE_DESCRIPTOR_ESTIMATE_BASIS = (
    'json.dumps({uriTemplate,name,description,mimeType}, ensure_ascii=False, separators=(",",":"))'
)
RESOURCE_DESCRIPTION_ESTIMATE_BASIS = 'json.dumps({uri,name,description}, ensure_ascii=False, separators=(",",":"))'
RESOURCE_TEMPLATE_DESCRIPTION_ESTIMATE_BASIS = (
    'json.dumps({uriTemplate,name,description}, ensure_ascii=False, separators=(",",":"))'
)
PROMPT_DESCRIPTOR_ESTIMATE_BASIS = (
    'json.dumps({name,title,description,arguments}, ensure_ascii=False, separators=(",",":"))'
)
PROMPT_DESCRIPTION_ESTIMATE_BASIS = (
    'json.dumps({name,title,description}, ensure_ascii=False, separators=(",",":"))'
)
RESOURCE_READ_ENVELOPE_ESTIMATE_BASIS = (
    "Compact JSON-RPC resources/read request with one uri param. Template estimates use the advertised "
    "uriTemplate string as the sample uri; real encoded path lengths are excluded."
)
PROMPT_GET_ENVELOPE_ESTIMATE_BASIS = (
    "Compact JSON-RPC prompts/get request with empty arguments. User-provided arguments are excluded."
)
CALL_ENVELOPE_ESTIMATE_BASIS = (
    "Compact JSON-RPC tools/call request with empty arguments. User-provided arguments are excluded; "
    "real client/model tool-use blocks are hidden and may add model-specific overhead."
)
RESPONSE_ESTIMATE_NOTE = (
    "Rough wrapped-result guidance only. Real response tokens depend on row counts, text length, client wrappers, "
    "model accounting, and image handling."
)
RESPONSE_ESTIMATE_PROFILES: dict[str, dict[str, str]] = {
    "small": {
        "label": "small scalar/result ~25-50 wrapped tokens",
        "typicalTokens": "25-50",
    },
    "status": {
        "label": "status/operation result ~50-150 wrapped tokens typical",
        "typicalTokens": "50-150",
    },
    "log-list": {
        "label": "logs/events/method lists ~100-300 wrapped tokens typical",
        "typicalTokens": "100-300",
    },
    "row-list": {
        "label": "row listings scale with row count; 500-2000+ possible",
        "typicalTokens": "100-300 typical; 500-2000+ on larger listings",
    },
    "large": {
        "label": "script/test output scales with logs/results; 500-2000+ possible",
        "typicalTokens": "100-300 typical; 500-2000+ on larger outputs",
    },
    "image": {
        "label": "image content; visual-token billing is model/client specific",
        "typicalTokens": "model/client specific",
    },
}
RESOURCE_RESPONSE_ESTIMATE_PROFILES: dict[str, dict[str, str]] = {
    "small": RESPONSE_ESTIMATE_PROFILES["small"],
    "status": RESPONSE_ESTIMATE_PROFILES["status"],
    "log-list": RESPONSE_ESTIMATE_PROFILES["log-list"],
    "row-list": RESPONSE_ESTIMATE_PROFILES["row-list"],
    "guide": {
        "label": "guide text resource payload scales with guide length; 500-2000+ possible",
        "typicalTokens": "100-300 typical; 500-2000+ on larger resource payloads",
    },
    "serialized-component": {
        "label": "serialized component data resource payload scales with fields/values; 500-2000+ possible",
        "typicalTokens": "100-300 typical; 500-2000+ on larger resource payloads",
    },
}
PROMPT_RESPONSE_ESTIMATE_PROFILES: dict[str, dict[str, str]] = {
    "small": {
        "label": "text prompt payload ~100-300 wrapped tokens typical",
        "typicalTokens": "100-300",
    },
    "dynamic": {
        "label": "Unity-backed text prompt payload scales with editor context; 100-500+ possible",
        "typicalTokens": "100-500+ depending on context",
    },
}
RESPONSE_PROFILE_BY_TOOL = {
    "screenshot-game-view": "image",
    "screenshot-camera": "image",
    "screenshot-editor-window": "image",
    "tool-batch": "status",
    "bridge-get-operation": "status",
    "bridge-get-status": "status",
    "asset-create": "status",
    "asset-delete": "status",
    "folder-ensure": "status",
    "recompile": "status",
    "events-check-since": "log-list",
    "events-wait": "log-list",
    "console-get-logs": "log-list",
    "console-get-logs-single": "log-list",
    "reflection-method-find": "log-list",
    "reflection-method-find-single": "row-list",
    "tools-list-categories": "row-list",
    "tools-list-category": "row-list",
    "tools-set-enabled-state": "status",
    "tools-get-roles": "status",
    "tools-get-role": "row-list",
    "tools-set-role": "status",
    "editor-window-list": "row-list",
    "editor-window-open": "status",
    "editor-window-focus": "status",
    "scene-create": "status",
    "scene-list-opened": "row-list",
    "scene-list-available": "row-list",
    "gameobject-hierarchy": "row-list",
    "gameobject-find": "row-list",
    "package-list": "row-list",
    "package-search": "row-list",
    "script-execute": "large",
    "tests-run": "large",
    "profiler-window-control": "status",
    "frame-debugger-control": "status",
    "frame-debugger-groups-list": "row-list",
    "frame-debugger-group-events-list": "row-list",
    "frame-debugger-drawcall-get": "status",
    "frame-debugger-drawcall-screenshot": "image",
    "frame-debugger-events-list": "row-list",
    "frame-debugger-event-get": "status",
}
RESPONSE_PROFILE_BY_RESOURCE = {
    "resources-guide": "guide",
    "editor-context": "status",
    "scene-opened": "row-list",
    "scene-go": "row-list",
    "scene-component": "serialized-component",
    "scene-current-go": "row-list",
    "scene-current-component": "serialized-component",
    "scene-current-go-name-contains": "row-list",
    "scene-current-go-name-pattern": "row-list",
    "scene-current-go-component": "row-list",
    "scene-current-go-filter": "row-list",
    "assets-name-contains": "row-list",
    "assets-type": "row-list",
    "assets-label": "row-list",
    "assets-filter": "row-list",
    "asset-detail": "row-list",
    "asset-subasset-detail": "row-list",
}


METHOD_REF_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "namespace": {"type": "string"},
        "typeName": {"type": "string"},
        "methodName": {"type": "string"},
        "inputParameters": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "typeName": {"type": "string"},
                },
            },
        },
    },
}


SERIALIZED_VALUE_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "value": {},
    },
}


OUTPUT_FORMAT_PROPERTY: dict[str, Any] = {
    "type": "string",
    "enum": ["toon", "json"],
    "default": "toon",
    "description": "Text output format. Defaults to compact TOON-like text; json returns compact JSON.",
}


VECTOR3_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "x": {"type": "number"},
        "y": {"type": "number"},
        "z": {"type": "number"},
    },
    "required": ["x", "y", "z"],
}


VECTOR3_REF: dict[str, str] = {"$ref": "#/$defs/Vector3"}


ADVERTISED_SCHEMA_DETAIL_KEYS = {"$defs", "default", "description", "maximum", "minimum"}
ADVERTISED_VECTOR3_SCHEMA: dict[str, str] = {"type": "object"}
ADVERTISED_PROPERTY_OMISSIONS: dict[str, set[str]] = {
    "assets-refresh": {"options"},
    "bridge-get-status": {"maxOperations", "verbose"},
    "console-get-logs": {"lastMinutes", "stack"},
    "console-get-logs-single": {"includeUnityConsole"},
    "events-check-since": {"includeData", "level", "maxEntries", "type"},
    "events-wait": {"includeData", "includeRecentMs", "level", "marker", "type"},
    "profiler-window-control": {"moduleIdentifier", "selectedModuleIdentifier", "stayOnLatestFrame"},
    "reflection-method-call": {"executeInMainThread", "inputParameters"},
    "script-execute": {"includeLogs", "logType", "parameters"},
    "screenshot-editor-window": {
        "captureArea",
        "delayFrames",
        "delayMs",
        "maxDimension",
        "selectDockedTab",
        "timeoutMs",
    },
    "tests-run": {
        "includeLogs",
        "includeLogsStacktrace",
        "includeMessages",
        "includePassingTests",
        "includeStacktrace",
        "logType",
        "maxResults",
    },
}
