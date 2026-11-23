import sys
import tempfile
import time
import unittest
from pathlib import Path
from unittest import mock


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class AwaitProgressTests(unittest.TestCase):
    """A call waiting under awaitMs reports progress, so clients that cut calls at a fixed time keep it alive."""

    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)
        self.server = mcp.McpServer("http://127.0.0.1:1", str(Path(self.temp_dir.name)), timeout_ms=1000)

    def _wait(self, progress_token: object) -> list[dict]:
        sent: list[dict] = []
        with mock.patch.object(mcp, "AWAIT_PROGRESS_TICK_SECONDS", 0.05):
            with self.server._await_progress_ticker(progress_token, sent.append, "gameai-skill-run", 1000):
                time.sleep(0.3)
        return sent

    def test_progress_is_sent_while_waiting(self) -> None:
        sent = self._wait("token-1")
        self.assertGreaterEqual(len(sent), 3)
        values = [item["params"]["progress"] for item in sent]
        self.assertEqual(values, sorted(values))
        self.assertLess(values[-1], 1.0)
        self.assertTrue(all(item["params"]["progressToken"] == "token-1" for item in sent))
        self.assertIn("gameai-skill-run", sent[0]["params"]["message"])

    def test_nothing_is_sent_without_a_progress_token(self) -> None:
        self.assertEqual(self._wait(None), [])

    def test_ticker_stops_with_the_wait(self) -> None:
        sent = self._wait("token-2")
        count = len(sent)
        time.sleep(0.2)
        self.assertEqual(len(sent), count)


if __name__ == "__main__":
    unittest.main()
