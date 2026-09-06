from __future__ import annotations

import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
for p in (ROOT / "parser", ROOT / "geometry", ROOT / "app"):
    sys.path.insert(0, str(p))

from b3d_parser import parse_current_model  # noqa: E402
from final_geometry import extract_final_meshes  # noqa: E402
from direct_publisher import publish  # noqa: E402

SAMPLE = ROOT / "samples" / "Стол_3100х750х1300.b3d"
DIRECT = ROOT / "app" / "direct_publisher.py"
FINAL_GEOM = ROOT / "geometry" / "final_geometry.py"


class DirectPublisherRegressionTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        if not SAMPLE.exists():
            raise unittest.SkipTest("Reference B3D sample is absent")

    def test_direct_b3d_final_geometry(self) -> None:
        model, _meta = parse_current_model(SAMPLE)
        geom = extract_final_meshes(model, 3.0)
        self.assertEqual(geom["status"], "direct-b3d-final-csg")
        self.assertEqual(geom["panel_count"], 37)
        self.assertEqual(geom["errors"], [])

        cuts = [cut for panel in geom["panels"] for cut in panel["cuts"]]
        self.assertEqual(len(cuts), 36)
        cut_types = {cut["params"].get("typ") for cut in cuts}
        self.assertTrue({1, 4, 7}.issubset(cut_types))

        bounds = geom["bounds"]
        self.assertIsNotNone(bounds)
        for actual, expected in zip(bounds["min"], (0.0, 0.0, 0.0)):
            self.assertAlmostEqual(actual, expected, delta=0.1)
        for actual, expected in zip(bounds["max"], (3100.0, 750.0, 1300.0)):
            self.assertAlmostEqual(actual, expected, delta=0.1)

    def test_production_publishes_one_offline_html(self) -> None:
        with tempfile.TemporaryDirectory() as td:
            out = Path(td) / "table.html"
            output, payload = publish(SAMPLE, out)
            self.assertEqual(output, out.resolve())
            self.assertTrue(out.exists())
            self.assertEqual(payload["format"], "local-view-direct-b3d-2")
            self.assertEqual(payload["panel_count"], 37)

            html = out.read_text(encoding="utf-8")
            for token in (
                "local-view-direct-b3d-2",
                "Снять выделение",
                "Escape",
                "gl.readPixels",
                "gl.LINES",
                "Прозрачность",
                "preserveDrawingBuffer:true",
            ):
                self.assertIn(token, html)
            self.assertNotIn("<script src=", html)
            self.assertNotIn("http://", html)
            self.assertNotIn("https://", html)
            self.assertGreater(len(html), 100_000)

    def test_production_route_has_no_old_bridge(self) -> None:
        production = DIRECT.read_text(encoding="utf-8") + FINAL_GEOM.read_text(encoding="utf-8")
        for required in (
            "parse_current_model",
            "extract_final_meshes",
            "decode_contour_blob",
            "Manifold",
            "local-view-direct-b3d-2",
        ):
            self.assertIn(required, production)
        for forbidden in (
            "Viewer3D",
            "Viewer24.exe",
            "VrmlParser",
            ".wrl",
            "UploadModelFromStream",
            "TryInvokePublisherScript",
            "currentFileData",
            "WebViewer.dll",
        ):
            self.assertNotIn(forbidden, production)


if __name__ == "__main__":
    unittest.main()
