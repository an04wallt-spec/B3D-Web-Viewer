using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace B3DPublisherHost;

internal static class OfflineHtmlPublisher
{
    private const string Format = "local-view-bazis-viewer3d-wrl-1";

    public static void Publish(VrmlModel model, string sourceB3d, string outputHtml)
    {
        var textures = new List<string>();
        var textureMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var parts = new List<object>();

        for (var pi = 0; pi < model.Parts.Count; pi++)
        {
            var p = model.Parts[pi];
            var textureIndex = -1;
            if (!string.IsNullOrWhiteSpace(p.TexturePath) && File.Exists(p.TexturePath))
            {
                var data = TextureDataUri(p.TexturePath!);
                if (data is not null)
                {
                    if (!textureMap.TryGetValue(data, out textureIndex))
                    {
                        textureIndex = textures.Count;
                        textures.Add(data);
                        textureMap.Add(data, textureIndex);
                    }
                }
            }

            var indexBytes = p.Positions.Length / 3 <= ushort.MaxValue ? 2 : 4;
            var edges = FeatureEdges(p.Positions, p.Indices, 20f);
            parts.Add(new
            {
                id = pi + 1,
                name = p.Name,
                color = p.Color,
                texture = textureIndex,
                p = Float32Base64(p.Positions),
                n = Float32Base64(p.Normals),
                u = Float32Base64(p.TexCoords),
                i = IndexBase64(p.Indices, indexBytes),
                it = indexBytes,
                ic = p.Indices.Length,
                e = Float32Base64(edges),
                ec = edges.Length / 3
            });
        }

        var payload = JsonSerializer.Serialize(new
        {
            format = Format,
            source = Path.GetFileName(sourceB3d),
            triangles = model.TriangleCount,
            parts,
            textures
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        var html = Template.Replace("__PAYLOAD__", EscapeScriptJson(payload), StringComparison.Ordinal);
        if (html.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("<script src=", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Шаблон viewer содержит внешнюю зависимость.");
        File.WriteAllText(outputHtml, html, new UTF8Encoding(false));
    }

    private static string EscapeScriptJson(string json) => json.Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);

    private static string? TextureDataUri(string path)
    {
        var mime = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => null
        };
        return mime is null ? null : $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}";
    }

    private static string Float32Base64(float[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4, 4), values[i]);
        return Convert.ToBase64String(bytes);
    }

