#!/usr/bin/env python3
"""Direct BAZIS B3D -> final render mesh.

The B3D file already contains panel contours, thickness, transforms and explicit
machining tool profiles/trajectories.  This module turns those records into a
closed solid and applies machining with robust manifold booleans.  It does not
use BAZIS, Viewer3D, scripts, WRL, OBJ or cloud services.
"""
from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path
from typing import Any

import numpy as np
from manifold3d import Manifold, Mesh

ROOT = Path(__file__).resolve().parents[1]
PARSER_DIR = ROOT / "parser"
GEOMETRY_DIR = ROOT / "geometry"
for p in (PARSER_DIR, GEOMETRY_DIR):
    if str(p) not in sys.path:
        sys.path.insert(0, str(p))

from b3d_parser import B3DError, Node, decode_contour_blob, parse_current_model  # noqa: E402
from geometry import (  # noqa: E402
    Vec2, Vec3, _fields, _node_scalar, _trans, apply_matrix, chain_loops,
    identity, loop_points, matmul, polygon_area, transform_matrix, triangulate,
)

EPS = 1e-5
CUT_OUTSIDE_EPS = 0.02  # mm; expands tool only outside the panel boundary


def _mesh_manifold(vertices: list[Vec3], faces: list[tuple[int, int, int]]) -> Manifold:
    if not vertices or not faces:
        raise B3DError("Empty mesh")
    vp = np.asarray([[v.x, v.y, v.z] for v in vertices], dtype=np.float32)
    tv = np.asarray(faces, dtype=np.uint32)
    mesh = Mesh(vert_properties=vp, tri_verts=tv)
    m = Manifold(mesh)
    if m.is_empty():
        # Mesh.merge can resolve duplicated/coincident vertices produced by input contours.
        mesh.merge()
        m = Manifold(mesh)
    if m.is_empty():
        raise B3DError("Generated mesh is not a closed manifold")
    return m


def _prism(poly: list[Vec2], z0: float, z1: float) -> Manifold:
    if len(poly) < 3:
        raise B3DError("Prism polygon has fewer than 3 points")
    # Remove near-duplicate consecutive points.
    clean: list[Vec2] = []
    for p in poly:
        if not clean or math.hypot(p.x-clean[-1].x, p.y-clean[-1].y) > EPS:
            clean.append(p)
    if len(clean) > 1 and math.hypot(clean[0].x-clean[-1].x, clean[0].y-clean[-1].y) <= EPS:
        clean.pop()
    n = len(clean)
    caps = triangulate(clean)
    verts = [Vec3(p.x,p.y,z0) for p in clean] + [Vec3(p.x,p.y,z1) for p in clean]
    faces: list[tuple[int,int,int]] = []
    ccw = polygon_area(clean) > 0
    # outward winding
    for a,b,c in caps:
        faces.append((c,b,a))
        faces.append((a+n,b+n,c+n))
    for i in range(n):
        j=(i+1)%n
        if ccw:
            faces.extend([(i,j,j+n),(i,j+n,i+n)])
        else:
            faces.extend([(j,i,i+n),(j,i+n,j+n)])
    return _mesh_manifold(verts,faces)


def _point_in_poly(p: Vec2, poly: list[Vec2]) -> bool:
    inside=False
    j=len(poly)-1
    for i in range(len(poly)):
        a,b=poly[i],poly[j]
        if ((a.y>p.y)!=(b.y>p.y)):
            x=(b.x-a.x)*(p.y-a.y)/(b.y-a.y)+a.x
            if p.x < x: inside=not inside
        j=i
    return inside


def _sample_decoded(decoded: dict[str, Any], arc_step_deg: float) -> list[list[Vec2]]:
    loops=chain_loops(decoded["segments"])
    return [loop_points(loop,arc_step_deg) for loop in loops]


def _sample_open_segments(segments: list[dict[str, Any]], arc_step_deg: float) -> tuple[list[Vec2], bool]:
    """Sample trajectory preserving its serialized order; cut trajectories are normally one segment."""
    from geometry import sample_segment, segment_ends, reversed_segment
    if not segments: return [],False
    unused=[dict(s) for s in segments]
    chain=[unused.pop(0)]
    _,end=segment_ends(chain[-1])
    while unused:
        found=None
        for i,s in enumerate(unused):
            a,b=segment_ends(s)
            if math.hypot(a.x-end.x,a.y-end.y)<=1e-3: found=(i,s); break
            if math.hypot(b.x-end.x,b.y-end.y)<=1e-3: found=(i,reversed_segment(s)); break
        if found is None: break
        i,s=found; unused.pop(i); chain.append(s); _,end=segment_ends(s)
    pts: list[Vec2]=[]
    for s in chain:
        sp=sample_segment(s,arc_step_deg)
        if pts and math.hypot(pts[-1].x-sp[0].x,pts[-1].y-sp[0].y)<=1e-3: sp=sp[1:]
        pts.extend(sp)
    closed=len(pts)>2 and math.hypot(pts[0].x-pts[-1].x,pts[0].y-pts[-1].y)<=1e-3
    if closed: pts.pop()
    return pts,closed


