import sys
import tempfile
import threading
import time
import unittest
import uuid
from pathlib import Path
from unittest import mock


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class BridgeVanishingFileTests(unittest.TestCase):
    """On Windows Unity's File.Replace and pruning make a just-listed file read as missing (WinError 2)."""

    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)
        self.bridge_dir = Path(self.temp_dir.name)
        self.server = mcp.McpServer("http://127.0.0.1:1", str(self.bridge_dir), timeout_ms=1000)

    def test_operation_rows_skip_a_record_that_vanished_after_listing(self) -> None:
        self.server.operation_dir.mkdir(parents=True, exist_ok=True)
        mcp.write_json_file_atomic(
            self.server.operation_dir / "live.json",
            {"operationId": "live", "toolName": "gameai-read", "state": "completed"},
        )
        vanished = self.server.operation_dir / "vanished.json"
        real_glob = Path.glob

        def glob_with_vanished(path: Path, pattern: str):
            found = list(real_glob(path, pattern))
            return found + [vanished] if path == self.server.operation_dir else found

        with mock.patch.object(Path, "glob", glob_with_vanished):
            rows = self.server.read_operation_rows(10, time.time())
        self.assertEqual([row.get("operationId") for row in rows], ["live"])

    def test_response_that_reads_missing_once_is_read_again(self) -> None:
        operation_id = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        response_path = self.server.response_dir / f"{operation_id}.json"
        real_read = mcp.read_text_file
        failures = {"left": 1}

        def flaky_read(path: Path, encoding: str = "utf-8") -> str:
            if path == response_path and failures["left"] > 0:
                failures["left"] -= 1
                raise FileNotFoundError(2, "The system cannot find the file specified")
            return real_read(path, encoding)

        results: dict[str, object] = {}

        def call_bridge() -> None:
            try:
                results["payload"] = self.server.call_unity_bridge("gameai-read", {}, request_id="read")
            except Exception as exc:  # noqa: BLE001 - the test reports it
                results["payload"] = exc

        with mock.patch.object(mcp.uuid, "uuid4", return_value=uuid.UUID(hex=operation_id)), mock.patch.object(mcp, "read_text_file", flaky_read):
            thread = threading.Thread(target=call_bridge)
            thread.start()
            time.sleep(0.3)
            mcp.write_json_file_atomic(response_path, {"ok": True, "contentType": "text", "result": "done"})
            thread.join(timeout=5)

        self.assertFalse(thread.is_alive())
        self.assertIsInstance(results.get("payload"), dict, results.get("payload"))
        self.assertEqual(failures["left"], 0)


if __name__ == "__main__":
    unittest.main()
