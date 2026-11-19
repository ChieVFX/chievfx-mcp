import json
import sys
import tempfile
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class _RemoteCmdTestCase(unittest.TestCase):
    """Shared fixture: a throwaway project root plus the HTTP transport remote-cmd requires."""

    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)
        self.root = Path(self.temp_dir.name)
        self._originals = {
            "PROJECT_ROOT": mcp.PROJECT_ROOT,
            "TOOL_SELECTION_PATH": mcp.TOOL_SELECTION_PATH,
            "AVAILABILITY_SETTINGS_PATH": mcp.AVAILABILITY_SETTINGS_PATH,
            "REMOTE_CMD_SETTINGS_PATH": mcp.REMOTE_CMD_SETTINGS_PATH,
            "REMOTE_CMD_AUDIT_PATH": mcp.REMOTE_CMD_AUDIT_PATH,
            "EXTENSION_CAPABILITY_MANIFEST_PATH": mcp.EXTENSION_CAPABILITY_MANIFEST_PATH,
            "ACTIVE_TRANSPORT": mcp.ACTIVE_TRANSPORT,
            "ACTIVE_BIND_HOST": mcp.ACTIVE_BIND_HOST,
            "ACTIVE_PORT": mcp.ACTIVE_PORT,
        }
        self.addCleanup(self._restore)
        mcp.configure_project_root(str(self.root))
        mcp.configure_extension_manifest_bridge_fetcher(None)
        mcp.configure_active_transport("http", "127.0.0.1", 27247)
        mcp.clear_remote_cmd_request_context()
        self.settings_path = self.root / "UserSettings" / "ChievfxMcpRemoteCmd.json"
        self.settings_path.parent.mkdir(parents=True, exist_ok=True)

    def _restore(self) -> None:
        for name, value in self._originals.items():
            setattr(mcp, name, value)
        mcp.clear_remote_cmd_request_context()

    def arm(self, **overrides: object) -> None:
        payload = {
            "enabled": True,
            "allowPattern": "^(?:unity|adb|echo)$",
            "unrestrictedAck": False,
            "securityKey": "test-key",
            "allowedIps": [],
            "bindHost": "127.0.0.1",
        }
        payload.update(overrides)
        self.settings_path.write_text(json.dumps(payload), encoding="utf-8")

    def advertised_tool_ids(self) -> set[str]:
        return {tool["name"] for tool in mcp.enabled_tools()}


