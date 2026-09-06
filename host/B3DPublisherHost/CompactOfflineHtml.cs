using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace B3DPublisherHost;

internal static class CompactOfflineHtml
{
    [ModuleInitializer]
    internal static void Register()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                // Ensure the existing publisher has produced its HTML regardless of
                // ProcessExit handler registration order, then compact that same output.
                typeof(CfrnOfflineHtmlPublisher)
                    .GetMethod("PublishLatestCapture", BindingFlags.NonPublic | BindingFlags.Static)?
                    .Invoke(null, null);
                CompactLatest();
            }
            catch { }
        };
    }

    private static void CompactLatest()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var capture = Directory.Exists(desktop)
            ? Directory.EnumerateDirectories(desktop, "B3D-Native-Capture_*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        if (capture is null) return;

        var htmlPath = Path.Combine(capture, "Published_Model.html");
        if (!File.Exists(htmlPath)) return;

        var html = File.ReadAllText(htmlPath, Encoding.UTF8);
        const string open = "<script id=\"data\" type=\"application/json\">";
        const string close = "</script>";
        var p0 = html.IndexOf(open, StringComparison.Ordinal);
        if (p0 < 0) return;
        p0 += open.Length;
        var p1 = html.IndexOf(close, p0, StringComparison.OrdinalIgnoreCase);
        if (p1 <= p0) return;

        using var doc = JsonDocument.Parse(html[p0..p1]);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

        var packed = new List<object>();
        long numericCharsApprox = 0;
        long binaryBytes = 0;
        foreach (var m in doc.RootElement.EnumerateArray())
        {
            var positions = m.GetProperty("positions").EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
            var indices = m.GetProperty("indices").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            if (positions.Length < 9 || indices.Length < 3) continue;

            var pb = new byte[positions.Length * sizeof(float)];
            Buffer.BlockCopy(positions, 0, pb, 0, pb.Length);
            var maxIndex = indices.Max();
            bool index32 = maxIndex > ushort.MaxValue;
            byte[] ib;
            if (index32)
            {
                var ui = indices.Select(x => checked((uint)x)).ToArray();
                ib = new byte[ui.Length * sizeof(uint)];
                Buffer.BlockCopy(ui, 0, ib, 0, ib.Length);
            }
            else
            {
                var ui = indices.Select(x => checked((ushort)x)).ToArray();
                ib = new byte[ui.Length * sizeof(ushort)];
                Buffer.BlockCopy(ui, 0, ib, 0, ib.Length);
            }

            binaryBytes += pb.Length + ib.Length;
            numericCharsApprox += positions.Length * 10L + indices.Length * 5L;
            packed.Add(new
            {
                name = GetString(m, "name"),
                materialName = GetString(m, "materialName"),
                color = m.GetProperty("color").EnumerateArray().Select(x => x.GetDouble()).ToArray(),
                matrix = m.GetProperty("matrix").EnumerateArray().Select(x => x.GetDouble()).ToArray(),
                p = Convert.ToBase64String(pb),
                i = Convert.ToBase64String(ib),
                ic = indices.Length,
                i32 = index32
            });
        }
        if (packed.Count == 0) return;

        var title = Path.GetFileNameWithoutExtension(htmlPath);
        var compact = BuildCompactHtml(title, packed);
        File.SetAttributes(htmlPath, FileAttributes.Normal);
        File.WriteAllText(htmlPath, compact, new UTF8Encoding(false));

        var reportPath = Path.Combine(capture, "publisher-compact-result.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            time = DateTime.Now,
            output = htmlPath,
            mode = "binary Float32/UInt16-or-UInt32 geometry embedded as base64 in one offline HTML",
            meshes = packed.Count,
            htmlBytes = new FileInfo(htmlPath).Length,
            binaryGeometryBytes = binaryBytes,
            estimatedOriginalNumericJsonChars = numericCharsApprox,
            note = "Geometry remains the exact pre-triangulated CFRN mesh; only transport/storage inside HTML is compacted."
        }, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);

        // Prevent the original ProcessExit handler (if it runs after this one) from
        // replacing the compacted output with its verbose JSON representation.
        File.SetAttributes(htmlPath, File.GetAttributes(htmlPath) | FileAttributes.ReadOnly);
    }

    private static string? GetString(JsonElement o, string name) =>
        o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static string BuildCompactHtml(string title, List<object> packed)
    {
        var payload = JsonSerializer.Serialize(packed).Replace("</", "<\\/");
        var safeTitle = System.Net.WebUtility.HtmlEncode(title);
        const string t = """
<!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>__TITLE__ — Local View B3D</title><style>
html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#f4f4f1;font-family:Segoe UI,Arial,sans-serif;color:#222}canvas{width:100%;height:100%;display:block;touch-action:none}.bar{position:fixed;right:18px;top:18px;display:flex;gap:8px;z-index:2}.info{position:fixed;left:18px;bottom:18px;background:#ffffffdf;border:1px solid #ccc;padding:8px 10px;border-radius:8px;font-size:12px}button{border:1px solid #c8c8c5;background:#fff;padding:8px 12px;border-radius:8px;cursor:pointer}</style></head><body>
<canvas id="c"></canvas><div class="bar"><button id="home">Перспектива</button><button id="fit">Вписать</button></div><div class="info">__TITLE__ · Local View B3D · автономный HTML</div><script id="data" type="application/json">__PAYLOAD__</script><script>
(()=>{'use strict';const raw=JSON.parse(document.getElementById('data').textContent),c=document.getElementById('c'),gl=c.getContext('webgl2',{antialias:true})||c.getContext('webgl',{antialias:true});if(!gl){alert('WebGL недоступен');return}const u8=s=>{let b=atob(s),a=new Uint8Array(b.length);for(let j=0;j<b.length;j++)a[j]=b.charCodeAt(j);return a},f32=s=>new Float32Array(u8(s).buffer),idx=m=>m.i32?new Uint32Array(u8(m.i).buffer):new Uint16Array(u8(m.i).buffer);function sh(k,s){let q=gl.createShader(k);gl.shaderSource(q,s);gl.compileShader(q);if(!gl.getShaderParameter(q,gl.COMPILE_STATUS))throw Error(gl.getShaderInfoLog(q));return q}let pr=gl.createProgram();gl.attachShader(pr,sh(gl.VERTEX_SHADER,'attribute vec3 a;uniform mat4 vp,mm;void main(){gl_Position=vp*mm*vec4(a,1.0);}'));gl.attachShader(pr,sh(gl.FRAGMENT_SHADER,'precision mediump float;uniform vec3 col;void main(){gl_FragColor=vec4(col,1.0);}'));gl.linkProgram(pr);gl.useProgram(pr);const A=gl.getAttribLocation(pr,'a'),VP=gl.getUniformLocation(pr,'vp'),MM=gl.getUniformLocation(pr,'mm'),COL=gl.getUniformLocation(pr,'col');const ext=gl instanceof WebGL2RenderingContext?true:gl.getExtension('OES_element_index_uint');let parts=raw.map(m=>{let p=f32(m.p),ii=idx(m);if(m.i32&&!ext)throw Error('Для этой модели нужен WebGL2 или OES_element_index_uint');let b=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,b);gl.bufferData(gl.ARRAY_BUFFER,p,gl.STATIC_DRAW);let ib=gl.createBuffer();gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER,ib);gl.bufferData(gl.ELEMENT_ARRAY_BUFFER,ii,gl.STATIC_DRAW);return{...m,p,b,ib,it:m.i32?gl.UNSIGNED_INT:gl.UNSIGNED_SHORT}});const mul=(a,b)=>{let r=new Float32Array(16);for(let i=0;i<4;i++)for(let j=0;j<4;j++)for(let k=0;k<4;k++)r[i*4+j]+=a[i*4+k]*b[k*4+j];return r},persp=(f,a,n,fa)=>{let s=1/Math.tan(f/2),r=new Float32Array(16);r[0]=s/a;r[5]=s;r[10]=(fa+n)/(n-fa);r[11]=-1;r[14]=2*fa*n/(n-fa);return r},dot=(a,b)=>a[0]*b[0]+a[1]*b[1]+a[2]*b[2],cross=(a,b)=>[a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]],norm=a=>{let l=Math.hypot(...a)||1;return a.map(v=>v/l)},look=(e,t)=>{let z=norm([e[0]-t[0],e[1]-t[1],e[2]-t[2]]),x=norm(cross([0,1,0],z)),y=cross(z,x),r=new Float32Array([x[0],y[0],z[0],0,x[1],y[1],z[1],0,x[2],y[2],z[2],0,0,0,0,1]);r[12]=-dot(x,e);r[13]=-dot(y,e);r[14]=-dot(z,e);return r},tr=(m,x,y,z)=>[m[0]*x+m[4]*y+m[8]*z+m[12],m[1]*x+m[5]*y+m[9]*z+m[13],m[2]*x+m[6]*y+m[10]*z+m[14]];let mn=[Infinity,Infinity,Infinity],mx=[-Infinity,-Infinity,-Infinity];for(const m of parts)for(let j=0;j<m.p.length;j+=3){let q=tr(m.matrix,m.p[j],m.p[j+1],m.p[j+2]);for(let k=0;k<3;k++){mn[k]=Math.min(mn[k],q[k]);mx[k]=Math.max(mx[k],q[k])}}if(!isFinite(mn[0])){mn=[-1,-1,-1];mx=[1,1,1]}let ctr=[0,1,2].map(i=>(mn[i]+mx[i])/2),size=Math.max(1,...[0,1,2].map(i=>mx[i]-mn[i])),yaw=-.8,pitch=.35,dist=size*1.8,target=[...ctr],drag=null;const home=()=>{yaw=-.8;pitch=.35;dist=size*1.8;target=[...ctr]};function draw(){let d=Math.min(devicePixelRatio||1,2),w=Math.max(1,c.clientWidth*d|0),h=Math.max(1,c.clientHeight*d|0);if(c.width!==w||c.height!==h){c.width=w;c.height=h}gl.viewport(0,0,w,h);gl.enable(gl.DEPTH_TEST);gl.clearColor(.957,.957,.945,1);gl.clear(gl.COLOR_BUFFER_BIT|gl.DEPTH_BUFFER_BIT);let cp=Math.cos(pitch),e=[target[0]+dist*cp*Math.cos(yaw),target[1]+dist*Math.sin(pitch),target[2]+dist*cp*Math.sin(yaw)],vp=mul(persp(Math.PI/4,w/h,Math.max(.01,size/10000),size*50+dist*10),look(e,target));gl.uniformMatrix4fv(VP,false,vp);gl.enableVertexAttribArray(A);for(const x of parts){gl.bindBuffer(gl.ARRAY_BUFFER,x.b);gl.vertexAttribPointer(A,3,gl.FLOAT,false,0,0);gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER,x.ib);gl.uniformMatrix4fv(MM,false,new Float32Array(x.matrix));gl.uniform3fv(COL,new Float32Array(x.color));gl.drawElements(gl.TRIANGLES,x.ic,x.it,0)}requestAnimationFrame(draw)}c.onpointerdown=e=>{drag=[e.clientX,e.clientY];c.setPointerCapture(e.pointerId)};c.onpointermove=e=>{if(!drag)return;let dx=e.clientX-drag[0],dy=e.clientY-drag[1];yaw+=dx*.0042;pitch=Math.max(-1.15,Math.min(1.15,pitch+dy*.0042));drag=[e.clientX,e.clientY]};c.onpointerup=()=>drag=null;c.onwheel=e=>{e.preventDefault();dist=Math.max(size*.08,Math.min(size*25,dist*Math.exp(e.deltaY*.001)))};document.getElementById('home').onclick=home;document.getElementById('fit').onclick=home;home();requestAnimationFrame(draw)})();
</script></body></html>
""";
        return t.Replace("__TITLE__", safeTitle, StringComparison.Ordinal).Replace("__PAYLOAD__", payload, StringComparison.Ordinal);
    }
}
