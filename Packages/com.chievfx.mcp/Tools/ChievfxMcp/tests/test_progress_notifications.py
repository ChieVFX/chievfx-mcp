import sys
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class ProgressNotificationTests(unittest.TestCase):
    def test_terminal_progress_is_not_emitted(self) -> None:
        sent: list[dict[str, object]] = []

        server = mcp.McpServer("http://127.0.0.1:1", "", timeout_ms=1000)
        server.emit_progress("token", sent.append, 1.0, "done")

        self.assertEqual(sent, [])

    def test_non_terminal_progress_is_emitted(self) -> None:
        sent: list[dict[str, object]] = []

        server = mcp.McpServer("http://127.0.0.1:1", "", timeout_ms=1000)
        server.emit_progress("token", sent.append, 0.5, "waiting")

        self.assertEqual(sent[0]["method"], "notifications/progress")
        self.assertEqual(sent[0]["params"]["progressToken"], "token")


if __name__ == "__main__":
    unittest.main()
