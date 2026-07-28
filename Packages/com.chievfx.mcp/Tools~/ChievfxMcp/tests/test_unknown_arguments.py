import json
import sys
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class UnknownArgumentTests(unittest.TestCase):
    """Unknown arguments used to be dropped silently, sending callers hunting for an effect that never
    happened (outputPath vs savePath on screenshot-game-view)."""

    def test_misspelled_argument_is_reported_with_suggestion(self) -> None:
        unknown = mcp.unknown_tool_arguments("screenshot-game-view", {"outputPath": "/tmp/x.png"})
        self.assertEqual(unknown, ["outputPath"])
        message = mcp.describe_unknown_tool_arguments("screenshot-game-view", unknown)
        self.assertIn("outputPath", message)
        self.assertIn("savePath", message)

    def test_declared_and_universal_arguments_are_silent(self) -> None:
        for arguments in (
            {"savePath": "/tmp/x.png", "maxDimension": 320},
            {"outputFormat": "json"},
            {"timeoutMs": 5000},
            {},
        ):
            self.assertEqual(mcp.unknown_tool_arguments("screenshot-game-view", arguments), [], arguments)

    def test_honored_aliases_are_never_called_unrecognized(self) -> None:
        # The editor acts on these even though only the canonical name is in the schema; claiming they
        # had no effect would be a lie.
        for tool, arguments in (
            ("editor-playmode-set", {"play": True}),
            ("editor-playmode-set", {"playing": True}),
            ("ui-control-find", {"query": "Btn"}),
            ("ui-runtime-drag", {"x": 1, "y": 2, "toX": 3, "toY": 4}),
            ("ui-runtime-click", {"normalized": {"x": 0.5, "y": 0.5}}),
            ("bridge-get-operation", {"operationId": "abc"}),
        ):
            self.assertEqual(mcp.unknown_tool_arguments(tool, arguments), [], (tool, arguments))

    def test_suggestion_only_when_plausible(self) -> None:
        self.assertIn("maxEntries", mcp.describe_unknown_tool_arguments("console-get-logs", ["maxEntires"]))
        self.assertNotIn("did you mean", mcp.describe_unknown_tool_arguments("console-get-logs", ["banana"]))

    def test_warning_prepends_to_human_text(self) -> None:
        result = {"content": [{"type": "text", "text": "captured"}], "isError": False}
        out = mcp.with_unknown_argument_warning(result, "screenshot-game-view", {"outputPath": "x"})
        self.assertTrue(out["content"][0]["text"].startswith("! Unrecognized"))
        self.assertIn("captured", out["content"][0]["text"])

    def test_warning_never_corrupts_json_output(self) -> None:
        payload = {"pngWidth": 320}
        result = {"content": [{"type": "text", "text": json.dumps(payload)}], "isError": False}
        out = mcp.with_unknown_argument_warning(result, "screenshot-game-view", {"outputPath": "x"})
        # First block must still parse as JSON; the warning goes in its own block.
        self.assertEqual(json.loads(out["content"][0]["text"]), payload)
        self.assertTrue(any("Unrecognized" in block.get("text", "") for block in out["content"][1:]))

    def test_clean_call_leaves_result_untouched(self) -> None:
        result = {"content": [{"type": "text", "text": "ok"}], "isError": False}
        out = mcp.with_unknown_argument_warning(result, "screenshot-game-view", {"savePath": "/tmp/x.png"})
        self.assertEqual(out["content"], [{"type": "text", "text": "ok"}])
        self.assertNotIn("structuredContent", out)

    def test_warning_reaches_clients_that_render_structured_content(self) -> None:
        # Screenshot results carry structuredContent, and clients that display it drop the content
        # text blocks. A warning that lives only in text is invisible exactly where it matters most.
        result = {
            "content": [{"type": "image", "data": "..."}, {"type": "text", "text": "pngWidth:320"}],
            "structuredContent": {"pngWidth": 320},
            "isError": False,
        }
        out = mcp.with_unknown_argument_warning(result, "screenshot-game-view", {"outputPath": "x"})
        self.assertTrue(any("Unrecognized" in n for n in out["structuredContent"]["notices"]))
        # The payload keys a caller reads are untouched.
        self.assertEqual(out["structuredContent"]["pngWidth"], 320)

