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
BRIDGE = HOST / "Bazis24FinalMeshPublisher.js"
CSPROJ = HOST / "B3DPublisherHost.csproj"


class PublisherRegressionTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        if not SAMPLE.exists():
            raise unittest.SkipTest("Reference B3D sample is absent")

    def test_legacy_research_viewer_regression(self) -> None:
        # Historical parser/geometry regression only. It is intentionally NOT
        # the production B3D-Publisher route.
        with tempfile.TemporaryDirectory() as td:
            out = Path(td) / "table.html"
            result = publish(SAMPLE, out)
            self.assertTrue(out.exists())
            self.assertEqual(result["panel_count"], 37)
            self.assertEqual(result["geometry_errors"], 0)

    def test_production_uses_official_bazis_final_mesh_api(self) -> None:
        self.assertTrue(PROGRAM.exists())
        self.assertTrue(BRIDGE.exists())
        self.assertTrue(CSPROJ.exists())
        program = PROGRAM.read_text(encoding="utf-8")
        bridge = BRIDGE.read_text(encoding="utf-8")
        csproj = CSPROJ.read_text(encoding="utf-8")

        # The EXE contains and deploys the official Script API bridge itself.
        self.assertIn('EmbeddedResource Include="Bazis24FinalMeshPublisher.js"', csproj)
        self.assertIn("InstallOfficialMeshBridge", program)
        self.assertIn("TryInvokePublisherScript", program)
        self.assertIn("ValidatePublishedHtml", program)

        # Geometry comes from already-built BAZIS triangles, not from B3D.
        for token in (
            "IsMesh()", "AsMesh()", "TriLists", "surface.Triangles",
            "Vertex1", "Vertex2", "Vertex3", "Normal1", "Normal2", "Normal3",
            "TexCoord1", "TexCoord2", "TexCoord3", "ToGlobal", "NToGlobal",
        ):
            self.assertIn(token, bridge)

        # Material/texture data is taken from BAZIS and embedded locally.
        self.assertIn("MaterialName", bridge)
        self.assertIn("DiffuseColor", bridge)
        self.assertIn("PathAbsolute", bridge)
        self.assertIn(";base64,", bridge)

        # Edges are derived only from BAZIS' final triangle topology. Coplanar
        # triangulation diagonals must not be rendered as cabinet edges.
        self.assertIn("featureEdges", bridge)
        self.assertIn("creaseAngleDeg", bridge)
        self.assertIn("gl.LINES", bridge)
        self.assertNotIn("gl.LINE_LOOP", bridge)

        # Arbitrary image dimensions must remain valid under the WebGL1 fallback.
        self.assertIn("isPowerOfTwo", bridge)
        self.assertIn("gl.CLAMP_TO_EDGE", bridge)
        self.assertIn("gl.LINEAR_MIPMAP_LINEAR", bridge)

        # Established viewer UX.
        self.assertIn("Снять выделение", bridge)
        self.assertIn("e.key==='Escape'", bridge)
        self.assertIn("Прозрачность", bridge)
        self.assertIn("Рёбра", bridge)

        # Offline/self-contained contract and forbidden production routes.
        self.assertIn("<script\\s+src=", bridge)
        self.assertIn("https?:\\/\\/", bridge)
        self.assertNotIn("ExportModelMeshFormat(", bridge)
        self.assertNotIn("UploadModelFromStream", program)
        self.assertNotIn("TryInvokeNativeWebViewer", program)
        self.assertNotIn("B3D-Native-Capture_", program)
        self.assertNotIn("Cfrn", program)

    def test_production_host_has_no_automatic_research_initializers(self) -> None:
        for path in HOST.glob("*.cs"):
            text = path.read_text(encoding="utf-8")
            self.assertNotIn("[ModuleInitializer]", text, path.name)
            self.assertNotIn("ProcessExit +=", text, path.name)


if __name__ == "__main__":
    unittest.main()
