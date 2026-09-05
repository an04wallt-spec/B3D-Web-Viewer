#!/usr/bin/env python3
"""Geometry layer for B3D-Web-Viewer.

Stage 1: turn BAZIS panel objects (Type 4002) into triangle meshes.
- decodes unordered line/arc contour segments
- chains them into closed loops
- samples true circular arcs (center/start/end + direction)
- triangulates a simple outer polygon with ear clipping
- extrudes by panel thickness along local +Z
- composes nested B3D quaternion transforms to world coordinates

Cuts/mitres are intentionally preserved as metadata but are not yet applied to
solid geometry. The engine reports that explicitly instead of pretending the
result is final.
"""
from __future__ import annotations

import argparse
import json
import math
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
PARSER_DIR = ROOT / "parser"
if str(PARSER_DIR) not in sys.path:
    sys.path.insert(0, str(PARSER_DIR))

from b3d_parser import B3DError, Node, decode_contour_blob, parse_current_model  # noqa: E402

EPS = 1e-6


@dataclass(frozen=True)
class Vec2:
    x: float
    y: float


@dataclass(frozen=True)
class Vec3:
    x: float
    y: float
    z: float


def _near(a: Vec2, b: Vec2, tol: float = 1e-4) -> bool:
    return abs(a.x - b.x) <= tol and abs(a.y - b.y) <= tol


def _fields(node: Node) -> dict[str, Node]:
    return {child.name: child for child in node.children or []}


def _trans(node: Node) -> tuple[float, float, float, float, float, float, float]:
    f = _fields(node)
    t = f.get("Trans")
    if t is None:
        return (0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0)
    d = {c.name: c.value for c in t.children or []}
    return (
        float(d.get("X", 0.0)),
        float(d.get("Y", 0.0)),
        float(d.get("Z", 0.0)),
        float(d.get("Rx", 0.0)),
        float(d.get("Ry", 0.0)),
        float(d.get("Rz", 0.0)),
        float(d.get("Rw", 1.0)),
    )


def quat_matrix(rx: float, ry: float, rz: float, rw: float) -> list[list[float]]:
    n = math.sqrt(rx * rx + ry * ry + rz * rz + rw * rw)
    if n < EPS:
        rx = ry = rz = 0.0
        rw = 1.0
    else:
        rx /= n
        ry /= n
        rz /= n
        rw /= n
    xx, yy, zz = rx * rx, ry * ry, rz * rz
    xy, xz, yz = rx * ry, rx * rz, ry * rz
    wx, wy, wz = rw * rx, rw * ry, rw * rz
    return [
        [1 - 2 * (yy + zz), 2 * (xy - wz), 2 * (xz + wy), 0.0],
        [2 * (xy + wz), 1 - 2 * (xx + zz), 2 * (yz - wx), 0.0],
        [2 * (xz - wy), 2 * (yz + wx), 1 - 2 * (xx + yy), 0.0],
        [0.0, 0.0, 0.0, 1.0],
    ]


def transform_matrix(t: tuple[float, float, float, float, float, float, float]) -> list[list[float]]:
    x, y, z, rx, ry, rz, rw = t
    m = quat_matrix(rx, ry, rz, rw)
    m[0][3] = x
    m[1][3] = y
    m[2][3] = z
    return m


def matmul(a: list[list[float]], b: list[list[float]]) -> list[list[float]]:
    return [[sum(a[i][k] * b[k][j] for k in range(4)) for j in range(4)] for i in range(4)]


def identity() -> list[list[float]]:
    return [
        [1.0, 0.0, 0.0, 0.0],
        [0.0, 1.0, 0.0, 0.0],
        [0.0, 0.0, 1.0, 0.0],
        [0.0, 0.0, 0.0, 1.0],
    ]


def apply_matrix(m: list[list[float]], p: Vec3) -> Vec3:
    return Vec3(
        m[0][0] * p.x + m[0][1] * p.y + m[0][2] * p.z + m[0][3],
        m[1][0] * p.x + m[1][1] * p.y + m[1][2] * p.z + m[1][3],
        m[2][0] * p.x + m[2][1] * p.y + m[2][2] * p.z + m[2][3],
    )


def segment_ends(seg: dict[str, Any]) -> tuple[Vec2, Vec2]:
    if seg["type"] == "line":
        return Vec2(seg["x1"], seg["y1"]), Vec2(seg["x2"], seg["y2"])
    # BZ85 arc payload: center=(x1,y1), start=(x2,y2), end=(x3,y3).
    return Vec2(seg["x2"], seg["y2"]), Vec2(seg["x3"], seg["y3"])


def reversed_segment(seg: dict[str, Any]) -> dict[str, Any]:
    s = dict(seg)
    if s["type"] == "line":
        s["x1"], s["x2"] = s["x2"], s["x1"]
        s["y1"], s["y2"] = s["y2"], s["y1"]
    else:
        s["x2"], s["x3"] = s["x3"], s["x2"]
        s["y2"], s["y3"] = s["y3"], s["y2"]
        s["direction"] = 0 if int(s.get("direction", 0)) else 1
    return s


