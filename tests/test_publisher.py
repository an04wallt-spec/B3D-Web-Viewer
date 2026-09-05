from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
for p in (ROOT / "parser", ROOT / "geometry", ROOT / "publisher"):
    sys.path.insert(0, str(p))

from publish import publish  # noqa: E402

SAMPLE = ROOT / "samples" / "Стол_3100х750х1300.b3d"


class PublisherRegressionTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        if not SAMPLE.exists():
            raise unittest.SkipTest("Reference B3D sample is absent")

    def test_autonomous_html_is_created(self) -> None:
        with tempfile.TemporaryDirectory() as td:
            out = Path(td) / "table.html"
            result = publish(SAMPLE, out)
            self.assertTrue(out.exists())
            self.assertEqual(result["panel_count"], 37)
            self.assertEqual(result["geometry_errors"], 0)
            text = out.read_text(encoding="utf-8")
            self.assertIn("b3d-offline-view-1", text)
            self.assertIn("WebGL", text)
            self.assertNotIn("<script src=", text)
            self.assertNotIn("https://", text)
            self.assertNotIn("http://", text)
            self.assertGreater(out.stat().st_size, SAMPLE.stat().st_size)
            self.assertLess(out.stat().st_size, 2_000_000)


if __name__ == "__main__":
    unittest.main()
