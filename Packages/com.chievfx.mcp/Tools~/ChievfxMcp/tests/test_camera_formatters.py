import sys
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chievfx_mcp_server as mcp  # noqa: E402


class CameraFormatterTests(unittest.TestCase):
    def test_orthographic_lens_shows_its_size_not_fov(self) -> None:
        row = {"name": "Cam", "lens": {"fieldOfView": 40.0, "orthographicSize": 5.0, "orthographic": True}}
        self.assertIn("lens:ortho size=5", mcp.format_camera_tool_row(row))

    def test_perspective_lens_shows_fov(self) -> None:
        row = {"name": "Cam", "lens": {"fieldOfView": 40.0, "orthographicSize": 5.0, "orthographic": False}}
        self.assertIn("lens:fov=40", mcp.format_camera_tool_row(row))

    def test_orthographic_unity_camera_row_shows_its_size_not_fov(self) -> None:
        row = {"name": "Cam", "fieldOfView": 60.0, "orthographic": True, "orthographicSize": 10.0}
        text = mcp.format_camera_tool_row(row)
        self.assertIn("ortho size:10", text)
        self.assertNotIn("fov:", text)

    def test_camera_detail_shows_target_and_lens_without_linking_to_itself(self) -> None:
        row = {
            "name": "Cam", "path": "Cam", "instanceId": 5, "detailUri": "chievfx://extensions/chievfx.cameras/cinemachine/camera/5",
            "lens": {"fieldOfView": 40.0, "orthographicSize": 10.0, "orthographic": True},
            "target": {"name": "Button", "path": "Canvas/Button", "instanceId": 7},
        }
        text = mcp.format_cameras_extension_resource_text("cameras-cinemachine-camera-detail", {"target": row})
        self.assertNotIn("detail:", text)
        self.assertIn("target: Canvas/Button (id:7)", text)
        self.assertIn("orthographicSize=10", text)


if __name__ == "__main__":
    unittest.main()
