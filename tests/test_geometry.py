from __future__ import annotations

import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PARSER_DIR = ROOT / "parser"
GEOMETRY_DIR = ROOT / "geometry"
SAMPLE = ROOT / "samples" / "Стол_3100х750х1300.b3d"

sys.path.insert(0, str(PARSER_DIR))
sys.path.insert(0, str(GEOMETRY_DIR))

import b3d_parser as bp  # noqa: E402
import geometry as geo  # noqa: E402


class GeometryRegressionTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        if not SAMPLE.exists():
            raise unittest.SkipTest("Reference B3D sample is absent: " + str(SAMPLE))
        cls.model, _ = bp.parse_current_model(SAMPLE)
        cls.geom = geo.extract_panel_meshes(cls.model)

    def test_all_panels_are_meshed(self) -> None:
        self.assertEqual(self.geom["panel_count"], 37)
        self.assertEqual(self.geom["errors"], [])

    def test_world_bounds_match_source_model(self) -> None:
        bounds = self.geom["bounds"]
        self.assertIsNotNone(bounds)
        assert bounds is not None
        size = [bounds["max"][i] - bounds["min"][i] for i in range(3)]
        self.assertAlmostEqual(size[0], 3100.0, places=5)
        self.assertAlmostEqual(size[1], 750.0, places=5)
        self.assertAlmostEqual(size[2], 1300.0, places=5)

    def test_hook_strip_mesh(self) -> None:
        panel = next(p for p in self.geom["panels"] if p["id"] == 1023)
        self.assertEqual(panel["name"], "4.1_планка зацеп")
        self.assertAlmostEqual(panel["thickness"], 16.0)
        self.assertEqual(len(panel["local_contour"]["outer_points"]), 4)
        self.assertEqual(len(panel["mesh"]["vertices"]), 8)
        self.assertEqual(len(panel["mesh"]["triangles"]), 12)
        self.assertEqual(len(panel["cuts"]), 1)
        self.assertEqual(panel["cuts"][0]["params"]["typ"], 1)

    def test_stone_top_uses_true_arcs(self) -> None:
        panel = next(p for p in self.geom["panels"] if p["id"] == 1069)
        # Four R22 outer corner arcs sampled at <=7.5 degrees create >4 points.
        self.assertGreater(len(panel["local_contour"]["outer_points"]), 40)
        self.assertEqual(len(panel["cuts"]), 8)
        self.assertTrue(all(c["params"]["typ"] == 7 for c in panel["cuts"]))


if __name__ == "__main__":
    unittest.main()
