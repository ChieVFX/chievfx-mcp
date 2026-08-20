import sys
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class SecondaryProjectLabelTests(unittest.TestCase):
    """CHIEVFX_MCP_SERVER_LABEL marks a server the editor injected for ANOTHER Unity project, so an
    agent holding two editors can tell them apart. It leads the instructions; without it nothing about
    the primary project's instructions changes."""

    def setUp(self) -> None:
        self.original_label = mcp.SERVER_LABEL
        self.addCleanup(self._restore_label)

    def _restore_label(self) -> None:
        mcp.SERVER_LABEL = self.original_label

    def test_primary_project_starts_with_the_descriptor_precondition(self) -> None:
        mcp.SERVER_LABEL = ""
        self.assertTrue(
            mcp.build_initialize_instructions().startswith("IMPORTANT: The tool list below is truncated")
        )

    def test_label_leads_and_the_precondition_follows_it(self) -> None:
        label = "SECONDARY Unity project: urp-sample at /tmp/urp-sample."
        mcp.SERVER_LABEL = label
        instructions = mcp.build_initialize_instructions()
        self.assertTrue(instructions.startswith(label), instructions[:200])
        # Nothing is dropped to make room: the precondition is still there, immediately after.
        self.assertIn("IMPORTANT: The tool list below is truncated", instructions)
        self.assertLess(
            instructions.index(label),
            instructions.index("IMPORTANT: The tool list below is truncated"),
        )

    def test_label_is_the_only_difference(self) -> None:
        mcp.SERVER_LABEL = ""
        primary = mcp.build_initialize_instructions()
        label = "SECONDARY Unity project: builtin-sample at /tmp/builtin-sample."
        mcp.SERVER_LABEL = label
        secondary = mcp.build_initialize_instructions()
        self.assertEqual(secondary, f"{label}\n{primary}")

    def test_blank_label_is_not_emitted(self) -> None:
        # The editor never writes a blank label, but a hand-edited config might; a leading empty line
        # would push the precondition off the top for nothing.
        mcp.SERVER_LABEL = "   "
        self.assertTrue(
            mcp.build_initialize_instructions().startswith("IMPORTANT: The tool list below is truncated")
        )


if __name__ == "__main__":
    unittest.main()
