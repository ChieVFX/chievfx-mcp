import sys
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402
from chievfx_mcp_server_parts.initialize_instructions import (  # noqa: E402
    _schema_arguments,
    format_tool_for_initialize_instructions,
)


class RuntimeUiProbeFormattingTests(unittest.TestCase):
    def test_initialize_instructions_advertises_typed_probe_arguments(self) -> None:
        schema = mcp.advertised_input_schema({"name": "ui-runtime-probe", "inputSchema": {}})
        self.assertEqual(_schema_arguments(schema), "x:num, y:num, isNormalized?:bool, page?:int")

        line = format_tool_for_initialize_instructions(
            {
                "name": "ui-runtime-probe",
                "description": "Probe runtime UI.",
                "inputSchema": schema,
            }
        )
        self.assertIn("args=(x:num, y:num, isNormalized?:bool, page?:int)", line)

    def test_initialize_instructions_advertises_typed_type_text_arguments(self) -> None:
        schema = mcp.advertised_input_schema({"name": "ui-runtime-type-text", "inputSchema": {}})
        self.assertEqual(
            _schema_arguments(schema),
            "framework?:auto|ugui|uitoolkit, x?:num, y?:num, isNormalized?:bool, path?:str, instanceId?:int, text:str, append?:bool, submit?:bool",
        )

        line = format_tool_for_initialize_instructions(
            {
                "name": "ui-runtime-type-text",
                "description": "Type into runtime text field.",
                "inputSchema": schema,
            }
        )
        self.assertIn(
            "args=(framework?:auto|ugui|uitoolkit, x?:num, y?:num, isNormalized?:bool, path?:str, instanceId?:int, text:str, append?:bool, submit?:bool)",
            line,
        )
        self.assertEqual(
            list(schema["properties"].keys()),
            [
                "framework",
                "x",
                "y",
                "isNormalized",
                "path",
                "instanceId",
                "text",
                "append",
                "submit",
            ],
        )

        # Fallback path: manifest snapshot may arrive alphabetically sorted from Unity.
        manifest_tool = {
            "name": "ui-runtime-type-text",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "append": {"type": "boolean"},
                    "framework": {"enum": ["auto", "ugui", "uitoolkit"], "type": "string"},
                    "instanceId": {"type": "integer"},
                    "isNormalized": {"type": "boolean"},
                    "path": {"type": "string"},
                    "submit": {"type": "boolean"},
                    "text": {"type": "string"},
                    "x": {"type": "number"},
                    "y": {"type": "number"},
                },
                "required": ["text"],
                "additionalProperties": True,
            },
        }
        fallback_schema = mcp.advertised_input_schema(manifest_tool)
        self.assertEqual(list(fallback_schema["properties"].keys()), list(schema["properties"].keys()))

    def test_merged_probe_renders_markdown_sections(self) -> None:
        result = {
            "runtimeAvailable": True,
            "page": 1,
            "totalPages": 2,
            "totalHits": 12,
            "pageSize": 10,
            "truncated": False,
            "probe": {
                "origin": "bottom-left",
                "normalized": {"x": 0.01, "y": 0.01},
                "screen": {"x": 25.6, "y": 14.4},
                "screenSize": {"x": 2560, "y": 1440},
            },
            "ugui": {
                "available": True,
                "probed": True,
                "count": 2,
                "hits": [
                    {
                        "i": 0,
                        "path": "Canvas/Button/Text (TMP)",
                        "type": "TextMeshProUGUI",
                        "handlerPath": "Canvas/Button",
                    },
                    {
                        "i": 1,
                        "path": "Canvas/Button",
                        "type": "Button",
                        "interactable": True,
                        "controls": ["Button"],
                    },
                ],
            },
            "uitoolkit": {
                "available": True,
                "probed": True,
                "yInverted": True,
                "panelScreen": {"x": 25.6, "y": 1425.6},
                "count": 0,
                "hits": [],
            },
            "warnings": ["sample warning"],
        }

        text = mcp.format_ugui_runtime_probe_text(result)

        self.assertIn("## Runtime UI probe", text)
        self.assertIn("page:1/2", text)
        self.assertIn("### Probe position", text)
        self.assertIn("origin bottom-left · normalized 0.01, 0.01 · screen 25.60, 14.40", text)
        self.assertIn("### uGUI", text)
        self.assertIn("`Canvas/Button/Text (TMP)`", text)
        self.assertIn("| TextMeshProUGUI |", text)
        self.assertIn("handler `Canvas/Button`", text)
        self.assertIn("### UI Toolkit", text)
        self.assertIn("**Y inverted:** yes", text)
        self.assertIn("**Panel screen:** 25.60, 1425.60", text)
        self.assertIn("_No hits._", text)
        self.assertIn("### Warnings", text)
        self.assertIn("- sample warning", text)
        self.assertNotIn("count:", text)

    def test_legacy_single_framework_probe_renders_markdown(self) -> None:
        result = {
            "extensionId": "chievfx.ugui",
            "runtimeAvailable": True,
            "count": 1,
            "coordinateConvention": {
                "origin": "bottom-left",
                "normalizedPosition": {"x": 0.5, "y": 0.99},
                "screenPosition": {"x": 1280, "y": 1425.6},
                "screenSize": {"x": 2560, "y": 1440},
            },
            "stack": [
                {
                    "i": 0,
                    "path": "Canvas/Panel",
                    "type": "Image",
                    "raycastTarget": True,
                }
            ],
        }

        text = mcp.format_ugui_runtime_probe_text(result)

        self.assertIn("### uGUI", text)
        self.assertIn("normalized 0.50, 0.99 · screen 1280.00, 1425.60", text)
        self.assertIn("`Canvas/Panel`", text)
        self.assertNotIn("### UI Toolkit", text)

    def test_adapters_probe_renders_markdown(self) -> None:
        result = {
            "runtimeAvailable": True,
            "count": 0,
            "maxRows": 256,
            "truncated": False,
            "probe": {
                "origin": "bottom-left",
                "uiToolkitYInverted": True,
                "screenSize": {"x": 2560, "y": 1440},
                "normalized": {"x": 0.01, "y": 0.01},
                "screen": {"x": 25.5999985, "y": 14.4},
                "uiToolkitScreen": {"x": 25.5999985, "y": 1425.6},
            },
            "adapters": [
                {
                    "framework": "ugui",
                    "available": True,
                    "probed": True,
                    "count": 0,
                    "warnings": ["enter Play Mode"],
                },
                {
                    "framework": "uitoolkit",
                    "available": True,
                    "probed": True,
                    "count": 0,
                    "warnings": ["enter Play Mode"],
                },
            ],
        }

        text = mcp.format_ugui_runtime_probe_text(result)

        self.assertIn("## Runtime UI probe", text)
        self.assertIn("normalized 0.01, 0.01", text)
        self.assertIn("### uGUI", text)
        self.assertIn("enter Play Mode", text)
        self.assertIn("### UI Toolkit", text)
        self.assertIn("_No hits._", text)


if __name__ == "__main__":
    unittest.main()
