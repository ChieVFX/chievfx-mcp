import sys
import unittest
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
        present = [name for name in mcp.TOOL_ORDER_BOTTOM if name in self.listed]
        self.assertTrue(present, "expected some deprioritized tools to be advertised")
        self.assertEqual(self.listed[-len(present) :], present)

    def test_curated_summaries_are_used_verbatim(self) -> None:
        # The point of curating them is to avoid truncating a real description mid-word.
        for line in self.tool_lines:
            name = line[2:].split("(", 1)[0]
            curated = mcp.TOOL_SHORT_SUMMARIES.get(name)
            if curated:
                self.assertTrue(line.endswith(f": {curated}"), line)
                self.assertNotIn("…", line.rsplit(": ", 1)[-1])

    def test_resources_are_not_advertised(self) -> None:
        # Only core-descriptors and the categories template are named; the budget goes to tools.
        self.assertNotIn("chievfx://editor/context", self.instructions)
        self.assertNotIn("chievfx://scene/opened", self.instructions)
        self.assertIn("chievfx://instructions/core-descriptors", self.instructions)
        self.assertIn("chievfx://categories/<domain>", self.instructions)


if __name__ == "__main__":
    unittest.main()
