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


if __name__ == "__main__":
    unittest.main()
