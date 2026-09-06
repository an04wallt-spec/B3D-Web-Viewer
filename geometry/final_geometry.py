#!/usr/bin/env python3
"""Direct BAZIS B3D -> final render mesh.

B3D stores panel contours, thickness, transforms and explicit machining
profiles/trajectories. This module builds the solid and applies those tools
with manifold booleans. No BAZIS process, Viewer3D, scripts or cloud is used.
"""
from __future__ import annotations
import argparse,json,math,sys
from pathlib import Path
from typing import Any
import numpy as np
from manifold3d import Manifold,Mesh
ROOT=Path(__file__).resolve().parents[1]
for p in (ROOT/'parser',ROOT/'geometry'):
    if str(p) not in sys.path: sys.path.insert(0,str(p))
from b3d_parser import B3DError,Node,decode_contour_blob,parse_current_model
from geometry import Vec2,Vec3,_fields,_node_scalar,_trans,apply_matrix,chain_loops,identity,loop_points,matmul,polygon_area,transform_matrix,triangulate
EPS=1e-5; CUT_OUTSIDE_EPS=.02

def _mesh_manifold(vertices,faces):
    vp=np.asarray([[v.x,v.y,v.z] for v in vertices],dtype=np.float32); tv=np.asarray(faces,dtype=np.uint32)
    mesh=Mesh(vert_properties=vp,tri_verts=tv); m=Manifold(mesh)
    if m.is_empty(): mesh.merge(); m=Manifold(mesh)
    if m.is_empty(): raise B3DError('Generated mesh is not a closed manifold')
    return m

def _prism(poly,z0,z1):
    clean=[]
    for p in poly:
        if not clean or math.hypot(p.x-clean[-1].x,p.y-clean[-1].y)>EPS: clean.append(p)
    if len(clean)>1 and math.hypot(clean[0].x-clean[-1].x,clean[0].y-clean[-1].y)<=EPS: clean.pop()
    n=len(clean); caps=triangulate(clean); verts=[Vec3(p.x,p.y,z0) for p in clean]+[Vec3(p.x,p.y,z1) for p in clean]; faces=[]; ccw=polygon_area(clean)>0
    for a,b,c in caps: faces.extend([(c,b,a),(a+n,b+n,c+n)])
    for i in range(n):
        j=(i+1)%n
        faces.extend([(i,j,j+n),(i,j+n,i+n)] if ccw else [(j,i,i+n),(j,i+n,j+n)])
    return _mesh_manifold(verts,faces)

def _point_in_poly(p,poly):
    inside=False; j=len(poly)-1
    for i,a in enumerate(poly):
        b=poly[j]
        if (a.y>p.y)!=(b.y>p.y):
            x=(b.x-a.x)*(p.y-a.y)/(b.y-a.y)+a.x
            if p.x<x: inside=not inside
        j=i
    return inside

def _sample_decoded(d,step): return [loop_points(loop,step) for loop in chain_loops(d['segments'])]

def _sample_open_segments(segments,step):
    from geometry import sample_segment,segment_ends,reversed_segment
    if not segments:return [],False
    unused=[dict(s) for s in segments]; chain=[unused.pop(0)]; _,end=segment_ends(chain[-1])
    while unused:
        found=None
        for i,s in enumerate(unused):
            a,b=segment_ends(s)
            if math.hypot(a.x-end.x,a.y-end.y)<=1e-3: found=(i,s); break
            if math.hypot(b.x-end.x,b.y-end.y)<=1e-3: found=(i,reversed_segment(s)); break
        if found is None: break
        i,s=found; unused.pop(i); chain.append(s); _,end=segment_ends(s)
    pts=[]
    for s in chain:
        q=sample_segment(s,step)
        if pts and math.hypot(pts[-1].x-q[0].x,pts[-1].y-q[0].y)<=1e-3:q=q[1:]
        pts.extend(q)
    closed=len(pts)>2 and math.hypot(pts[0].x-pts[-1].x,pts[0].y-pts[-1].y)<=1e-3
    if closed:pts.pop()
    return pts,closed

