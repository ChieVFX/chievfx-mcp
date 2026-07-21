import base64
import json
import sys
import tempfile
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class ExtensionToolBridgeServer(mcp.McpServer):
    def __init__(self, bridge_dir: Path) -> None:
        super().__init__("http://127.0.0.1:1", str(bridge_dir), timeout_ms=1000)
        self.calls: list[tuple[str, dict[str, object]]] = []

    def call_unity_bridge(
        self,
        name: str,
        arguments: dict[str, object],
        request_id: object = None,
        progress_token: object = None,
        notify: object = None,
    ) -> dict[str, object]:
        self.calls.append((name, arguments))
        return {"ok": True, "contentType": "json", "result": {"tool": name, "arguments": arguments}}


class ImageScreenshotBridgeServer(mcp.McpServer):
    def __init__(self, bridge_dir: Path, base64_data: str = "aGVsbG8td29ybGQ=") -> None:
        super().__init__("http://127.0.0.1:1", str(bridge_dir), timeout_ms=1000)
        self.base64_data = base64_data
        self.calls: list[tuple[str, dict[str, object]]] = []

    def call_unity_bridge(
        self,
        name: str,
        arguments: dict[str, object],
        request_id: object = None,
        progress_token: object = None,
        notify: object = None,
    ) -> dict[str, object]:
        self.calls.append((name, dict(arguments)))
        return {"ok": True, "contentType": "image", "base64": self.base64_data, "mimeType": "image/png"}


class ScreenshotSavePathTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)
        self.original_tool_selection_path = mcp.TOOL_SELECTION_PATH
        mcp.TOOL_SELECTION_PATH = Path(self.temp_dir.name) / "UserSettings" / "ChievfxMcpToolSelection.json"
        self.addCleanup(lambda: setattr(mcp, "TOOL_SELECTION_PATH", self.original_tool_selection_path))

    def text_blocks(self, result: dict[str, object]) -> list[str]:
        return [item["text"] for item in result["content"] if item["type"] == "text"]

    def test_save_path_writes_png_and_is_stripped_from_bridge_args(self) -> None:
        server = ImageScreenshotBridgeServer(Path(self.temp_dir.name) / "bridge")
        save_target = Path(self.temp_dir.name) / "shots" / "view.png"

        result = server.call_tool(
            {"name": "screenshot-game-view", "arguments": {"maxDimension": 512, "savePath": str(save_target)}}
        )

        self.assertFalse(result["isError"])
        # savePath is handled server-side and must never reach the Unity bridge.
        self.assertEqual(server.calls, [("screenshot-game-view", {"maxDimension": 512})])
        self.assertTrue(save_target.exists())
        self.assertEqual(save_target.read_bytes(), base64.b64decode(server.base64_data))
        self.assertEqual(result["structuredContent"]["savedPath"], str(save_target.resolve()))
        self.assertTrue(any("savedPath" in block for block in self.text_blocks(result)))

    def test_save_path_resolves_relative_to_project_root(self) -> None:
        server = ImageScreenshotBridgeServer(Path(self.temp_dir.name) / "bridge")
        project_root = Path(self.temp_dir.name) / "project"
        original_root = mcp.PROJECT_ROOT
        mcp.PROJECT_ROOT = project_root
        self.addCleanup(lambda: setattr(mcp, "PROJECT_ROOT", original_root))

        result = server.call_tool(
            {"name": "screenshot-camera", "arguments": {"savePath": "./Temp/cam.png"}}
        )

        expected = (project_root / "Temp" / "cam.png").resolve()
        self.assertTrue(expected.exists())
        self.assertEqual(result["structuredContent"]["savedPath"], str(expected))
        self.assertEqual(server.calls, [("screenshot-camera", {})])

    def test_image_without_save_path_is_unchanged(self) -> None:
        server = ImageScreenshotBridgeServer(Path(self.temp_dir.name) / "bridge")

        result = server.call_tool({"name": "screenshot-game-view", "arguments": {}})

        self.assertFalse(result["isError"])
        self.assertEqual(server.calls, [("screenshot-game-view", {})])
        self.assertNotIn("structuredContent", result)
        self.assertEqual([item["type"] for item in result["content"]], ["image"])

    def test_save_path_failure_is_reported_without_losing_image(self) -> None:
        server = ImageScreenshotBridgeServer(Path(self.temp_dir.name) / "bridge")
        # Point savePath at a path whose parent is an existing file, so mkdir fails.
        blocker = Path(self.temp_dir.name) / "blocker"
        blocker.write_text("not a directory", encoding="utf-8")
        save_target = blocker / "nested" / "view.png"

        result = server.call_tool(
            {"name": "screenshot-game-view", "arguments": {"savePath": str(save_target)}}
        )

        self.assertFalse(result["isError"])
        self.assertEqual([item["type"] for item in result["content"]], ["image", "text"])
        self.assertIn("savePathError", result["structuredContent"])
        self.assertFalse(save_target.exists())


class editorWindowScreenshotMetadataTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)
        self.original_extension_manifest_path = mcp.EXTENSION_CAPABILITY_MANIFEST_PATH
        self.original_prompt_selection_path = mcp.PROMPT_SELECTION_PATH
        self.original_tool_selection_path = mcp.TOOL_SELECTION_PATH
        mcp.EXTENSION_CAPABILITY_MANIFEST_PATH = (
            Path(self.temp_dir.name) / "Library" / "ChievfxMcpBridge" / "extension-capabilities.snapshot.json"
        )
        mcp.PROMPT_SELECTION_PATH = Path(self.temp_dir.name) / "UserSettings" / "ChievfxMcpPromptSelection.json"
        mcp.TOOL_SELECTION_PATH = Path(self.temp_dir.name) / "UserSettings" / "ChievfxMcpToolSelection.json"
        self.addCleanup(self.restore_paths)

    def restore_paths(self) -> None:
        mcp.EXTENSION_CAPABILITY_MANIFEST_PATH = self.original_extension_manifest_path
        mcp.PROMPT_SELECTION_PATH = self.original_prompt_selection_path
        mcp.TOOL_SELECTION_PATH = self.original_tool_selection_path

    def write_extension_manifest(self, extensions: list[dict[str, object]]) -> None:
        mcp.EXTENSION_CAPABILITY_MANIFEST_PATH.parent.mkdir(parents=True, exist_ok=True)
        mcp.EXTENSION_CAPABILITY_MANIFEST_PATH.write_text(
            json.dumps(
                {
                    "schemaVersion": mcp.EXTENSION_CAPABILITY_MANIFEST_SCHEMA_VERSION,
                    "extensions": extensions,
                }
            ),
            encoding="utf-8",
        )
        mcp.invalidate_extension_manifest_cache()

    def test_schema_documents_default_repaint_wait(self) -> None:
        tool = next(tool for tool in mcp.TOOLS if tool["name"] == "screenshot-editor-window")

        properties = tool["inputSchema"]["properties"]
        delay_frames = properties["delayFrames"]
        delay_ms = properties["delayMs"]

        self.assertNotIn("focus", properties)
        self.assertEqual(delay_frames["maximum"], 120)
        self.assertEqual(delay_ms["maximum"], 10000)
        self.assertIn("2-frame plus 1000 ms", delay_frames["description"])
        self.assertIn("2-frame plus 1000 ms", delay_ms["description"])

    def test_server_metadata_keeps_editor_window_screenshot_image_category(self) -> None:
        self.assertEqual(mcp.TOOL_CATEGORIES["screenshot-editor-window"], "editor-window")
        self.assertEqual(mcp.RESPONSE_PROFILE_BY_TOOL["screenshot-editor-window"], "image")

    def test_game_view_screenshot_schema_uses_max_dimension(self) -> None:
        tool = next(tool for tool in mcp.TOOLS if tool["name"] == "screenshot-game-view")
        properties = tool["inputSchema"]["properties"]

        self.assertEqual(set(properties), {"maxDimension", "savePath"})
        self.assertEqual(properties["maxDimension"]["default"], 960)
        self.assertEqual(properties["savePath"]["type"], "string")

    def test_editor_window_list_formatter_keeps_target_selectors_compact(self) -> None:
        result = {
            "count": 1,
            "matched": 1,
            "truncated": False,
            "focusedInstanceId": 0,
            "mouseOverInstanceId": 123,
            "windows": [
                {
                    "instanceId": 123,
                    "title": "Console",
                    "typeName": "ConsoleWindow",
                    "fullTypeName": "Unityeditor.ConsoleWindow",
                    "focused": False,
                    "mouseOver": True,
                    "selected": True,
                    "docked": True,
                    "floating": False,
                    "hostViewInstanceId": 456,
                    "tabIndex": 1,
                    "selectedTabIndex": 1,
                    "tabCount": 3,
                }
            ],
            "diagnostics": [],
        }

        text = mcp.format_editor_window_list_text(result)

        self.assertEqual(
            text,
            "count:1 matched:1 mouseOverInstanceId:123\n"
            "windows[1]:\n"
            "- id:123 title:Console type:ConsoleWindow full:Unityeditor.ConsoleWindow mouseOver selected docked tab:1/3 host:456",
        )
        self.assertNotIn("contentRect", text)
        self.assertNotIn("tabs[", text)

    def test_editor_window_action_formatter_omits_rects_and_tabs(self) -> None:
        result = {
            "action": "typeName",
            "success": True,
            "window": {
                "instanceId": 123,
                "title": "Game",
                "typeName": "GameView",
                "fullTypeName": "Unityeditor.GameView",
                "focused": False,
                "selected": True,
                "docked": True,
                "floating": False,
                "contentRect": {"x": 1, "y": 2, "width": 3, "height": 4},
                "hostViewScreenRect": {"x": 1, "y": 2, "width": 3, "height": 4},
                "containerWindowRect": {"x": 1, "y": 2, "width": 3, "height": 4},
                "tabIndex": 1,
                "selectedTabIndex": 1,
                "tabCount": 3,
                "tabs": [{"title": "scene"}, {"title": "Game"}],
            },
            "diagnostics": [],
        }

        text = mcp.format_editor_window_action_text(result)

        self.assertEqual(
            text,
            "action:typeName success:true id:123 title:Game type:GameView selected docked tab:1/3",
        )
        self.assertNotIn("contentRect", text)
        self.assertNotIn("fullTypeName", text)
        self.assertNotIn("tabs", text)

    def test_advertised_editor_window_screenshot_schema_keeps_common_path_only(self) -> None:
        tool = next(tool for tool in mcp.TOOLS if tool["name"] == "screenshot-editor-window")

        advertised = mcp.advertised_input_schema(tool)
        properties = advertised["properties"]

        self.assertEqual(set(properties), {"target", "openIfMissing", "savePath"})
        self.assertIn("selectDockedTab", tool["inputSchema"]["properties"])
        self.assertIn("captureArea", tool["inputSchema"]["properties"])
        self.assertIn("delayFrames", tool["inputSchema"]["properties"])
        self.assertIn("maxDimension", tool["inputSchema"]["properties"])
        self.assertNotIn("description", json.dumps(advertised))
        self.assertNotIn("default", json.dumps(advertised))
        self.assertNotIn("minimum", json.dumps(advertised))
        self.assertNotIn("maximum", json.dumps(advertised))

    def test_advertised_schema_hides_runtime_only_compat_args(self) -> None:
        tools = {tool["name"]: tool for tool in mcp.TOOLS}

        self.assertIn("maxOperations", tools["bridge-get-status"]["inputSchema"]["properties"])
        self.assertNotIn("maxOperations", mcp.advertised_input_schema(tools["bridge-get-status"])["properties"])
        self.assertIn("options", tools["assets-refresh"]["inputSchema"]["properties"])
        self.assertIn("path", tools["assets-refresh"]["inputSchema"]["properties"])
        self.assertIn("recompile", tools)
        self.assertIn("timeoutMs", tools["recompile"]["inputSchema"]["properties"])
        self.assertNotIn("options", mcp.advertised_input_schema(tools["assets-refresh"])["properties"])
        self.assertIn("outputFormat", tools["console-get-logs"]["inputSchema"]["properties"])
        self.assertNotIn("outputFormat", mcp.advertised_input_schema(tools["console-get-logs"])["properties"])

        hidden_args = {
            "bridge-get-status": {"verbose"},
            "console-get-logs": {"lastMinutes", "stack"},
            "console-get-logs-single": {"includeUnityConsole"},
            "events-check-since": {"includeData", "level", "maxEntries", "source", "type"},
            "events-wait": {"includeData", "level", "source", "type"},
            "profiler-window-control": {"moduleIdentifier", "selectedModuleIdentifier", "stayOnLatestFrame"},
            "reflection-method-call": {"executeInMainThread", "inputParameters"},
            "script-execute": {"includeLogs", "logType", "parameters"},
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
        for tool_name, property_names in hidden_args.items():
            runtime_properties = tools[tool_name]["inputSchema"]["properties"]
            advertised_properties = mcp.advertised_input_schema(tools[tool_name])["properties"]
            for property_name in property_names:
                self.assertIn(property_name, runtime_properties)
                self.assertNotIn(property_name, advertised_properties)

        self.assertIn("timeoutMs", tools["script-execute"]["inputSchema"]["properties"])
        self.assertIn("timeoutMs", tools["reflection-method-call"]["inputSchema"]["properties"])
        self.assertIn("timeoutMs", tools["tests-run"]["inputSchema"]["properties"])
        events_wait_advertised = mcp.advertised_input_schema(tools["events-wait"])["properties"]
        self.assertEqual(
            set(events_wait_advertised),
            {"contains", "marker", "sinceEventId", "includeRecentMs", "timeoutMs"},
        )
        events_check_advertised = mcp.advertised_input_schema(tools["events-check-since"])["properties"]
        self.assertEqual(
            set(events_check_advertised),
            {"contains", "marker", "sinceEventId", "sinceTimestampUtc"},
        )
        # These two tools keep per-property descriptions in the advertised schema (self-documenting),
        # while default/min/max noise is still stripped.
        for property_name in ("contains", "marker"):
            self.assertIn("description", events_wait_advertised[property_name])
            self.assertIn("description", events_check_advertised[property_name])
        self.assertNotIn("default", events_wait_advertised["timeoutMs"])
        self.assertNotIn("maximum", events_wait_advertised["timeoutMs"])

        self.assertIn("timeoutMs", mcp.advertised_input_schema(tools["script-execute"])["properties"])
        self.assertIn("timeoutMs", mcp.advertised_input_schema(tools["reflection-method-call"])["properties"])
        self.assertIn("timeoutMs", mcp.advertised_input_schema(tools["tests-run"])["properties"])
        self.assertEqual(tools["script-execute"]["inputSchema"]["properties"]["timeoutMs"]["default"], 60000)
        self.assertEqual(tools["reflection-method-call"]["inputSchema"]["properties"]["timeoutMs"]["default"], 60000)
        self.assertEqual(tools["tests-run"]["inputSchema"]["properties"]["timeoutMs"]["default"], 60000)

    def test_long_running_tools_default_to_sixty_seconds(self) -> None:
        server = mcp.McpServer("http://127.0.0.1:1", "", timeout_ms=1000)

        self.assertEqual(server.get_tool_timeout_ms("script-execute", {}), 60000)
        self.assertEqual(server.get_tool_timeout_ms("reflection-method-call", {}), 60000)
        self.assertEqual(server.get_tool_timeout_ms("tests-run", {}), 60000)
        self.assertEqual(server.get_tool_timeout_ms("script-execute", {"timeoutMs": 120000}), 120000)
        self.assertEqual(server.get_tool_timeout_ms("reflection-method-call", {"timeoutMs": 120000}), 120000)
        self.assertEqual(server.get_tool_timeout_ms("tests-run", {"timeoutMs": 120000}), 120000)
        self.assertEqual(server.get_tool_timeout_ms("console-get-logs", {}), 1000)

    def test_advertised_vector3_schema_is_compact(self) -> None:
        tool = next(tool for tool in mcp.TOOLS if tool["name"] == "gameobject-transform-update")

        advertised = mcp.advertised_input_schema(tool)
        properties = advertised["properties"]

        self.assertNotIn("$defs", advertised)
        self.assertEqual(properties["position"], {"type": "object"})
        self.assertEqual(properties["rotationEuler"], {"type": "object"})

    def test_advertised_vector3_ref_with_extra_keys_is_inlined(self) -> None:
        # gameobject-duplicate declares position as {"$ref": ..., "description": ...}; the extra
        # key must not defeat inlining, or the advertised schema ships a dangling $ref.
        tool = next(tool for tool in mcp.TOOLS if tool["name"] == "gameobject-duplicate")

        properties = mcp.advertised_input_schema(tool)["properties"]

        self.assertEqual(properties["position"], {"type": "object"})
        self.assertEqual(properties["rotationEuler"], {"type": "object"})

    def test_advertised_schemas_never_contain_refs(self) -> None:
        # Strict clients (Kimi/Moonshot) reject tool schemas with unresolved $ref; the advertised
        # surface drops $defs, so no $ref may survive anywhere in any tool schema.
        def collect_keys(value: object, found: set[str]) -> None:
            if isinstance(value, dict):
                for key, item in value.items():
                    if key in {"$ref", "$defs"}:
                        found.add(key)
                    collect_keys(item, found)
            elif isinstance(value, list):
                for item in value:
                    collect_keys(item, found)

        for tool in mcp.TOOLS:
            found: set[str] = set()
            collect_keys(mcp.advertised_input_schema(tool), found)
            self.assertFalse(found, f"{tool['name']} advertises {sorted(found)} in its inputSchema")

    def test_advertised_descriptor_estimates_stay_compact(self) -> None:
        metadata = mcp.build_tool_metadata()
        estimates = {tool["name"]: tool["estimatedTokens"] for tool in metadata["tools"]}

        # events-wait/events-check-since/asset-find intentionally keep per-property descriptions because
        # correct usage is intuition-critical; they are exempt from the lean-descriptor cap.
        documented_tools = mcp.ADVERTISED_SCHEMA_DESCRIPTION_TOOLS
        lean_estimates = {name: value for name, value in estimates.items() if name not in documented_tools}
        self.assertLessEqual(max(lean_estimates.values()), 160)
        self.assertLessEqual(estimates["tests-run"], 130)
        # screenshot-editor-window's advertised surface is design-locked to {target, openIfMissing,
        # savePath} (see test_advertised_editor_window_screenshot_schema_keeps_common_path_only); the
        # target oneOf structure alone is ~105, so 110 is the realistic lean cap.
        self.assertLessEqual(estimates["screenshot-editor-window"], 110)
        self.assertLessEqual(estimates["gameobject-transform-update"], 100)
        # documented_tools may include extension tools (e.g. editor-playmode-set,
        # input-control-mouse-gesture) that are absent when metadata carries only core tools.
        for name in documented_tools:
            if name in estimates:
                self.assertLessEqual(estimates[name], 500)

    def test_frame_debugger_control_documents_one_based_event_limit(self) -> None:
        tool = next(tool for tool in mcp.TOOLS if tool["name"] == "frame-debugger-control")

        event_limit = tool["inputSchema"]["properties"]["eventLimit"]

        self.assertEqual(event_limit["minimum"], 1)
        self.assertIn("One-based", event_limit["description"])

    def test_frame_debugger_event_tools_are_advertised(self) -> None:
        names = [tool["name"] for tool in mcp.TOOLS]

        self.assertIn("frame-debugger-events-list", names)
        self.assertIn("frame-debugger-event-get", names)
        self.assertIn("frame-debugger-groups-list", names)
        self.assertIn("frame-debugger-group-events-list", names)
        self.assertIn("frame-debugger-drawcall-get", names)
        self.assertIn("frame-debugger-drawcall-screenshot", names)

        list_tool = next(tool for tool in mcp.TOOLS if tool["name"] == "frame-debugger-events-list")
        get_tool = next(tool for tool in mcp.TOOLS if tool["name"] == "frame-debugger-event-get")
        group_events_tool = next(tool for tool in mcp.TOOLS if tool["name"] == "frame-debugger-group-events-list")
        drawcall_tool = next(tool for tool in mcp.TOOLS if tool["name"] == "frame-debugger-drawcall-get")
        screenshot_tool = next(tool for tool in mcp.TOOLS if tool["name"] == "frame-debugger-drawcall-screenshot")

        self.assertIn("maxResults", list_tool["inputSchema"]["properties"])
        self.assertNotIn("includeDetails", list_tool["inputSchema"]["properties"])
        self.assertIn("eventIndex", get_tool["inputSchema"]["properties"])
        self.assertEqual(get_tool["inputSchema"]["required"], ["eventIndex"])
        self.assertEqual(group_events_tool["inputSchema"]["required"], ["groupIndex"])
        self.assertEqual(drawcall_tool["inputSchema"]["required"], ["groupIndex", "drawCallIndex"])
        self.assertEqual(screenshot_tool["inputSchema"]["required"], ["groupIndex", "drawCallIndex"])
        self.assertEqual(mcp.RESPONSE_PROFILE_BY_TOOL["frame-debugger-drawcall-screenshot"], "image")

    def test_screenshot_camera_schema_accepts_path_and_instance_id(self) -> None:
        tool = next(tool for tool in mcp.TOOLS if tool["name"] == "screenshot-camera")
        properties = tool["inputSchema"]["properties"]

        self.assertIn("cameraPath", properties)
        self.assertIn("cameraInstanceId", properties)
        self.assertIn("path", properties)
        self.assertIn("instanceId", properties)
        self.assertIn("duplicate", properties["cameraPath"]["description"])
        self.assertIn("ambiguous", properties["cameraInstanceId"]["description"])

    def test_extension_manifest_tools_and_prompts_are_advertised_with_source_metadata(self) -> None:
        self.write_extension_manifest(
            [
                {
                    "id": "sample.extension",
                    "displayName": "Sample Extension",
                    "version": "0.1.0",
                    "sourceAssembly": "Sample.Extension",
                    "tools": [
                        {
                            "name": "sample-extension-inspect",
                            "description": "Descriptor-only inspection tool.",
                            "category": "extensions",
                            "inputSchema": {"type": "object", "properties": {"target": {"type": "string"}}},
                        }
                    ],
                    "prompts": [
                        {
                            "name": "sample-extension-prompt",
                            "description": "Prompt descriptor.",
                            "arguments": [{"name": "target", "required": False}],
                        }
                    ],
                }
            ]
        )

        metadata = mcp.build_tool_metadata()
        tools = {tool["name"]: tool for tool in metadata["tools"]}
        prompts = mcp.enabled_prompts()

        self.assertEqual(tools["sample-extension-inspect"]["sourceExtensionId"], "sample.extension")
        self.assertEqual(tools["sample-extension-inspect"]["sourceAssembly"], "Sample.Extension")
        self.assertRegex(tools["sample-extension-inspect"]["descriptorHash"], r"^[0-9a-f]{64}$")
        self.assertGreater(tools["sample-extension-inspect"]["estimatedTokens"], 0)
        self.assertIn("sample-extension-prompt", {prompt["name"] for prompt in prompts})

    def test_enabled_extension_tool_forwards_to_unity_bridge(self) -> None:
        self.write_extension_manifest(
            [
                {
                    "id": "sample.extension",
                    "displayName": "Sample Extension",
                    "version": "0.1.0",
                    "sourceAssembly": "Sample.Extension",
                    "tools": [
                        {
                            "name": "sample-extension-inspect",
                            "description": "Executable extension inspection tool.",
                            "category": "extensions",
                            "inputSchema": {"type": "object", "properties": {"target": {"type": "string"}}},
                        }
                    ],
                }
            ]
        )
        mcp.TOOL_SELECTION_PATH.parent.mkdir(parents=True, exist_ok=True)
        mcp.TOOL_SELECTION_PATH.write_text(
            json.dumps({"schemaVersion": mcp.TOOL_SELECTION_SCHEMA_VERSION, "enabledToolIds": ["sample-extension-inspect"]}),
            encoding="utf-8",
        )
        server = ExtensionToolBridgeServer(Path(self.temp_dir.name) / "bridge")

        result = server.call_tool({"name": "sample-extension-inspect", "arguments": {"target": "Canvas"}})

        self.assertFalse(result["isError"])
        self.assertEqual(server.calls, [("sample-extension-inspect", {"target": "Canvas"})])


if __name__ == "__main__":
    unittest.main()
