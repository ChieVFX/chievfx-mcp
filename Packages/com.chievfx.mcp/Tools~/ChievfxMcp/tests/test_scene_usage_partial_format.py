import sys
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class SceneUsagePartialFormatTests(unittest.TestCase):
    """A usage scan stopped by its time budget answered refs:0 in the compact text with no word that it was partial."""

    def test_asset_usage_says_the_scan_was_partial(self) -> None:
        result = {
            "asset": {"path": "Assets/M.mat", "name": "M", "guid": "f1607eeeff209434a86b5e39afd8dd00"},
            "referenceCount": 0,
            "gameObjectCount": 0,
            "locationCount": 0,
            "totalObjects": 62428,
            "scannedObjects": 2176,
            "truncated": True,
            "scanWarnings": ["The scan stopped after 4 s at 2176 of 62428 objects: counts and lists cover only that part."],
        }
        text = mcp.format_scene_usage_asset_detail_text(result)
        self.assertIn("usage refs:0", text)
        self.assertIn("partial: scanned 2176 of 62428 objects", text)

    def test_a_full_scan_has_no_partial_line(self) -> None:
        result = {"asset": {"path": "Assets/M.mat"}, "referenceCount": 3, "gameObjectCount": 2, "locationCount": 3, "totalObjects": 40}
        self.assertNotIn("partial", mcp.format_scene_usage_asset_detail_text(result))


if __name__ == "__main__":
    unittest.main()
