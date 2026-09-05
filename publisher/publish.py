#!/usr/bin/env python3
"""Publish a BAZIS .b3d model as one autonomous offline HTML file.

The customer-facing HTML has no external dependencies and makes no network
requests. Geometry is generated on the author's computer, packed into compact
base64 Float32 triangle streams, and rendered with plain WebGL.

Current scope: panel geometry produced by geometry/geometry.py. Cuts that the
geometry layer does not yet apply remain a geometry-layer limitation; the
publisher itself is already a complete offline delivery pipeline.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import math
import struct
import sys
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
for p in (ROOT / "parser", ROOT / "geometry"):
    if str(p) not in sys.path:
        sys.path.insert(0, str(p))

from b3d_parser import parse_current_model  # noqa: E402
from geometry import extract_panel_meshes  # noqa: E402


def _normal(a: list[float], b: list[float], c: list[float]) -> tuple[float, float, float]:
    ux, uy, uz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
    vx, vy, vz = c[0] - a[0], c[1] - a[1], c[2] - a[2]
    nx = uy * vz - uz * vy
    ny = uz * vx - ux * vz
    nz = ux * vy - uy * vx
    length = math.sqrt(nx * nx + ny * ny + nz * nz)
    if length <= 1e-12:
        return (0.0, 0.0, 1.0)
    return (nx / length, ny / length, nz / length)


def _material_color(value: Any) -> list[float]:
    # Stable neutral palette generated from the material identity. This is only
    # a fallback until embedded B3D textures are decoded by the material layer.
    key = str(value if value is not None else "default").encode("utf-8", errors="replace")
    d = hashlib.sha256(key).digest()
    base = [0.50 + d[i] / 255.0 * 0.30 for i in range(3)]
    # Avoid saturated toy-like colors: pull toward warm grey.
    grey = sum(base) / 3.0
    return [round(c * 0.55 + grey * 0.25 + 0.16, 4) for c in base]


def _pack_panel(panel: dict[str, Any]) -> dict[str, Any]:
    vertices = panel["mesh"]["vertices"]
    triangles = panel["mesh"]["triangles"]
    floats: list[float] = []
    edges: list[float] = []

    for ia, ib, ic in triangles:
        a, b, c = vertices[ia], vertices[ib], vertices[ic]
        nx, ny, nz = _normal(a, b, c)
        for p in (a, b, c):
            floats.extend((float(p[0]), float(p[1]), float(p[2]), nx, ny, nz))
        for p, q in ((a, b), (b, c), (c, a)):
            edges.extend((float(p[0]), float(p[1]), float(p[2]), float(q[0]), float(q[1]), float(q[2])))

    tri_raw = struct.pack("<" + "f" * len(floats), *floats) if floats else b""
    edge_raw = struct.pack("<" + "f" * len(edges), *edges) if edges else b""
    return {
        "id": panel.get("id"),
        "name": panel.get("name") or "Деталь",
        "material": panel.get("material"),
        "color": _material_color(panel.get("material")),
        "triangles": len(triangles),
        "vertex_count": len(floats) // 6,
        "data": base64.b64encode(tri_raw).decode("ascii"),
        "edge_vertex_count": len(edges) // 3,
        "edges": base64.b64encode(edge_raw).decode("ascii"),
        "warnings": panel.get("warnings", []),
    }


def _compact_bounds(bounds: dict[str, Any] | None) -> dict[str, list[float]]:
    if not bounds:
        return {"min": [-1.0, -1.0, -1.0], "max": [1.0, 1.0, 1.0]}
    return {
        "min": [float(x) for x in bounds["min"]],
        "max": [float(x) for x in bounds["max"]],
    }


def build_payload(input_path: str | Path) -> dict[str, Any]:
    model, meta = parse_current_model(input_path)
    geom = extract_panel_meshes(model)
    return {
        "format": "b3d-offline-view-1",
        "title": Path(input_path).stem,
        "source": {
            "signature": meta.get("signature"),
            "file_size": meta.get("file_size"),
        },
        "status": geom.get("status"),
        "bounds": _compact_bounds(geom.get("bounds")),
        "panel_count": geom.get("panel_count", 0),
        "errors": geom.get("errors", []),
        "panels": [_pack_panel(panel) for panel in geom.get("panels", [])],
    }


HTML_TEMPLATE = r'''<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1,maximum-scale=1">
<title>__TITLE__</title>
<style>
html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#f3f3f1;font-family:Segoe UI,Arial,sans-serif;color:#202020}
#gl{display:block;width:100%;height:100%;touch-action:none}
#bar{position:fixed;left:18px;top:18px;display:flex;gap:8px;flex-wrap:wrap;z-index:4;max-width:calc(100% - 36px)}
button{border:1px solid #c9c9c6;background:rgba(255,255,255,.94);border-radius:9px;padding:9px 13px;font-size:14px;cursor:pointer;box-shadow:0 1px 5px #00000012}
button:hover{background:white} button.on{background:#202020;color:white;border-color:#202020}
#info{position:fixed;left:18px;bottom:18px;background:rgba(255,255,255,.88);border:1px solid #d7d7d3;border-radius:9px;padding:8px 11px;font-size:12px;z-index:4;backdrop-filter:blur(5px)}
#hint{position:fixed;right:18px;bottom:18px;color:#666;font-size:12px;background:rgba(255,255,255,.72);padding:7px 9px;border-radius:8px}
</style>
</head>
<body>
<canvas id="gl"></canvas>
<div id="bar">
  <button id="reset">Исходный вид</button>
  <button id="wire">Каркас</button>
  <button id="solid" class="on">Материалы</button>
  <button id="alpha">Прозрачность</button>
  <button id="full">На весь экран</button>
</div>
<div id="info"></div><div id="hint">ЛКМ — вращение · колесо — масштаб · ПКМ — панорама</div>
<script id="payload" type="application/json">__PAYLOAD__</script>
<script>
(()=>{'use strict';
const DATA=JSON.parse(document.getElementById('payload').textContent);
const canvas=document.getElementById('gl'), gl=canvas.getContext('webgl',{alpha:false,antialias:true});
if(!gl){document.body.innerHTML='<p style="padding:30px">В этом браузере недоступен WebGL.</p>';return;}
const vs=`attribute vec3 p;attribute vec3 n;uniform mat4 mvp;uniform mat4 model;varying vec3 N;varying vec3 W;void main(){vec4 w=model*vec4(p,1.0);W=w.xyz;N=mat3(model)*n;gl_Position=mvp*vec4(p,1.0);}`;
const fs=`precision mediump float;uniform vec3 color;uniform float alpha;varying vec3 N;varying vec3 W;void main(){vec3 nn=normalize(N);vec3 l=normalize(vec3(.38,.55,.74));float d=max(dot(nn,l),0.0);float hemi=.56+.34*max(nn.z,0.0);float shade=.30+.52*d+.30*hemi;vec3 c=color*shade+vec3(.10);gl_FragColor=vec4(c,alpha);}`;
const lvs=`attribute vec3 p;uniform mat4 mvp;void main(){gl_Position=mvp*vec4(p,1.0);}`;
const lfs=`precision mediump float;uniform vec4 color;void main(){gl_FragColor=color;}`;
function shader(type,src){const s=gl.createShader(type);gl.shaderSource(s,src);gl.compileShader(s);if(!gl.getShaderParameter(s,gl.COMPILE_STATUS))throw new Error(gl.getShaderInfoLog(s));return s;}
function program(a,b){const p=gl.createProgram();gl.attachShader(p,shader(gl.VERTEX_SHADER,a));gl.attachShader(p,shader(gl.FRAGMENT_SHADER,b));gl.linkProgram(p);if(!gl.getProgramParameter(p,gl.LINK_STATUS))throw new Error(gl.getProgramInfoLog(p));return p;}
const prog=program(vs,fs), lineProg=program(lvs,lfs);
function b64f32(s){const bin=atob(s), u=new Uint8Array(bin.length);for(let i=0;i<bin.length;i++)u[i]=bin.charCodeAt(i);return new Float32Array(u.buffer);}
const parts=DATA.panels.map(x=>{const a=b64f32(x.data),e=b64f32(x.edges);const b=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,b);gl.bufferData(gl.ARRAY_BUFFER,a,gl.STATIC_DRAW);const eb=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,eb);gl.bufferData(gl.ARRAY_BUFFER,e,gl.STATIC_DRAW);return {...x,b,eb};});
const B=DATA.bounds, center=[0,1,2].map(i=>(B.min[i]+B.max[i])/2), size=Math.max(...[0,1,2].map(i=>B.max[i]-B.min[i]));
let yaw=.72,pitch=.42,dist=size*1.75||10,pan=[0,0,0],wire=false,solid=true,alpha=.42;
function ident(){return new Float32Array([1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1]);}
function mul(a,b){const o=new Float32Array(16);for(let c=0;c<4;c++)for(let r=0;r<4;r++)o[c*4+r]=a[0*4+r]*b[c*4+0]+a[1*4+r]*b[c*4+1]+a[2*4+r]*b[c*4+2]+a[3*4+r]*b[c*4+3];return o;}
function persp(fov,asp,n,f){const t=1/Math.tan(fov/2),o=new Float32Array(16);o[0]=t/asp;o[5]=t;o[10]=(f+n)/(n-f);o[11]=-1;o[14]=2*f*n/(n-f);return o;}
function look(eye,target,up){let z=norm(sub(eye,target)),x=norm(cross(up,z)),y=cross(z,x);const o=ident();o[0]=x[0];o[1]=y[0];o[2]=z[0];o[4]=x[1];o[5]=y[1];o[6]=z[1];o[8]=x[2];o[9]=y[2];o[10]=z[2];o[12]=-dot(x,eye);o[13]=-dot(y,eye);o[14]=-dot(z,eye);return o;}
const sub=(a,b)=>[a[0]-b[0],a[1]-b[1],a[2]-b[2]],dot=(a,b)=>a[0]*b[0]+a[1]*b[1]+a[2]*b[2],cross=(a,b)=>[a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]],norm=a=>{const l=Math.hypot(...a)||1;return a.map(v=>v/l)};
function cam(){const cp=Math.cos(pitch),sp=Math.sin(pitch),cy=Math.cos(yaw),sy=Math.sin(yaw);const target=[center[0]+pan[0],center[1]+pan[1],center[2]+pan[2]];return {eye:[target[0]+dist*cp*cy,target[1]+dist*cp*sy,target[2]+dist*sp],target};}
function draw(){const dpr=Math.min(devicePixelRatio||1,2),w=Math.max(1,innerWidth*dpr|0),h=Math.max(1,innerHeight*dpr|0);if(canvas.width!==w||canvas.height!==h){canvas.width=w;canvas.height=h;}gl.viewport(0,0,w,h);gl.clearColor(.953,.953,.945,1);gl.clear(gl.COLOR_BUFFER_BIT|gl.DEPTH_BUFFER_BIT);gl.enable(gl.DEPTH_TEST);const C=cam(),P=persp(Math.PI/4,w/h,Math.max(.1,size/1000),size*30+dist*5),V=look(C.eye,C.target,[0,0,1]),M=ident(),MVP=mul(P,mul(V,M));
 if(solid){gl.useProgram(prog);const ap=gl.getAttribLocation(prog,'p'),an=gl.getAttribLocation(prog,'n');gl.uniformMatrix4fv(gl.getUniformLocation(prog,'mvp'),false,MVP);gl.uniformMatrix4fv(gl.getUniformLocation(prog,'model'),false,M);gl.enable(gl.BLEND);gl.blendFunc(gl.SRC_ALPHA,gl.ONE_MINUS_SRC_ALPHA);for(const x of parts){gl.bindBuffer(gl.ARRAY_BUFFER,x.b);gl.enableVertexAttribArray(ap);gl.vertexAttribPointer(ap,3,gl.FLOAT,false,24,0);gl.enableVertexAttribArray(an);gl.vertexAttribPointer(an,3,gl.FLOAT,false,24,12);gl.uniform3fv(gl.getUniformLocation(prog,'color'),x.color);gl.uniform1f(gl.getUniformLocation(prog,'alpha'),alpha===1?1:alpha);gl.drawArrays(gl.TRIANGLES,0,x.vertex_count);}gl.disable(gl.BLEND);}
 if(wire){gl.useProgram(lineProg);const ap=gl.getAttribLocation(lineProg,'p');gl.uniformMatrix4fv(gl.getUniformLocation(lineProg,'mvp'),false,MVP);gl.uniform4f(gl.getUniformLocation(lineProg,'color'),.06,.06,.06,.70);for(const x of parts){gl.bindBuffer(gl.ARRAY_BUFFER,x.eb);gl.enableVertexAttribArray(ap);gl.vertexAttribPointer(ap,3,gl.FLOAT,false,12,0);gl.drawArrays(gl.LINES,0,x.edge_vertex_count);}}
 requestAnimationFrame(draw);}
let drag=false,button=0,last=[0,0];canvas.addEventListener('contextmenu',e=>e.preventDefault());canvas.addEventListener('pointerdown',e=>{drag=true;button=e.button;last=[e.clientX,e.clientY];canvas.setPointerCapture(e.pointerId)});canvas.addEventListener('pointerup',()=>drag=false);canvas.addEventListener('pointermove',e=>{if(!drag)return;const dx=e.clientX-last[0],dy=e.clientY-last[1];last=[e.clientX,e.clientY];if(button===0){yaw-=dx*.006;pitch=Math.max(-1.45,Math.min(1.45,pitch+dy*.006));}else{const s=dist*.0015;pan[0]-=dx*s*Math.sin(yaw)+dy*s*Math.cos(yaw)*Math.sin(pitch);pan[1]+=dx*s*Math.cos(yaw)-dy*s*Math.sin(yaw)*Math.sin(pitch);pan[2]+=dy*s*Math.cos(pitch);}});canvas.addEventListener('wheel',e=>{e.preventDefault();dist*=Math.exp(e.deltaY*.001);dist=Math.max(size*.08,Math.min(size*20,dist));},{passive:false});
function cls(id,on){document.getElementById(id).classList.toggle('on',on)}
document.getElementById('reset').onclick=()=>{yaw=.72;pitch=.42;dist=size*1.75;pan=[0,0,0]};document.getElementById('wire').onclick=()=>{wire=!wire;cls('wire',wire)};document.getElementById('solid').onclick=()=>{solid=!solid;cls('solid',solid)};document.getElementById('alpha').onclick=()=>{alpha=alpha===1?.42:1;cls('alpha',alpha<1)};document.getElementById('full').onclick=()=>document.fullscreenElement?document.exitFullscreen():document.documentElement.requestFullscreen();
const warn=DATA.panels.reduce((n,p)=>n+(p.warnings?.length||0),0);document.getElementById('info').textContent=`${DATA.title} · деталей: ${DATA.panel_count}`+(warn?` · необработанных операций: ${warn}`:'');draw();
})();
</script>
</body></html>'''


def publish(input_path: str | Path, output_path: str | Path) -> dict[str, Any]:
    payload = build_payload(input_path)
    payload_json = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).replace("</", "<\\/")
    html = HTML_TEMPLATE.replace("__TITLE__", payload["title"]).replace("__PAYLOAD__", payload_json)
    output = Path(output_path)
    output.write_text(html, encoding="utf-8")
    return {
        "output": str(output),
        "html_size": output.stat().st_size,
        "source_size": Path(input_path).stat().st_size,
        "panel_count": payload["panel_count"],
        "geometry_status": payload["status"],
        "geometry_errors": len(payload["errors"]),
    }


def main() -> None:
    ap = argparse.ArgumentParser(description="Publish BAZIS B3D as one autonomous offline HTML")
    ap.add_argument("input", help="Input .b3d")
    ap.add_argument("-o", "--output", help="Output .html; defaults next to input")
    args = ap.parse_args()
    src = Path(args.input)
    out = Path(args.output) if args.output else src.with_suffix(".html")
    result = publish(src, out)
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
