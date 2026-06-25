import json
import sys
import tempfile
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402

AUTONOMOUS_TOOLS = {
    "tools-list-categories",
    "tools-list-category",
    "tools-set-enabled-state",
    "tools-get-roles",
    "tools-get-role",
    "tools-set-role",
}


class ToolRoleTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)
        self.root = Path(self.temp_dir.name)
        self.selection_path = self.root / "UserSettings" / "ChievfxMcpToolSelection.json"
        self.extension_manifest_path = self.root / "Library" / "ChievfxMcpBridge" / "extension-capabilities.snapshot.json"
        self.original_selection_path = mcp.TOOL_SELECTION_PATH
        self.original_extension_manifest_path = mcp.EXTENSION_CAPABILITY_MANIFEST_PATH
        self.original_project_root = mcp.PROJECT_ROOT
        mcp.TOOL_SELECTION_PATH = self.selection_path
        mcp.EXTENSION_CAPABILITY_MANIFEST_PATH = self.extension_manifest_path
        mcp.PROJECT_ROOT = self.root
        self.addCleanup(self.restore_paths)
        self.server = mcp.McpServer("http://127.0.0.1:1", str(self.root / "bridge"), timeout_ms=1000)
        mcp.configure_extension_manifest_bridge_fetcher(None)

    def restore_paths(self) -> None:
        mcp.TOOL_SELECTION_PATH = self.original_selection_path
        mcp.EXTENSION_CAPABILITY_MANIFEST_PATH = self.original_extension_manifest_path
        mcp.PROJECT_ROOT = self.original_project_root

    def request(self, method: str, params: dict[str, object] | None = None) -> dict[str, object]:
        response = self.server.handle_message({"jsonrpc": "2.0", "id": 1, "method": method, "params": params or {}})
        self.assertIsInstance(response, dict)
        return response

    def enable_autonomous_tools(self) -> None:
        enabled_ids = mcp.load_enabled_tool_ids() | AUTONOMOUS_TOOLS
        mcp.save_enabled_tool_ids(enabled_ids)

    def call_tool(self, name: str, arguments: dict[str, object] | None = None) -> dict[str, object]:
        payload = arguments or {}
        batched_tool = payload.get("tool") if isinstance(payload, dict) else None
        if name in AUTONOMOUS_TOOLS or batched_tool in AUTONOMOUS_TOOLS:
            self.enable_autonomous_tools()
        return self.request("tools/call", {"name": name, "arguments": payload})

    def call_json_tool(self, name: str, arguments: dict[str, object] | None = None) -> dict[str, object]:
        payload = dict(arguments or {})
        payload["outputFormat"] = "json"
        response = self.call_tool(name, payload)
        return json.loads(response["result"]["content"][0]["text"])

    def test_all_non_autonomous_tools_are_default_without_saved_selection(self) -> None:
        tools = self.request("tools/list")["result"]["tools"]
        names = {tool["name"] for tool in tools}
        role_state = mcp.load_tool_role_state()

        self.assertFalse(self.selection_path.exists())
        self.assertEqual(role_state["kind"], "manual")
        self.assertFalse(role_state["manualOverride"])
        self.assertIn("scene-open", names)
        self.assertIn("gameobject-find", names)
        self.assertIn("prefab-open", names)
        self.assertIn("tests-run", names)
        # Every non-autonomous tool is enabled out of the box...
        self.assertIn("profiler-get-state", names)
        # ...while the autonomy/discovery helpers stay hidden by default.
        for autonomous_tool in AUTONOMOUS_TOOLS:
            self.assertNotIn(autonomous_tool, names)

    def test_builtin_role_applies_deterministically_and_filters_tools_list(self) -> None:
        result = self.call_json_tool("tools-set-role", {"role": "qa"})
        tools = self.request("tools/list")["result"]["tools"]
        names = {tool["name"] for tool in tools}

        self.assertTrue(result["mutated"])
        self.assertEqual(result["roleState"]["roleId"], "qa")
        self.assertIn("tests-run", names)
        self.assertNotIn("tools-set-role", names)
        self.assertIn("gameobject-find", names)
        self.assertNotIn("prefab-open", names)

    def test_required_tools_remain_enabled_when_role_is_narrow(self) -> None:
        self.call_tool("tools-set-role", {"role": "qa"})
        enabled_ids = mcp.load_enabled_tool_ids()

        known_tool_ids = {tool["name"] for tool in mcp.all_tools()}
        required_tool_ids = mcp.load_required_tool_ids() & known_tool_ids
        for required_tool_id in required_tool_ids:
            self.assertIn(required_tool_id, enabled_ids)

    def test_autonomous_tools_can_be_disabled(self) -> None:
        autonomous_tools = [
            "tools-list-categories",
            "tools-list-category",
            "tools-set-enabled-state",
            "tools-get-roles",
            "tools-get-role",
            "tools-set-role",
        ]

        result = self.call_json_tool("tools-set-enabled-state", {"category": "Autonomous", "enabled": False})

        self.assertTrue(result["mutated"])
        self.assertEqual(result["requestedEnabled"], False)
        enabled_tool_ids = mcp.load_enabled_tool_ids()
        for tool_id in autonomous_tools:
            self.assertNotIn(tool_id, enabled_tool_ids)

    def test_essentials_are_always_on(self) -> None:
        inventory = self.call_json_tool("tools-list-category", {"category": "Essentials"})
        essentials = inventory["categories"][0]

        self.assertTrue(essentials["requiredOnly"])
        self.assertEqual(essentials["optionalToolCount"], 0)
        self.assertEqual(essentials["state"], "required-only")
        self.assertIn("events-check-since", {tool["name"] for tool in essentials["tools"] if tool["required"]})
        self.assertIn("tool-batch", {tool["name"] for tool in essentials["tools"] if tool["required"]})

        result = self.call_json_tool("tools-set-enabled-state", {"category": "Essentials", "enabled": False})

        self.assertFalse(result["mutated"])
        self.assertTrue(all(row["reason"] == "required-tool-locked-enabled" for row in result["skipped"]))
        self.assertIn("events-check-since", mcp.load_enabled_tool_ids())
        self.assertIn("tool-batch", mcp.load_enabled_tool_ids())

    def test_tool_policy_locks_all_essentials(self) -> None:
        policy = json.loads(mcp.TOOL_POLICY_PATH.read_text(encoding="utf-8"))
        required_tool_ids = set(policy["requiredToolIds"])
        essentials_tool_ids = {
            tool["name"]
            for tool in mcp.build_tool_metadata()["tools"]
            if tool["category"] == "Essentials"
        }

        self.assertEqual(set(), essentials_tool_ids - required_tool_ids)

    def test_tools_list_categories_default_output_is_compact(self) -> None:
        text = self.call_tool("tools-list-categories")["result"]["content"][0]["text"]

        self.assertIn("enabled:", text)
        self.assertIn("- Essentials (", text)
        self.assertIn("Always-on safe basics", text)
        self.assertNotIn("disabled:", text)
        self.assertNotIn("descriptorPreview", text)
        self.assertNotIn("extensions[", text)
        self.assertNotIn("roles[", text)

    def test_tool_batch_runs_one_tool_for_many_items(self) -> None:
        text = self.call_tool(
            "tool-batch",
            {
                "tool": "tools-get-role",
                "items": [{"roleIndex": 1}, {"roleIndex": 2}],
            },
        )["result"]["content"][0]["text"]

        self.assertEqual(text, "tool:tools-get-role success:true successCount:2 failedCount:0 totalCount:2")

    def test_tool_batch_reports_item_failures_compactly(self) -> None:
        text = self.call_tool(
            "tool-batch",
            {
                "tool": "tools-get-role",
                "items": [{"roleIndex": 1}, {"roleIndex": 999}],
            },
        )["result"]["content"][0]["text"]

        self.assertIn("tool:tools-get-role success:false successCount:1 failedCount:1 totalCount:2", text)
        self.assertIn("index:1", text)
        self.assertIn("error:", text)

    def test_tools_list_categories_can_include_disabled(self) -> None:
        self.call_tool("tools-set-enabled-state", {"category": "Profiler", "enabled": False})
        text = self.call_tool("tools-list-categories", {"includeDisabled": True})["result"]["content"][0]["text"]

        self.assertIn("enabled:", text)
        self.assertIn("disabled:", text)
        self.assertIn("- Profiler (0/5)", text)

    def test_tools_list_category_default_output_is_compact(self) -> None:
        self.call_tool("tools-set-role", {"role": "developer"})
        text = self.call_tool("tools-list-category", {"category": "GameObject"})["result"]["content"][0]["text"]

        self.assertIn("enabled:", text)
        self.assertIn("- gameobject-find (path, name, namePattern, componentType, instanceId, includeInactive, includeDetails, includeComponents, maxResults)", text)
        self.assertIn("Finds GameObjects by filters", text)
        self.assertNotIn("GameObject (", text)
        self.assertNotIn("disabled:", text)
        self.assertNotIn("descriptorPreview", text)
        self.assertNotIn("estimatedTokens", text)

    def test_tools_list_category_can_include_disabled(self) -> None:
        self.call_tool("tools-set-enabled-state", {"category": "Profiler", "enabled": False})
        text = self.call_tool("tools-list-category", {"category": "Profiler", "includeDisabled": True})["result"]["content"][0]["text"]

        self.assertIn("enabled:", text)
        self.assertIn("disabled:", text)
        self.assertIn("- profiler-get-state", text)

    def test_tools_list_disabled_category_still_shows_enabled_section(self) -> None:
        self.call_tool("tools-set-enabled-state", {"category": "Profiler", "enabled": False})
        text = self.call_tool("tools-list-category", {"category": "Profiler", "includeDisabled": True})["result"]["content"][0]["text"]

        self.assertIn("enabled:\n- none", text)
        self.assertIn("disabled:", text)

    def test_disabled_tool_call_returns_clear_error(self) -> None:
        self.call_tool("tools-set-role", {"role": "qa"})

        response = self.call_tool("prefab-open", {})

        self.assertIn("error", response)
        self.assertIn("disabled", response["error"]["message"])
        self.assertIn("MCP Tools", response["error"]["message"])

    def test_manual_tool_edit_marks_role_modified(self) -> None:
        self.call_tool("tools-set-role", {"role": "qa"})
        self.call_tool("tools-set-enabled-state", {"tool": "prefab-open", "enabled": True})

        state = mcp.load_tool_role_state()

        self.assertEqual(state["roleId"], "qa")
        self.assertTrue(state["manualOverride"])

    def test_get_roles_returns_only_titles_and_descriptions(self) -> None:
        result = self.call_json_tool("tools-get-roles", {"includeTools": True})

        self.assertEqual(
            result,
            {
                "roles": [
                    {"index": 1, "title": "Developer", "description": "Builder mode: scene, objects, prefabs, tests, and sharp tools in one belt."},
                    {"index": 2, "title": "QA", "description": "Bug net: inspect scenes, run tests, and poke windows without opening the toolbox volcano."},
                ],
            },
        )

    def test_get_roles_default_output_is_readable(self) -> None:
        text = self.call_tool("tools-get-roles", {"includeTools": True})["result"]["content"][0]["text"]

        self.assertIn("1. Developer: Builder mode:", text)
        self.assertNotIn("enabledCategoryIds", text)
        self.assertNotIn("enabledToolIds", text)
        self.assertNotIn("counts", text)

    def test_get_role_returns_tools_grouped_by_category(self) -> None:
        result = self.call_json_tool("tools-get-role", {"roleIndex": 2})
        categories = {category["name"]: category["tools"] for category in result["categories"]}

        self.assertEqual(result["role"]["title"], "QA")
        self.assertIn("Scene", categories)
        self.assertIn("GameObject", categories)
        self.assertIn("Script Execution / Tests", categories)
        self.assertIn("tests-run", categories["Script Execution / Tests"])
        self.assertIn("gameobject-find", categories["GameObject"])

        text = self.call_tool("tools-get-role", {"roleIndex": 2})["result"]["content"][0]["text"]
        self.assertIn("Role 2: QA", text)
        self.assertIn("- Script Execution / Tests\n  - script-execute", text)

    def test_set_role_accepts_role_index(self) -> None:
        result = self.call_json_tool("tools-set-role", {"roleIndex": 2})
        tools = self.request("tools/list")["result"]["tools"]
        names = {tool["name"] for tool in tools}

        self.assertEqual(result["roleState"]["roleId"], "qa")
        self.assertIn("tests-run", names)
        self.assertNotIn("prefab-open", names)

    def test_set_role_default_output_matches_get_role_detail(self) -> None:
        text = self.call_tool("tools-set-role", {"roleIndex": 2})["result"]["content"][0]["text"]

        self.assertEqual(text, "success")
        self.assertNotIn("- Scene\n  - scene-list-available", text)
        self.assertNotIn("appliedEnabledToolIds", text)
        self.assertNotIn("counts", text)

    def test_set_enabled_state_default_output_is_compact(self) -> None:
        self.call_tool("tools-set-enabled-state", {"tool": "profiler-get-state", "enabled": False})
        text = self.call_tool("tools-set-enabled-state", {"tool": "profiler-get-state", "enabled": True})["result"]["content"][0]["text"]

        self.assertEqual(text, "success")
        self.assertNotIn("selectionPath", text)
        self.assertNotIn("counts", text)

    def test_set_enabled_state_no_change_mentions_reason(self) -> None:
        self.call_tool("tools-set-enabled-state", {"category": "Profiler", "enabled": False})
        text = self.call_tool("tools-set-enabled-state", {"category": "Profiler", "enabled": False})["result"]["content"][0]["text"]

        self.assertEqual(text, "success: 5 tools already disabled")

    def test_set_enabled_state_locked_required_tool_is_failure(self) -> None:
        text = self.call_tool("tools-set-enabled-state", {"tool": "events-wait", "enabled": False})["result"]["content"][0]["text"]

        self.assertEqual(text, "failure: events-wait required-tool-locked-enabled")

    def test_custom_role_asset_loads_and_applies(self) -> None:
        role_path = self.root / "Assets" / "ChievfxMcp" / "Roles" / "QaTiny.asset"
        role_path.parent.mkdir(parents=True, exist_ok=True)
        role_path.write_text(
            "\n".join(
                [
                    "%YAML 1.1",
                    "--- !u!114 &11400000",
                    "MonoBehaviour:",
                    "  m_Name: QaTiny",
                    "  roleId: qa-tiny",
                    "  displayName: QA Tiny",
                    "  description: Small custom QA role.",
                    "  enabledCategoryIds:",
                    "  - Scene",
                    "  enabledToolIds:",
                    "  - tests-run",
                ]
            ),
            encoding="utf-8",
        )

        result = self.call_json_tool("tools-set-role", {"customAssetPath": "Assets/ChievfxMcp/Roles/QaTiny.asset"})
        tools = self.request("tools/list")["result"]["tools"]
        names = {tool["name"] for tool in tools}

        self.assertEqual(result["roleState"]["roleId"], "qa-tiny")
        self.assertEqual(result["roleState"]["customAssetPath"], "Assets/ChievfxMcp/Roles/QaTiny.asset")
        self.assertIn("scene-open", names)
        self.assertIn("tests-run", names)
        self.assertNotIn("gameobject-find", names)


if __name__ == "__main__":
    unittest.main()