class RemoteCmdExposureTests(_RemoteCmdTestCase):
    """The tool must be unreachable by every path except the Connection tab switch."""

    def test_absent_without_settings_file(self) -> None:
        self.assertNotIn(mcp.REMOTE_CMD_TOOL_ID, self.advertised_tool_ids())
        self.assertNotIn(mcp.REMOTE_CMD_TOOL_ID, {tool["name"] for tool in mcp.all_tools()})
        self.assertEqual(mcp.remote_cmd_instruction_banner(), "")

    def test_expose_all_default_does_not_enable_it(self) -> None:
        # The master exposure toggle off (default) means "advertise every non-hidden tool"; a tool that
        # runs host commands must not ride along with that.
        self.assertFalse(mcp.manual_tool_resource_selection_enabled())
        self.assertNotIn(mcp.REMOTE_CMD_TOOL_ID, self.advertised_tool_ids())

    def test_saved_selection_naming_it_does_not_enable_it(self) -> None:
        mcp.AVAILABILITY_SETTINGS_PATH.write_text(json.dumps({"manualSelection": True}), encoding="utf-8")
        mcp.TOOL_SELECTION_PATH.write_text(
            json.dumps({"schemaVersion": mcp.TOOL_SELECTION_SCHEMA_VERSION, "enabledToolIds": [mcp.REMOTE_CMD_TOOL_ID]}),
            encoding="utf-8",
        )
        self.assertNotIn(mcp.REMOTE_CMD_TOOL_ID, self.advertised_tool_ids())

    def test_armed_tool_is_advertised_even_under_a_restrictive_selection(self) -> None:
        mcp.AVAILABILITY_SETTINGS_PATH.write_text(json.dumps({"manualSelection": True}), encoding="utf-8")
        mcp.TOOL_SELECTION_PATH.write_text(
            json.dumps({"schemaVersion": mcp.TOOL_SELECTION_SCHEMA_VERSION, "enabledToolIds": ["console-get-logs"]}),
            encoding="utf-8",
        )
        self.arm()
        self.assertIn(mcp.REMOTE_CMD_TOOL_ID, self.advertised_tool_ids())

    def test_stdio_never_arms(self) -> None:
        self.arm()
        mcp.configure_active_transport("stdio", "127.0.0.1", 27247)
        self.assertFalse(mcp.remote_cmd_armed())
        self.assertIn("stdio", mcp.remote_cmd_gate()["reason"])

    def test_empty_security_key_never_arms(self) -> None:
        self.arm(securityKey="")
        self.assertFalse(mcp.remote_cmd_armed())
        self.assertIn("security key", mcp.remote_cmd_gate()["reason"])

    def test_empty_filter_needs_explicit_acknowledgement(self) -> None:
        self.arm(allowPattern="")
        self.assertFalse(mcp.remote_cmd_armed())
        self.arm(allowPattern="", unrestrictedAck=True)
        self.assertTrue(mcp.remote_cmd_armed())

    def test_banner_is_advertised_above_the_global_records(self) -> None:
        self.arm()
        instructions = mcp.build_initialize_instructions()
        banner = mcp.remote_cmd_instruction_banner()
        self.assertIn(banner, instructions)
        self.assertLess(instructions.index(banner), instructions.index("ChievFX Unity MCP is project-local"))

    def test_disarmed_call_explains_which_switch_is_off(self) -> None:
        server = mcp.McpServer("http://127.0.0.1:27248", str(self.root / "Library" / "ChievfxMcpBridge"), 1000)
        with self.assertRaises(mcp.ToolCallError) as caught:
            server._dispatch_tool_call({"name": mcp.REMOTE_CMD_TOOL_ID, "arguments": {"command": "echo hi"}})
        self.assertIn("Connection tab", str(caught.exception))


class RemoteCmdFilterTests(_RemoteCmdTestCase):
    """With a filter on, only a single invocation of a matching program survives."""

    PATTERN = "^(?:unity|adb|echo)$"

    def validate(self, command: str) -> list[str]:
        return mcp.validate_filtered_command(command, self.PATTERN, self.root)

    def test_accepts_a_matching_program_and_resolves_it(self) -> None:
        argv = self.validate("echo hello world")
        self.assertTrue(Path(argv[0]).is_absolute())
        self.assertEqual(mcp.normalize_program_name(argv[0]), "echo")
        self.assertEqual(argv[1:], ["hello", "world"])

    def test_quoted_arguments_stay_one_argument(self) -> None:
        self.assertEqual(self.validate('echo "ls /sdcard"')[1:], ["ls /sdcard"])

    def test_multi_line_is_refused_and_names_the_pattern(self) -> None:
        with self.assertRaises(mcp.ToolCallError) as caught:
            self.validate("echo one\necho two")
        message = str(caught.exception)
        self.assertIn("multi-line", message)
        self.assertIn(self.PATTERN, message)

    def test_chaining_and_redirection_are_refused(self) -> None:
        for command in ("echo a; echo b", "echo a && echo b", "echo a | tee b", "echo a > b", "echo `id`", "echo $(id)"):
            with self.subTest(command=command), self.assertRaises(mcp.ToolCallError) as caught:
                self.validate(command)
            self.assertIn("not allowed while the program filter is on", str(caught.exception))

    def test_interpreters_are_refused_before_the_pattern_is_consulted(self) -> None:
        # Would otherwise be the whole bypass: a pattern that admits bash admits every command.
        for command in ("bash -c 'id'", "/bin/sh -c id", "python3 -c 'import os'", "env echo hi", "sudo echo hi"):
            with self.subTest(command=command), self.assertRaises(mcp.ToolCallError) as caught:
                self.validate(command)
            self.assertIn("runs whatever code it is given", str(caught.exception))

    def test_a_path_that_merely_contains_the_pattern_is_refused(self) -> None:
        with self.assertRaises(mcp.ToolCallError):
            mcp.validate_filtered_command("/usr/local/adb-tools/sh --version", "adb", self.root)

    def test_a_name_pattern_is_not_satisfied_by_a_directory_in_the_path(self) -> None:
        # "adb" names a program, so a binary that merely lives under .../adb-tools/ must not pass.
        decoy_dir = self.root / "adb-tools"
        decoy_dir.mkdir()
        decoy = decoy_dir / "payload"
        decoy.write_text("#!/bin/sh\necho pwned\n", encoding="utf-8")
        decoy.chmod(0o755)
        with self.assertRaises(mcp.ToolCallError) as caught:
            mcp.validate_filtered_command(f"{decoy} --go", "adb", self.root)
        self.assertIn("does not match", str(caught.exception))

    def test_a_path_anchored_pattern_still_matches_the_full_path(self) -> None:
        tool_dir = self.root / "sdk" / "platform-tools"
        tool_dir.mkdir(parents=True)
        fake_adb = tool_dir / "adb"
        fake_adb.write_text("#!/bin/sh\necho devices\n", encoding="utf-8")
        fake_adb.chmod(0o755)
        argv = mcp.validate_filtered_command(f"{fake_adb} devices", r"/platform-tools/adb$", self.root)
        self.assertEqual(argv[0], str(fake_adb))

    def test_non_matching_program_is_refused(self) -> None:
        with self.assertRaises(mcp.ToolCallError) as caught:
            self.validate("git status")
        self.assertIn("does not match", str(caught.exception))

    def test_invalid_configured_pattern_is_reported_not_ignored(self) -> None:
        with self.assertRaises(mcp.ToolCallError) as caught:
            mcp.validate_filtered_command("echo hi", "^(unclosed", self.root)
        self.assertIn("not a valid regex", str(caught.exception))