class CoreDescriptorReminderTests(unittest.TestCase):
    """The startup imperative is easy to skip (clients truncate it), so the first tool call of a
    session carries the pointer when it is actionable."""

    def _server(self):
        import tempfile

        return mcp.McpServer("http://127.0.0.1:1", tempfile.mkdtemp(), timeout_ms=1000)

    def _text_result(self):
        return {"content": [{"type": "text", "text": "captureSource:gameview"}], "isError": False}

    def test_first_call_carries_the_pointer(self) -> None:
        out = self._server()._with_core_descriptor_reminder(self._text_result(), "screenshot-game-view")
        blocks = [block.get("text", "") for block in out["content"]]
        self.assertTrue(any(mcp.CORE_DESCRIPTOR_INSTRUCTIONS_URI in text for text in blocks))
        # The original payload must be untouched; the pointer rides in its own block.
        self.assertEqual(out["content"][0]["text"], "captureSource:gameview")

    def test_image_only_result_is_not_reshaped(self) -> None:
        server = self._server()
        result = {"content": [{"type": "image", "data": "..."}], "isError": False}
        out = server._with_core_descriptor_reminder(result, "screenshot-game-view")
        self.assertEqual([block["type"] for block in out["content"]], ["image"])
        # Still pending, so the next result that has text carries it.
        self.assertFalse(server.core_descriptor_reminder_sent)

    def test_reminder_is_sent_once_per_session(self) -> None:
        server = self._server()
        server._with_core_descriptor_reminder(self._text_result(), "screenshot-game-view")
        second = server._with_core_descriptor_reminder(self._text_result(), "screenshot-game-view")
        self.assertFalse(any(mcp.CORE_DESCRIPTOR_INSTRUCTIONS_URI in b.get("text", "") for b in second["content"]))

    def test_reminder_reaches_clients_that_render_structured_content(self) -> None:
        # The regression this guards: the reminder was emitted as a trailing text block on a
        # screenshot result, and Claude Code renders structuredContent and drops those blocks — so
        # the nudge never arrived on the likeliest first call of a session.
        server = self._server()
        result = {
            "content": [{"type": "image", "data": "..."}, {"type": "text", "text": "pngWidth:320"}],
            "structuredContent": {"pngWidth": 320},
            "isError": False,
        }
        out = server._with_core_descriptor_reminder(result, "screenshot-game-view")
        notices = out["structuredContent"]["notices"]
        self.assertTrue(any(mcp.CORE_DESCRIPTOR_INSTRUCTIONS_URI in n for n in notices))
        self.assertEqual(out["structuredContent"]["pngWidth"], 320)
        self.assertTrue(server.core_descriptor_reminder_sent)

    def test_no_reminder_once_the_resource_was_read(self) -> None:
        server = self._server()
        server.core_descriptors_read = True
        out = server._with_core_descriptor_reminder(self._text_result(), "screenshot-game-view")
        self.assertFalse(any(mcp.CORE_DESCRIPTOR_INSTRUCTIONS_URI in b.get("text", "") for b in out["content"]))

    def test_reminder_never_corrupts_json_output(self) -> None:
        payload = {"pngWidth": 320}
        result = {"content": [{"type": "text", "text": json.dumps(payload)}], "isError": False}
        out = self._server()._with_core_descriptor_reminder(result, "screenshot-game-view")
        self.assertEqual(json.loads(out["content"][0]["text"]), payload)
        self.assertTrue(any("First ChievFX" in block.get("text", "") for block in out["content"][1:]))

if __name__ == "__main__":
    unittest.main()
