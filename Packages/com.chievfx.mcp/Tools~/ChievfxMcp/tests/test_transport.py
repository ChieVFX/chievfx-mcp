import sys
import unittest
from pathlib import Path
from unittest import mock


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class _FakeStream:
    def __init__(self) -> None:
        self.calls: list[dict[str, object]] = []

    def reconfigure(self, **kwargs: object) -> None:
        self.calls.append(kwargs)


class _NoReconfigureStream:
    pass


class ForceUtf8StdioTests(unittest.TestCase):
    def test_reconfigures_both_streams_to_utf8(self) -> None:
        fake_in = _FakeStream()
        fake_out = _FakeStream()
        with mock.patch.object(mcp.sys, "stdin", fake_in), mock.patch.object(mcp.sys, "stdout", fake_out):
            mcp.force_utf8_stdio()

        for stream in (fake_in, fake_out):
            self.assertEqual(len(stream.calls), 1)
            self.assertEqual(stream.calls[0]["encoding"], "utf-8")
            self.assertEqual(stream.calls[0]["errors"], "replace")

    def test_skips_streams_without_reconfigure(self) -> None:
        with mock.patch.object(mcp.sys, "stdin", _NoReconfigureStream()), mock.patch.object(
            mcp.sys, "stdout", _NoReconfigureStream()
        ):
            mcp.force_utf8_stdio()  # must not raise

    def test_survives_reconfigure_failure(self) -> None:
        class _RaisingStream:
            def reconfigure(self, **_kwargs: object) -> None:
                raise ValueError("locked stream")

        with mock.patch.object(mcp.sys, "stdin", _RaisingStream()), mock.patch.object(
            mcp.sys, "stdout", _RaisingStream()
        ):
            mcp.force_utf8_stdio()  # must not raise


if __name__ == "__main__":
    unittest.main()


class HttpPortTests(unittest.TestCase):
    """A second server on a busy port fails with a clear error instead of listening next to the first."""

    def test_second_bind_on_a_busy_port_names_the_server_holding_it(self) -> None:
        import tempfile
        import threading

        bridge_dir = tempfile.mkdtemp()
        holder = mcp.bind_http_server("127.0.0.1", 0, mcp.make_http_handler(mcp.McpServer("http://127.0.0.1:1", bridge_dir, timeout_ms=1000)))
        self.addCleanup(holder.server_close)
        threading.Thread(target=holder.serve_forever, daemon=True).start()
        self.addCleanup(holder.shutdown)
        port = holder.server_address[1]

        with self.assertRaises(mcp.PortInUseError) as caught:
            mcp.bind_http_server("127.0.0.1", port, mcp.make_http_handler(mcp.McpServer("http://127.0.0.1:1", bridge_dir, timeout_ms=1000)))

        message = str(caught.exception)
        self.assertIn(f"port {port}", message)
        self.assertIn(f"another {mcp.SERVER_NAME} server (bridge {bridge_dir})", message)


class ExtensionSnapshotWatchTests(unittest.TestCase):
    """A rewritten extension snapshot (Unity after a recompile) drops the cached schemas and announces new lists."""

    def test_snapshot_change_invalidates_cache_and_sends_list_changed(self) -> None:
        import tempfile
        import threading
        import time

        snapshot = Path(tempfile.mkdtemp()) / "extension-capabilities.snapshot.json"
        snapshot.write_text('{"schemaVersion": 1, "extensions": []}', encoding="utf-8")
        sent: list[str] = []
        with mock.patch.object(mcp, "EXTENSION_CAPABILITY_MANIFEST_PATH", snapshot), mock.patch.object(
            mcp, "invalidate_extension_manifest_cache"
        ) as invalidate, mock.patch.object(mcp, "refresh_debug_artifacts_on_tools_list_changed"):
            threading.Thread(target=mcp.watch_selection_files, args=(lambda payload: sent.append(payload["method"]), 0.02), daemon=True).start()
            time.sleep(0.1)
            snapshot.write_text('{"schemaVersion": 1, "extensions": [{"id": "x"}]}', encoding="utf-8")
            for _ in range(100):
                if sent:
                    break
                time.sleep(0.02)

            self.assertTrue(invalidate.called)
        self.assertIn("notifications/tools/list_changed", sent)