class RemoteCmdExecutionTests(_RemoteCmdTestCase):
    def test_runs_a_filtered_command_with_no_shell(self) -> None:
        self.arm()
        mcp.set_remote_cmd_request_context("127.0.0.1", "test-key")
        result = mcp.run_remote_cmd({"command": "echo host-side"})
        self.assertEqual(result["exitCode"], 0)
        self.assertEqual(result["stdout"].strip(), "host-side")
        self.assertEqual(result["shell"], "none")
        self.assertTrue(result["filtered"])
        self.assertEqual(result["workingDirectory"], str(self.root.resolve()))

    def test_wrong_key_is_refused_per_call(self) -> None:
        self.arm()
        mcp.set_remote_cmd_request_context("127.0.0.1", "not-the-key")
        with self.assertRaises(mcp.ToolCallError) as caught:
            mcp.run_remote_cmd({"command": "echo hi"})
        self.assertIn("security key", str(caught.exception))

    def test_peer_outside_the_allow_list_is_refused(self) -> None:
        self.arm(allowedIps=["10.0.0.0/24"])
        mcp.set_remote_cmd_request_context("192.168.1.9", "test-key")
        with self.assertRaises(mcp.ToolCallError) as caught:
            mcp.run_remote_cmd({"command": "echo hi"})
        self.assertIn("not in the configured allowed-IP list", str(caught.exception))

    def test_cidr_entry_admits_a_peer_inside_it(self) -> None:
        self.assertTrue(mcp.remote_cmd_ip_allowed("192.168.1.9", ["10.0.0.1", "192.168.1.0/24"]))
        self.assertFalse(mcp.remote_cmd_ip_allowed("192.168.2.9", ["192.168.1.0/24"]))
        # Empty list = any peer, which the Unity window warns about in as many words.
        self.assertTrue(mcp.remote_cmd_ip_allowed("8.8.8.8", []))

    def test_output_is_capped(self) -> None:
        self.arm()
        mcp.set_remote_cmd_request_context("127.0.0.1", "test-key")
        result = mcp.run_remote_cmd({"command": "echo " + "x" * 5000, "maxOutputBytes": 1024})
        self.assertTrue(result["stdoutTruncated"])
        self.assertEqual(len(result["stdout"].encode("utf-8")), 1024)

    def test_timeout_kills_the_command_and_keeps_partial_output(self) -> None:
        self.arm(allowPattern="", unrestrictedAck=True)
        mcp.set_remote_cmd_request_context("127.0.0.1", "test-key")
        result = mcp.run_remote_cmd({"command": "echo first\nsleep 30", "timeoutMs": 1000})
        self.assertTrue(result["timedOut"])
        self.assertEqual(result["stdout"].strip(), "first")
        self.assertIn("TIMED OUT", mcp.format_remote_cmd_text(result))

    def test_missing_working_directory_is_refused(self) -> None:
        self.arm()
        mcp.set_remote_cmd_request_context("127.0.0.1", "test-key")
        with self.assertRaises(mcp.ToolCallError) as caught:
            mcp.run_remote_cmd({"command": "echo hi", "workingDirectory": "does/not/exist"})
        self.assertIn("does not exist", str(caught.exception))

    def test_every_call_is_audited(self) -> None:
        self.arm()
        mcp.set_remote_cmd_request_context("127.0.0.1", "test-key")
        mcp.run_remote_cmd({"command": "echo audited"})
        mcp.set_remote_cmd_request_context("127.0.0.1", "wrong")
        with self.assertRaises(mcp.ToolCallError):
            mcp.run_remote_cmd({"command": "echo refused"})

        entries = [json.loads(line) for line in mcp.REMOTE_CMD_AUDIT_PATH.read_text(encoding="utf-8").splitlines()]
        self.assertEqual([entry["outcome"] for entry in entries], ["ran", "rejected"])
        self.assertEqual(entries[1]["reason"], "bad-key")


