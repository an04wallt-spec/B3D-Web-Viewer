/*
 * Local View B3D — BAZIS 24 final-mesh publisher
 * Production geometry source: official BAZIS Script API only.
 * Reads already-built TTriMesh / TTriangleList / T3DTriangle data.
 * No B3D parsing, no panel/groove reconstruction, no OBJ/3DS/DAE, no cloud.
 */
'use strict';

apiVersion.AwareAndThrowIfApiVersionIsLowestThan(1);

const fs = require('fs');
const path = require('path');

function safeName(s) {
  return String(s || 'BAZIS_Model').replace(/[\\/:*?"<>|]+/g, '_').trim() || 'BAZIS_Model';
}

function mimeFor(file) {
  const ext = path.extname(file || '').toLowerCase();
  if (ext === '.png') return 'image/png';
  if (ext === '.jpg' || ext === '.jpeg') return 'image/jpeg';
  if (ext === '.webp') return 'image/webp';
  if (ext === '.gif') return 'image/gif';
  if (ext === '.bmp') return 'image/bmp';
  return '';
}

function colorFromBazis(value) {
  let c = Number(value);
  if (!isFinite(c) || c < 0) return [0.72, 0.70, 0.66];
  c = c >>> 0;
  return [(c & 255) / 255, ((c >>> 8) & 255) / 255, ((c >>> 16) & 255) / 255];
}

function textureData(material) {
  try {
    if (!material || typeof material.PathAbsolute !== 'function') return null;
    const file = String(material.PathAbsolute() || '');
    const mime = mimeFor(file);
    if (!file || !mime || !fs.existsSync(file)) return null;
    return 'data:' + mime + ';base64,' + fs.readFileSync(file).toString('base64');
  } catch (_) { return null; }
}

function normalize(v) {
  const x = Number(v && v.x) || 0, y = Number(v && v.y) || 0, z = Number(v && v.z) || 0;
  const l = Math.sqrt(x*x + y*y + z*z) || 1;
  return [x/l, y/l, z/l];
}
function globalPoint(obj, v) { const p = obj.ToGlobal(v); return [Number(p.x), Number(p.y), Number(p.z)]; }
function globalNormal(obj, v) { return normalize(obj.NToGlobal(v)); }
function uv(v) { return [Number(v && v.x) || 0, Number(v && v.y) || 0]; }
function push3(a, v) { a.push(v[0], v[1], v[2]); }
function push2(a, v) { a.push(v[0], v[1]); }

const textures = [], textureIndex = new Map();
function internTexture(dataUri) {
  if (!dataUri) return -1;
  if (textureIndex.has(dataUri)) return textureIndex.get(dataUri);
  const i = textures.length; textures.push(dataUri); textureIndex.set(dataUri, i); return i;
}

const parts = [];
let triangleCount = 0;

function extractMesh(obj) {
  if (!obj || !obj.IsVisible || !obj.IsVisible()) return;
  if (!obj.IsMesh || !obj.IsMesh()) return;
  const mesh = obj.AsMesh();
  if (!mesh || !mesh.TriListsCount) return;

  for (let si = 0; si < mesh.TriListsCount; si++) {
    const surface = mesh.TriLists[si];
    if (!surface || !surface.Count) continue;
    const positions = [], normals = [], texcoords = [];
    for (let ti = 0; ti < surface.Count; ti++) {
      const t = surface.Triangles[ti];
      if (!t) continue;
      push3(positions, globalPoint(obj, t.Vertex1)); push3(positions, globalPoint(obj, t.Vertex2)); push3(positions, globalPoint(obj, t.Vertex3));
      push3(normals, globalNormal(obj, t.Normal1 || t.Normal)); push3(normals, globalNormal(obj, t.Normal2 || t.Normal)); push3(normals, globalNormal(obj, t.Normal3 || t.Normal));
      push2(texcoords, uv(t.TexCoord1)); push2(texcoords, uv(t.TexCoord2)); push2(texcoords, uv(t.TexCoord3));
    }
    if (!positions.length) continue;

    const material = surface.Material || mesh.Material || null;
    let materialName = 'Без материала', color = [0.72, 0.70, 0.66];
    if (material) {
      try { materialName = String(material.MaterialName || mesh.MaterialName || materialName); } catch (_) {}
      try { color = colorFromBazis(material.DiffuseColor); } catch (_) {}
    }
    const tex = internTexture(textureData(material));
    parts.push({name:String(obj.Name || obj.Designation || ('Объект ' + parts.length)), materialName:materialName, color:color, texture:tex, positions:positions, normals:normals, texcoords:texcoords});
    triangleCount += positions.length / 9;
  }
}

function walk(list) {
  if (!list) return;
  const count = Number(list.Count) || 0;
  for (let i = 0; i < count; i++) {
    const obj = list.Objects[i];
    if (!obj) continue;
    extractMesh(obj);
    if (obj.List && obj.AsList) walk(obj.AsList());
  }
}
walk(currentFileData.model);

if (!parts.length) {
  UI.dialogs.ErrorBox('B3D Publisher: БАЗИС не вернул ни одного готового треугольника. Публикация остановлена; геометрия не реконструируется.');
  throw new Error('No final TTriMesh triangles returned by BAZIS');
}

function f32Base64(values) {
  const b = Buffer.alloc(values.length * 4);
  for (let i = 0; i < values.length; i++) b.writeFloatLE(Number(values[i]) || 0, i * 4);
  return b.toString('base64');
}
const packed = parts.map(function(p) { return {name:p.name, materialName:p.materialName, color:p.color, texture:p.texture, p:f32Base64(p.positions), n:f32Base64(p.normals), u:f32Base64(p.texcoords), vc:p.positions.length/3}; });

const modelFile = String(currentFileData.filename || '');
const modelName = safeName(path.basename(modelFile, path.extname(modelFile)) || 'BAZIS_Model');
const outDir = modelFile ? path.dirname(modelFile) : process.cwd();
const outFile = path.join(outDir, modelName + '_просмотр.html');
const payload = JSON.stringify({parts:packed, textures:textures}).replace(/<\//g, '<\\/');
const title = modelName.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
const stats = parts.length + ' пов. · ' + triangleCount + ' треуг.';

const html = `<!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>${title} — Local View B3D</title><style>
html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#f1f1ee;font-family:Segoe UI,Arial,sans-serif;color:#222}canvas{width:100%;height:100%;display:block;touch-action:none}.bar{position:fixed;right:16px;top:16px;display:flex;flex-wrap:wrap;justify-content:flex-end;gap:7px;max-width:min(780px,calc(100vw - 32px));z-index:2}.info{position:fixed;left:16px;bottom:16px;background:#ffffffed;border:1px solid #c9c9c5;padding:8px 11px;border-radius:8px;font-size:12px;box-shadow:0 2px 10px #00000012;user-select:none}button{border:1px solid #c8c8c5;background:#fff;color:#222;padding:8px 11px;border-radius:8px;cursor:pointer;font:inherit}button:hover{background:#f7f7f4}button.on{background:#262626;color:#fff;border-color:#262626}@media(max-width:680px){.bar{left:10px;right:10px;top:10px}.info{left:10px;bottom:10px}}
</style></head><body><canvas id="c"></canvas><div class="bar"><button id="home">Перспектива</button><button id="fit">Вписать</button><button id="edges" class="on">Рёбра</button><button id="alpha">Прозрачность</button><button id="clearSel">Снять выделение</button></div><div class="info">${title} · ${stats} · Local View B3D</div><script id="data" type="application/json">${payload}</script><script>
(()=>{'use strict';const D=JSON.parse(document.getElementById('data').textContent),c=document.getElementById('c'),gl=c.getContext('webgl2',{antialias:true})||c.getContext('webgl',{antialias:true});if(!gl){alert('WebGL недоступен');return}const u8=s=>{const b=atob(s),a=new Uint8Array(b.length);for(let i=0;i<b.length;i++)a[i]=b.charCodeAt(i);return a},f32=s=>{const a=u8(s);return new Float32Array(a.buffer,a.byteOffset,a.byteLength/4)};function sh(k,s){const q=gl.createShader(k);gl.shaderSource(q,s);gl.compileShader(q);if(!gl.getShaderParameter(q,gl.COMPILE_STATUS))throw Error(gl.getShaderInfoLog(q));return q}const pr=gl.createProgram();gl.attachShader(pr,sh(gl.VERTEX_SHADER,'attribute vec3 a,n;attribute vec2 uv;uniform mat4 vp;varying vec3 N;varying vec2 U;void main(){N=n;U=uv;gl_Position=vp*vec4(a,1.0);}'));gl.attachShader(pr,sh(gl.FRAGMENT_SHADER,'precision mediump float;varying vec3 N;varying vec2 U;uniform vec4 col;uniform sampler2D tx;uniform float useTx;void main(){float l=.32+.68*max(0.0,dot(normalize(N),normalize(vec3(.35,.75,.55))));vec4 base=useTx>.5?texture2D(tx,U):col;gl_FragColor=vec4(base.rgb*l,base.a*col.a);}'));gl.linkProgram(pr);gl.useProgram(pr);const A=gl.getAttribLocation(pr,'a'),N=gl.getAttribLocation(pr,'n'),UV=gl.getAttribLocation(pr,'uv'),VP=gl.getUniformLocation(pr,'vp'),COL=gl.getUniformLocation(pr,'col'),USE=gl.getUniformLocation(pr,'useTx');function buf(data){const b=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,b);gl.bufferData(gl.ARRAY_BUFFER,data,gl.STATIC_DRAW);return b}const tex=D.textures.map(src=>{const t=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,t);gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,1,1,0,gl.RGBA,gl.UNSIGNED_BYTE,new Uint8Array([210,210,210,255]));const im=new Image();im.onload=()=>{gl.bindTexture(gl.TEXTURE_2D,t);gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL,1);gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,gl.RGBA,gl.UNSIGNED_BYTE,im);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_S,gl.REPEAT);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_T,gl.REPEAT);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.LINEAR_MIPMAP_LINEAR);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.LINEAR);gl.generateMipmap(gl.TEXTURE_2D)};im.src=src;return t});const parts=D.parts.map((m,i)=>{const p=f32(m.p),n=f32(m.n),u=f32(m.u);return{...m,id:i+1,p,n,u,pb:buf(p),nb:buf(n),ub:buf(u)}});let mn=[Infinity,Infinity,Infinity],mx=[-Infinity,-Infinity,-Infinity];for(const m of parts)for(let i=0;i<m.p.length;i+=3)for(let k=0;k<3;k++){mn[k]=Math.min(mn[k],m.p[i+k]);mx[k]=Math.max(mx[k],m.p[i+k])}const ctr=[0,1,2].map(i=>(mn[i]+mx[i])/2),size=Math.max(1,...[0,1,2].map(i=>mx[i]-mn[i]));const dot=(a,b)=>a[0]*b[0]+a[1]*b[1]+a[2]*b[2],cross=(a,b)=>[a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]],norm=a=>{const l=Math.hypot(...a)||1;return a.map(v=>v/l)},mul=(a,b)=>{const r=new Float32Array(16);for(let c=0;c<4;c++)for(let r0=0;r0<4;r0++)for(let k=0;k<4;k++)r[c*4+r0]+=a[k*4+r0]*b[c*4+k];return r},persp=(f,a,n,fa)=>{const s=1/Math.tan(f/2),r=new Float32Array(16);r[0]=s/a;r[5]=s;r[10]=(fa+n)/(n-fa);r[11]=-1;r[14]=2*fa*n/(n-fa);return r},look=(e,t)=>{const z=norm([e[0]-t[0],e[1]-t[1],e[2]-t[2]]),x=norm(cross([0,1,0],z)),y=cross(z,x),r=new Float32Array(16);r[0]=x[0];r[1]=y[0];r[2]=z[0];r[4]=x[1];r[5]=y[1];r[6]=z[1];r[8]=x[2];r[9]=y[2];r[10]=z[2];r[12]=-dot(x,e);r[13]=-dot(y,e);r[14]=-dot(z,e);r[15]=1;return r};let yaw=-.8,pitch=.35,dist=size*1.85,target=[...ctr],drag=null,moved=false,edges=true,transparent=false,selected=0;const fit=()=>{target=[...ctr];dist=size*1.85},home=()=>{yaw=-.8;pitch=.35;fit()};function cam(){const cp=Math.cos(pitch),eye=[target[0]+dist*cp*Math.cos(yaw),target[1]+dist*Math.sin(pitch),target[2]+dist*cp*Math.sin(yaw)];return mul(persp(Math.PI/4,Math.max(1,c.width)/Math.max(1,c.height),Math.max(.01,size/10000),size*60+dist*10),look(eye,target))}function bindAttr(loc,b,n){gl.bindBuffer(gl.ARRAY_BUFFER,b);gl.enableVertexAttribArray(loc);gl.vertexAttribPointer(loc,n,gl.FLOAT,false,0,0)}function scene(){const d=Math.min(devicePixelRatio||1,2),w=Math.max(1,c.clientWidth*d|0),h=Math.max(1,c.clientHeight*d|0);if(c.width!==w||c.height!==h){c.width=w;c.height=h}gl.viewport(0,0,w,h);gl.enable(gl.DEPTH_TEST);gl.depthMask(true);gl.clearColor(.945,.945,.93,1);gl.clear(gl.COLOR_BUFFER_BIT|gl.DEPTH_BUFFER_BIT);gl.uniformMatrix4fv(VP,false,cam());if(transparent){gl.enable(gl.BLEND);gl.blendFunc(gl.SRC_ALPHA,gl.ONE_MINUS_SRC_ALPHA)}else gl.disable(gl.BLEND);for(const x of parts){bindAttr(A,x.pb,3);bindAttr(N,x.nb,3);bindAttr(UV,x.ub,2);const sel=selected===x.id;gl.uniform4fv(COL,new Float32Array(sel?[1,.56,.1,1]:[x.color[0],x.color[1],x.color[2],transparent?.34:1]));if(x.texture>=0&&!sel){gl.activeTexture(gl.TEXTURE0);gl.bindTexture(gl.TEXTURE_2D,tex[x.texture]);gl.uniform1f(USE,1)}else gl.uniform1f(USE,0);gl.drawArrays(gl.TRIANGLES,0,x.vc)}if(edges){gl.disable(gl.BLEND);gl.uniform1f(USE,0);gl.uniform4fv(COL,new Float32Array([.06,.06,.06,.75]));for(const x of parts){bindAttr(A,x.pb,3);bindAttr(N,x.nb,3);bindAttr(UV,x.ub,2);for(let i=0;i<x.vc;i+=3)gl.drawArrays(gl.LINE_LOOP,i,3)}}}function pick(cx,cy){const r=c.getBoundingClientRect(),x=(cx-r.left)/Math.max(1,r.width)*2-1,y=1-(cy-r.top)/Math.max(1,r.height)*2;let best=0,bestD=Infinity;const vp=cam();function proj(px,py,pz){const q=[vp[0]*px+vp[4]*py+vp[8]*pz+vp[12],vp[1]*px+vp[5]*py+vp[9]*pz+vp[13],vp[3]*px+vp[7]*py+vp[11]*pz+vp[15]];return q[2]?[q[0]/q[2],q[1]/q[2]]:[99,99]}for(const m of parts){let sx=0,sy=0,cnt=0;const step=Math.max(3,Math.floor(m.p.length/300/3)*3);for(let i=0;i<m.p.length;i+=step){const q=proj(m.p[i],m.p[i+1],m.p[i+2]);sx+=q[0];sy+=q[1];cnt++}if(cnt){const d=Math.hypot(sx/cnt-x,sy/cnt-y);if(d<bestD){bestD=d;best=m.id}}}selected=bestD<.18?best:0}function frame(){scene();requestAnimationFrame(frame)}c.oncontextmenu=e=>e.preventDefault();c.onpointerdown=e=>{drag=[e.clientX,e.clientY,e.button];moved=false;c.setPointerCapture(e.pointerId)};c.onpointermove=e=>{if(!drag)return;const dx=e.clientX-drag[0],dy=e.clientY-drag[1];if(Math.abs(dx)+Math.abs(dy)>2)moved=true;if(drag[2]===2||drag[2]===1){const s=dist*.00125,r=[-Math.sin(yaw),0,Math.cos(yaw)];target[0]-=dx*s*r[0];target[2]-=dx*s*r[2];target[1]+=dy*s}else{yaw+=dx*.0034;pitch=Math.max(-1.18,Math.min(1.18,pitch+dy*.0034))}drag=[e.clientX,e.clientY,drag[2]]};c.onpointerup=e=>{if(drag&&!moved&&drag[2]===0)pick(e.clientX,e.clientY);drag=null};c.onpointercancel=()=>drag=null;c.onwheel=e=>{e.preventDefault();dist=Math.max(size*.06,Math.min(size*30,dist*Math.exp(e.deltaY*.001)))};const eb=document.getElementById('edges'),ab=document.getElementById('alpha');document.getElementById('home').onclick=home;document.getElementById('fit').onclick=fit;eb.onclick=()=>{edges=!edges;eb.classList.toggle('on',edges)};ab.onclick=()=>{transparent=!transparent;ab.classList.toggle('on',transparent)};document.getElementById('clearSel').onclick=()=>selected=0;document.addEventListener('keydown',e=>{if(e.key==='Escape')selected=0});home();requestAnimationFrame(frame)})();
</script></body></html>`;

if (/<script\s+src=/i.test(html) || /https?:\/\//i.test(html)) throw new Error('Offline HTML contract failed');
fs.writeFileSync(outFile, html, 'utf8');
UI.dialogs.MessageBox('B3D Publisher: готово.\r\n\r\n' + outFile + '\r\n\r\n' + parts.length + ' поверхностей, ' + triangleCount + ' треугольников.');
