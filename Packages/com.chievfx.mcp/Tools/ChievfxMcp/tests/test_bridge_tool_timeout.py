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


class BridgeToolTimeoutTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)
        self.bridge_dir = Path(self.temp_dir.name)
        self.server = mcp.McpServer("http://127.0.0.1:1", str(self.bridge_dir), timeout_ms=1000)

    def test_call_unity_bridge_waits_for_per_tool_timeout(self) -> None:
        results: dict[str, object] = {}
        operation_id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"

        def call_bridge() -> None:
            try:
                results["payload"] = self.server.call_unity_bridge(
                    "tests-run",
                    {"timeoutMs": 3000},
                    request_id="slow-tests-run",
                )
            except Exception as exc:  # noqa: BLE001 - test reports unexpected timeout payload.
                results["payload"] = exc

        with mock.patch.object(mcp.uuid, "uuid4", return_value=uuid.UUID(hex=operation_id)):
            thread = threading.Thread(target=call_bridge)
            thread.start()
            time.sleep(1.2)
            mcp.write_json_file_atomic(
                self.bridge_dir / "responses" / f"{operation_id}.json",
                {"ok": True, "contentType": "json", "result": {"summary": {"status": "Passed"}}},
            )

        thread.join(timeout=1)

        self.assertFalse(thread.is_alive())
        self.assertIsInstance(results["payload"], dict)
        self.assertEqual(results["payload"]["result"]["summary"]["status"], "Passed")

    def test_tests_run_uses_fresh_test_results_xml_after_domain_reload(self) -> None:
        results: dict[str, object] = {}
        operation_id = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        result_path = self.bridge_dir / "TestResults.xml"

        def call_bridge() -> None:
            try:
                results["payload"] = self.server.call_unity_bridge(
                    "tests-run",
                    {"testMode": "PlayMode", "includePassingTests": True, "timeoutMs": 3000},
                    request_id="playmode-tests-run",
                )
            except Exception as exc:  # noqa: BLE001 - test reports unexpected timeout payload.
                results["payload"] = exc

        with (
            mock.patch.object(mcp.uuid, "uuid4", return_value=uuid.UUID(hex=operation_id)),
            mock.patch.object(mcp, "unity_test_result_paths", return_value=[result_path]),
        ):
            thread = threading.Thread(target=call_bridge)
            thread.start()
            time.sleep(1.2)
            result_path.write_text(
                """<?xml version="1.0" encoding="utf-8"?>
<test-run testcasecount="1" result="Passed" total="1" passed="1" failed="0" skipped="0" duration="0.2">
  <test-case name="MouseAndTouchAffectRuntimeTargets" fullname="Chievfx.Mcp.Input.PlayMode.Tests.ChievfxMcpInputPlayModeTests.MouseAndTouchAffectRuntimeTargets" result="Passed" duration="0.1" />
</test-run>
""",
                encoding="utf-8",
            )
            thread.join(timeout=1)

        self.assertFalse(thread.is_alive())
        self.assertIsInstance(results["payload"], dict)
        payload = results["payload"]
        self.assertEqual(payload["result"]["summary"]["status"], "Passed")
        self.assertEqual(payload["result"]["summary"]["totalTests"], 1)
        self.assertEqual(payload["result"]["results"][0]["status"], "Passed")
        self.assertEqual(payload["result"]["source"], "TestResults.xml")

    def test_bridge_call_waits_for_compile_recovery_before_queueing(self) -> None:
        results: dict[str, object] = {}
        operation_id = "cccccccccccccccccccccccccccccccc"
        request_path = self.bridge_dir / "requests" / f"{operation_id}.json"
        response_path = self.bridge_dir / "responses" / f"{operation_id}.json"
        mcp.write_json_file_atomic(
            self.bridge_dir / "state.json",
            {
                "editor": {"isCompiling": True, "isUpdating": False},
                "busy": {"isCompiling": True},
                "busyReasons": ["editor-compiling"],
            },
        )

        def call_bridge() -> None:
            try:
                results["payload"] = self.server.call_unity_bridge("console-get-logs", {}, request_id="compile-wait")
            except Exception as exc:  # noqa: BLE001 - test reports unexpected timeout payload.
                results["payload"] = exc

        with (
            mock.patch.object(mcp.uuid, "uuid4", return_value=uuid.UUID(hex=operation_id)),
            mock.patch.object(mcp, "BRIDGE_READY_POLL_SECONDS", 0.01),
            mock.patch.object(mcp, "BRIDGE_READY_POST_BUSY_GRACE_SECONDS", 0.01),
            mock.patch.object(mcp, "BRIDGE_RECOVERY_WAIT_SECONDS", 2.0),
        ):
            thread = threading.Thread(target=call_bridge)
            thread.start()
            time.sleep(0.05)
            self.assertFalse(request_path.exists())
            mcp.write_json_file_atomic(
                self.bridge_dir / "state.json",
                {
                    "editor": {"isCompiling": False, "isUpdating": False},
                    "busy": {"isCompiling": False},
                    "busyReasons": [],
                },
            )
            for _ in range(100):
                if request_path.exists():
                    break
                time.sleep(0.01)
            self.assertTrue(request_path.exists())
            mcp.write_json_file_atomic(
                response_path,
                {"ok": True, "contentType": "json", "result": {"entries": []}},
            )

        thread.join(timeout=1)

        self.assertFalse(thread.is_alive())
        self.assertIsInstance(results["payload"], dict)
        self.assertEqual(results["payload"]["result"]["entries"], [])

    def test_bridge_call_extends_timeout_during_compile_recovery(self) -> None:
        results: dict[str, object] = {}
        operation_id = "dddddddddddddddddddddddddddddddd"
        request_path = self.bridge_dir / "requests" / f"{operation_id}.json"
        response_path = self.bridge_dir / "responses" / f"{operation_id}.json"

        def call_bridge() -> None:
            try:
                results["payload"] = self.server.call_unity_bridge("console-get-logs", {}, request_id="compile-timeout")
            except Exception as exc:  # noqa: BLE001 - test reports unexpected timeout payload.
                results["payload"] = exc

        with (
            mock.patch.object(mcp.uuid, "uuid4", return_value=uuid.UUID(hex=operation_id)),
            mock.patch.object(mcp, "BRIDGE_RECOVERY_WAIT_SECONDS", 2.0),
            mock.patch.object(mcp, "BRIDGE_RECOVERY_RECHECK_SECONDS", 0.05),
            mock.patch.object(mcp, "BRIDGE_POST_RECOVERY_RESPONSE_GRACE_SECONDS", 0.2),
        ):
            thread = threading.Thread(target=call_bridge)
            thread.start()
            for _ in range(100):
                if request_path.exists():
                    break
                time.sleep(0.01)
            self.assertTrue(request_path.exists())
            mcp.write_json_file_atomic(
                self.bridge_dir / "state.json",
                {
                    "editor": {"isCompiling": True, "isUpdating": False},
                    "busy": {"isCompiling": True},
                    "busyReasons": ["editor-compiling"],
                },
            )
            time.sleep(1.2)
            mcp.write_json_file_atomic(
                response_path,
                {"ok": True, "contentType": "json", "result": {"entries": []}},
            )

        thread.join(timeout=1)

        self.assertFalse(thread.is_alive())
        self.assertIsInstance(results["payload"], dict)
        self.assertEqual(results["payload"]["result"]["entries"], [])

