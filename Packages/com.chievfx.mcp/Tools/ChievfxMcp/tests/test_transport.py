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
