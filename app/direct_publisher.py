#!/usr/bin/env python3
"""Production B3D Publisher: direct BZ85 parser -> final CSG mesh -> one offline HTML."""
from __future__ import annotations
import base64,hashlib,json,math,os,struct,sys,traceback
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
for p in (ROOT/'parser',ROOT/'geometry'):
    if str(p) not in sys.path: sys.path.insert(0,str(p))
from b3d_parser import parse_current_model
from final_geometry import extract_final_meshes

def normal(a,b,c):
    ux,uy,uz=b[0]-a[0],b[1]-a[1],b[2]-a[2];vx,vy,vz=c[0]-a[0],c[1]-a[1],c[2]-a[2]
    x,y,z=uy*vz-uz*vy,uz*vx-ux*vz,ux*vy-uy*vx;l=math.sqrt(x*x+y*y+z*z) or 1
    return x/l,y/l,z/l

def color(v):
    d=hashlib.sha256(str(v).encode('utf-8','replace')).digest(); b=[.58+d[i]/255*.18 for i in range(3)];g=sum(b)/3
    return [round(.68*x+.18*g+.08,4) for x in b]

def feature_edges(vertices,triangles,angle=22.):
    norms=[];ef={}
    for fi,(a,b,c) in enumerate(triangles):
        norms.append(normal(vertices[a],vertices[b],vertices[c]))
        for u,v in ((a,b),(b,c),(c,a)):
            k=(u,v) if u<v else (v,u);ef.setdefault(k,[]).append(fi)
    lim=math.cos(math.radians(angle));out=[]
    for (u,v),fs in ef.items():
        keep=len(fs)==1
        if len(fs)>1:
            n0=norms[fs[0]]
            keep=any(sum(n0[i]*norms[f][i] for i in range(3))<lim for f in fs[1:])
        if keep: out.extend(vertices[u]+vertices[v])
    return out

def pack_f32(vals):
    if not vals:return ''
    return base64.b64encode(struct.pack('<'+'f'*len(vals),*map(float,vals))).decode('ascii')