def _choose_inward_sign(path,outer):
    k=max(0,min(len(path)-2,len(path)//2)); a,b=path[k],path[k+1]; dx,dy=b.x-a.x,b.y-a.y; ln=math.hypot(dx,dy)
    if ln<EPS:return 1.
    lx,ly=-dy/ln,dx/ln; mx,my=(a.x+b.x)/2,(a.y+b.y)/2
    left=_point_in_poly(Vec2(mx+lx*.2,my+ly*.2),outer); right=_point_in_poly(Vec2(mx-lx*.2,my-ly*.2),outer)
    if left and not right:return 1.
    if right and not left:return -1.
    return 1. if polygon_area(outer)>0 else -1.

def _sweep_tool(profile,path,closed,outer):
    if len(profile)<3 or len(path)<2:raise B3DError('Cut profile/trajectory is too short')
    # The sweep topology below is defined for clockwise section loops. BAZIS
    # serializes the same operation in either winding depending on Front.
    profile=list(profile)
    if polygon_area(profile)>0: profile.reverse()
    prof=[Vec2(-CUT_OUTSIDE_EPS if abs(p.x)<=1e-6 else p.x,p.y) for p in profile]
    sign=_choose_inward_sign(path,outer); rings=[]; m=len(path)
    for i,p in enumerate(path):
        if closed: prev,nxt=path[(i-1)%m],path[(i+1)%m]
        elif i==0: prev,nxt=p,path[1]
        elif i==m-1: prev,nxt=path[m-2],p
        else: prev,nxt=path[i-1],path[i+1]
        tx,ty=nxt.x-prev.x,nxt.y-prev.y; ln=math.hypot(tx,ty) or 1.; tx/=ln; ty/=ln; nx,ny=sign*(-ty),sign*tx
        rings.append([Vec3(p.x+nx*q.x,p.y+ny*q.x,q.y) for q in prof])
    nv=len(prof); verts=[v for r in rings for v in r]; faces=[]
    for i in range(m if closed else m-1):
        j=(i+1)%m
        for k in range(nv):
            l=(k+1)%nv; a=i*nv+k;b=j*nv+k;c=j*nv+l;d=i*nv+l;faces.extend([(a,b,c),(a,c,d)])
    if not closed:
        off=(m-1)*nv
        for a,b,c in triangulate(prof): faces.extend([(c,b,a),(off+a,off+b,off+c)])
    try:return _mesh_manifold(verts,faces)
    except B3DError:return _mesh_manifold(verts,[(a,c,b) for a,b,c in faces])

def _build_panel_local(obj,step=3.):
    f=_fields(obj); thick=float(_node_scalar(f,'Thick','Thickness',default=0.) or 0.); cn=f.get('Contour')
    if thick<=0 or cn is None or not isinstance(cn.value,(bytes,bytearray)):raise B3DError('Invalid panel thickness/contour')
    loops=_sample_decoded(decode_contour_blob(bytes(cn.value)),step); outer_i=max(range(len(loops)),key=lambda i:abs(polygon_area(loops[i]))); outer=loops[outer_i]; solid=_prism(outer,0.,thick)
    holes=0
    for i,h in enumerate(loops):
        if i!=outer_i: holes+=1; solid=solid-_prism(h,-CUT_OUTSIDE_EPS,thick+CUT_OUTSIDE_EPS)
    applied=[]; cuts=f.get('Cuts')
    if cuts:
        for cut in cuts.children_named('Cut'):
            cf=_fields(cut); pn=cf.get('Contour');tn=cf.get('Trajectory')
            if pn is None or tn is None or not isinstance(pn.value,(bytes,bytearray)) or not isinstance(tn.value,(bytes,bytearray)):raise B3DError('Cut lacks profile/trajectory')
            pl=_sample_decoded(decode_contour_blob(bytes(pn.value)),1.5); profile=max(pl,key=lambda p:abs(polygon_area(p))); td=decode_contour_blob(bytes(tn.value)); path,closed=_sample_open_segments(td['segments'],step)
            solid=solid-_sweep_tool(profile,path,closed,outer)
            if solid.is_empty():raise B3DError(f"Cut {_node_scalar(cf,'Name')} removed entire panel")
            prm=cf.get('Params');params={c.name:c.value for c in (prm.children if prm else [])};applied.append({'name':_node_scalar(cf,'Name'),'front':_node_scalar(cf,'Front'),'params':params,'trajectory_points':len(path),'profile_points':len(profile)})
    return solid,{'holes':holes,'cuts':applied,'thickness':thick}

def _to_world_mesh(solid,world):
    mesh=solid.to_mesh(); verts=[]
    for row in np.asarray(mesh.vert_properties)[:,:3]:
        v=apply_matrix(world,Vec3(float(row[0]),float(row[1]),float(row[2])));verts.append([v.x,v.y,v.z])
    return verts,np.asarray(mesh.tri_verts,dtype=np.int64).tolist()

def extract_final_meshes(model,arc_step_deg=3.):
    panels=[];errors=[]
    def visit(node,parent):
        if node.name=='Obj' and node.value_type==0:
            f=_fields(node);world=matmul(parent,transform_matrix(_trans(node)))
            if _node_scalar(f,'Type')==4002:
                try:
                    solid,info=_build_panel_local(node,arc_step_deg);verts,faces=_to_world_mesh(solid,world);panels.append({'id':_node_scalar(f,'ID'),'type':4002,'name':_node_scalar(f,'Name'),'material':_node_scalar(f,'Mat','Material'),'thickness':info['thickness'],'holes':info['holes'],'cuts':info['cuts'],'mesh':{'vertices':verts,'triangles':faces}})
                except Exception as exc:errors.append({'id':_node_scalar(f,'ID'),'name':_node_scalar(f,'Name'),'error':str(exc)})
            objs=f.get('Objs')
            if objs:
                for c in objs.children or []:
                    if c.name=='Obj':visit(c,world)
            return
        for c in node.children or []:visit(c,parent)
    visit(model,identity());allv=[v for p in panels for v in p['mesh']['vertices']];bounds=None
    if allv:bounds={'min':[min(v[i] for v in allv) for i in range(3)],'max':[max(v[i] for v in allv) for i in range(3)]}
    return {'geometry_version':2,'status':'direct-b3d-final-csg','panel_count':len(panels),'errors':errors,'bounds':bounds,'panels':panels}

def main():
    ap=argparse.ArgumentParser();ap.add_argument('input');ap.add_argument('-o','--output',required=True);ap.add_argument('--arc-step',type=float,default=3.);a=ap.parse_args();model,meta=parse_current_model(a.input);g=extract_final_meshes(model,a.arc_step);Path(a.output).write_text(json.dumps({'source_meta':meta,**g},ensure_ascii=False),encoding='utf-8');print(json.dumps({'panel_count':g['panel_count'],'errors':g['errors'],'bounds':g['bounds'],'triangles':sum(len(p['mesh']['triangles']) for p in g['panels'])},ensure_ascii=False,indent=2));raise SystemExit(2 if g['errors'] else 0)
if __name__=='__main__':main()