def chain_loops(segments: list[dict[str, Any]], tol: float = 1e-4) -> list[list[dict[str, Any]]]:
    unused = [dict(s) for s in segments]
    loops: list[list[dict[str, Any]]] = []
    while unused:
        chain = [unused.pop(0)]
        start, _ = segment_ends(chain[0])
        _, end = segment_ends(chain[-1])
        guard = 0
        while not _near(end, start, tol) and unused:
            guard += 1
            if guard > len(segments) + 2:
                break
            found = None
            for i, s in enumerate(unused):
                a, b = segment_ends(s)
                if _near(a, end, tol):
                    found = (i, s)
                    break
                if _near(b, end, tol):
                    found = (i, reversed_segment(s))
                    break
            if found is None:
                break
            i, s = found
            unused.pop(i)
            chain.append(s)
            _, end = segment_ends(s)
        if not _near(end, start, tol):
            raise B3DError(f"Open contour chain: start={start}, end={end}")
        loops.append(chain)
    return loops


def _angle_delta(a0: float, a1: float, direction: int) -> float:
    # Observed BZ85 convention: 0 = clockwise, 1 = counter-clockwise.
    if direction:
        d = (a1 - a0) % (2 * math.pi)
    else:
        d = -((a0 - a1) % (2 * math.pi))
    if abs(d) < EPS:
        d = 2 * math.pi if direction else -2 * math.pi
    return d


def sample_segment(seg: dict[str, Any], max_angle_deg: float = 7.5) -> list[Vec2]:
    if seg["type"] == "line":
        return [Vec2(seg["x1"], seg["y1"]), Vec2(seg["x2"], seg["y2"])]
    c = Vec2(seg["x1"], seg["y1"])
    a = Vec2(seg["x2"], seg["y2"])
    b = Vec2(seg["x3"], seg["y3"])
    r = math.hypot(a.x - c.x, a.y - c.y)
    if r < EPS:
        return [a, b]
    a0 = math.atan2(a.y - c.y, a.x - c.x)
    a1 = math.atan2(b.y - c.y, b.x - c.x)
    d = _angle_delta(a0, a1, int(seg.get("direction", 0)))
    steps = max(1, math.ceil(abs(d) / math.radians(max_angle_deg)))
    return [
        Vec2(c.x + r * math.cos(a0 + d * i / steps), c.y + r * math.sin(a0 + d * i / steps))
        for i in range(steps + 1)
    ]


def loop_points(loop: list[dict[str, Any]], max_angle_deg: float = 7.5) -> list[Vec2]:
    pts: list[Vec2] = []
    for s in loop:
        sp = sample_segment(s, max_angle_deg)
        if pts and _near(pts[-1], sp[0]):
            sp = sp[1:]
        pts.extend(sp)
    if len(pts) > 1 and _near(pts[0], pts[-1]):
        pts.pop()
    out: list[Vec2] = []
    for p in pts:
        if not out or not _near(out[-1], p):
            out.append(p)
    return out


def polygon_area(poly: list[Vec2]) -> float:
    return 0.5 * sum(
        poly[i].x * poly[(i + 1) % len(poly)].y - poly[(i + 1) % len(poly)].x * poly[i].y
        for i in range(len(poly))
    )


def _cross(a: Vec2, b: Vec2, c: Vec2) -> float:
    return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x)


def _inside_triangle(p: Vec2, a: Vec2, b: Vec2, c: Vec2) -> bool:
    c1 = _cross(a, b, p)
    c2 = _cross(b, c, p)
    c3 = _cross(c, a, p)
    return (c1 >= -EPS and c2 >= -EPS and c3 >= -EPS) or (c1 <= EPS and c2 <= EPS and c3 <= EPS)


def triangulate(poly: list[Vec2]) -> list[tuple[int, int, int]]:
    if len(poly) < 3:
        raise B3DError("Polygon has fewer than three vertices")
    idx = list(range(len(poly)))
    ccw = polygon_area(poly) > 0
    tris: list[tuple[int, int, int]] = []
    guard = 0
    while len(idx) > 3:
        guard += 1
        if guard > len(poly) * len(poly) * 2:
            raise B3DError("Ear clipping failed; polygon may self-intersect")
        clipped = False
        for k in range(len(idx)):
            i0, i1, i2 = idx[k - 1], idx[k], idx[(k + 1) % len(idx)]
            cr = _cross(poly[i0], poly[i1], poly[i2])
            if (ccw and cr <= EPS) or ((not ccw) and cr >= -EPS):
                continue
            if any(
                j not in (i0, i1, i2) and _inside_triangle(poly[j], poly[i0], poly[i1], poly[i2])
                for j in idx
            ):
                continue
            tris.append((i0, i1, i2) if ccw else (i2, i1, i0))
            del idx[k]
            clipped = True
            break
        if not clipped:
            raise B3DError("No ear found during triangulation")
    a, b, c = idx
    tris.append((a, b, c) if ccw else (c, b, a))
    return tris


