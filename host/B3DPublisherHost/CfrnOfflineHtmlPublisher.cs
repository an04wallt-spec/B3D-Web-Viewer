using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace B3DPublisherHost;

internal static class CfrnOfflineHtmlPublisher
{
    [ModuleInitializer]
    internal static void Register()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { PublishLatestCapture(); } catch { } };
    }

    private static readonly string[] PositionNames = { "vertices", "vertexes", "positions", "points", "coords", "coordinates" };
    private static readonly string[] IndexNames = { "indices", "indexes", "triangles", "faces" };

    private sealed record MaterialData(string Name, double[] Color);
    private sealed record MeshData(string Name, double[] Positions, int[] Indices, double[] Matrix, int? MaterialIndex, string MaterialName, double[] Color, string Source);

    private static void PublishLatestCapture()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!Directory.Exists(desktop)) return;
        var capture = Directory.EnumerateDirectories(desktop, "B3D-Native-Capture_*", SearchOption.TopDirectoryOnly)
            .OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault();
        if (capture is null) return;
        var payloadDir = Path.Combine(capture, "payload-candidates");
        if (!Directory.Exists(payloadDir)) return;

        var attempts = new List<object>();
        foreach (var path in Directory.EnumerateFiles(payloadDir, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (!LooksLikeZip(path)) continue;
                using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read, false);
                var fileJson = zip.Entries.FirstOrDefault(e => e.FullName.Equals("file.json", StringComparison.OrdinalIgnoreCase));
                if (fileJson is null) continue;
                using var s = fileJson.Open();
                using var doc = JsonDocument.Parse(s);
                var materials = ExtractMaterials(doc.RootElement);
                var meshes = ExtractExactMeshes(doc.RootElement, materials);
                var textureFiles = zip.Entries.Where(e => e.FullName.StartsWith("textures/", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(e.Name)).Select(e => e.FullName).ToArray();
                attempts.Add(new { file = path, exactMeshCount = meshes.Count, triangles = meshes.Sum(m => m.Indices.Length / 3), vertices = meshes.Sum(m => m.Positions.Length / 3), materials = materials.Count, textures = textureFiles.Length });
                if (meshes.Count == 0) continue;

                var output = Path.Combine(capture, "Published_Model.html");
                var html = BuildHtml(Path.GetFileNameWithoutExtension(path), meshes);
                ValidateGeneratedHtml(html);
                File.WriteAllText(output, html, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(capture, "publisher-result.json"), JsonSerializer.Serialize(new
                {
                    time = DateTime.Now,
                    source = path,
                    output,
                    mode = "exact pre-triangulated CFRN geometry only",
                    meshCount = meshes.Count,
                    triangles = meshes.Sum(m => m.Indices.Length / 3),
                    vertices = meshes.Sum(m => m.Positions.Length / 3),
                    materialCount = materials.Count,
                    textureFiles,
                    materialMode = "CFRN material identity preserved; stable local colors are used when no documented texture binding is present.",
                    note = "No B3D reconstruction, panel extrusion, groove reconstruction, OBJ/3DS/DAE conversion, cloud dependency or license bypass is used."
                }, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
                return;
            }
            catch (Exception ex) { attempts.Add(new { file = path, error = ex.Message }); }
        }

        File.WriteAllText(Path.Combine(capture, "publisher-result.json"), JsonSerializer.Serialize(new
        {
            time = DateTime.Now,
            output = (string?)null,
            mode = "exact pre-triangulated CFRN geometry only",
            reason = "No exact pre-triangulated CFRN mesh payload was found among captured files. Publisher deliberately did not reconstruct geometry from B3D or panel contours.",
            attempts
        }, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
    }

    private static void ValidateGeneratedHtml(string html)
    {
        if (!html.Contains("<!doctype html>", StringComparison.OrdinalIgnoreCase) || !html.Contains("<script id=\"data\"", StringComparison.Ordinal) || !html.Contains("Local View B3D", StringComparison.Ordinal) || html.Contains("__TITLE__", StringComparison.Ordinal) || html.Contains("__PAYLOAD__", StringComparison.Ordinal))
            throw new InvalidDataException("Generated offline HTML failed self-check.");
    }

    private static bool LooksLikeZip(string path)
    {
        try { using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); Span<byte> sig = stackalloc byte[4]; return fs.Read(sig) == 4 && sig[0] == (byte)'P' && sig[1] == (byte)'K'; }
        catch { return false; }
    }

    private static List<MaterialData> ExtractMaterials(JsonElement root)
    {
        var result = new List<MaterialData>();
        if (!root.TryGetProperty("table", out var table) || table.ValueKind != JsonValueKind.Object || !table.TryGetProperty("materials", out var a) || a.ValueKind != JsonValueKind.Array) return result;
        foreach (var m in a.EnumerateArray())
        {
            var name = GetString(m, "name") ?? GetString(m, "art") ?? GetString(m, "sign") ?? $"Material {result.Count}";
            result.Add(new MaterialData(name, StableColor(name)));
        }
        return result;
    }

    private static double[] StableColor(string key)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var c = new[] { .45 + h[0] / 255.0 * .35, .45 + h[1] / 255.0 * .35, .45 + h[2] / 255.0 * .35 };
        var g = c.Average();
        for (var i = 0; i < 3; i++) c[i] = Math.Clamp(c[i] * .70 + g * .15 + .12, 0, 1);
        return c;
    }

    private static List<MeshData> ExtractExactMeshes(JsonElement root, IReadOnlyList<MaterialData> materials)
    {
        var result = new List<MeshData>();
        if (!root.TryGetProperty("table", out var table) || table.ValueKind != JsonValueKind.Object) return result;
        var objects = table.TryGetProperty("objects", out var oa) && oa.ValueKind == JsonValueKind.Array ? oa.EnumerateArray().Select(x => x.Clone()).ToArray() : Array.Empty<JsonElement>();
        var triangleTable = table.TryGetProperty("triangles", out var ta) && ta.ValueKind == JsonValueKind.Array ? ta.EnumerateArray().Select(x => x.Clone()).ToArray() : Array.Empty<JsonElement>();
        if (!root.TryGetProperty("model", out var model)) return result;
        TraverseModel(model, Identity(), objects, triangleTable, materials, result, "model");
        return result;
    }

    private static void TraverseModel(JsonElement node, double[] parent, JsonElement[] objects, JsonElement[] triangleTable, IReadOnlyList<MaterialData> materials, List<MeshData> result, string source)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            var i = 0; foreach (var child in node.EnumerateArray()) TraverseModel(child, parent, objects, triangleTable, materials, result, source + "/" + i++); return;
        }
        if (node.ValueKind != JsonValueKind.Object) return;
        var world = Multiply(parent, TryReadMatrix(node, out var lm) ? lm : Identity());

        if (TryGetInt(node, "tableIndex", out var ti) && ti >= 0 && ti < objects.Length)
        {
            var obj = objects[ti];
            var name = GetString(obj, "name") ?? GetString(obj, "designation") ?? $"Object {ti}";
            var materialIndex = TryGetInt(obj, "materialIndex", out var mi) ? mi : null;
            var validMaterial = materialIndex is >= 0 && materialIndex < materials.Count;
            var materialName = validMaterial ? materials[materialIndex!.Value].Name : "Без материала";
            var color = validMaterial ? materials[materialIndex!.Value].Color : new[] { .72, .70, .66 };
            if (TryExtractMesh(obj, out var pos0, out var idx0)) result.Add(new MeshData(name, pos0, idx0, world, materialIndex, materialName, color, source + $"/objects[{ti}]"));
            else
            {
                var tr = FindTriangleReference(obj, triangleTable.Length);
                if (tr is not null && TryExtractMesh(triangleTable[tr.Value], out var pos, out var idx)) result.Add(new MeshData(name, pos, idx, world, materialIndex, materialName, color, source + $"/objects[{ti}]->triangles[{tr.Value}]"));
            }
        }
        foreach (var propName in new[] { "objs", "objects", "children", "items" }) if (node.TryGetProperty(propName, out var children) && children.ValueKind is JsonValueKind.Array or JsonValueKind.Object) TraverseModel(children, world, objects, triangleTable, materials, result, source + "/" + propName);
    }

    private static int? FindTriangleReference(JsonElement obj, int count)
    {
        if (count <= 0 || obj.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in obj.EnumerateObject())
        {
            var n = p.Name;
            if (!(n.Contains("tri", StringComparison.OrdinalIgnoreCase) || n.Contains("mesh", StringComparison.OrdinalIgnoreCase) || n.Contains("model", StringComparison.OrdinalIgnoreCase))) continue;
            if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var i) && i >= 0 && i < count) return i;
        }
        return null;
    }

    private static bool TryExtractMesh(JsonElement e, out double[] positions, out int[] indices)
    {
        positions = Array.Empty<double>(); indices = Array.Empty<int>();
        if (e.ValueKind != JsonValueKind.Object && e.ValueKind != JsonValueKind.Array) return false;
        var pe = FindNamedArray(e, PositionNames, 4); var ie = FindNamedArray(e, IndexNames, 4); if (pe is null) return false;
        positions = ReadNumericVector(pe.Value, 3); if (positions.Length < 9 || positions.Length % 3 != 0) return false;
        if (ie is not null) indices = ReadIntegerVector(ie.Value);
        if (indices.Length == 0) { var vc = positions.Length / 3; if (vc % 3 != 0) return false; indices = Enumerable.Range(0, vc).ToArray(); }
        return indices.Length >= 3 && indices.Length % 3 == 0 && !indices.Any(i => i < 0 || i >= positions.Length / 3);
    }

    private static JsonElement? FindNamedArray(JsonElement root, IEnumerable<string> names, int depth)
    {
        if (depth < 0) return null;
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in root.EnumerateObject()) if (names.Any(n => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) && p.Value.ValueKind == JsonValueKind.Array) return p.Value.Clone();
            foreach (var p in root.EnumerateObject()) if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) { var f = FindNamedArray(p.Value, names, depth - 1); if (f is not null) return f; }
        }
        else if (root.ValueKind == JsonValueKind.Array) foreach (var x in root.EnumerateArray()) if (x.ValueKind is JsonValueKind.Object or JsonValueKind.Array) { var f = FindNamedArray(x, names, depth - 1); if (f is not null) return f; }
        return null;
    }

    private static double[] ReadNumericVector(JsonElement a, int tupleSize) { var list = new List<double>(); FlattenNumbers(a, list); return list.Count % tupleSize == 0 ? list.ToArray() : Array.Empty<double>(); }
    private static int[] ReadIntegerVector(JsonElement a)
    {
        var nums = new List<double>(); FlattenNumbers(a, nums); var r = new int[nums.Count];
        for (var i = 0; i < nums.Count; i++) { var v = Math.Round(nums[i]); if (Math.Abs(nums[i] - v) > 1e-9 || v < int.MinValue || v > int.MaxValue) return Array.Empty<int>(); r[i] = (int)v; }
        return r;
    }
    private static void FlattenNumbers(JsonElement e, List<double> list) { if (e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var d)) { list.Add(d); return; } if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray()) FlattenNumbers(x, list); }
    private static bool TryReadMatrix(JsonElement obj, out double[] matrix) { matrix = Identity(); if (!obj.TryGetProperty("matrix", out var m) || m.ValueKind != JsonValueKind.Array) return false; var v = new List<double>(); FlattenNumbers(m, v); if (v.Count != 16) return false; matrix = v.ToArray(); return true; }
    private static double[] Identity() => new[] { 1d,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1 };
    private static double[] Multiply(double[] a, double[] b) { var r = new double[16]; for (var row = 0; row < 4; row++) for (var col = 0; col < 4; col++) for (var k = 0; k < 4; k++) r[row * 4 + col] += a[row * 4 + k] * b[k * 4 + col]; return r; }
    private static bool TryGetInt(JsonElement o, string n, out int v) { v = 0; return o.ValueKind == JsonValueKind.Object && o.TryGetProperty(n, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out v); }
    private static string? GetString(JsonElement o, string n) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(n, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static string BuildHtml(string title, List<MeshData> meshes)
    {
        var payload = JsonSerializer.Serialize(meshes.Select(m => new { name = m.Name, positions = m.Positions, indices = m.Indices, matrix = m.Matrix, materialIndex = m.MaterialIndex, materialName = m.MaterialName, color = m.Color, source = m.Source })).Replace("</", "<\\/");
        var safeTitle = System.Net.WebUtility.HtmlEncode(title);
        const string template = """
<!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>__TITLE__ — Local View B3D</title><style>
html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#f4f4f1;font-family:Segoe UI,Arial,sans-serif;color:#222}canvas{width:100%;height:100%;display:block;touch-action:none}.bar{position:fixed;right:18px;top:18px;display:flex;gap:8px;z-index:2}.info{position:fixed;left:18px;bottom:18px;background:#ffffffdd;border:1px solid #ccc;padding:8px 10px;border-radius:8px;font-size:12px}button{border:1px solid #c8c8c5;background:#fff;padding:8px 12px;border-radius:8px;cursor:pointer}</style></head><body>
<canvas id="c"></canvas><div class="bar"><button id="home">Перспектива</button><button id="fit">Вписать</button></div><div class="info">__TITLE__ · готовая треугольная геометрия CFRN · автономный HTML</div><script id="data" type="application/json">__PAYLOAD__</script><script>
(()=>{'use strict';const meshes=JSON.parse(document.getElementById('data').textContent),c=document.getElementById('c'),gl=c.getContext('webgl',{antialias:true});if(!gl){alert('WebGL недоступен');return;}function sh(t,s){const x=gl.createShader(t);gl.shaderSource(x,s);gl.compileShader(x);if(!gl.getShaderParameter(x,gl.COMPILE_STATUS))throw Error(gl.getShaderInfoLog(x));return x}const p=gl.createProgram();gl.attachShader(p,sh(gl.VERTEX_SHADER,'attribute vec3 a;uniform mat4 vp;uniform mat4 mm;void main(){gl_Position=vp*mm*vec4(a,1.0);}'));gl.attachShader(p,sh(gl.FRAGMENT_SHADER,'precision mediump float;uniform vec3 col;void main(){gl_FragColor=vec4(col,1.0);}'));gl.linkProgram(p);if(!gl.getProgramParameter(p,gl.LINK_STATUS))throw Error(gl.getProgramInfoLog(p));gl.useProgram(p);const A=gl.getAttribLocation(p,'a'),VP=gl.getUniformLocation(p,'vp'),MM=gl.getUniformLocation(p,'mm'),COL=gl.getUniformLocation(p,'col');function mat4(a){return new Float32Array(a)}function mul(a,b){let r=new Float32Array(16);for(let i=0;i<4;i++)for(let j=0;j<4;j++)for(let k=0;k<4;k++)r[i*4+j]+=a[i*4+k]*b[k*4+j];return r}function persp(f,a,n,fa){let s=1/Math.tan(f/2),r=new Float32Array(16);r[0]=s/a;r[5]=s;r[10]=(fa+n)/(n-fa);r[11]=-1;r[14]=2*fa*n/(n-fa);return r}const D=(a,b)=>a[0]*b[0]+a[1]*b[1]+a[2]*b[2],C=(a,b)=>[a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]],N=a=>{let l=Math.hypot(...a)||1;return a.map(v=>v/l)};function look(e,t){let z=N([e[0]-t[0],e[1]-t[1],e[2]-t[2]]),x=N(C([0,1,0],z)),y=C(z,x),r=new Float32Array([x[0],y[0],z[0],0,x[1],y[1],z[1],0,x[2],y[2],z[2],0,0,0,0,1]);r[12]=-D(x,e);r[13]=-D(y,e);r[14]=-D(z,e);return r}function tr(m,x,y,z){return[m[0]*x+m[4]*y+m[8]*z+m[12],m[1]*x+m[5]*y+m[9]*z+m[13],m[2]*x+m[6]*y+m[10]*z+m[14]]}const ext=gl.getExtension('OES_element_index_uint'),parts=meshes.map(m=>{let b=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,b);gl.bufferData(gl.ARRAY_BUFFER,new Float32Array(m.positions),gl.STATIC_DRAW);let mx=m.indices.reduce((a,v)=>Math.max(a,v),0),u32=mx>65535;if(u32&&!ext)throw Error('Модель требует OES_element_index_uint');let ib=gl.createBuffer();gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER,ib);gl.bufferData(gl.ELEMENT_ARRAY_BUFFER,u32?new Uint32Array(m.indices):new Uint16Array(m.indices),gl.STATIC_DRAW);return{...m,b,ib,it:u32?gl.UNSIGNED_INT:gl.UNSIGNED_SHORT}});let mn=[Infinity,Infinity,Infinity],mx=[-Infinity,-Infinity,-Infinity];for(const m of meshes)for(let i=0;i<m.positions.length;i+=3){const q=tr(m.matrix,m.positions[i],m.positions[i+1],m.positions[i+2]);for(let k=0;k<3;k++){if(q[k]<mn[k])mn[k]=q[k];if(q[k]>mx[k])mx[k]=q[k]}}if(!isFinite(mn[0])){mn=[-1,-1,-1];mx=[1,1,1]}const ctr=[0,1,2].map(i=>(mn[i]+mx[i])/2),size=Math.max(1,...[0,1,2].map(i=>mx[i]-mn[i]));let yaw=-.8,pitch=.35,dist=size*1.8,target=[...ctr],drag=null;function home(){yaw=-.8;pitch=.35;dist=size*1.8;target=[...ctr]}function draw(){let d=Math.min(devicePixelRatio||1,2),w=Math.max(1,c.clientWidth*d|0),h=Math.max(1,c.clientHeight*d|0);if(c.width!==w||c.height!==h){c.width=w;c.height=h}gl.viewport(0,0,w,h);gl.enable(gl.DEPTH_TEST);gl.clearColor(.957,.957,.945,1);gl.clear(gl.COLOR_BUFFER_BIT|gl.DEPTH_BUFFER_BIT);let cp=Math.cos(pitch),e=[target[0]+dist*cp*Math.cos(yaw),target[1]+dist*Math.sin(pitch),target[2]+dist*cp*Math.sin(yaw)],vp=mul(persp(Math.PI/4,w/h,Math.max(.01,size/10000),size*50+dist*10),look(e,target));gl.uniformMatrix4fv(VP,false,vp);gl.enableVertexAttribArray(A);for(const x of parts){gl.bindBuffer(gl.ARRAY_BUFFER,x.b);gl.vertexAttribPointer(A,3,gl.FLOAT,false,0,0);gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER,x.ib);gl.uniformMatrix4fv(MM,false,mat4(x.matrix));gl.uniform3fv(COL,new Float32Array(x.color));gl.drawElements(gl.TRIANGLES,x.indices.length,x.it,0)}requestAnimationFrame(draw)}c.onpointerdown=e=>{drag=[e.clientX,e.clientY];c.setPointerCapture(e.pointerId)};c.onpointermove=e=>{if(!drag)return;let dx=e.clientX-drag[0],dy=e.clientY-drag[1];yaw+=dx*.0042;pitch=Math.max(-1.15,Math.min(1.15,pitch+dy*.0042));drag=[e.clientX,e.clientY]};c.onpointerup=()=>drag=null;c.onwheel=e=>{e.preventDefault();dist*=Math.exp(e.deltaY*.001);dist=Math.max(size*.08,Math.min(size*25,dist))};document.getElementById('home').onclick=home;document.getElementById('fit').onclick=home;home();requestAnimationFrame(draw)})();
</script></body></html>
""";
        return template.Replace("__TITLE__", safeTitle, StringComparison.Ordinal).Replace("__PAYLOAD__", payload, StringComparison.Ordinal);
    }
}
