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
HOST = ROOT / "host" / "B3DPublisherHost"
PROGRAM = HOST / "Program.cs"
EXPORTER = HOST / "Viewer3DExporter.cs"
VRML = HOST / "VrmlParser.cs"
HTML = HOST / "OfflineHtmlPublisher.cs"
CSPROJ = HOST / "B3DPublisherHost.csproj"


class PublisherRegressionTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        if not SAMPLE.exists():
            raise unittest.SkipTest("Reference B3D sample is absent")

    def test_legacy_research_viewer_regression(self) -> None:
        # Historical parser regression only. Production never uses this parser.
        with tempfile.TemporaryDirectory() as td:
            out = Path(td) / "table.html"
            result = publish(SAMPLE, out)
            self.assertTrue(out.exists())
            self.assertEqual(result["panel_count"], 37)
            self.assertEqual(result["geometry_errors"], 0)

    def test_production_is_scriptless_viewer3d_pipeline(self) -> None:
        for path in (PROGRAM, EXPORTER, VRML, HTML, CSPROJ):
            self.assertTrue(path.exists(), path)
        program = PROGRAM.read_text(encoding="utf-8")
        exporter = EXPORTER.read_text(encoding="utf-8")
        vrml = VRML.read_text(encoding="utf-8")
        html = HTML.read_text(encoding="utf-8")
        csproj = CSPROJ.read_text(encoding="utf-8")
        production = program + exporter + vrml + html + csproj

        # B3D interpretation and mesh creation are delegated to the official
        # BAZIS Viewer3D utility. WRL is temporary and deleted after packaging.
        self.assertIn("Viewer3DExporter.ExportToTemporaryWrl", program)
        self.assertIn("VrmlParser.Parse", program)
        self.assertIn("OfflineHtmlPublisher.Publish", program)
        self.assertIn("Directory.Delete(tempDirectory, true)", program)
        self.assertIn("Viewer24.exe", exporter)
        self.assertIn("VRML", exporter)
        self.assertIn("model.wrl", exporter)
        self.assertIn('FindByAutomationId(dialog, "1136")', exporter)
        self.assertIn('FindByAutomationId(dialog, "1001")', exporter)

        # Only the official VRML output is interpreted by production.
        for token in (
            "IndexedFaceSet", "coordIndex", "Coordinate", "TextureCoordinate",
            "texCoordIndex", "Normal", "normalIndex", "ImageTexture", "diffuseColor",
        ):
            self.assertIn(token, vrml)

        # One self-contained HTML with compact binary mesh payload and textures.
        self.assertIn("local-view-bazis-viewer3d-wrl-1", html)
        self.assertIn("Float32Base64", html)
        self.assertIn("IndexBase64", html)
        self.assertIn(";base64,", html)
        self.assertIn("FeatureEdges", html)
        self.assertIn("gl.drawElements", html)
        self.assertIn("gl.LINES", html)
        self.assertIn("preserveDrawingBuffer:true", html)
        self.assertIn("gl.readPixels", html)
        self.assertIn("Снять выделение", html)
        self.assertIn("e.key==='Escape'", html)
        self.assertIn("Прозрачность", html)
        self.assertIn("Рёбра", html)
        self.assertNotIn("<script src=", html.lower())
        self.assertNotIn("http://", html.lower())
        self.assertNotIn("https://", html.lower())

        # Explicitly reject the blocked/forbidden production routes.
        for forbidden in (
            "InstallOfficialMeshBridge", "TryInvokePublisherScript", "currentFileData",
            "UploadModelFromStream", "TryInvokeNativeWebViewer", "B3D-Native-Capture_",
            "Cfrn", "ExportModelMeshFormat", ".obj", ".3ds", ".dae",
        ):
            self.assertNotIn(forbidden, production)
        self.assertNotIn("Bazis24FinalMeshPublisher.js", csproj)
        self.assertFalse((HOST / "Bazis24FinalMeshPublisher.js").exists())
        self.assertNotIn(".publisher.txt", program)

    def test_production_host_has_no_automatic_research_initializers(self) -> None:
        for path in HOST.glob("*.cs"):
            text = path.read_text(encoding="utf-8")
            self.assertNotIn("[ModuleInitializer]", text, path.name)
            self.assertNotIn("ProcessExit +=", text, path.name)


if __name__ == "__main__":
    unittest.main()