def extrude_polygon(poly: list[Vec2], thick: float) -> tuple[list[Vec3], list[tuple[int, int, int]]]:
    n = len(poly)
    vertices = [Vec3(p.x, p.y, 0.0) for p in poly] + [Vec3(p.x, p.y, thick) for p in poly]
    caps = triangulate(poly)
    faces: list[tuple[int, int, int]] = []
    for a, b, c in caps:
        faces.append((c, b, a))
        faces.append((a + n, b + n, c + n))
    ccw = polygon_area(poly) > 0
    for i in range(n):
        j = (i + 1) % n
        if ccw:
            faces.extend([(i, j, j + n), (i, j + n, i + n)])
        else:
            faces.extend([(j, i, i + n), (j, i + n, j + n)])
    return vertices, faces


def _node_scalar(f: dict[str, Node], *names: str, default: Any = None) -> Any:
    for name in names:
        if name in f:
            return f[name].value
    return default


def _cut_metadata(f: dict[str, Node]) -> list[dict[str, Any]]:
    cuts = f.get("Cuts")
    out: list[dict[str, Any]] = []
    if not cuts:
        return out
    for cut in cuts.children_named("Cut"):
        cf = _fields(cut)
        params_node = cf.get("Params")
        params = {c.name: c.value for c in (params_node.children if params_node else [])}
        out.append({"name": _node_scalar(cf, "Name"), "front": _node_scalar(cf, "Front"), "params": params})
    return out


def panel_mesh(obj: Node, world: list[list[float]], arc_step_deg: float = 7.5) -> dict[str, Any]:
    f = _fields(obj)
    contour = f.get("Contour")
    if contour is None or not isinstance(contour.value, (bytes, bytearray)):
        raise B3DError("Panel has no binary Contour")
    thick = float(_node_scalar(f, "Thick", "Thickness", default=0.0) or 0.0)
    if thick <= 0:
        raise B3DError(f"Panel {_node_scalar(f, 'ID')} has invalid thickness {thick}")
    decoded = decode_contour_blob(bytes(contour.value))
    loops = chain_loops(decoded["segments"])
    sampled = [loop_points(loop, arc_step_deg) for loop in loops]
    if not sampled:
        raise B3DError("No contour loop")
    areas = [abs(polygon_area(p)) for p in sampled]
    outer_i = max(range(len(sampled)), key=lambda i: areas[i])
    outer = sampled[outer_i]
    vertices, faces = extrude_polygon(outer, thick)
    wverts = [apply_matrix(world, v) for v in vertices]
    cuts = _cut_metadata(f)
    warnings: list[str] = []
    if len(sampled) > 1:
        warnings.append(f"{len(sampled) - 1} additional contour loop(s) not yet subtracted")
    if cuts:
        warnings.append(f"{len(cuts)} cut operation(s) preserved as metadata, not yet applied")
    return {
        "id": _node_scalar(f, "ID"),
        "type": _node_scalar(f, "Type"),
        "name": _node_scalar(f, "Name"),
        "material": _node_scalar(f, "Mat", "Material"),
        "thickness": thick,
        "local_contour": {"loops": len(sampled), "outer_points": [[p.x, p.y] for p in outer]},
        "cuts": cuts,
        "warnings": warnings,
        "mesh": {
            "vertices": [[v.x, v.y, v.z] for v in wverts],
            "triangles": [list(t) for t in faces],
        },
    }


def extract_panel_meshes(model: Node, arc_step_deg: float = 7.5) -> dict[str, Any]:
    panels: list[dict[str, Any]] = []
    errors: list[dict[str, Any]] = []

    def visit(node: Node, parent_world: list[list[float]]) -> None:
        if node.name == "Obj" and node.value_type == 0:
            f = _fields(node)
            world = matmul(parent_world, transform_matrix(_trans(node)))
            if _node_scalar(f, "Type") == 4002:
                try:
                    panels.append(panel_mesh(node, world, arc_step_deg))
                except Exception as exc:
                    errors.append({"id": _node_scalar(f, "ID"), "name": _node_scalar(f, "Name"), "error": str(exc)})
            objs = f.get("Objs")
            if objs:
                for child in objs.children or []:
                    if child.name == "Obj":
                        visit(child, world)
            return
        for child in node.children or []:
            visit(child, parent_world)

    visit(model, identity())
    all_vertices = [v for p in panels for v in p["mesh"]["vertices"]]
    bounds = None
    if all_vertices:
        bounds = {
            "min": [min(v[i] for v in all_vertices) for i in range(3)],
            "max": [max(v[i] for v in all_vertices) for i in range(3)],
        }
    return {
        "geometry_version": 1,
        "status": "stage-1-panels-no-cuts",
        "panel_count": len(panels),
        "errors": errors,
        "bounds": bounds,
        "panels": panels,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Build stage-1 triangle meshes from BAZIS B3D panels")
    parser.add_argument("input")
    parser.add_argument("-o", "--output", required=True)
    parser.add_argument("--arc-step", type=float, default=7.5)
    args = parser.parse_args()
    model, meta = parse_current_model(args.input)
    geom = extract_panel_meshes(model, args.arc_step)
    payload = {"source_meta": meta, **geom}
    Path(args.output).write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"output": args.output, "panel_count": geom["panel_count"], "errors": len(geom["errors"]), "bounds": geom["bounds"]}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
