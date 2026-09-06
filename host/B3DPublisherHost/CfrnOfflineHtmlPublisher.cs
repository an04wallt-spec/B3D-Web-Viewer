using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace B3DPublisherHost;

internal static class CfrnOfflineHtmlPublisher
{
    [ModuleInitializer]
    internal static void Register()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { PublishLatestCapture(); } catch { }
        };
    }

    private static readonly string[] PositionNames =
    {
        "vertices", "vertexes", "positions", "points", "coords", "coordinates"
    };

    private static readonly string[] IndexNames =
    {
        "indices", "indexes", "triangles", "faces"
    };

    private sealed record MeshData(string Name, double[] Positions, int[] Indices, double[] Matrix, int? MaterialIndex, string Source);

    private static void PublishLatestCapture()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!Directory.Exists(desktop)) return;

        var capture = Directory.EnumerateDirectories(desktop, "B3D-Native-Capture_*", SearchOption.TopDirectoryOnly)
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
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
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
                var fileJson = zip.Entries.FirstOrDefault(e => e.FullName.Equals("file.json", StringComparison.OrdinalIgnoreCase));
                if (fileJson is null) continue;

                using var s = fileJson.Open();
                using var doc = JsonDocument.Parse(s);
                var meshes = ExtractExactMeshes(doc.RootElement);

                attempts.Add(new
                {
                    file = path,
                    exactMeshCount = meshes.Count,
                    triangles = meshes.Sum(m => m.Indices.Length / 3),
                    vertices = meshes.Sum(m => m.Positions.Length / 3)
                });

                if (meshes.Count == 0) continue;

                var title = Path.GetFileNameWithoutExtension(path);
                var output = Path.Combine(capture, "Published_Model.html");
                File.WriteAllText(output, BuildHtml(title, meshes), new UTF8Encoding(false));

                File.WriteAllText(
                    Path.Combine(capture, "publisher-result.json"),
                    JsonSerializer.Serialize(new
                    {
                        time = DateTime.Now,
                        source = path,
                        output,
                        mode = "exact pre-triangulated CFRN geometry only",
                        meshCount = meshes.Count,
                        triangles = meshes.Sum(m => m.Indices.Length / 3),
                        vertices = meshes.Sum(m => m.Positions.Length / 3),
                        note = "No B3D reconstruction, panel extrusion, groove reconstruction, OBJ/3DS/DAE conversion, cloud dependency or license bypass is used."
                    }, new JsonSerializerOptions { WriteIndented = true }),
                    Encoding.UTF8);
                return;
            }
            catch (Exception ex)
            {
                attempts.Add(new { file = path, error = ex.Message });
            }
        }

        File.WriteAllText(
            Path.Combine(capture, "publisher-result.json"),
            JsonSerializer.Serialize(new
            {
                time = DateTime.Now,
                output = (string?)null,
                mode = "exact pre-triangulated CFRN geometry only",
                reason = "No exact pre-triangulated CFRN mesh payload was found among captured files. Publisher deliberately did not reconstruct geometry from B3D or panel contours.",
                attempts
            }, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);
    }

    private static bool LooksLikeZip(string path)
    {
        try
        {
            using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> sig = stackalloc byte[4];
            return fs.Read(sig) == 4 && sig[0] == (byte)'P' && sig[1] == (byte)'K';
        }
        catch { return false; }
    }

    private static List<MeshData> ExtractExactMeshes(JsonElement root)
    {
        var result = new List<MeshData>();
        if (!root.TryGetProperty("table", out var table) || table.ValueKind != JsonValueKind.Object) return result;

        var objects = table.TryGetProperty("objects", out var oa) && oa.ValueKind == JsonValueKind.Array
            ? oa.EnumerateArray().Select(x => x.Clone()).ToArray()
            : Array.Empty<JsonElement>();
        var triangleTable = table.TryGetProperty("triangles", out var ta) && ta.ValueKind == JsonValueKind.Array
            ? ta.EnumerateArray().Select(x => x.Clone()).ToArray()
            : Array.Empty<JsonElement>();

        if (!root.TryGetProperty("model", out var model)) return result;
        TraverseModel(model, Identity(), objects, triangleTable, result, "model");
        return result;
    }

    private static void TraverseModel(
        JsonElement node,
        double[] parentMatrix,
        JsonElement[] objects,
        JsonElement[] triangleTable,
        List<MeshData> result,
        string source)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var child in node.EnumerateArray())
                TraverseModel(child, parentMatrix, objects, triangleTable, result, source + "/" + i++);
            return;
        }
        if (node.ValueKind != JsonValueKind.Object) return;

        var local = TryReadMatrix(node, out var lm) ? lm : Identity();
        var world = Multiply(parentMatrix, local);

        if (TryGetInt(node, "tableIndex", out var tableIndex) && tableIndex >= 0 && tableIndex < objects.Length)
        {
            var obj = objects[tableIndex];
            var name = GetString(obj, "name") ?? GetString(obj, "designation") ?? $"Object {tableIndex}";
            var materialIndex = TryGetInt(obj, "materialIndex", out var mi) ? mi : null;

            if (TryExtractMesh(obj, out var inlinePos, out var inlineIdx))
                result.Add(new MeshData(name, inlinePos, inlineIdx, world, materialIndex, source + $"/objects[{tableIndex}]"));
            else
            {
                var triRef = FindTriangleReference(obj, triangleTable.Length);
                if (triRef is not null && TryExtractMesh(triangleTable[triRef.Value], out var pos, out var idx))
                    result.Add(new MeshData(name, pos, idx, world, materialIndex, source + $"/objects[{tableIndex}]->triangles[{triRef.Value}]"));
            }
        }

        foreach (var propName in new[] { "objs", "objects", "children", "items" })
        {
            if (node.TryGetProperty(propName, out var children) && (children.ValueKind == JsonValueKind.Array || children.ValueKind == JsonValueKind.Object))
                TraverseModel(children, world, objects, triangleTable, result, source + "/" + propName);
        }
    }

    private static int? FindTriangleReference(JsonElement obj, int count)
    {
        if (count <= 0 || obj.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in obj.EnumerateObject())
        {
            var n = p.Name;
            if (!(n.Contains("tri", StringComparison.OrdinalIgnoreCase) ||
                  n.Contains("mesh", StringComparison.OrdinalIgnoreCase) ||
                  n.Contains("model", StringComparison.OrdinalIgnoreCase))) continue;
            if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var i) && i >= 0 && i < count)
                return i;
        }
        return null;
    }

    private static bool TryExtractMesh(JsonElement element, out double[] positions, out int[] indices)
    {
        positions = Array.Empty<double>();
        indices = Array.Empty<int>();
        if (element.ValueKind != JsonValueKind.Object && element.ValueKind != JsonValueKind.Array) return false;

        JsonElement? posElement = FindNamedArray(element, PositionNames, 4);
        JsonElement? idxElement = FindNamedArray(element, IndexNames, 4);
        if (posElement is null) return false;

        positions = ReadNumericVector(posElement.Value, 3);
        if (positions.Length < 9 || positions.Length % 3 != 0) return false;

        if (idxElement is not null)
            indices = ReadIntegerVector(idxElement.Value);

        if (indices.Length == 0)
        {
            var vertexCount = positions.Length / 3;
            if (vertexCount % 3 != 0) return false;
            indices = Enumerable.Range(0, vertexCount).ToArray();
        }

        if (indices.Length < 3 || indices.Length % 3 != 0) return false;
        var max = positions.Length / 3;
        if (indices.Any(i => i < 0 || i >= max)) return false;
        return true;
    }

    private static JsonElement? FindNamedArray(JsonElement root, IEnumerable<string> names, int depth)
    {
        if (depth < 0) return null;
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in root.EnumerateObject())
            {
                if (names.Any(n => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) && p.Value.ValueKind == JsonValueKind.Array)
                    return p.Value.Clone();
            }
            foreach (var p in root.EnumerateObject())
            {
                if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    var found = FindNamedArray(p.Value, names, depth - 1);
                    if (found is not null) return found;
                }
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in root.EnumerateArray())
            {
                if (x.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    var found = FindNamedArray(x, names, depth - 1);
                    if (found is not null) return found;
                }
            }
        }
        return null;
    }

    private static double[] ReadNumericVector(JsonElement a, int tupleSize)
    {
        var list = new List<double>();
        FlattenNumbers(a, list);
        if (list.Count % tupleSize != 0) return Array.Empty<double>();
        return list.ToArray();
    }

    private static int[] ReadIntegerVector(JsonElement a)
    {
        var nums = new List<double>();
        FlattenNumbers(a, nums);
        var result = new int[nums.Count];
        for (var i = 0; i < nums.Count; i++)
        {
            var rounded = Math.Round(nums[i]);
            if (Math.Abs(nums[i] - rounded) > 1e-9 || rounded < int.MinValue || rounded > int.MaxValue)
                return Array.Empty<int>();
            result[i] = (int)rounded;
        }
        return result;
    }

    private static void FlattenNumbers(JsonElement e, List<double> list)
    {
        if (e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var d))
        {
            list.Add(d);
            return;
        }
        if (e.ValueKind != JsonValueKind.Array) return;
        foreach (var x in e.EnumerateArray()) FlattenNumbers(x, list);
    }

    private static bool TryReadMatrix(JsonElement obj, out double[] matrix)
    {
        matrix = Identity();
        if (!obj.TryGetProperty("matrix", out var m) || m.ValueKind != JsonValueKind.Array) return false;
        var values = new List<double>();
        FlattenNumbers(m, values);
        if (values.Count != 16) return false;
        matrix = values.ToArray();
        return true;
    }

    private static double[] Identity() => new double[]
    {
        1,0,0,0,
        0,1,0,0,
        0,0,1,0,
        0,0,0,1
    };

    private static double[] Multiply(double[] a, double[] b)
    {
        var r = new double[16];
        for (var row = 0; row < 4; row++)
            for (var col = 0; col < 4; col++)
                for (var k = 0; k < 4; k++)
                    r[row * 4 + col] += a[row * 4 + k] * b[k * 4 + col];
        return r;
    }

    private static bool TryGetInt(JsonElement obj, string name, out int value)
    {
        value = 0;
        return obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out value);
    }

    private static string? GetString(JsonElement obj, string name)
    {
        return obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }

    private static string BuildHtml(string title, List<MeshData> meshes)
    {
        var payload = JsonSerializer.Serialize(meshes.Select(m => new
        {
            name = m.Name,
            positions = m.Positions,
            indices = m.Indices,
            matrix = m.Matrix,
            materialIndex = m.MaterialIndex,
            source = m.Source
        }));
        payload = payload.Replace("</", "<\\/");
        var safeTitle = System.Net.WebUtility.HtmlEncode(title);

        return $$"""
<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>{{safeTitle}} — Local View B3D</title>
<style>
html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#f4f4f1;font-family:Segoe UI,Arial,sans-serif;color:#222}
canvas{width:100%;height:100%;display:block;touch-action:none}.bar{position:fixed;right:18px;top:18px;display:flex;gap:8px;z-index:2}.info{position:fixed;left:18px;bottom:18px;background:#ffffffdd;border:1px solid #ccc;padding:8px 10px;border-radius:8px;font-size:12px}
button{border:1px solid #c8c8c5;background:#fff;padding:8px 12px;border-radius:8px;cursor:pointer}
</style></head><body>
<canvas id="c"></canvas><div class="bar"><button id="home">Перспектива</button><button id="fit">Вписать</button></div><div class="info">{{safeTitle}} · готовая треугольная геометрия CFRN · автономный HTML</div>
<script id="data" type="application/json">{{payload}}</script>
<script>
(()=>{'use strict';
const meshes=JSON.parse(document.getElementById('data').textContent),c=document.getElementById('c'),gl=c.getContext('webgl',{antialias:true});if(!gl){alert('WebGL недоступен');return;}
function sh(t,s){const x=gl.createShader(t);gl.shaderSource(x,s);gl.compileShader(x);if(!gl.getShaderParameter(x,gl.COMPILE_STATUS))throw Error(gl.getShaderInfoLog(x));return x}
const p=gl.createProgram();gl.attachShader(p,sh(gl.VERTEX_SHADER,'attribute vec3 a;uniform mat4 vp;uniform mat4 mm;varying vec3 w;void main(){vec4 q=mm*vec4(a,1.0);w=q.xyz;gl_Position=vp*q;}'));gl.attachShader(p,sh(gl.FRAGMENT_SHADER,'precision mediump float;varying vec3 w;void main(){float k=.82+.18*abs(normalize(w+vec3(.001)).z);gl_FragColor=vec4(vec3(.73,.70,.64)*k,1.0);}'));gl.linkProgram(p);gl.useProgram(p);
const A=gl.getAttribLocation(p,'a'),VP=gl.getUniformLocation(p,'vp'),MM=gl.getUniformLocation(p,'mm');
function mat4(a){return new Float32Array(a)}function mul(a,b){let r=new Float32Array(16);for(let i=0;i<4;i++)for(let j=0;j<4;j++)for(let k=0;k<4;k++)r[i*4+j]+=a[i*4+k]*b[k*4+j];return r}
function persp(f,a,n,fa){let s=1/Math.tan(f/2),r=new Float32Array(16);r[0]=s/a;r[5]=s;r[10]=(fa+n)/(n-fa);r[11]=-1;r[14]=2*fa*n/(n-fa);return r}
function look(e,t){let z=N([e[0]-t[0],e[1]-t[1],e[2]-t[2]]),x=N(C([0,1,0],z)),y=C(z,x),r=new Float32Array([x[0],y[0],z[0],0,x[1],y[1],z[1],0,x[2],y[2],z[2],0,0,0,0,1]);r[12]=-D(x,e);r[13]=-D(y,e);r[14]=-D(z,e);return r}const D=(a,b)=>a[0]*b[0]+a[1]*b[1]+a[2]*b[2],C=(a,b)=>[a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]],N=a=>{let l=Math.hypot(...a)||1;return a.map(v=>v/l)};
const parts=meshes.map((m,i)=>{let b=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,b);gl.bufferData(gl.ARRAY_BUFFER,new Float32Array(m.positions),gl.STATIC_DRAW);let ib=gl.createBuffer();gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER,ib);gl.bufferData(gl.ELEMENT_ARRAY_BUFFER,new Uint32Array(m.indices),gl.STATIC_DRAW);return{...m,b,ib}});const ext=gl.getExtension('OES_element_index_uint');if(!ext&&parts.some(x=>x.indices.some(i=>i>65535)))alert('Эта модель требует OES_element_index_uint.');
let yaw=-.8,pitch=.35,dist=2200,target=[0,0,0],drag=null;
function draw(){let d=Math.min(devicePixelRatio||1,2),w=c.clientWidth*d|0,h=c.clientHeight*d|0;if(c.width!==w||c.height!==h){c.width=w;c.height=h}gl.viewport(0,0,w,h);gl.enable(gl.DEPTH_TEST);gl.clearColor(.957,.957,.945,1);gl.clear(gl.COLOR_BUFFER_BIT|gl.DEPTH_BUFFER_BIT);let cp=Math.cos(pitch),e=[target[0]+dist*cp*Math.cos(yaw),target[1]+dist*Math.sin(pitch),target[2]+dist*cp*Math.sin(yaw)],vp=mul(persp(Math.PI/4,w/h,.1,1e7),look(e,target));gl.uniformMatrix4fv(VP,false,vp);gl.enableVertexAttribArray(A);for(const x of parts){gl.bindBuffer(gl.ARRAY_BUFFER,x.b);gl.vertexAttribPointer(A,3,gl.FLOAT,false,0,0);gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER,x.ib);gl.uniformMatrix4fv(MM,false,mat4(x.matrix));gl.drawElements(gl.TRIANGLES,x.indices.length,gl.UNSIGNED_INT,0)}requestAnimationFrame(draw)}
c.onpointerdown=e=>{drag=[e.clientX,e.clientY];c.setPointerCapture(e.pointerId)};c.onpointermove=e=>{if(!drag)return;let dx=e.clientX-drag[0],dy=e.clientY-drag[1];yaw+=dx*.005;pitch=Math.max(-1.35,Math.min(1.35,pitch+dy*.005));drag=[e.clientX,e.clientY]};c.onpointerup=()=>drag=null;c.onwheel=e=>{e.preventDefault();dist*=Math.exp(e.deltaY*.001);dist=Math.max(1,Math.min(1e7,dist))};
document.getElementById('home').onclick=()=>{yaw=-.8;pitch=.35};document.getElementById('fit').onclick=()=>{dist=2200;target=[0,0,0]};requestAnimationFrame(draw);
})();
</script></body></html>
""";
    }
}
