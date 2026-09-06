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
NATIVE_PUBLISHER = ROOT / "host" / "B3DPublisherHost" / "CfrnOfflineHtmlPublisher.cs"
LEGACY_COMPACTOR = ROOT / "host" / "B3DPublisherHost" / "CompactOfflineHtml.cs"


class PublisherRegressionTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        if not SAMPLE.exists():
            raise unittest.SkipTest("Reference B3D sample is absent")

    def test_autonomous_html_is_created(self) -> None:
        # Historical viewer regression. The Windows release publisher is tested
        # separately below and does not use this B3D reconstruction path.
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

    def test_windows_release_publisher_contract(self) -> None:
        self.assertTrue(NATIVE_PUBLISHER.exists())
        text = NATIVE_PUBLISHER.read_text(encoding="utf-8")

        # One direct CFRN -> compact offline HTML path. No second ProcessExit
        # compactor and no read-only file lock are allowed in the release host.
        self.assertFalse(LEGACY_COMPACTOR.exists())
        self.assertEqual(text.count("ProcessExit +="), 1)
        self.assertNotIn("FileAttributes.ReadOnly", text)
        self.assertIn("pre-triangulated CFRN geometry only", text)
        self.assertIn("Convert.ToBase64String(positionBytes)", text)
        self.assertIn("clientInstallRequired = false", text)
        self.assertIn("cloudRequests = false", text)

        # Established viewer UX and offline self-checks.
        self.assertIn("Снять выделение", text)
        self.assertIn("e.key==='Escape'", text)
        self.assertIn("Прозрачность", text)
        self.assertIn("Рёбра", text)
        self.assertIn("html.Contains(\"http://\"", text)
        self.assertIn("html.Contains(\"https://\"", text)
        self.assertIn("html.Contains(\"<script src=\"", text)

        # Production host must explicitly reject reconstruction/conversion routes.
        self.assertIn("Geometry reconstruction was deliberately not attempted", text)
        self.assertIn("OBJ/3DS/DAE conversion", text)


if __name__ == "__main__":
    unittest.main()
