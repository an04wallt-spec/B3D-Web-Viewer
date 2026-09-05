from __future__ import annotations

import sys
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
PARSER_DIR = ROOT / "parser"
SAMPLE = ROOT / "samples" / "Стол_3100х750х1300.b3d"

sys.path.insert(0, str(PARSER_DIR))

import b3d_parser as bp  # noqa: E402


class B3DParserRegressionTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        if not SAMPLE.exists():
            raise unittest.SkipTest(
                "Reference B3D sample is absent: " + str(SAMPLE)
            )

    def test_reference_table_model(self) -> None:
        model, meta = bp.parse_current_model(SAMPLE)
        summary = bp.summarize_model(model)

        self.assertEqual(meta["signature"], "BZ85")
        self.assertEqual(meta["zlib_offset"], 0x47BA)
        self.assertEqual(meta["model_stream_size"], 1119306)
        self.assertEqual(summary["object_records"], 62)
        self.assertEqual(summary["object_types"]["4002"], 37)

        target = None
        for node in bp.walk(model):
            if node.name != "Obj" or node.value_type != 0:
                continue
            fields = {child.name: child for child in node.children or []}
            if fields.get("ID") and fields["ID"].value == 1023:
                target = fields
                break

        self.assertIsNotNone(target)
        assert target is not None

        self.assertEqual(target["Name"].value, "4.1_планка зацеп")
        self.assertAlmostEqual(target["Thick"].value, 16.0)

        transform = {child.name: child.value for child in target["Trans"].children or []}
        self.assertAlmostEqual(transform["X"], 583.0, places=6)
        self.assertAlmostEqual(transform["Z"], 194.0, places=6)

        contour = bp.decode_contour_blob(target["Contour"].value)
        self.assertEqual(contour["segment_count"], 4)

        xs = [
            segment[key]
            for segment in contour["segments"]
            for key in ("x1", "x2")
        ]
        ys = [
            segment[key]
            for segment in contour["segments"]
            for key in ("y1", "y2")
        ]
        self.assertAlmostEqual(max(xs) - min(xs), 64.0, places=6)
        self.assertAlmostEqual(max(ys) - min(ys), 696.0, places=6)

        cuts = target["Cuts"].children_named("Cut")
        self.assertEqual(len(cuts), 1)

        cut_fields = {child.name: child for child in cuts[0].children or []}
        params = {
            child.name: child.value for child in cut_fields["Params"].children or []
        }
        self.assertEqual(params["typ"], 1)
        self.assertAlmostEqual(params["depth"], 8.0)
        self.assertAlmostEqual(params["width"], 8.0)

        trajectory = bp.decode_contour_blob(cut_fields["Trajectory"].value)
        self.assertEqual(trajectory["segment_count"], 1)


if __name__ == "__main__":
    unittest.main()
