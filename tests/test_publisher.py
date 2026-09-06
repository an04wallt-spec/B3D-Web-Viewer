from __future__ import annotations

import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
HOST = ROOT / "host" / "B3DPublisherHost"
PROGRAM = HOST / "Program.cs"
EXPORTER = HOST / "Viewer3DExporter.cs"
VRML = HOST / "VrmlParser.cs"
HTML = HOST / "OfflineHtmlPublisher.cs"
PROBE = HOST / "B3DHandlerProbe.cs"
PROJECT = HOST / "B3DPublisherHost.csproj"


class PublisherProductionPolicyTest(unittest.TestCase):
    def test_production_consumes_bazis_emitted_final_geometry(self) -> None:
        for path in (PROGRAM, EXPORTER, VRML, HTML, PROBE, PROJECT):
            self.assertTrue(path.exists(), path)

        program = PROGRAM.read_text(encoding="utf-8")
        exporter = EXPORTER.read_text(encoding="utf-8")
        vrml = VRML.read_text(encoding="utf-8")
        html = HTML.read_text(encoding="utf-8")
        production = program + exporter + vrml + html + PROBE.read_text(encoding="utf-8") + PROJECT.read_text(encoding="utf-8")

        self.assertIn("Viewer3DExporter.ExportToTemporaryWrl", program)
        self.assertIn("VrmlParser.Parse", program)
        self.assertIn("OfflineHtmlPublisher.Publish", program)
        self.assertIn("Directory.Delete(tempDirectory, true)", program)
        self.assertIn("Viewer24.exe", exporter)
        self.assertIn("model.wrl", exporter)

        for token in (
            "IndexedFaceSet", "coordIndex", "Coordinate", "TextureCoordinate",
            "texCoordIndex", "Normal", "normalIndex", "ImageTexture", "diffuseColor",
        ):
            self.assertIn(token, vrml)

        # Production host may not reconstruct B3D geometry. Research code is
        # allowed elsewhere in the repository, but it must not enter this assembly.
        for forbidden in (
            "parse_current_model", "extract_final_meshes", "decode_contour_blob",
            "Manifold", "solid=solid-", "direct_publisher.py", "final_geometry.py",
            ".obj", ".3ds", ".dae", "UploadModelFromStream", "TryInvokePublisherScript",
            "currentFileData", "TryInvokeNativeWebViewer", "WebViewer.dll",
        ):
            self.assertNotIn(forbidden, production)

    def test_offline_html_contract_is_preserved(self) -> None:
        html = HTML.read_text(encoding="utf-8")
        for token in (
            "local-view-bazis-viewer3d-wrl-1", "data:", ";base64,",
            "gl.drawElements", "gl.LINES", "gl.readPixels", "Снять выделение",
            "Escape", "Прозрачность", "Рёбра",
        ):
            self.assertIn(token, html)
        self.assertIn('html.Contains("http://"', html)
        self.assertIn('html.Contains("https://"', html)
        self.assertIn('html.Contains("<script src="', html)

    def test_shell_probe_covers_thumbnail_preview_and_bitness(self) -> None:
        program = PROGRAM.read_text(encoding="utf-8")
        probe = PROBE.read_text(encoding="utf-8")
        self.assertIn("--probe-b3d-handler", program)
        self.assertIn("B3DHandlerProbe.BuildReport", program)
        for token in (
            "E357FCCD-A995-4576-B01F-234630154E96",
            "8895B1C6-B41F-4C1C-A562-0D564250836F",
            "InprocServer32", "LocalServer32",
            "RegistryView.Registry64", "RegistryView.Registry32",
        ):
            self.assertIn(token, probe)


if __name__ == "__main__":
    unittest.main()