def _choose_inward_sign(path: list[Vec2], outer: list[Vec2]) -> float:
    if len(path)<2: return 1.0
    k=max(0,min(len(path)-2,len(path)//2))
    a,b=path[k],path[k+1]
    dx,dy=b.x-a.x,b.y-a.y
    ln=math.hypot(dx,dy)
    if ln<EPS: return 1.0
    lx,ly=-dy/ln,dx/ln
    mx,my=(a.x+b.x)/2,(a.y+b.y)/2
    # A 0.2 mm test is large enough to beat floating noise and tiny vs furniture dimensions.
    left=_point_in_poly(Vec2(mx+lx*0.2,my+ly*0.2),outer)
    right=_point_in_poly(Vec2(mx-lx*0.2,my-ly*0.2),outer)
    if left and not right: return 1.0
    if right and not left: return -1.0
    # fallback from winding: for CCW outer loop the interior is left of forward boundary
    return 1.0 if polygon_area(outer)>0 else -1.0


def _expand_profile_outside(profile: list[Vec2]) -> list[Vec2]:
    # The tool shares x=0 with the panel side. Push only those points slightly
    # outward so the Boolean has a true overlap instead of a coincident face.
    return [Vec2(-CUT_OUTSIDE_EPS if abs(p.x)<=1e-6 else p.x,p.y) for p in profile]


def _sweep_tool(profile: list[Vec2], path: list[Vec2], closed: bool, outer: list[Vec2]) -> Manifold:
    if len(profile)<3 or len(path)<2:
        raise B3DError("Cut profile/trajectory is too short")
    prof=_expand_profile_outside(profile)
    sign=_choose_inward_sign(path,outer)
    rings: list[list[Vec3]]=[]
    m=len(path)
    for i,p in enumerate(path):
        if closed:
            prev=path[(i-1)%m]; nxt=path[(i+1)%m]
        elif i==0:
            prev=p; nxt=path[1]
        elif i==m-1:
            prev=path[m-2]; nxt=p
        else:
            prev=path[i-1]; nxt=path[i+1]
        tx,ty=nxt.x-prev.x,nxt.y-prev.y
        ln=math.hypot(tx,ty)
        if ln<EPS: tx,ty=1.0,0.0; ln=1.0
        tx/=ln; ty/=ln
        nx,ny=sign*(-ty),sign*tx
        rings.append([Vec3(p.x+nx*q.x,p.y+ny*q.x,q.y) for q in prof])
    nv=len(prof)
    verts=[v for ring in rings for v in ring]
    faces: list[tuple[int,int,int]]=[]
    ring_pairs=m if closed else m-1
    # profile polygon winding determines side orientation; Manifold can accept either
    # globally as long as the caps agree. Build first, then flip globally if volume is negative.
    for i in range(ring_pairs):
        j=(i+1)%m
        for k in range(nv):
            l=(k+1)%nv
            a=i*nv+k; b=j*nv+k; c=j*nv+l; d=i*nv+l
            faces.extend([(a,b,c),(a,c,d)])
    if not closed:
        cap=triangulate(prof)
        for a,b,c in cap:
            faces.append((c,b,a))
            off=(m-1)*nv
            faces.append((off+a,off+b,off+c))
    try:
        return _mesh_manifold(verts,faces)
    except B3DError:
        # Reverse all triangles once; this handles paths serialized opposite to the
        # orientation assumed above without adding operation-specific rules.
        return _mesh_manifold(verts,[(a,c,b) for a,b,c in faces])


def _build_panel_local(obj: Node, arc_step_deg: float=3.0) -> tuple[Manifold,dict[str,Any]]:
    f=_fields(obj)
    thick=float(_node_scalar(f,"Thick","Thickness",default=0.0) or 0.0)
    if thick<=0: raise B3DError("Panel thickness is not positive")
    cn=f.get("Contour")
    if cn is None or not isinstance(cn.value,(bytes,bytearray)): raise B3DError("Panel has no Contour")
    loops=_sample_decoded(decode_contour_blob(bytes(cn.value)),arc_step_deg)
    if not loops: raise B3DError("Panel contour is empty")
    outer_i=max(range(len(loops)),key=lambda i:abs(polygon_area(loops[i])))
    outer=loops[outer_i]
    solid=_prism(outer,0.0,thick)
    # Inner contour loops are holes through the panel.
    hole_count=0
    for i,hole in enumerate(loops):
        if i==outer_i: continue
        hole_count+=1
        # Extend slightly in Z to avoid coincident cap faces.
        solid = solid - _prism(hole,-CUT_OUTSIDE_EPS,thick+CUT_OUTSIDE_EPS)
    applied=[]
    cuts=f.get("Cuts")
    if cuts:
        for cut in cuts.children_named("Cut"):
            cf=_fields(cut)
            profn=cf.get("Contour"); trajn=cf.get("Trajectory")
            if profn is None or trajn is None or not isinstance(profn.value,(bytes,bytearray)) or not isinstance(trajn.value,(bytes,bytearray)):
                raise B3DError(f"Cut {_node_scalar(cf,'Name')} lacks profile/trajectory")
            prof_loops=_sample_decoded(decode_contour_blob(bytes(profn.value)),1.5)
            if not prof_loops: raise B3DError("Empty cut profile")
            profile=max(prof_loops,key=lambda p:abs(polygon_area(p)))
            traj_dec=decode_contour_blob(bytes(trajn.value))
            path,closed=_sample_open_segments(traj_dec["segments"],arc_step_deg)
            tool=_sweep_tool(profile,path,closed,outer)
            solid=solid-tool
            if solid.is_empty():
                raise B3DError(f"Cut {_node_scalar(cf,'Name')} removed entire panel")
            params_node=cf.get("Params")
            params={c.name:c.value for c in (params_node.children if params_node else [])}
            applied.append({"name":_node_scalar(cf,"Name"),"front":_node_scalar(cf,"Front"),"params":params,"trajectory_points":len(path),"profile_points":len(profile)})
    return solid,{"holes":hole_count,"cuts":applied,"thickness":thick}


def _to_world_mesh(solid: Manifold, world: list[list[float]]) -> tuple[list[list[float]],list[list[int]]]:
    mesh=solid.to_mesh()
    verts=[]
    for row in np.asarray(mesh.vert_properties)[:,:3]:
        v=apply_matrix(world,Vec3(float(row[0]),float(row[1]),float(row[2])))
        verts.append([v.x,v.y,v.z])
    faces=np.asarray(mesh.tri_verts,dtype=np.int64).tolist()
    return verts,faces


def extract_final_meshes(model: Node, arc_step_deg: float=3.0) -> dict[str,Any]:
    panels=[]; errors=[]
    def visit(node:Node,parent_world:list[list[float]]):
        if node.name=="Obj" and node.value_type==0:
            f=_fields(node); world=matmul(parent_world,transform_matrix(_trans(node)))
            if _node_scalar(f,"Type")==4002:
                try:
                    solid,info=_build_panel_local(node,arc_step_deg)
                    verts,faces=_to_world_mesh(solid,world)
                    panels.append({"id":_node_scalar(f,"ID"),"type":4002,"name":_node_scalar(f,"Name"),"material":_node_scalar(f,"Mat","Material"),"thickness":info["thickness"],"holes":info["holes"],"cuts":info["cuts"],"mesh":{"vertices":verts,"triangles":faces}})
                except Exception as exc:
                    errors.append({"id":_node_scalar(f,"ID"),"name":_node_scalar(f,"Name"),"error":str(exc)})
            objs=f.get("Objs")
            if objs:
                for c in objs.children or []:
                    if c.name=="Obj": visit(c,world)
            return
        for c in node.children or []: visit(c,parent_world)
    visit(model,identity())
    allv=[v for p in panels for v in p["mesh"]["vertices"]]
    bounds=None
    if allv: bounds={"min":[min(v[i] for v in allv) for i in range(3)],"max":[max(v[i] for v in allv) for i in range(3)]}
    return {"geometry_version":2,"status":"direct-b3d-final-csg","panel_count":len(panels),"errors":errors,"bounds":bounds,"panels":panels}


def main():
    ap=argparse.ArgumentParser(description="Build final render geometry directly from BAZIS B3D")
    ap.add_argument("input"); ap.add_argument("-o","--output",required=True); ap.add_argument("--arc-step",type=float,default=3.0)
    a=ap.parse_args(); model,meta=parse_current_model(a.input); geom=extract_final_meshes(model,a.arc_step)
    Path(a.output).write_text(json.dumps({"source_meta":meta,**geom},ensure_ascii=False),encoding="utf-8")
    print(json.dumps({"panel_count":geom["panel_count"],"errors":geom["errors"],"bounds":geom["bounds"],"triangles":sum(len(p["mesh"]["triangles"]) for p in geom["panels"])},ensure_ascii=False,indent=2))
    if geom["errors"]: raise SystemExit(2)

if __name__=="__main__": main()