def pack_panel(p):
    vs=p['mesh']['vertices'];ts=p['mesh']['triangles'];arr=[]
    for ia,ib,ic in ts:
        n=normal(vs[ia],vs[ib],vs[ic])
        for q in (vs[ia],vs[ib],vs[ic]):arr.extend((q[0],q[1],q[2],*n))
    ed=feature_edges(vs,ts)
    return {'id':p.get('id'),'name':p.get('name') or 'Деталь','material':p.get('material'),'color':color(p.get('material')),'triangles':len(ts),'vertex_count':len(arr)//6,'data':pack_f32(arr),'edge_vertex_count':len(ed)//3,'edges':pack_f32(ed)}

def build_payload(path):
    model,meta=parse_current_model(path);g=extract_final_meshes(model,3.)
    if g['errors']:raise RuntimeError('Не удалось построить геометрию: '+json.dumps(g['errors'],ensure_ascii=False))
    if g['panel_count']==0:raise RuntimeError('В B3D не найдено панелей модели')
    return {'format':'local-view-direct-b3d-2','title':Path(path).stem,'source':{'signature':meta['signature'],'file_size':meta['file_size']},'status':g['status'],'bounds':g['bounds'],'panel_count':g['panel_count'],'panels':[pack_panel(p) for p in g['panels']]}

HTML=r'''<!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>__TITLE__</title><style>
html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#f3f3f1;font-family:Segoe UI,Arial,sans-serif;color:#202020}#gl{width:100%;height:100%;display:block;touch-action:none}#bar{position:fixed;left:16px;top:16px;z-index:3;display:flex;gap:7px;flex-wrap:wrap;max-width:calc(100% - 32px)}button{border:1px solid #c9c9c6;background:#fffffff2;border-radius:8px;padding:8px 12px;font-size:14px;cursor:pointer;box-shadow:0 1px 4px #0001}button.on{background:#252525;color:white;border-color:#252525}#info{position:fixed;left:16px;bottom:16px;background:#ffffffdd;border:1px solid #d5d5d2;border-radius:8px;padding:8px 10px;font-size:12px;z-index:3}#hint{position:fixed;right:16px;bottom:16px;color:#666;font-size:12px;background:#ffffffbb;padding:7px 9px;border-radius:7px}</style></head><body><canvas id="gl"></canvas><div id="bar"><button id="home">Исходный вид</button><button id="edges" class="on">Рёбра</button><button id="alpha">Прозрачность</button><button id="clear">Снять выделение</button><button id="full">На весь экран</button></div><div id="info"></div><div id="hint">ЛКМ — вращение · колесо — масштаб · ПКМ — панорама · Esc — снять выделение</div><script id="payload" type="application/json">__PAYLOAD__</script><script>
(()=>{'use strict';const D=JSON.parse(document.getElementById('payload').textContent),cv=document.getElementById('gl'),gl=cv.getContext('webgl',{antialias:true,alpha:false,preserveDrawingBuffer:true});if(!gl){document.body.innerHTML='<p style="padding:30px">WebGL недоступен.</p>';return}
function sh(t,s){let x=gl.createShader(t);gl.shaderSource(x,s);gl.compileShader(x);if(!gl.getShaderParameter(x,gl.COMPILE_STATUS))throw Error(gl.getShaderInfoLog(x));return x}function pr(v,f){let p=gl.createProgram();gl.attachShader(p,sh(gl.VERTEX_SHADER,v));gl.attachShader(p,sh(gl.FRAGMENT_SHADER,f));gl.linkProgram(p);if(!gl.getProgramParameter(p,gl.LINK_STATUS))throw Error(gl.getProgramInfoLog(p));return p}
const V=`attribute vec3 p;attribute vec3 n;uniform mat4 mvp;varying vec3 N;void main(){N=n;gl_Position=mvp*vec4(p,1.);}`,F=`precision mediump float;uniform vec3 c;uniform float a;varying vec3 N;void main(){vec3 n=normalize(N),l=normalize(vec3(.32,.62,.72));float q=.48+.52*max(dot(n,l),0.);gl_FragColor=vec4(c*q+vec3(.13),a);}`,LV=`attribute vec3 p;uniform mat4 mvp;void main(){gl_Position=mvp*vec4(p,1.);}`,LF=`precision mediump float;uniform vec4 c;void main(){gl_FragColor=c;}`;const P=pr(V,F),LP=pr(LV,LF);
function dec(s){let b=atob(s),u=new Uint8Array(b.length);for(let i=0;i<b.length;i++)u[i]=b.charCodeAt(i);return new Float32Array(u.buffer)}const parts=D.panels.map((x,i)=>{let b=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,b);gl.bufferData(gl.ARRAY_BUFFER,dec(x.data),gl.STATIC_DRAW);let e=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,e);gl.bufferData(gl.ARRAY_BUFFER,dec(x.edges),gl.STATIC_DRAW);return {...x,b,e,i}});
const B=D.bounds,C=[0,1,2].map(i=>(B.min[i]+B.max[i])/2),S=Math.max(...[0,1,2].map(i=>B.max[i]-B.min[i]))||1;let yaw=-.72,pitch=.42,dist=S*1.65,pan=[0,0,0],showEdges=true,alpha=1,sel=-1;
const I=()=>new Float32Array([1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1]);function mul(a,b){let o=new Float32Array(16);for(let c=0;c<4;c++)for(let r=0;r<4;r++)o[c*4+r]=a[r]*b[c*4]+a[4+r]*b[c*4+1]+a[8+r]*b[c*4+2]+a[12+r]*b[c*4+3];return o}function per(f,a,n,z){let t=1/Math.tan(f/2),o=new Float32Array(16);o[0]=t/a;o[5]=t;o[10]=(z+n)/(n-z);o[11]=-1;o[14]=2*z*n/(n-z);return o}const sub=(a,b)=>a.map((x,i)=>x-b[i]),dot=(a,b)=>a.reduce((s,x,i)=>s+x*b[i],0),cross=(a,b)=>[a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]],norm=a=>{let l=Math.hypot(...a)||1;return a.map(x=>x/l)};function look(e,t,u){let z=norm(sub(e,t)),x=norm(cross(u,z)),y=cross(z,x),o=I();o[0]=x[0];o[1]=y[0];o[2]=z[0];o[4]=x[1];o[5]=y[1];o[6]=z[1];o[8]=x[2];o[9]=y[2];o[10]=z[2];o[12]=-dot(x,e);o[13]=-dot(y,e);o[14]=-dot(z,e);return o}
function matrix(){let cp=Math.cos(pitch),sp=Math.sin(pitch),cy=Math.cos(yaw),sy=Math.sin(yaw),t=[C[0]+pan[0],C[1]+pan[1],C[2]+pan[2]],e=[t[0]+dist*cp*cy,t[1]+dist*sp,t[2]+dist*cp*sy],p=per(Math.PI/4,cv.width/cv.height,Math.max(.1,S/10000),S*40+dist*4);return mul(p,look(e,t,[0,1,0]))}
function size(){let d=Math.min(devicePixelRatio||1,2),w=Math.max(1,innerWidth*d|0),h=Math.max(1,innerHeight*d|0);if(cv.width!=w||cv.height!=h){cv.width=w;cv.height=h}gl.viewport(0,0,w,h)}function col(i){return [(i&255)/255,((i>>8)&255)/255,((i>>16)&255)/255]}
function draw(pick=false){size();gl.clearColor(.953,.953,.945,1);gl.clear(gl.COLOR_BUFFER_BIT|gl.DEPTH_BUFFER_BIT);gl.enable(gl.DEPTH_TEST);let m=matrix();gl.useProgram(P);let ap=gl.getAttribLocation(P,'p'),an=gl.getAttribLocation(P,'n');gl.uniformMatrix4fv(gl.getUniformLocation(P,'mvp'),false,m);if(!pick&&alpha<1){gl.enable(gl.BLEND);gl.blendFunc(gl.SRC_ALPHA,gl.ONE_MINUS_SRC_ALPHA)}else gl.disable(gl.BLEND);for(const x of parts){gl.bindBuffer(gl.ARRAY_BUFFER,x.b);gl.enableVertexAttribArray(ap);gl.vertexAttribPointer(ap,3,gl.FLOAT,false,24,0);gl.enableVertexAttribArray(an);gl.vertexAttribPointer(an,3,gl.FLOAT,false,24,12);let c=pick?col(x.i+1):(x.i===sel?[1,.48,.08]:x.color);gl.uniform3fv(gl.getUniformLocation(P,'c'),c);gl.uniform1f(gl.getUniformLocation(P,'a'),pick?1:alpha);gl.drawArrays(gl.TRIANGLES,0,x.vertex_count)}if(!pick&&showEdges){gl.disable(gl.BLEND);gl.useProgram(LP);let a=gl.getAttribLocation(LP,'p');gl.uniformMatrix4fv(gl.getUniformLocation(LP,'mvp'),false,m);gl.uniform4f(gl.getUniformLocation(LP,'c'),.08,.08,.08,.72);for(const x of parts){gl.bindBuffer(gl.ARRAY_BUFFER,x.e);gl.enableVertexAttribArray(a);gl.vertexAttribPointer(a,3,gl.FLOAT,false,12,0);gl.drawArrays(gl.LINES,0,x.edge_vertex_count)}}if(!pick){let x=sel>=0?parts[sel]:null;document.getElementById('info').textContent=x?`${x.name} · ID ${x.id}`:`${D.title} · деталей: ${D.panel_count}`}}
function pick(ev){draw(true);let r=cv.getBoundingClientRect(),d=cv.width/r.width,x=Math.floor((ev.clientX-r.left)*d),y=Math.floor(cv.height-(ev.clientY-r.top)*d),px=new Uint8Array(4);gl.readPixels(x,y,1,1,gl.RGBA,gl.UNSIGNED_BYTE,px);let id=px[0]+(px[1]<<8)+(px[2]<<16);sel=id?Math.min(parts.length-1,id-1):-1;draw()}
let down=null,moved=false;cv.addEventListener('contextmenu',e=>e.preventDefault());cv.addEventListener('pointerdown',e=>{cv.setPointerCapture(e.pointerId);down={x:e.clientX,y:e.clientY,b:e.button};moved=false});cv.addEventListener('pointermove',e=>{if(!down)return;let dx=e.clientX-down.x,dy=e.clientY-down.y;if(Math.abs(dx)+Math.abs(dy)>2)moved=true;if(down.b===0){yaw-=dx*.004;pitch=Math.max(-1.2,Math.min(1.2,pitch+dy*.004))}else if(down.b===2){let k=dist*.0012;pan[0]-=dx*k*Math.sin(yaw);pan[2]+=dx*k*Math.cos(yaw);pan[1]+=dy*k}down.x=e.clientX;down.y=e.clientY;draw()});cv.addEventListener('pointerup',e=>{if(down&&down.b===0&&!moved)pick(e);down=null});cv.addEventListener('wheel',e=>{e.preventDefault();dist*=Math.exp(e.deltaY*.001);dist=Math.max(S*.12,Math.min(S*8,dist));draw()},{passive:false});
function clear(){sel=-1;draw()}document.getElementById('home').onclick=()=>{yaw=-.72;pitch=.42;dist=S*1.65;pan=[0,0,0];draw()};document.getElementById('edges').onclick=e=>{showEdges=!showEdges;e.target.classList.toggle('on',showEdges);draw()};document.getElementById('alpha').onclick=e=>{alpha=alpha===1?.36:1;e.target.classList.toggle('on',alpha<1);draw()};document.getElementById('clear').onclick=clear;document.getElementById('full').onclick=()=>document.fullscreenElement?document.exitFullscreen():document.documentElement.requestFullscreen();addEventListener('keydown',e=>{if(e.key==='Escape')clear()});addEventListener('resize',draw);draw();})();</script></body></html>'''

def publish(inp,out=None):
    inp=Path(inp).resolve();out=Path(out).resolve() if out else inp.with_name(inp.stem+'_просмотр.html');p=build_payload(inp);raw=json.dumps(p,ensure_ascii=False,separators=(',',':')).replace('</','<\\/');html=HTML.replace('__TITLE__',p['title']+' — Local View B3D').replace('__PAYLOAD__',raw)
    if '<script src=' in html or 'http://' in html or 'https://' in html:raise RuntimeError('HTML не прошёл offline-проверку')
    out.write_text(html,encoding='utf-8');return out,p

def gui():
    import tkinter as tk
    from tkinter import filedialog,messagebox
    root=tk.Tk();root.withdraw();root.update()
    src=filedialog.askopenfilename(title='Выберите файл БАЗИС B3D',filetypes=[('BAZIS 3D','*.b3d'),('Все файлы','*.*')])
    if not src:return 0
    try:
        out,p=publish(src);messagebox.showinfo('B3D Publisher',f"Готово.\n\n{out}\n\nДеталей: {p['panel_count']}")
        try:os.startfile(str(out.parent))
        except Exception:pass
        return 0
    except Exception as e:
        log=Path(src).with_name(Path(src).stem+'_publisher_error.txt');log.write_text(str(e)+'\n\n'+traceback.format_exc(),encoding='utf-8');messagebox.showerror('B3D Publisher',f"Ошибка:\n{e}\n\nДиагностика: {log}");return 2

def main():
    if len(sys.argv)>1:
        try:
            out,p=publish(sys.argv[1],sys.argv[2] if len(sys.argv)>2 else None);print(out);print('panels',p['panel_count']);return 0
        except Exception as e:print(e,file=sys.stderr);return 2
    return gui()
if __name__=='__main__':raise SystemExit(main())
