import json
import sys
import tempfile
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class PromptServer(mcp.McpServer):
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
        return {
            "ok": True,
            "result": {
                "description": "Dynamic test prompt.",
                "messages": [
                    {
                        "role": "user",
                        "content": {
                            "type": "text",
                            "text": f"dynamic {arguments['name']} {arguments['arguments'].get('focus', '')}",
                        },
                    }
                ],
            },
        }


class PromptTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)
        self.bridge_dir = Path(self.temp_dir.name) / "bridge"
        self.selection_path = Path(self.temp_dir.name) / "UserSettings" / "ChievfxMcpPromptSelection.json"
        self.extension_manifest_path = Path(self.temp_dir.name) / "Library" / "ChievfxMcpBridge" / "extension-capabilities.json"
        self.original_selection_path = mcp.PROMPT_SELECTION_PATH
        self.original_extension_manifest_path = mcp.EXTENSION_CAPABILITY_MANIFEST_PATH
        mcp.PROMPT_SELECTION_PATH = self.selection_path
        mcp.EXTENSION_CAPABILITY_MANIFEST_PATH = self.extension_manifest_path
        self.addCleanup(self.restore_paths)
        self.server = PromptServer(self.bridge_dir)

    def restore_paths(self) -> None:
        mcp.PROMPT_SELECTION_PATH = self.original_selection_path
        mcp.EXTENSION_CAPABILITY_MANIFEST_PATH = self.original_extension_manifest_path

    def write_selection(self, enabled_prompt_names: list[str]) -> None:
        self.selection_path.parent.mkdir(parents=True, exist_ok=True)
        self.selection_path.write_text(
            json.dumps(
                {
                    "schemaVersion": mcp.PROMPT_SELECTION_SCHEMA_VERSION,
                    "enabledPromptNames": enabled_prompt_names,
                }
            ),
            encoding="utf-8",
        )

    def enable_all_prompts(self) -> None:
        self.write_selection([prompt["name"] for prompt in mcp.all_prompts()])

    def write_extension_manifest(self, extensions: list[dict[str, object]]) -> None:
        self.extension_manifest_path.parent.mkdir(parents=True, exist_ok=True)
        self.extension_manifest_path.write_text(
            json.dumps(
                {
                    "schemaVersion": mcp.EXTENSION_CAPABILITY_MANIFEST_SCHEMA_VERSION,
                    "extensions": extensions,
                }
            ),
            encoding="utf-8",
        )

    def request(self, method: str, params: dict[str, object] | None = None) -> dict[str, object]:
        response = self.server.handle_message({"jsonrpc": "2.0", "id": 1, "method": method, "params": params or {}})
        self.assertIsInstance(response, dict)
        return response

    def test_initialize_advertises_prompts_when_enabled(self) -> None:
        self.write_selection(["unity-scene-review"])

        response = self.request("initialize", {"protocolVersion": "2024-11-05"})

        capabilities = response["result"]["capabilities"]
        self.assertEqual(capabilities["prompts"], {})
        self.assertIn("tools", capabilities)
        self.assertIn("resources", capabilities)

    def test_initialize_omits_prompts_by_default(self) -> None:
        response = self.request("initialize", {"protocolVersion": "2024-11-05"})

        self.assertNotIn("prompts", response["result"]["capabilities"])

    def test_prompts_list_and_get_static_prompt(self) -> None:
        self.write_selection(["unity-scene-review"])

        prompts = self.request("prompts/list")["result"]["prompts"]
        result = self.request("prompts/get", {"name": "unity-scene-review", "arguments": {"goal": "wire UI"}})["result"]

        prompt = {item["name"]: item for item in prompts}["unity-scene-review"]
        self.assertEqual(prompt["title"], "Review current Unity scene work")
        self.assertIn("arguments", prompt)
        self.assertEqual(result["messages"][0]["role"], "user")
        self.assertEqual(result["messages"][0]["content"]["type"], "text")
        self.assertIn("wire UI", result["messages"][0]["content"]["text"])
        self.assertEqual(self.server.calls, [])

    def test_prompts_list_advertises_shader_prompts(self) -> None:
        self.enable_all_prompts()

        prompts = {item["name"]: item for item in self.request("prompts/list")["result"]["prompts"]}
        expected_shader_prompts = {
            "unity-shader-built-in-draft",
            "unity-shader-urp-draft",
            "unity-shader-hdrp-draft",
            "unity-shader-graph-plan",
            "unity-material-profile-review",
        }

        self.assertTrue(expected_shader_prompts.issubset(prompts))
        for name in expected_shader_prompts:
            self.assertEqual(prompts[name]["category"], "Shader")

        built_in_arguments = {item["name"]: item for item in prompts["unity-shader-built-in-draft"]["arguments"]}
        self.assertTrue(built_in_arguments["goal"]["required"])
        self.assertFalse(built_in_arguments["shaderName"]["required"])
        self.assertFalse(built_in_arguments["context"]["required"])

    def test_shader_prompt_get_uses_static_scalar_arguments(self) -> None:
        self.write_selection(["unity-shader-urp-draft"])

        result = self.request(
            "prompts/get",
            {
                "name": "unity-shader-urp-draft",
                "arguments": {
                    "goal": "rim-lit hologram",
                    "shaderName": "FX/Hologram",
                    "context": "mobile URP",
                },
            },
        )["result"]

        text = result["messages"][0]["content"]["text"]
        self.assertIn("rim-lit hologram", text)
        self.assertIn("FX/Hologram", text)
        self.assertIn("URP package/version", text)
        self.assertIn("chievfx://editor/context", text)
        self.assertEqual(self.server.calls, [])

    def test_shader_prompt_get_fills_optional_args_without_bridge_call(self) -> None:
        self.write_selection(["unity-shader-graph-plan"])

        result = self.request(
            "prompts/get",
            {"name": "unity-shader-graph-plan", "arguments": {"goal": "dissolve edge"}},
        )["result"]

        text = result["messages"][0]["content"]["text"]
        self.assertIn("dissolve edge", text)
        self.assertIn("Target render pipeline:", text)
        self.assertIn("Do not write .shadergraph JSON directly", text)
        self.assertEqual(self.server.calls, [])

    def test_dynamic_prompt_forwards_hidden_bridge_command(self) -> None:
        self.write_selection(["unity-editor-context"])

        result = self.request(
            "prompts/get",
            {"name": "unity-editor-context", "arguments": {"focus": "selection"}},
        )["result"]

        self.assertEqual(
            self.server.calls,
            [("prompt-get", {"name": "unity-editor-context", "arguments": {"focus": "selection"}})],
        )
        self.assertIn("dynamic unity-editor-context selection", result["messages"][0]["content"]["text"])

    def test_disabled_prompt_is_not_listed_or_fetchable(self) -> None:
        self.write_selection(["unity-editor-context"])

        prompts = self.request("prompts/list")["result"]["prompts"]
        response = self.request("prompts/get", {"name": "unity-scene-review", "arguments": {"goal": "wire UI"}})

        self.assertNotIn("unity-scene-review", {prompt["name"] for prompt in prompts})
        self.assertEqual(response["error"]["code"], -32003)

    def test_disabled_shader_prompt_is_not_listed_or_fetchable(self) -> None:
        self.write_selection(["unity-editor-context", "unity-scene-review"])

        prompts = self.request("prompts/list")["result"]["prompts"]
        response = self.request("prompts/get", {"name": "unity-shader-hdrp-draft", "arguments": {"goal": "water"}})

        self.assertNotIn("unity-shader-hdrp-draft", {prompt["name"] for prompt in prompts})
        self.assertEqual(response["error"]["code"], -32003)

    def test_extension_slowmo_prompt_renders_literal_csharp_braces(self) -> None:
        self.write_extension_manifest(
            [
                {
                    "id": "chievfx.cameras",
                    "displayName": "ChievFX MCP Cameras",
                    "version": "1.0.0",
                    "prompts": [
                        {
                            "name": "gamefeel-ending-session-slowmo",
                            "title": "Guide ending-session slow motion",
                            "description": "Prompt-only slow motion guidance.",
                            "category": "Game Feel",
                            "arguments": [{"name": "goal", "required": False}],
                            "staticText": (
                                "Goal: {goal}\n"
                                "```csharp\n"
                                "public void StartEndingSlowMo()\n"
                                "{{\n"
                                "    Time.timeScale = 0.15f;\n"
                                "    if (adjustFixedDeltaTime)\n"
                                "    {{\n"
                                "        Time.fixedDeltaTime = savedFixedDeltaTime * Time.timeScale;\n"
                                "    }}\n"
                                "}}\n"
                                "```\n"
                                "Drive AudioSource.pitch or AudioMixer snapshots, and choose AudioMixer.updateMode / AudioMixerUpdateMode deliberately.\n"
                            ),
                        }
                    ],
                }
            ]
        )
        self.write_selection(["gamefeel-ending-session-slowmo"])

        prompts = {item["name"]: item for item in self.request("prompts/list")["result"]["prompts"]}
        result = self.request(
            "prompts/get",
            {"name": "gamefeel-ending-session-slowmo", "arguments": {"goal": "victory zoom"}},
        )["result"]

        text = result["messages"][0]["content"]["text"]
        self.assertEqual(prompts["gamefeel-ending-session-slowmo"]["category"], "Game Feel")
        self.assertIn("Goal: victory zoom", text)
        self.assertIn("public void StartEndingSlowMo()\n{", text)
        self.assertIn("if (adjustFixedDeltaTime)\n    {", text)
        self.assertIn("Time.fixedDeltaTime = savedFixedDeltaTime * Time.timeScale;", text)
        self.assertIn("AudioMixer.updateMode / AudioMixerUpdateMode", text)
        self.assertNotIn("{{", text)
        self.assertEqual(self.server.calls, [])

        self.write_selection(["unity-editor-context"])
        disabled_prompts = {item["name"] for item in self.request("prompts/list")["result"]["prompts"]}
        disabled_response = self.request(
            "prompts/get",
            {"name": "gamefeel-ending-session-slowmo", "arguments": {"goal": "victory zoom"}},
        )

        self.assertNotIn("gamefeel-ending-session-slowmo", disabled_prompts)
        self.assertEqual(disabled_response["error"]["code"], -32003)

    def test_required_extension_prompt_stays_enabled_with_empty_selection(self) -> None:
        self.write_extension_manifest(
            [
                {
                    "id": "chievfx.diagnostics",
                    "displayName": "ChievFX MCP Diagnostics",
                    "version": "1.0.0",
                    "sourceAssembly": "Chievfx.Mcp.Diagnostics",
                    "prompts": [
                        {
                            "name": "sample-required-review",
                            "title": "Summarize diagnostics",
                            "description": "Diagnostic prompt.",
                            "category": "Review",
                            "required": True,
                            "staticText": "diagnostics active",
                            "arguments": [],
                        }
                    ],
                }
            ]
        )
        self.write_selection([])

        prompts = self.request("prompts/list")["result"]["prompts"]
        result = self.request("prompts/get", {"name": "sample-required-review"})["result"]

        self.assertIn("sample-required-review", {prompt["name"] for prompt in prompts})
        self.assertEqual(result["messages"][0]["content"]["text"], "diagnostics active")

    def test_extension_manifest_rejects_core_prompt_name_collision(self) -> None:
        self.write_extension_manifest(
            [
                {
                    "id": "bad.extension",
                    "displayName": "Bad",
                    "prompts": [
                        {
                            "name": "unity-scene-review",
                            "title": "Bad duplicate",
                            "description": "Should be rejected.",
                            "arguments": [],
                        }
                    ],
                }
            ]
        )

        metadata = mcp.build_prompt_metadata()

        self.assertEqual(1, sum(prompt["name"] == "unity-scene-review" for prompt in metadata["prompts"]))
        self.assertTrue(any("name collision unity-scene-review" in error for error in metadata["extensionErrors"]))

    def test_missing_required_argument_returns_invalid_params(self) -> None:
        self.write_selection(["unity-scene-review"])

        response = self.request("prompts/get", {"name": "unity-scene-review", "arguments": {}})

        self.assertEqual(response["error"]["code"], -32602)
        self.assertIn("Missing required argument", response["error"]["message"])

    def test_shader_prompt_missing_required_argument_returns_invalid_params(self) -> None:
        self.write_selection(["unity-material-profile-review"])

        response = self.request("prompts/get", {"name": "unity-material-profile-review", "arguments": {"focus": "mobile"}})

        self.assertEqual(response["error"]["code"], -32602)
        self.assertIn("goal", response["error"]["message"])

    def test_malformed_arguments_return_invalid_params(self) -> None:
        self.write_selection(["unity-scene-review"])

        response = self.request("prompts/get", {"name": "unity-scene-review", "arguments": []})

        self.assertEqual(response["error"]["code"], -32602)
        self.assertIn("arguments", response["error"]["message"])


if __name__ == "__main__":
    unittest.main()
