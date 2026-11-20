import sys
import tempfile
import unittest
from pathlib import Path
from typing import Any


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class ScriptExecuteSourceFileTests(unittest.TestCase):
    """script-execute csharpPath: the server reads the file and sends its text as csharpCode."""

    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)
        self.root = Path(self.temp_dir.name)
        bridge_dir = self.root / "Library" / "ChievfxMcpBridge"
        bridge_dir.mkdir(parents=True)
        names = ("TOOL_SELECTION_PATH", "CATEGORY_SELECTION_PATH", "AVAILABILITY_SETTINGS_PATH", "EXTENSION_CAPABILITY_MANIFEST_PATH", "PROJECT_ROOT")
        originals = {name: getattr(mcp, name) for name in names}
        self.addCleanup(lambda: [setattr(mcp, name, value) for name, value in originals.items()])
        mcp.TOOL_SELECTION_PATH = self.root / "UserSettings" / "ChievfxMcpToolSelection.json"
        mcp.CATEGORY_SELECTION_PATH = self.root / "UserSettings" / "ChievfxMcpCategorySelection.json"
        mcp.AVAILABILITY_SETTINGS_PATH = self.root / "UserSettings" / "ChievfxMcpAvailability.json"
        mcp.EXTENSION_CAPABILITY_MANIFEST_PATH = bridge_dir / "extension-capabilities.snapshot.json"
        mcp.PROJECT_ROOT = self.root
        self.server = mcp.McpServer("http://127.0.0.1:1", str(bridge_dir), timeout_ms=1000)
        mcp.configure_extension_manifest_bridge_fetcher(None)
        self.addCleanup(lambda: mcp.configure_extension_manifest_bridge_fetcher(None))
        self.server.core_descriptors_read = True
        self.sent: list[dict[str, Any]] = []

        def bridge(name: str, arguments: dict[str, Any], *args: Any, **kwargs: Any) -> dict[str, Any]:
            self.sent.append(dict(arguments))
            return {"result": {"success": True, "stage": "completed"}}

        self.server.call_unity_bridge = bridge  # type: ignore[method-assign]

    def call(self, arguments: dict[str, Any]) -> dict[str, Any]:
        return self.server.call_tool({"name": "script-execute", "arguments": arguments})

    def test_file_text_is_sent_as_csharp_code(self) -> None:
        source = 'Debug.Log("Привет \\"мир\\" C:\\\\temp");\nreturn 1;\n'
        (self.root / "Temp").mkdir()
        (self.root / "Temp" / "probe.cs").write_text("\ufeff" + source, encoding="utf-8")

        self.call({"csharpPath": "Temp/probe.cs", "timeoutMs": 5000})

        self.assertEqual(self.sent, [{"csharpCode": source, "timeoutMs": 5000}])

    def test_absolute_path_works(self) -> None:
        path = self.root / "abs.cs"
        path.write_text("return 2;", encoding="utf-8")

        self.call({"csharpPath": str(path)})

        self.assertEqual(self.sent[0]["csharpCode"], "return 2;")

    def test_missing_file_and_both_sources_are_refused_before_unity(self) -> None:
        with self.assertRaisesRegex(mcp.ToolCallError, "no file at"):
            self.call({"csharpPath": "nope.cs"})
        (self.root / "a.cs").write_text("return 1;", encoding="utf-8")
        with self.assertRaisesRegex(mcp.ToolCallError, "not both"):
            self.call({"csharpPath": "a.cs", "csharpCode": "return 2;"})
        self.assertEqual(self.sent, [])

    def test_csharp_path_is_a_declared_argument(self) -> None:
        tool = next(item for item in mcp.all_tools() if item["name"] == "script-execute")

        self.assertIn("csharpPath", tool["inputSchema"]["properties"])
        self.assertNotIn("csharpCode", tool["inputSchema"].get("required", []))

    def test_parameters_sent_as_json_text_arrive_as_a_list(self) -> None:
        # A client that does not know the property's type sends the array as a string; the editor read that as
        # no parameters ("no overload with 0 supplied parameters").
        self.call({"csharpCode": "x", "methodName": "Hello", "parameters": '[{"value": "agent"}, {"value": 2}]'})

        self.assertEqual(self.sent[0]["parameters"], [{"value": "agent"}, {"value": 2}])

    def test_bare_parameter_values_are_wrapped_as_value_entries(self) -> None:
        # The editor reads only {value} entries: bare values reached the method as null and 0 with success:true.
        self.call({"csharpCode": "x", "methodName": "Hello", "parameters": ["a", 2, {"x": 1, "y": 2, "z": 3}, {"value": 4}]})

        self.assertEqual(
            self.sent[0]["parameters"],
            [{"value": "a"}, {"value": 2}, {"value": {"x": 1, "y": 2, "z": 3}}, {"value": 4}],
        )

    def test_parameters_that_are_not_an_array_are_refused_before_unity(self) -> None:
        with self.assertRaisesRegex(mcp.ToolCallError, "JSON array"):
            self.call({"csharpCode": "x", "parameters": "agent"})
        self.assertEqual(self.sent, [])

    def test_parameters_are_advertised_as_an_array(self) -> None:
        tool = next(item for item in mcp.all_tools() if item["name"] == "script-execute")

        self.assertEqual(mcp.advertised_input_schema(tool)["properties"]["parameters"], {"type": "array"})


if __name__ == "__main__":
    unittest.main()
