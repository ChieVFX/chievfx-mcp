import sys
import unittest
from unittest import mock
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class InstructionLayoutTests(unittest.TestCase):
    """initialize.instructions layout: header, domains, then commonly used tools in reaching-for
    order with hand-written caveman summaries."""

    def setUp(self) -> None:
        self.instructions = mcp.build_initialize_instructions()
        self.tool_lines = [line for line in self.instructions.splitlines() if line.startswith("- ")]
        self.listed = [line[2:].split("(", 1)[0] for line in self.tool_lines]

    def test_sections_are_in_order(self) -> None:
        for earlier, later in (
            ("IMPORTANT: The tool list below is truncated", "ChievFX Unity MCP is project-local"),
            ("ChievFX Unity MCP is project-local", "When calling `CallMcpTool`"),
            ("When calling `CallMcpTool`", "One domain in detail"),
            ("One domain in detail", "Domains: "),
            ("Domains: ", "Commonly used tools:"),
        ):
            self.assertLess(self.instructions.index(earlier), self.instructions.index(later), (earlier, later))

    def test_precondition_is_first_and_carries_the_literal_call(self) -> None:
        # A precondition with a trigger and the exact call is treated as configuration; a description of
        # available context is not. It leads so client truncation cannot remove it.
        self.assertTrue(self.instructions.startswith("IMPORTANT: The tool list below is truncated"))
        self.assertIn(f'ReadMcpResourceTool({{ server: "{mcp.CURSOR_SERVER_NAME}"', self.instructions)
        self.assertIn(mcp.CORE_DESCRIPTOR_INSTRUCTIONS_URI, self.instructions)
        self.assertIn('ToolSearch({ query: "select:ReadMcpResourceTool" })', self.instructions)
        # Claude Code subagents have no ReadMcpResourceTool at all, so the precondition names the tool fallback.
        precondition = self.instructions.split("ChievFX Unity MCP is project-local")[0]
        self.assertIn(mcp.MCP_RESOURCE_READ_TOOL_ID, precondition)
        # Names the read-only calls that do NOT require reading it first.
        for exempt in ("screenshot", "bridge-get-status", "console-get-logs"):
            self.assertIn(exempt, self.instructions.split("Commonly used tools:")[0])

    def test_no_extra_api_capabilities_header(self) -> None:
        # That batched header now lives only in the detailed core-descriptors body.
        self.assertNotIn("Extra API capabilities", self.instructions)

    def test_priority_tools_come_first_in_declared_order(self) -> None:
        present = [name for name in mcp.TOOL_ORDER_TOP if name in self.listed]
        self.assertTrue(present, "expected some priority tools to be advertised")
        self.assertEqual(self.listed[: len(present)], present)

    def test_deprioritized_tools_come_last_in_declared_order(self) -> None:
        # Past the budget they are the first to drop out, so none may be listed at all.
        present = [name for name in mcp.TOOL_ORDER_BOTTOM if name in self.listed]
        if present:
            self.assertEqual(self.listed[-len(present) :], present)

    def test_instructions_fit_the_client_budget(self) -> None:
        # Claude Code cuts initialize.instructions at 4096 characters; the model never sees the rest.
        self.assertLessEqual(len(self.instructions), mcp.INSTRUCTIONS_CHAR_BUDGET)
        self.assertLessEqual(mcp.INSTRUCTIONS_CHAR_BUDGET, 4096)

    def test_tools_past_the_budget_are_named_as_left_out(self) -> None:
        listed = mcp.build_enabled_descriptor_instructions(budget=10**6).splitlines()
        budgeted = mcp.build_enabled_descriptor_instructions(budget=600).splitlines()
        self.assertLess(len(budgeted), len(listed))
        self.assertLessEqual(len("\n".join(budgeted)), 600)
        left_out = len(listed) - (len(budgeted) - 1)
        self.assertEqual(budgeted[-1], mcp._omitted_tools_line(left_out))
        self.assertIn(mcp.CORE_DESCRIPTOR_INSTRUCTIONS_URI, budgeted[-1])
        self.assertEqual(budgeted[:-1], listed[: len(budgeted) - 1])

    def test_curated_summaries_are_used_verbatim(self) -> None:
        # The point of curating them is to avoid truncating a real description mid-word.
        for line in self.tool_lines:
            name = line[2:].split("(", 1)[0]
            curated = mcp.TOOL_SHORT_SUMMARIES.get(name)
            if curated:
                self.assertTrue(line.endswith(f": {curated}"), line)
                self.assertNotIn("…", line.rsplit(": ", 1)[-1])

    def test_applied_role_is_named(self) -> None:
        state = {"kind": "custom", "roleId": "madout-gameai-playtest", "displayName": "GameAI playtest", "manualOverride": False}
        with mock.patch.object(mcp, "load_tool_role_state", return_value=state), mock.patch.object(
            mcp, "manual_tool_resource_selection_enabled", return_value=True
        ):
            line = mcp.build_active_role_line()
        self.assertTrue(line.startswith("Tool role: GameAI playtest - "), line)
        self.assertIn("tools are on", line)
        # With manual selection off the saved role is not in force: every non-hidden tool is on.
        with mock.patch.object(mcp, "load_tool_role_state", return_value=state), mock.patch.object(
            mcp, "manual_tool_resource_selection_enabled", return_value=False
        ):
            self.assertEqual(mcp.build_active_role_line(), "")
        with mock.patch.object(mcp, "load_tool_role_state", return_value={**state, "manualOverride": True}):
            self.assertEqual(mcp.build_active_role_line(), "")
        with mock.patch.object(mcp, "load_tool_role_state", return_value={"kind": "manual"}):
            self.assertEqual(mcp.build_active_role_line(), "")

    def test_core_descriptors_carry_every_argument_a_tool_accepts(self) -> None:
        # The instructions call core-descriptors the complete list: the token-saving tools/list schema dropped arguments
        # (screenshot-editor-window captureArea) that only the short startup list showed.
        body = mcp.build_core_descriptor_instructions_resource_body()
        lines = {line[2:].split(":", 1)[0]: line for line in body.splitlines() if line.startswith("- ")}
        self.assertIn("captureArea?:", lines["screenshot-editor-window"])
        self.assertIn("delayFrames?:", lines["screenshot-editor-window"])
        self.assertIn("verbose?:bool", lines["bridge-get-status"])
        self.assertNotIn("outputFormat", body)
        # Old alias names stay accepted but are shown nowhere.
        line = mcp.format_full_tool_descriptor_line(
            {"name": "editor-playmode-set", "description": "Play.", "inputSchema": {"type": "object", "properties": {"isPlaying": {"type": "boolean"}, "playing": {"type": "boolean"}}}}
        )
        self.assertIn("isPlaying?:bool", line)
        self.assertNotIn("playing?:", line.replace("isPlaying?:", ""))

    def test_resources_are_not_advertised(self) -> None:
        # Only core-descriptors and the categories template are named; the budget goes to tools.
        self.assertNotIn("chievfx://editor/context", self.instructions)
        self.assertNotIn("chievfx://scene/opened", self.instructions)
        self.assertIn("chievfx://instructions/core-descriptors", self.instructions)
        self.assertIn("chievfx://categories/<domain>", self.instructions)


if __name__ == "__main__":
    unittest.main()