class RemoteCmdHttpGuardTests(_RemoteCmdTestCase):
    def make_handler(self, headers: dict[str, str], peer: str = "127.0.0.1"):
        server = mcp.McpServer("http://127.0.0.1:27248", str(self.root / "Library" / "ChievfxMcpBridge"), 1000)
        handler_class = mcp.make_http_handler(server)
        handler = handler_class.__new__(handler_class)
        handler.headers = headers
        handler.client_address = (peer, 51000)
        handler.sent: list[tuple[object, int]] = []
        handler.send_json = lambda payload, status=200: handler.sent.append((payload, status))
        return handler

    def test_requests_carrying_an_origin_are_refused(self) -> None:
        handler = self.make_handler({"Host": "127.0.0.1:27247", "Origin": "https://evil.example"})
        self.assertTrue(handler.reject_untrusted_request())
        self.assertEqual(handler.sent[0][1], 403)

    def test_unexpected_host_header_is_refused(self) -> None:
        handler = self.make_handler({"Host": "rebind.evil.example:27247"})
        self.assertTrue(handler.reject_untrusted_request())
        self.assertEqual(handler.sent[0][1], 403)

    def test_loopback_without_remote_cmd_needs_no_key(self) -> None:
        handler = self.make_handler({"Host": "127.0.0.1:27247"})
        self.assertFalse(handler.reject_untrusted_request())

    def test_armed_remote_cmd_requires_the_key_on_every_request(self) -> None:
        self.arm()
        handler = self.make_handler({"Host": "127.0.0.1:27247"})
        self.assertTrue(handler.reject_untrusted_request())
        self.assertEqual(handler.sent[0][1], 401)

        handler = self.make_handler({"Host": "127.0.0.1:27247", "X-Chievfx-Key": "test-key"})
        self.assertFalse(handler.reject_untrusted_request())

        handler = self.make_handler({"Host": "127.0.0.1:27247", "Authorization": "Bearer test-key"})
        self.assertFalse(handler.reject_untrusted_request())

    def test_non_loopback_bind_requires_the_key_even_with_remote_cmd_off(self) -> None:
        mcp.configure_active_transport("http", "0.0.0.0", 27247)
        required, _ = mcp.http_requires_security_key()
        self.assertTrue(required)
        handler = self.make_handler({"Host": "192.168.1.20:27247"}, peer="192.168.1.9")
        self.assertTrue(handler.reject_untrusted_request())
        self.assertEqual(handler.sent[0][1], 401)

    def test_host_header_forms(self) -> None:
        for host in ("127.0.0.1:27247", "localhost", "[::1]:27247", "192.168.1.20:27247"):
            self.assertTrue(mcp.http_host_header_is_allowed(host), host)
        for host in ("", "unity.evil.example", "unity.evil.example:27247"):
            self.assertFalse(mcp.http_host_header_is_allowed(host), host)


if __name__ == "__main__":
    unittest.main()
