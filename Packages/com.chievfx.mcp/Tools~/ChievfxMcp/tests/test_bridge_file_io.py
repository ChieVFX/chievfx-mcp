import sys
import unittest
from pathlib import Path
from unittest import mock


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class BridgeFileIoRetryTests(unittest.TestCase):
    def test_read_text_file_retries_transient_permission_error(self) -> None:
        path = Path("bridge-read.json")
        with mock.patch.object(Path, "read_text", side_effect=[PermissionError(13, "denied"), '{"ok":true}']) as read_text:
            with mock.patch.object(mcp.time, "sleep") as sleep:
                text = mcp.read_text_file(path)

        self.assertEqual(text, '{"ok":true}')
        self.assertEqual(read_text.call_count, 2)
        sleep.assert_called_once()

    def test_write_json_file_atomic_retries_transient_replace_failure(self) -> None:
        path = Path("bridge-write.json")
        real_path = Path
        with (
            mock.patch.object(real_path, "mkdir"),
            mock.patch.object(real_path, "with_name", return_value=Path("bridge-write.tmp")),
            mock.patch.object(Path, "exists", return_value=True),
            mock.patch.object(Path, "write_text"),
            mock.patch.object(Path, "replace", side_effect=[PermissionError(13, "denied"), None]) as replace,
            mock.patch.object(mcp.time, "sleep") as sleep,
        ):
            mcp.write_json_file_atomic(path, {"ok": True})

        self.assertEqual(replace.call_count, 2)
        sleep.assert_called_once()

    def test_is_transient_file_lock_error_recognizes_windows_access_denied(self) -> None:
        exc = OSError()
        exc.winerror = 5  # type: ignore[attr-defined]
        self.assertTrue(mcp.is_transient_file_lock_error(exc))


if __name__ == "__main__":
    unittest.main()