    private static string IndexBase64(uint[] values, int bytesPerIndex)
    {
        var bytes = new byte[values.Length * bytesPerIndex];
        if (bytesPerIndex == 2)
        {
            for (var i = 0; i < values.Length; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), checked((ushort)values[i]));
        }
        else
        {
            for (var i = 0; i < values.Length; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4, 4), values[i]);
        }
        return Convert.ToBase64String(bytes);
    }

    private sealed class EdgeInfo
    {
        public required float[] A { get; init; }
        public required float[] B { get; init; }
        public List<float[]> Normals { get; } = new();
    }

    private static float[] FeatureEdges(float[] positions, uint[] indices, float creaseAngleDeg)
    {
        var map = new Dictionary<string, EdgeInfo>();
        var cosLimit = MathF.Cos(creaseAngleDeg * MathF.PI / 180f);
        float[] P(uint idx) => new[] { positions[idx * 3], positions[idx * 3 + 1], positions[idx * 3 + 2] };
        string K(float[] p) => $"{MathF.Round(p[0] * 1000)},{MathF.Round(p[1] * 1000)},{MathF.Round(p[2] * 1000)}";
        void Add(float[] a, float[] b, float[] n)
        {
            var ka = K(a); var kb = K(b); var key = string.CompareOrdinal(ka, kb) < 0 ? ka + "|" + kb : kb + "|" + ka;
            if (!map.TryGetValue(key, out var e))
            {
                e = new EdgeInfo { A = a, B = b };
                map.Add(key, e);
            }
            e.Normals.Add(n);
        }

        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            var a = P(indices[i]); var b = P(indices[i + 1]); var c = P(indices[i + 2]);
            var n = Unit(Cross(Sub(b, a), Sub(c, a)));
            Add(a, b, n); Add(b, c, n); Add(c, a, n);
        }
        var result = new List<float>();
        foreach (var e in map.Values)
        {
            var keep = e.Normals.Count != 2 || Dot(e.Normals[0], e.Normals[1]) < cosLimit;
            if (!keep) continue;
            result.AddRange(e.A); result.AddRange(e.B);
        }
        return result.ToArray();
    }

    private static float[] Sub(float[] a, float[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
    private static float[] Cross(float[] a, float[] b) => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
    private static float Dot(float[] a, float[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    private static float[] Unit(float[] v)
    {
        var l = MathF.Sqrt(Dot(v, v));
        return l > 1e-12f ? new[] { v[0] / l, v[1] / l, v[2] / l } : new[] { 0f, 0f, 1f };
    }

    private const string Template = """
<!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Local View B3D</title>
<style>html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#eef1f3;font-family:Segoe UI,Arial,sans-serif;color:#20252a}canvas{display:block;width:100%;height:100%}.bar{position:fixed;left:12px;top:12px;display:flex;gap:7px;flex-wrap:wrap;z-index:2}.bar button{border:1px solid #aab2b8;background:#fff;border-radius:6px;padding:7px 11px;cursor:pointer}.bar button.on{background:#e0e7ec}.info{position:fixed;left:12px;bottom:12px;background:rgba(255,255,255,.92);border:1px solid #c8cdd1;border-radius:6px;padding:8px 10px;max-width:45%;font-size:12px}.sel{position:fixed;right:12px;top:12px;background:rgba(255,255,255,.92);border:1px solid #c8cdd1;border-radius:6px;padding:8px 10px;font-size:12px}</style></head><body>
<div class="bar"><button id="fit">Вписать</button><button id="edges" class="on">Рёбра</button><button id="alpha">Прозрачность</button><button id="clear">Снять выделение</button></div><canvas id="c"></canvas><div class="info" id="info"></div><div class="sel" id="sel">Ничего не выделено</div>
<script id="data" type="application/json">__PAYLOAD__</script><script>
'use strict';const D=JSON.parse(document.getElementById('data').textContent),c=document.getElementById('c');const gl=c.getContext('webgl2',{antialias:true,preserveDrawingBuffer:true})||c.getContext('webgl',{antialias:true,preserveDrawingBuffer:true});if(!gl)throw Error('WebGL недоступен');
function sh(t,s){const x=gl.createShader(t);gl.shaderSource(x,s);gl.compileShader(x);if(!gl.getShaderParameter(x,gl.COMPILE_STATUS))throw Error(gl.getShaderInfoLog(x));return x}function pr(v,f){const p=gl.createProgram();gl.attachShader(p,sh(gl.VERTEX_SHADER,v));gl.attachShader(p,sh(gl.FRAGMENT_SHADER,f));gl.linkProgram(p);if(!gl.getProgramParameter(p,gl.LINK_STATUS))throw Error(gl.getProgramInfoLog(p));return p}
const vs=`attribute vec3 aP;attribute vec3 aN;attribute vec2 aU;uniform mat4 uM;varying vec3 n;varying vec2 uv;void main(){gl_Position=uM*vec4(aP,1.);n=aN;uv=aU;}`;const fs=`precision mediump float;varying vec3 n;varying vec2 uv;uniform vec3 uC;uniform sampler2D uT;uniform float uHasT,uA,uSel,uPick;uniform vec3 uPickC;void main(){if(uPick>.5){gl_FragColor=vec4(uPickC,1.);return;}vec3 base=mix(uC,texture2D(uT,uv).rgb,uHasT);float l=.45+.55*abs(dot(normalize(n),normalize(vec3(.35,.55,1.))));if(uSel>.5)base=mix(base,vec3(1.,.45,.05),.55);gl_FragColor=vec4(base*l,uA);}`;const P=pr(vs,fs),LP=pr(`attribute vec3 aP;uniform mat4 uM;void main(){gl_Position=uM*vec4(aP,1.);}`,`precision mediump float;void main(){gl_FragColor=vec4(.08,.09,.10,1.);}`);
function b64(s,T){const q=atob(s),a=new Uint8Array(q.length);for(let i=0;i<q.length;i++)a[i]=q.charCodeAt(i);return new T(a.buffer)}function buf(data){const b=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,b);gl.bufferData(gl.ARRAY_BUFFER,data,gl.STATIC_DRAW);return b}function ibuf(data){const b=gl.createBuffer();gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER,b);gl.bufferData(gl.ELEMENT_ARRAY_BUFFER,data,gl.STATIC_DRAW);return b}
const tex=D.textures.map(src=>{const t=gl.createTexture(),im=new Image();im.onload=()=>{gl.bindTexture(gl.TEXTURE_2D,t);gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL,1);gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,gl.RGBA,gl.UNSIGNED_BYTE,im);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.LINEAR);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.LINEAR);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_S,gl.CLAMP_TO_EDGE);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_T,gl.CLAMP_TO_EDGE);draw()};im.src=src;return t});
const parts=D.parts.map(x=>{const p=b64(x.p,Float32Array),n=b64(x.n,Float32Array),u=b64(x.u,Float32Array),I=x.it===2?b64(x.i,Uint16Array):b64(x.i,Uint32Array),e=b64(x.e,Float32Array);return {...x,P:p,pb:buf(p),nb:buf(n),ub:buf(u),ib:ibuf(I),eb:buf(e),itype:x.it===2?gl.UNSIGNED_SHORT:gl.UNSIGNED_INT}});if(parts.some(x=>x.it===4)&&!gl.drawElementsInstanced&&!gl.getExtension('OES_element_index_uint'))throw Error('Эта модель требует 32-битные индексы, не поддерживаемые браузером');
const V=(a,b,c)=>[a,b,c],sub=(a,b)=>V(a[0]-b[0],a[1]-b[1],a[2]-b[2]),add=(a,b)=>V(a[0]+b[0],a[1]+b[1],a[2]+b[2]),mul=(a,s)=>V(a[0]*s,a[1]*s,a[2]*s),dot=(a,b)=>a[0]*b[0]+a[1]*b[1]+a[2]*b[2],cross=(a,b)=>V(a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]),unit=a=>{const l=Math.hypot(...a)||1;return mul(a,1/l)};
function mm(a,b){const o=new Float32Array(16);for(let r=0;r<4;r++)for(let k=0;k<4;k++)for(let q=0;q<4;q++)o[q*4+r]+=a[k*4+r]*b[q*4+k];return o}function persp(f,a,n,z){const t=1/Math.tan(f/2),o=new Float32Array(16);o[0]=t/a;o[5]=t;o[10]=(z+n)/(n-z);o[11]=-1;o[14]=2*z*n/(n-z);return o}function look(e,t,up){const z=unit(sub(e,t)),x=unit(cross(up,z)),y=cross(z,x),o=new Float32Array(16);o[0]=x[0];o[4]=x[1];o[8]=x[2];o[1]=y[0];o[5]=y[1];o[9]=y[2];o[2]=z[0];o[6]=z[1];o[10]=z[2];o[12]=-dot(x,e);o[13]=-dot(y,e);o[14]=-dot(z,e);o[15]=1;return o}
let mn=V(Infinity,Infinity,Infinity),mx=V(-Infinity,-Infinity,-Infinity);for(const s of parts)for(let i=0;i<s.P.length;i+=3)for(let k=0;k<3;k++){mn[k]=Math.min(mn[k],s.P[i+k]);mx[k]=Math.max(mx[k],s.P[i+k])}let target=mul(add(mn,mx),.5),radius=Math.max(...sub(mx,mn))*.7||1,dist=radius*2.8,yaw=.75,pitch=.45,showEdges=true,transparent=false,selected=0;
function eye(){const cp=Math.cos(pitch);return add(target,V(dist*cp*Math.cos(yaw),dist*cp*Math.sin(yaw),dist*Math.sin(pitch)))}function matrix(){return mm(persp(.72,c.width/c.height,Math.max(.01,dist/1000),dist*20+radius*20),look(eye(),target,V(0,0,1)))}function attr(loc,b,n){gl.bindBuffer(gl.ARRAY_BUFFER,b);gl.enableVertexAttribArray(loc);gl.vertexAttribPointer(loc,n,gl.FLOAT,false,0,0)}
function render(pick){resize();gl.viewport(0,0,c.width,c.height);gl.clearColor(.93,.95,.96,1);gl.clear(gl.COLOR_BUFFER_BIT|gl.DEPTH_BUFFER_BIT);gl.enable(gl.DEPTH_TEST);const M=matrix();gl.useProgram(P);const aP=gl.getAttribLocation(P,'aP'),aN=gl.getAttribLocation(P,'aN'),aU=gl.getAttribLocation(P,'aU');gl.uniformMatrix4fv(gl.getUniformLocation(P,'uM'),false,M);gl.uniform1f(gl.getUniformLocation(P,'uPick'),pick?1:0);for(const s of parts){attr(aP,s.pb,3);attr(aN,s.nb,3);attr(aU,s.ub,2);gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER,s.ib);gl.uniform3fv(gl.getUniformLocation(P,'uC'),s.color);gl.uniform1f(gl.getUniformLocation(P,'uSel'),selected===s.id?1:0);gl.uniform1f(gl.getUniformLocation(P,'uA'),transparent?.32:1);const id=s.id;gl.uniform3f(gl.getUniformLocation(P,'uPickC'),(id&255)/255,((id>>8)&255)/255,((id>>16)&255)/255);const has=s.texture>=0&&!pick;gl.uniform1f(gl.getUniformLocation(P,'uHasT'),has?1:0);if(has){gl.activeTexture(gl.TEXTURE0);gl.bindTexture(gl.TEXTURE_2D,tex[s.texture]);gl.uniform1i(gl.getUniformLocation(P,'uT'),0)}gl.drawElements(gl.TRIANGLES,s.ic,s.itype,0)}if(!pick&&showEdges){gl.disable(gl.BLEND);gl.useProgram(LP);gl.uniformMatrix4fv(gl.getUniformLocation(LP,'uM'),false,M);const ap=gl.getAttribLocation(LP,'aP');for(const s of parts){if(!s.ec)continue;attr(ap,s.eb,3);gl.drawArrays(gl.LINES,0,s.ec)}}}
function draw(){if(transparent){gl.enable(gl.BLEND);gl.blendFunc(gl.SRC_ALPHA,gl.ONE_MINUS_SRC_ALPHA)}else gl.disable(gl.BLEND);render(false)}function resize(){const d=devicePixelRatio||1,w=Math.floor(c.clientWidth*d),h=Math.floor(c.clientHeight*d);if(c.width!==w||c.height!==h){c.width=w;c.height=h}}
function fit(){target=mul(add(mn,mx),.5);radius=Math.max(...sub(mx,mn))*.7||1;dist=radius*2.8;draw()}document.getElementById('fit').onclick=fit;document.getElementById('edges').onclick=e=>{showEdges=!showEdges;e.target.classList.toggle('on',showEdges);draw()};document.getElementById('alpha').onclick=e=>{transparent=!transparent;e.target.classList.toggle('on',transparent);draw()};function clearSel(){selected=0;document.getElementById('sel').textContent='Ничего не выделено';draw()}document.getElementById('clear').onclick=clearSel;addEventListener('keydown',e=>{if(e.key==='Escape')clearSel()});
let down=false,pan=false,lx=0,ly=0,moved=false;c.oncontextmenu=e=>e.preventDefault();c.onpointerdown=e=>{down=true;pan=e.button===2||e.button===1;lx=e.clientX;ly=e.clientY;moved=false;c.setPointerCapture(e.pointerId)};c.onpointermove=e=>{if(!down)return;const dx=e.clientX-lx,dy=e.clientY-ly;lx=e.clientX;ly=e.clientY;if(Math.abs(dx)+Math.abs(dy)>2)moved=true;if(pan){const E=eye(),right=unit(cross(unit(sub(target,E)),V(0,0,1))),up=unit(cross(right,unit(sub(target,E))));const s=dist*.0015;target=add(target,add(mul(right,-dx*s),mul(up,dy*s)))}else{yaw-=dx*.006;pitch=Math.max(-1.48,Math.min(1.48,pitch+dy*.006))}draw()};c.onpointerup=e=>{down=false;if(!moved&&e.button===0)pick(e)};c.onwheel=e=>{e.preventDefault();dist*=Math.exp(e.deltaY*.001);dist=Math.max(radius*.03,Math.min(radius*100,dist));draw()};
function pick(e){render(true);const d=devicePixelRatio||1,x=Math.floor(e.offsetX*d),y=c.height-1-Math.floor(e.offsetY*d),px=new Uint8Array(4);gl.readPixels(x,y,1,1,gl.RGBA,gl.UNSIGNED_BYTE,px);selected=px[0]|(px[1]<<8)|(px[2]<<16);const s=parts.find(q=>q.id===selected);document.getElementById('sel').textContent=s?s.name:'Ничего не выделено';draw()}
document.getElementById('info').textContent=`${D.source} · ${D.triangles.toLocaleString()} треугольников · ${D.parts.length} поверхностей · полностью локально`;addEventListener('resize',draw);fit();
</script></body></html>
""";
}
