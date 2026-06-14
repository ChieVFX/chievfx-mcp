import sys
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class GameObjectFormattingTests(unittest.TestCase):
    def test_gameobject_get_is_not_advertised(self) -> None:
        names = [tool["name"] for tool in mcp.TOOLS]
        self.assertIn("asset-find", names)
        self.assertIn("asset-find", mcp.DEFAULT_REQUIRED_TOOL_IDS)
        self.assertIn("asset-create", names)
        self.assertIn("asset-create", mcp.DEFAULT_REQUIRED_TOOL_IDS)
        self.assertIn("asset-delete", names)
        self.assertIn("asset-delete", mcp.DEFAULT_REQUIRED_TOOL_IDS)
        self.assertIn("folder-ensure", names)
        self.assertIn("folder-ensure", mcp.DEFAULT_REQUIRED_TOOL_IDS)
        self.assertIn("scene-create", names)
        self.assertIn("gameobject-find", names)
        self.assertIn("gameobject-create", names)
        self.assertIn("gameobject-update", names)
        self.assertIn("gameobject-component-update-or-create", names)
        self.assertIn("gameobject-transform-get", names)
        self.assertIn("gameobject-transform-update", names)
        self.assertNotIn("gameobject-get", names)
        self.assertNotIn("gameobject-transform-modify", names)

        find_tool = next(tool for tool in mcp.TOOLS if tool["name"] == "gameobject-find")
        properties = find_tool["inputSchema"]["properties"]
        self.assertIn("instanceId", properties)
        self.assertIn("includeDetails", properties)
        self.assertNotIn("requireSingle", properties)

        duplicate_tool = next(tool for tool in mcp.TOOLS if tool["name"] == "gameobject-duplicate")
        self.assertIn("count", duplicate_tool["inputSchema"]["properties"])
        self.assertNotIn("count", mcp.advertised_input_schema(duplicate_tool)["properties"])

        component_get_tool = next(tool for tool in mcp.TOOLS if tool["name"] == "gameobject-component-get")
        self.assertIn("componentIndex", component_get_tool["inputSchema"]["properties"])

        component_update_tool = next(tool for tool in mcp.TOOLS if tool["name"] == "gameobject-component-update-or-create")
        component_update_properties = component_update_tool["inputSchema"]["properties"]
        self.assertIn("isCreateIfNone", component_update_properties)
        self.assertIn("writeNonSerialized", component_update_properties)
        self.assertIn("properties", component_update_properties)

        package_add_tool = next(tool for tool in mcp.TOOLS if tool["name"] == "package-add")
        self.assertIn("install those URL dependencies first recursively", package_add_tool["description"])

        asset_create_tool = next(tool for tool in mcp.TOOLS if tool["name"] == "asset-create")
        self.assertIn("Scripts, shaders, uxml, uss, json", asset_create_tool["description"])

        asset_find_tool = next(tool for tool in mcp.TOOLS if tool["name"] == "asset-find")
        asset_find_properties = asset_find_tool["inputSchema"]["properties"]
        self.assertIn("includeSubassets", asset_find_properties)
        self.assertIn("maxResults", asset_find_properties)

    def test_asset_find_text_uses_compact_rows(self) -> None:
        result = {
            "count": 2,
            "totalAssetGuids": 1,
            "maxResults": 80,
            "truncated": False,
            "assetDatabaseFilter": "wood t:Material",
            "assets": [
                {
                    "name": "Wood",
                    "path": "Assets/Materials/Wood.mat",
                    "guid": "0123456789abcdef0123456789abcdef",
                    "mainType": "Material",
                    "labels": ["ui"],
                    "isMainAsset": True,
                    "localId": 2100000,
                    "resourceUri": "chievfx://asset/0123456789abcdef0123456789abcdef",
                },
                {
                    "name": "Preview",
                    "path": "Assets/Materials/Wood.mat",
                    "guid": "0123456789abcdef0123456789abcdef",
                    "type": "Texture2D",
                    "labels": [],
                    "isMainAsset": False,
                    "localId": 2800000,
                    "resourceUri": "chievfx://asset/0123456789abcdef0123456789abcdef/id/2800000",
                },
            ],
        }

        text = mcp.format_asset_find_text(result)

        self.assertIn("(2 shown, 1 asset guid) filter:wood t:Material", text)
        self.assertIn("detail: chievfx://asset/{guid} or chievfx://asset/{guid}/id/{localId}", text)
        self.assertIn("- Assets/Materials/Wood.mat name:Wood guid:0123456789abcdef0123456789abcdef [Material, labels:ui]", text)
        self.assertNotIn("detail: chievfx://asset/0123456789abcdef0123456789abcdef", text)
        self.assertIn("[Texture2D, localId:2800000]", text)

    def test_gameobject_find_text_uses_compact_rows(self) -> None:
        result = {
            "source": "activeScene",
            "sceneName": "SampleScene",
            "scenePath": "Assets/Scenes/SampleScene.unity",
            "count": 1,
            "totalMatches": 1,
            "maxResults": 100,
            "truncated": False,
            "objects": [
                {
                    "name": "Some_Light",
                    "path": "Main Camera/Some_Light",
                    "instanceId": -187268,
                    "activeSelf": True,
                    "activeInHierarchy": True,
                    "scenePath": "Assets/Scenes/SampleScene.unity",
                    "componentTypes": ["Transform", "Light", "UniversalAdditionalLightData"],
                    "componentTypesTruncated": False,
                }
            ],
        }

        text = mcp.format_gameobject_find_text(result)

        self.assertEqual(
            text,
            "scene: SampleScene (1 shown, 1 match)\n"
            "- Main Camera/Some_Light (id: -187268)\n"
            "  components[3]: Transform, Light, UniversalAdditionalLightData",
        )
        self.assertNotIn("name:", text)
        self.assertNotIn("scenePath", text)
        self.assertNotIn("componentTypesTruncated", text)

    def test_gameobject_find_text_marks_inactive_and_truncated_components(self) -> None:
        result = {
            "source": "activeScene",
            "count": 1,
            "objects": [
                {
                    "path": "Hidden",
                    "instanceId": 42,
                    "activeSelf": False,
                    "activeInHierarchy": False,
                    "componentTypes": ["Transform"],
                    "componentTypesTruncated": True,
                }
            ],
        }

        text = mcp.format_gameobject_find_text(result)

        self.assertIn("- Hidden (id: 42, inactiveSelf, inactiveHierarchy)", text)
        self.assertIn("components[1]: Transform, ...", text)

    def test_component_get_text_focuses_component_and_fields(self) -> None:
        result = {
            "source": "activeScene",
            "sceneName": "SampleScene",
            "scenePath": "Assets/Scenes/SampleScene.unity",
            "gameObject": {
                "name": "Some_Light",
                "path": "Main Camera/Some_Light",
                "instanceId": -187268,
                "activeSelf": True,
                "activeInHierarchy": True,
                "scenePath": "Assets/Scenes/SampleScene.unity",
                "componentTypes": ["Transform", "Light"],
                "componentTypesTruncated": False,
            },
            "component": {
                "type": "Light",
                "fullType": "UnityEngine.Light",
                "instanceId": -187566,
                "enabled": True,
                "serializedFieldsMode": "inspector",
                "serializedFields": [
                    {"typeName": "Enum", "name": "m_Type", "value": "Spot"},
                    {"typeName": "Float", "name": "m_Intensity", "value": 1.5},
                ],
            },
            "serializedDataTruncated": False,
        }

        text = mcp.format_gameobject_component_get_text(result)

        self.assertEqual(
            text,
            "Light enabled\n"
            "- m_Type:Enum = Spot\n"
            "- m_Intensity:Float = 1.5",
        )
        self.assertNotIn("componentTypes", text)
        self.assertNotIn("scenePath", text)

    def test_component_value_formats_rgba_compactly(self) -> None:
        self.assertEqual(
            mcp.format_component_value("RGBA(1.000, 1.000, 1.000, 1.000)"),
            '"RGBA(1.0,1.0,1.0,1.0)"',
        )

    def test_transform_get_text_is_human_readable(self) -> None:
        result = {
            "success": True,
            "isWorld": False,
            "transform": {
                "position": {"x": 1.25, "y": 2.5, "z": 3.75},
                "rotationEuler": {"x": 0, "y": 45.0000038, "z": 0},
                "scale": {"x": 1.5, "y": 1.5, "z": 1.5},
            },
        }

        text = mcp.format_gameobject_transform_get_text(result)

        self.assertEqual(
            text,
            "space: local\n"
            "position: 1.25, 2.5, 3.75\n"
            "rotationEuler: 0, 45, 0\n"
            "scale: 1.5, 1.5, 1.5",
        )

    def test_gameobject_find_text_supports_detail_rows_and_components(self) -> None:
        result = {
            "source": "activeScene",
            "count": 1,
            "totalMatches": 1,
            "includeDetails": True,
            "objects": [
                {
                    "path": "Main Camera/Some_Light",
                    "instanceId": -187268,
                    "activeSelf": True,
                    "activeInHierarchy": True,
                    "componentTypes": ["Transform", "Light"],
                    "componentTypesTruncated": False,
                    "tag": "Untagged",
                    "layer": 0,
                    "isStatic": False,
                    "childCount": 0,
                    "parentPath": "Main Camera",
                    "components": [
                        {"type": "Transform", "instanceId": -1},
                        {"type": "Light", "instanceId": -2, "enabled": True},
                    ],
                }
            ],
        }

        text = mcp.format_gameobject_find_text(result)

        self.assertEqual(
            text,
            "(1 shown, 1 match)\n"
            "- Main Camera/Some_Light (id: -187268)\n"
            "  details: tag: Untagged, layer: 0, children: 0\n"
            "  components[2]: Transform, Light enabled",
        )
        self.assertNotIn("scenePath", text)

    def test_gameobject_find_text_supports_detail_rows_without_components(self) -> None:
        result = {
            "source": "activeScene",
            "count": 1,
            "totalMatches": 1,
            "objects": [
                {
                    "path": "Main Camera",
                    "instanceId": 78848,
                    "activeSelf": True,
                    "activeInHierarchy": True,
                    "componentTypes": ["Transform", "Camera"],
                    "tag": "MainCamera",
                    "layer": 0,
                    "isStatic": False,
                    "childCount": 1,
                }
            ],
        }

        text = mcp.format_gameobject_find_text(result)

        self.assertEqual(
            text,
            "(1 shown, 1 match)\n"
            "- Main Camera (id: 78848)\n"
            "  details: tag: MainCamera, layer: 0, children: 1\n"
            "  components[2]: Transform, Camera",
        )

    def test_gameobject_hierarchy_text_uses_indented_compact_rows(self) -> None:
        result = {
            "sceneName": "SampleScene",
            "count": 2,
            "totalObjects": 2,
            "maxDepth": 3,
            "maxResults": 50,
            "truncated": False,
            "depthLimited": False,
            "roots": [
                {
                    "path": "Main Camera",
                    "instanceId": 1,
                    "activeSelf": True,
                    "activeInHierarchy": True,
                    "componentTypes": ["Transform", "Camera"],
                    "children": [
                        {
                            "path": "Main Camera/Some_Light",
                            "instanceId": 2,
                            "activeSelf": True,
                            "activeInHierarchy": True,
                            "componentTypes": ["Transform", "Light"],
                        }
                    ],
                }
            ],
        }

        text = mcp.format_gameobject_hierarchy_text(result)

        self.assertEqual(
            text,
            "scene:SampleScene count:2 totalObjects:2 maxDepth:3 maxResults:50 truncated:false depthLimited:false\n"
            "• Main Camera (id: 1)\n"
            "  components[2]: Transform, Camera\n"
            "  • Some_Light (id: 2)\n"
            "    components[2]: Transform, Light",
        )

    def test_ugui_ui_tools_use_hierarchy_and_find_rows(self) -> None:
        hierarchy = {
            "count": 1,
            "totalObjects": 2,
            "maxDepth": 3,
            "maxResults": 50,
            "truncated": False,
            "depthLimited": False,
            "roots": [
                {
                    "path": "Canvas",
                    "instanceId": 1,
                    "activeSelf": True,
                    "activeInHierarchy": True,
                    "children": [
                        {
                            "path": "Canvas/Button",
                            "instanceId": 2,
                            "activeSelf": True,
                            "activeInHierarchy": True,
                        }
                    ],
                }
            ],
        }
        detail = {
            "count": 1,
            "totalMatches": 1,
            "objects": [
                {
                    "path": "Canvas/Button",
                    "instanceId": 2,
                    "activeSelf": True,
                    "activeInHierarchy": True,
                    "componentTypes": ["RectTransform", "Image", "Button"],
                    "tag": "Untagged",
                    "layer": 5,
                    "childCount": 1,
                }
            ],
        }
        rect_result = {
            "count": 1,
            "totalMatches": 1,
            "rects": [
                {
                    "path": "Canvas/Button",
                    "instanceId": 2,
                    "rectTransform": {
                        "anchorMin": {"x": 0.5, "y": 0.5},
                        "anchorMax": {"x": 0.5, "y": 0.5},
                        "anchoredPosition": {"x": 10, "y": 20},
                        "sizeDelta": {"x": 100, "y": 40},
                        "pivot": {"x": 0.5, "y": 0.5},
                    },
                }
            ],
        }

        self.assertEqual(
            mcp.format_ugui_ui_hierarchy_text(hierarchy),
            "count:1 totalObjects:2 maxDepth:3 maxResults:50 truncated:false depthLimited:false\n"
            "• Canvas (id: 1)\n"
            "  • Button (id: 2)",
        )
        self.assertNotIn("rect:", mcp.format_ugui_ui_find_text(detail))
        self.assertIn("anchors 0.5, 0.5->0.5, 0.5 pos 10, 20 size 100, 40", mcp.format_ugui_rect_get_text(rect_result))

    def test_package_list_text_omits_descriptions_and_paths(self) -> None:
        result = {
            "count": 2,
            "sourceFilter": "All",
            "directDependenciesOnly": True,
            "offlineMode": False,
            "packages": [
                {
                    "name": "com.unity.services.economy",
                    "displayName": "Economy",
                    "version": "3.5.3",
                    "source": "Registry",
                    "description": "Huge registry description",
                    "isDirectDependency": True,
                    "manifestVersion": "3.5.3",
                    "resolvedPath": "/tmp/Library/PackageCache/com.unity.services.economy",
                    "assetPath": "Packages/com.unity.services.economy",
                    "dependencyCount": 5,
                },
                {
                    "name": "com.chievfx.easy-stateful",
                    "displayName": "Easy Stateful",
                    "version": "1.0.0",
                    "source": "Git",
                    "isDirectDependency": True,
                    "manifestVersion": "https://github.com/ChieVFX/unity-easy-stateful.git?path=Assets/Project",
                },
            ],
        }

        text = mcp.format_package_list_text(result)

        self.assertEqual(
            text,
            "packages:2 directOnly\n"
            "- com.unity.services.economy@3.5.3 name:Economy src:Registry direct\n"
            "- com.chievfx.easy-stateful@1.0.0 name:\"Easy Stateful\" src:Git direct "
            "manifest:\"https://github.com/ChieVFX/unity-easy-stateful.git?path=Assets/Project\"",
        )
        self.assertNotIn("description", text)
        self.assertNotIn("resolvedPath", text)
        self.assertNotIn("dependencyCount", text)

    def test_package_search_text_uses_rows_without_descriptions(self) -> None:
        result = {
            "query": "Economy",
            "count": 2,
            "truncated": False,
            "offlineMode": False,
            "results": [
                {
                    "name": "com.unity.services.economy",
                    "displayName": "Economy",
                    "latestVersion": "3.5.3",
                    "description": "Huge registry description",
                    "isInstalled": False,
                    "availableVersions": ["3.5.3"],
                    "matchRank": 1,
                },
                {
                    "name": "com.unity.services.deployment",
                    "displayName": "Deployment",
                    "latestVersion": "1.7.2",
                    "isInstalled": True,
                    "installedVersion": "1.7.2",
                    "installedSource": "Registry",
                },
            ],
        }

        text = mcp.format_package_search_text(result)

        self.assertEqual(
            text,
            "query:Economy results:2\n"
            "- com.unity.services.economy name:Economy latest:3.5.3 notInstalled\n"
            "- com.unity.services.deployment name:Deployment latest:1.7.2 installed:1.7.2/Registry",
        )
        self.assertNotIn("description", text)
        self.assertNotIn("availableVersions", text)
        self.assertNotIn("matchRank", text)

    def test_package_mutation_text_omits_package_noise(self) -> None:
        result = {
            "operation": "add",
            "packageId": "com.unity.services.economy",
            "completed": True,
            "restoredAfterDomainReload": False,
            "verification": "request-completed",
            "package": {
                "name": "com.unity.services.economy",
                "displayName": "Economy",
                "version": "3.5.3",
                "source": "Registry",
                "description": "Huge registry description",
                "isDirectDependency": True,
                "manifestVersion": "3.5.3",
                "resolvedPath": "/tmp/Library/PackageCache/com.unity.services.economy",
                "assetPath": "Packages/com.unity.services.economy",
                "dependencyCount": 5,
            },
            "manifestChanges": [
                {"name": "com.unity.services.economy", "change": "added", "version": "3.5.3"}
            ],
        }

        text = mcp.format_package_mutation_text(result)

        self.assertEqual(
            text,
            "operation:add packageId:com.unity.services.economy completed verification:request-completed\n"
            "package: com.unity.services.economy@3.5.3 name:Economy src:Registry direct\n"
            "changes:1\n"
            "- com.unity.services.economy change:added version:3.5.3",
        )
        self.assertNotIn("description", text)
        self.assertNotIn("resolvedPath", text)
        self.assertNotIn("dependencyCount", text)

    def test_frame_debugger_control_text_omits_window_rect_noise(self) -> None:
        result = {
            "success": True,
            "window": {
                "title": "Frame Debugger",
                "contentRect": {"x": 1, "y": 2, "width": 3, "height": 4},
                "tabs": [{"title": "Project"}],
            },
            "frameDebugger": {
                "enabled": True,
                "eventCount": 25,
                "currentEventLimit": 1,
                "selectedEventIndex": 0,
            },
        }

        text = mcp.format_frame_debugger_control_text(result)

        self.assertEqual(
            text,
            "success:true enabled:true eventCount:25 currentEventLimit:1 selectedEventIndex:0",
        )
        self.assertNotIn("contentRect", text)
        self.assertNotIn("tabs", text)

    def test_frame_debugger_events_list_text_focuses_draw_call_fields(self) -> None:
        result = {
            "count": 1,
            "totalEvents": 25,
            "startIndex": 0,
            "truncated": True,
            "frameDebugger": {"enabled": True, "eventCount": 25, "currentEventLimit": 1, "selectedEventIndex": 0},
            "events": [
                {
                    "index": 0,
                    "type": "DrawMesh",
                    "name": "Render Mesh",
                    "objectName": "Cube",
                    "objectType": "GameObject",
                    "shader": "Universal Render Pipeline/Lit",
                    "pass": "ForwardLit",
                    "meshName": "Cube",
                    "drawCalls": 1,
                    "vertices": 24,
                    "renderTarget": "CameraColor",
                    "batchBreakReason": "Objects have different materials.",
                }
            ],
        }

        text = mcp.format_frame_debugger_events_list_text(result)

        self.assertEqual(
            text,
            "events:1 total:25 truncated enabled:true eventCount:25 currentEventLimit:1 selectedEventIndex:0\n"
            "- #0 type:DrawMesh name:\"Render Mesh\" obj:Cube/GameObject shader:\"Universal Render Pipeline/Lit\" "
            "pass:ForwardLit mesh:Cube draws:1 verts:24 rt:CameraColor batch:\"Objects have different materials.\"",
        )

    def test_frame_debugger_groups_and_group_drawcalls_are_compact(self) -> None:
        groups_result = {
            "count": 2,
            "totalEvents": 3,
            "frameDebugger": {"enabled": True, "eventCount": 3},
            "groups": [
                {"index": 0, "name": "(RP 0:0) DrawOpaqueObjects", "eventCount": 2, "firstEventIndex": 0, "lastEventIndex": 1},
                {"index": 1, "name": "Bloom", "eventCount": 1, "firstEventIndex": 2, "lastEventIndex": 2},
            ],
        }
        events_result = {
            "group": {"index": 0, "name": "(RP 0:0) DrawOpaqueObjects"},
            "count": 1,
            "totalEvents": 2,
            "events": [
                {"groupIndex": 0, "drawCallIndex": 1, "index": 1, "type": "SRPBatch", "name": "RenderLoop.DrawSRPBatcher"}
            ],
        }

        self.assertEqual(
            mcp.format_frame_debugger_groups_list_text(groups_result),
            "groups:2 events:3 enabled:true eventCount:3\n"
            "- g#0 name:\"(RP 0:0) DrawOpaqueObjects\" events:2 range:0-1\n"
            "- g#1 name:Bloom events:1 range:2-2",
        )
        self.assertEqual(
            mcp.format_frame_debugger_group_events_list_text(events_result),
            "group:0 name:\"(RP 0:0) DrawOpaqueObjects\" drawcalls:1 total:2\n"
            "- g#0 d#1 event:1 type:SRPBatch name:RenderLoop.DrawSRPBatcher",
        )


if __name__ == "__main__":
    unittest.main()
