import sys
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class ReflectionFormattingTests(unittest.TestCase):
    def test_method_find_text_uses_compact_signature_rows(self) -> None:
        result = {
            "count": 1,
            "truncated": False,
            "page": 1,
            "pageSize": 10,
            "hasMore": False,
            "methods": [
                {
                    "index": 0,
                    "ns": "Chievfx.Mcp.Editor.Tests.Auxillary",
                    "type": "TestClass",
                    "method": "TestMethod",
                    "signature": "TestMethod()",
                    "return": "System.Void",
                    "params": [],
                    "static": False,
                    "visibility": "public",
                }
            ],
        }

        text = mcp.format_reflection_method_find_text(result)

        self.assertEqual(
            text,
            "count:1 page:1 pageSize:10 hasMore:false truncated:false\n"
            "methods[1]:\n"
            "0 Chievfx.Mcp.Editor.Tests.Auxillary.TestClass.TestMethod() -> void public instance",
        )

    def test_method_find_text_collapses_parameter_types(self) -> None:
        result = {
            "count": 1,
            "methods": [
                {
                    "index": 1,
                    "ns": "Example",
                    "type": "Tools",
                    "method": "Run",
                    "return": "System.String",
                    "params": [
                        {"type": "System.Int32", "name": "count"},
                        {"type": "UnityEngine.Vector3", "name": "position"},
                    ],
                    "static": True,
                    "visibility": "internal",
                }
            ],
        }

        text = mcp.format_reflection_method_find_text(result)

        self.assertIn("1 Example.Tools.Run(int count, Vector3 position) -> string internal static", text)

    def test_method_find_text_collapses_generic_return_types(self) -> None:
        result = {
            "count": 1,
            "methods": [
                {
                    "index": 0,
                    "ns": "Example",
                    "type": "Enumerator",
                    "method": "GetEnumerator",
                    "return": "System.Collections.Generic.IEnumerator`1[[Example.Item, example, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null]]",
                    "params": [],
                    "static": False,
                    "visibility": "private",
                }
            ],
        }

        text = mcp.format_reflection_method_find_text(result)

        self.assertIn("-> IEnumerator private instance", text)

    def test_method_find_single_text_includes_detail_and_call_filter(self) -> None:
        result = {
            "index": 1,
            "page": 2,
            "pageSize": 10,
            "method": {
                "index": 1,
                "ns": "Example",
                "type": "Tools",
                "method": "Run",
                "signature": "Run(int count)",
                "return": "System.String",
                "params": [{"type": "System.Int32", "name": "count"}],
                "static": True,
                "visibility": "internal",
                "callFilter": {
                    "namespace": "Example",
                    "typeName": "Tools",
                    "methodName": "Run",
                    "inputParameters": [{"typeName": "System.Int32"}],
                },
            },
        }

        text = mcp.format_reflection_method_find_single_text(result)

        self.assertIn("index:1 page:2 pageSize:10", text)
        self.assertIn("params[1]:\n- int count", text)
        self.assertIn("callFilter:", text)
        self.assertIn("  methodName:Run", text)
        self.assertIn("  - typeName:System.Int32", text)


if __name__ == "__main__":
    unittest.main()
