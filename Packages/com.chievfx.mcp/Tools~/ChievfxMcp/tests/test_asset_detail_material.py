import sys
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class AssetDetailMaterialTests(unittest.TestCase):
    """The asset row of a material said only "[Material]": the shader of a material on screen took a script."""

    def test_material_shader_keywords_and_textures_are_shown(self) -> None:
        text = mcp.format_asset_detail_text(
            {
                "asset": {
                    "name": "RobotoCondensed-RegularBold SDF",
                    "type": "Material",
                    "path": "Assets/Menu/Fonts/Roboto/RobotoCondensed-RegularBold SDF.mat",
                    "fullType": "UnityEngine.Material",
                    "material": {
                        "shader": "TextMeshPro/Distance Field",
                        "shaderPath": "Assets/TextMesh Pro/Shaders/TMP_SDF.shader",
                        "renderQueue": 3000,
                        "keywords": ["OUTLINE_ON"],
                        "textures": ["_MainTex=RobotoCondensed-Regular SDF Atlas 2048x2048"],
                    },
                }
            }
        )
        self.assertIn("shader: \"TextMeshPro/Distance Field\"", text)
        self.assertIn("queue:3000", text)
        self.assertIn("keywords: OUTLINE_ON", text)
        self.assertIn("_MainTex=RobotoCondensed-Regular SDF Atlas 2048x2048", text)


class MaterialProfileSummaryTests(unittest.TestCase):
    def test_shader_rows_and_partial_scan_are_shown(self) -> None:
        text = mcp.format_material_profile_summary_text(
            {
                "materialCount": 556,
                "shaderGroupCount": 1,
                "textureCount": 1772,
                "rendererCount": 0,
                "truncated": True,
                "scanWarnings": ["The scan stopped after 4 s at 9000 of 410000 objects: counts and lists cover only that part."],
                "countByShader": [{"shaderName": "TextMeshPro/Distance Field", "materialCount": 12, "rendererReferenceCount": 30, "textureCount": 4}],
            }
        )
        self.assertIn("partial: The scan stopped after 4 s", text)
        self.assertIn("TextMeshPro/Distance Field", text)


class AssetFindNoteTests(unittest.TestCase):
    def test_component_type_note_is_shown(self) -> None:
        # asset-find type:CarControl found 0 rows silently, which read as "no prefab uses it".
        text = mcp.format_asset_find_text(
            {"count": 0, "totalAssetGuids": 0, "assetDatabaseFilter": "t:CarControl a:assets", "assets": [],
             "note": "type filters asset types: CarControl is a component"}
        )
        self.assertIn("note: type filters asset types: CarControl is a component", text)


if __name__ == "__main__":
    unittest.main()
