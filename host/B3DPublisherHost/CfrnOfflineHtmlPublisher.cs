using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace B3DPublisherHost;

internal static class CfrnOfflineHtmlPublisher
{
    [ModuleInitializer]
    internal static void Register() => AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        try { PublishLatestCapture(); }
        catch { }
    };

    private static readonly string[] PositionNames = { "vertices", "vertexes", "positions", "points", "coords", "coordinates" };
    private static readonly string[] IndexNames = { "indices", "indexes", "triangles", "faces" };

    private sealed record MaterialData(string Name, double[] Color);
    private sealed record MeshData(
        string Name,
        double[] Positions,
        int[] Indices,
        double[] Matrix,
        int? MaterialIndex,
        string MaterialName,
        double[] Color,
        string Source);

    private sealed record PackedMesh(
        string name,
        string materialName,
        double[] color,
        double[] matrix,
        string p,
        string i,
        int ic,
        bool i32);

    private static void PublishLatestCapture()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!Directory.Exists(desktop)) return;

        var capture = Directory
            .EnumerateDirectories(desktop, "B3D-Native-Capture_*", SearchOption.TopDirectoryOnly)
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
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read, false);
                var fileJson = zip.Entries.FirstOrDefault(e => e.FullName.Equals("file.json", StringComparison.OrdinalIgnoreCase));
                if (fileJson is null) continue;

                using var stream = fileJson.Open();
                using var doc = JsonDocument.Parse(stream);
                var materials = ExtractMaterials(doc.RootElement);
                var meshes = ExtractExactMeshes(doc.RootElement, materials);
                var textureFiles = zip.Entries
                    .Where(e => e.FullName.StartsWith("textures/", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(e.Name))
                    .Select(e => e.FullName)
                    .ToArray();

                attempts.Add(new
                {
                    file = path,
                    exactMeshCount = meshes.Count,
                    triangles = meshes.Sum(m => m.Indices.Length / 3),
                    vertices = meshes.Sum(m => m.Positions.Length / 3),
                    materials = materials.Count,
                    textures = textureFiles.Length
                });

                if (meshes.Count == 0) continue;

                var modelTitle = ReadModelTitle(capture, path);
                var packed = PackMeshes(meshes, out var binaryBytes, out var maxFloat32Error);
                if (packed.Count == 0) continue;

                var output = Path.Combine(capture, "Published_Model.html");
                var html = BuildHtml(modelTitle, packed, meshes.Sum(m => m.Indices.Length / 3));
                ValidateGeneratedHtml(html);
                if (File.Exists(output)) File.SetAttributes(output, FileAttributes.Normal);
                File.WriteAllText(output, html, new UTF8Encoding(false));

                var sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
                File.WriteAllText(Path.Combine(capture, "publisher-result.json"), JsonSerializer.Serialize(new
                {
                    time = DateTime.Now,
                    source = path,
                    sourceSha256 = sourceHash,
                    output,
                    outputBytes = new FileInfo(output).Length,
                    binaryGeometryBytes = binaryBytes,
                    maxFloat32PositionQuantization = maxFloat32Error,
                    mode = "pre-triangulated CFRN geometry only; compact self-contained offline HTML",
                    meshCount = meshes.Count,
                    triangles = meshes.Sum(m => m.Indices.Length / 3),
                    vertices = meshes.Sum(m => m.Positions.Length / 3),
                    materialCount = materials.Count,
                    textureFiles,
                    materialMode = "CFRN material identity is preserved. Stable local colors are used unless an explicit texture binding is present in captured data; texture files are never guessed by filename.",
                    htmlContract = new
                    {
                        singleFile = true,
                        offline = true,
                        externalScripts = false,
                        externalStyles = false,
                        cloudRequests = false,
                        clientInstallRequired = false
                    },
                    note = "No B3D reconstruction, panel extrusion, groove reconstruction, OBJ/3DS/DAE conversion, cloud dependency or license bypass is used."
                }, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
                return;
            }
            catch (Exception ex)
            {
                attempts.Add(new { file = path, error = ex.Message });
            }
        }

        File.WriteAllText(Path.Combine(capture, "publisher-result.json"), JsonSerializer.Serialize(new
        {
            time = DateTime.Now,
            output = (string?)null,
            mode = "pre-triangulated CFRN geometry only",
            reason = "No pre-triangulated CFRN mesh payload was found. Geometry reconstruction was deliberately not attempted.",
            attempts
        }, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
    }

    private static string ReadModelTitle(string capture, string payloadPath)
    {
        try
        {
            var startInfo = Path.Combine(capture, "start-info.json");
            if (File.Exists(startInfo))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(startInfo, Encoding.UTF8));
                if (doc.RootElement.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.String)
                {
                    var value = input.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) return Path.GetFileNameWithoutExtension(value);
                }
            }
        }
        catch { }
        return Path.GetFileNameWithoutExtension(payloadPath);
    }

    private static List<PackedMesh> PackMeshes(List<MeshData> meshes, out long binaryBytes, out double maxFloat32Error)
    {
        var result = new List<PackedMesh>(meshes.Count);
        binaryBytes = 0;
        maxFloat32Error = 0;

        foreach (var m in meshes)
        {
            var positions = new float[m.Positions.Length];
            for (var n = 0; n < positions.Length; n++)
            {
                positions[n] = checked((float)m.Positions[n]);
                maxFloat32Error = Math.Max(maxFloat32Error, Math.Abs(m.Positions[n] - positions[n]));
            }

            var positionBytes = new byte[positions.Length * sizeof(float)];
            Buffer.BlockCopy(positions, 0, positionBytes, 0, positionBytes.Length);

            var maxIndex = m.Indices.Max();
            var index32 = maxIndex > ushort.MaxValue;
            byte[] indexBytes;
            if (index32)
            {
                var indices = m.Indices.Select(x => checked((uint)x)).ToArray();
                indexBytes = new byte[indices.Length * sizeof(uint)];
                Buffer.BlockCopy(indices, 0, indexBytes, 0, indexBytes.Length);
            }
            else
            {
                var indices = m.Indices.Select(x => checked((ushort)x)).ToArray();
                indexBytes = new byte[indices.Length * sizeof(ushort)];
                Buffer.BlockCopy(indices, 0, indexBytes, 0, indexBytes.Length);
            }

            binaryBytes += positionBytes.Length + indexBytes.Length;
            result.Add(new PackedMesh(
                m.Name,
                m.MaterialName,
                m.Color,
                m.Matrix,
                Convert.ToBase64String(positionBytes),
                Convert.ToBase64String(indexBytes),
                m.Indices.Length,
                index32));
        }
        return result;
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

    private static List<MaterialData> ExtractMaterials(JsonElement root)
    {
        var result = new List<MaterialData>();
        if (!root.TryGetProperty("table", out var table) || table.ValueKind != JsonValueKind.Object ||
            !table.TryGetProperty("materials", out var array) || array.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var m in array.EnumerateArray())
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

        var objects = table.TryGetProperty("objects", out var oa) && oa.ValueKind == JsonValueKind.Array
            ? oa.EnumerateArray().Select(x => x.Clone()).ToArray()
            : Array.Empty<JsonElement>();
        var triangles = table.TryGetProperty("triangles", out var ta) && ta.ValueKind == JsonValueKind.Array
            ? ta.EnumerateArray().Select(x => x.Clone()).ToArray()
            : Array.Empty<JsonElement>();

        if (!root.TryGetProperty("model", out var model)) return result;
        TraverseModel(model, Identity(), objects, triangles, materials, result, "model");
        return result;
    }

    private static void TraverseModel(
        JsonElement node,
        double[] parent,
        JsonElement[] objects,
        JsonElement[] triangleTable,
        IReadOnlyList<MaterialData> materials,
        List<MeshData> result,
        string source)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var child in node.EnumerateArray())
                TraverseModel(child, parent, objects, triangleTable, materials, result, source + "/" + i++);
            return;
        }
        if (node.ValueKind != JsonValueKind.Object) return;

        var world = Multiply(parent, TryReadMatrix(node, out var local) ? local : Identity());
        if (TryGetInt(node, "tableIndex", out var ti) && ti >= 0 && ti < objects.Length)
        {
            var obj = objects[ti];
            var name = GetString(obj, "name") ?? GetString(obj, "designation") ?? $"Object {ti}";
            int? materialIndex = TryGetInt(obj, "materialIndex", out var mi) ? mi : null;
            var validMaterial = materialIndex.HasValue && materialIndex.Value >= 0 && materialIndex.Value < materials.Count;
            var materialName = validMaterial ? materials[materialIndex!.Value].Name : "Без материала";
            var color = validMaterial ? materials[materialIndex!.Value].Color : new[] { .72, .70, .66 };

            if (TryExtractMesh(obj, out var p0, out var i0))
            {
                result.Add(new MeshData(name, p0, i0, world, materialIndex, materialName, color, source + $"/objects[{ti}]"));
            }
            else
            {
                var tr = FindTriangleReference(obj, triangleTable.Length);
                if (tr.HasValue && TryExtractMesh(triangleTable[tr.Value], out var p1, out var i1))
                    result.Add(new MeshData(name, p1, i1, world, materialIndex, materialName, color, source + $"/objects[{ti}]->triangles[{tr.Value}]"));
            }
        }

        foreach (var propName in new[] { "objs", "objects", "children", "items" })
            if (node.TryGetProperty(propName, out var children) && children.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                TraverseModel(children, world, objects, triangleTable, materials, result, source + "/" + propName);
    }

    private static int? FindTriangleReference(JsonElement obj, int count)
    {
        if (count <= 0 || obj.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in obj.EnumerateObject())
        {
            if (!(p.Name.Contains("tri", StringComparison.OrdinalIgnoreCase) ||
                  p.Name.Contains("mesh", StringComparison.OrdinalIgnoreCase) ||
                  p.Name.Contains("model", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var i) && i >= 0 && i < count)
                return i;
        }
        return null;
    }

    private static bool TryExtractMesh(JsonElement e, out double[] positions, out int[] indices)
    {
        positions = Array.Empty<double>();
        indices = Array.Empty<int>();
        if (e.ValueKind != JsonValueKind.Object && e.ValueKind != JsonValueKind.Array) return false;

        var pe = FindNamedArray(e, PositionNames, 4);
        var ie = FindNamedArray(e, IndexNames, 4);
        if (pe is null) return false;

        positions = ReadNumericVector(pe.Value, 3);
        if (positions.Length < 9 || positions.Length % 3 != 0) return false;
        if (ie is not null) indices = ReadIntegerVector(ie.Value);
        if (indices.Length == 0)
        {
            var vertexCount = positions.Length / 3;
            if (vertexCount % 3 != 0) return false;
            indices = Enumerable.Range(0, vertexCount).ToArray();
        }
        if (indices.Length < 3 || indices.Length % 3 != 0) return false;

        var count = positions.Length / 3;
        foreach (var index in indices)
            if (index < 0 || index >= count) return false;
        return true;
    }

    private static JsonElement? FindNamedArray(JsonElement root, IEnumerable<string> names, int depth)
    {
        if (depth < 0) return null;
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in root.EnumerateObject())
                if (names.Any(n => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) && p.Value.ValueKind == JsonValueKind.Array)
                    return p.Value.Clone();
            foreach (var p in root.EnumerateObject())
                if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    var found = FindNamedArray(p.Value, names, depth - 1);
                    if (found is not null) return found;
                }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in root.EnumerateArray())
                if (x.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    var found = FindNamedArray(x, names, depth - 1);
                    if (found is not null) return found;
                }
        }
        return null;
    }

    private static double[] ReadNumericVector(JsonElement array, int tupleSize)
    {
        var list = new List<double>();
        FlattenNumbers(array, list);
        return list.Count % tupleSize == 0 ? list.ToArray() : Array.Empty<double>();
    }

    private static int[] ReadIntegerVector(JsonElement array)
    {
        var nums = new List<double>();
        FlattenNumbers(array, nums);
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
        if (e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var value))
        {
            list.Add(value);
            return;
        }
        if (e.ValueKind == JsonValueKind.Array)
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

    private static double[] Identity() => new[] { 1d, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

    private static double[] Multiply(double[] a, double[] b)
    {
        var result = new double[16];
        for (var row = 0; row < 4; row++)
        for (var col = 0; col < 4; col++)
        for (var k = 0; k < 4; k++)
            result[row * 4 + col] += a[row * 4 + k] * b[k * 4 + col];
        return result;
    }

    private static bool TryGetInt(JsonElement o, string name, out int value)
    {
        value = 0;
        return o.ValueKind == JsonValueKind.Object &&
               o.TryGetProperty(name, out var e) &&
               e.ValueKind == JsonValueKind.Number &&
               e.TryGetInt32(out value);
    }

    private static string? GetString(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object &&
        o.TryGetProperty(name, out var e) &&
        e.ValueKind == JsonValueKind.String
            ? e.GetString()
            : null;

    private static void ValidateGeneratedHtml(string html)
    {
        var invalid = !html.Contains("<!doctype html>", StringComparison.OrdinalIgnoreCase) ||
                      !html.Contains("<script id=\"data\"", StringComparison.Ordinal) ||
                      !html.Contains("Local View B3D", StringComparison.Ordinal) ||
                      html.Contains("__TITLE__", StringComparison.Ordinal) ||
                      html.Contains("__PAYLOAD__", StringComparison.Ordinal) ||
                      html.Contains("<script src=", StringComparison.OrdinalIgnoreCase) ||
                      html.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
                      html.Contains("https://", StringComparison.OrdinalIgnoreCase);
        if (invalid) throw new InvalidDataException("Generated offline HTML failed self-check.");
    }

    private static string BuildHtml(string title, List<PackedMesh> meshes, int triangleCount)
    {
        var payload = JsonSerializer.Serialize(meshes).Replace("</", "<\\/");
        var safeTitle = System.Net.WebUtility.HtmlEncode(title);
        var safeStats = $"{meshes.Count} дет. · {triangleCount:N0} треуг.".Replace(',', ' ');

        const string template = """
<!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>__TITLE__ — Local View B3D</title><style>
html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#f3f3f0;font-family:Segoe UI,Arial,sans-serif;color:#222}canvas{width:100%;height:100%;display:block;touch-action:none}.bar{position:fixed;right:16px;top:16px;display:flex;flex-wrap:wrap;justify-content:flex-end;gap:7px;max-width:min(760px,calc(100vw - 32px));z-index:2}.info{position:fixed;left:16px;bottom:16px;background:#ffffffea;border:1px solid #c9c9c5;padding:8px 11px;border-radius:8px;font-size:12px;box-shadow:0 2px 10px #00000012;user-select:none}button{border:1px solid #c8c8c5;background:#fff;color:#222;padding:8px 11px;border-radius:8px;cursor:pointer;font:inherit}button:hover{background:#f7f7f4}button.on{background:#262626;color:#fff;border-color:#262626}@media(max-width:680px){.bar{left:10px;right:10px;top:10px}.info{left:10px;bottom:10px}}
</style></head><body><canvas id="c"></canvas><div class="bar"><button id="home">Перспектива</button><button id="fit">Вписать</button><button id="edges" class="on">Рёбра</button><button id="alpha">Прозрачность</button><button id="clearSel">Снять выделение</button></div><div class="info">__TITLE__ · __STATS__ · Local View B3D</div><script id="data" type="application/json">__PAYLOAD__</script><script>
(()=>{'use strict';const raw=JSON.parse(document.getElementById('data').textContent),c=document.getElementById('c'),gl=c.getContext('webgl2',{antialias:true,preserveDrawingBuffer:true})||c.getContext('webgl',{antialias:true,preserveDrawingBuffer:true});if(!gl){alert('WebGL недоступен');return}const u8=s=>{const b=atob(s),a=new Uint8Array(b.length);for(let j=0;j<b.length;j++)a[j]=b.charCodeAt(j);return a},f32=s=>{const a=u8(s);return new Float32Array(a.buffer,a.byteOffset,a.byteLength/4)},idx=m=>{const a=u8(m.i);return m.i32?new Uint32Array(a.buffer,a.byteOffset,a.byteLength/4):new Uint16Array(a.buffer,a.byteOffset,a.byteLength/2)};function sh(k,s){const q=gl.createShader(k);gl.shaderSource(q,s);gl.compileShader(q);if(!gl.getShaderParameter(q,gl.COMPILE_STATUS))throw Error(gl.getShaderInfoLog(q));return q}const pr=gl.createProgram();gl.attachShader(pr,sh(gl.VERTEX_SHADER,'attribute vec3 a;uniform mat4 vp,mm;void main(){gl_Position=vp*mm*vec4(a,1.0);}'));gl.attachShader(pr,sh(gl.FRAGMENT_SHADER,'precision mediump float;uniform vec4 col;void main(){gl_FragColor=col;}'));gl.linkProgram(pr);if(!gl.getProgramParameter(pr,gl.LINK_STATUS))throw Error(gl.getProgramInfoLog(pr));gl.useProgram(pr);const A=gl.getAttribLocation(pr,'a'),VP=gl.getUniformLocation(pr,'vp'),MM=gl.getUniformLocation(pr,'mm'),COL=gl.getUniformLocation(pr,'col'),u32ok=typeof WebGL2RenderingContext!=='undefined'&&gl instanceof WebGL2RenderingContext||gl.getExtension('OES_element_index_uint');const edgeIndex=a=>{const E=a instanceof Uint32Array?Uint32Array:Uint16Array,r=new E(a.length*2);let q=0;for(let k=0;k<a.length;k+=3){const x=a[k],y=a[k+1],z=a[k+2];r[q++]=x;r[q++]=y;r[q++]=y;r[q++]=z;r[q++]=z;r[q++]=x}return r};const parts=raw.map((m,n)=>{const p=f32(m.p),ii=idx(m);if(m.i32&&!u32ok)throw Error('Для этой модели нужен WebGL2 или OES_element_index_uint');const b=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,b);gl.bufferData(gl.ARRAY_BUFFER,p,gl.STATIC_DRAW);const ib=gl.createBuffer();gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER,ib);gl.bufferData(gl.ELEMENT_ARRAY_BUFFER,ii,gl.STATIC_DRAW);const ei=edgeIndex(ii),eb=gl.createBuffer();gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER,eb);gl.bufferData(gl.ELEMENT_ARRAY_BUFFER,ei,gl.STATIC_DRAW);return{...m,id:n+1,p,ii,b,ib,eb,ec:ei.length,it:m.i32?gl.UNSIGNED_INT:gl.UNSIGNED_SHORT}});const mul=(a,b)=>{const r=new Float32Array(16);for(let i=0;i<4;i++)for(let j=0;j<4;j++)for(let k=0;k<4;k++)r[i*4+j]+=a[i*4+k]*b[k*4+j];return r},persp=(f,a,n,fa)=>{const s=1/Math.tan(f/2),r=new Float32Array(16);r[0]=s/a;r[5]=s;r[10]=(fa+n)/(n-fa);r[11]=-1;r[14]=2*fa*n/(n-fa);return r},dot=(a,b)=>a[0]*b[0]+a[1]*b[1]+a[2]*b[2],cross=(a,b)=>[a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]],norm=a=>{const l=Math.hypot(...a)||1;return a.map(v=>v/l)},look=(e,t)=>{const z=norm([e[0]-t[0],e[1]-t[1],e[2]-t[2]]),x=norm(cross([0,1,0],z)),y=cross(z,x),r=new Float32Array([x[0],y[0],z[0],0,x[1],y[1],z[1],0,x[2],y[2],z[2],0,0,0,0,1]);r[12]=-dot(x,e);r[13]=-dot(y,e);r[14]=-dot(z,e);return r},tr=(m,x,y,z)=>[m[0]*x+m[4]*y+m[8]*z+m[12],m[1]*x+m[5]*y+m[9]*z+m[13],m[2]*x+m[6]*y+m[10]*z+m[14]];let mn=[Infinity,Infinity,Infinity],mx=[-Infinity,-Infinity,-Infinity];for(const m of parts)for(let j=0;j<m.p.length;j+=3){const q=tr(m.matrix,m.p[j],m.p[j+1],m.p[j+2]);for(let k=0;k<3;k++){mn[k]=Math.min(mn[k],q[k]);mx[k]=Math.max(mx[k],q[k])}}if(!isFinite(mn[0])){mn=[-1,-1,-1];mx=[1,1,1]}const ctr=[0,1,2].map(i=>(mn[i]+mx[i])/2),size=Math.max(1,...[0,1,2].map(i=>mx[i]-mn[i]));let yaw=-.8,pitch=.35,dist=size*1.85,target=[...ctr],drag=null,moved=false,edges=true,transparent=false,selected=0,lastVP=null;const fit=()=>{target=[...ctr];dist=size*1.85},home=()=>{yaw=-.8;pitch=.35;fit()},rgbaId=id=>[(id&255)/255,((id>>8)&255)/255,((id>>16)&255)/255,1];function camera(){const w=Math.max(1,c.width),h=Math.max(1,c.height),cp=Math.cos(pitch),eye=[target[0]+dist*cp*Math.cos(yaw),target[1]+dist*Math.sin(pitch),target[2]+dist*cp*Math.sin(yaw)];return mul(persp(Math.PI/4,w/h,Math.max(.01,size/10000),size*60+dist*10),look(eye,target))}function scene(picking=false){const d=Math.min(devicePixelRatio||1,2),w=Math.max(1,c.clientWidth*d|0),h=Math.max(1,c.clientHeight*d|0);if(c.width!==w||c.height!==h){c.width=w;c.height=h}gl.viewport(0,0,w,h);gl.enable(gl.DEPTH_TEST);gl.depthMask(true);gl.disable(gl.BLEND);gl.clearColor(picking?0:.953,picking?0:.953,picking?0:.941,1);gl.clear(gl.COLOR_BUFFER_BIT|gl.DEPTH_BUFFER_BIT);const vp=camera();lastVP=vp;gl.uniformMatrix4fv(VP,false,vp);gl.enableVertexAttribArray(A);if(transparent&&!picking){gl.enable(gl.BLEND);gl.blendFunc(gl.SRC_ALPHA,gl.ONE_MINUS_SRC_ALPHA);gl.depthMask(false)}for(const x of parts){gl.bindBuffer(gl.ARRAY_BUFFER,x.b);gl.vertexAttribPointer(A,3,gl.FLOAT,false,0,0);gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER,x.ib);gl.uniformMatrix4fv(MM,false,new Float32Array(x.matrix));const col=picking?rgbaId(x.id):(selected===x.id?[1,.58,.12,1]:[x.color[0],x.color[1],x.color[2],transparent?.32:1]);gl.uniform4fv(COL,new Float32Array(col));gl.drawElements(gl.TRIANGLES,x.ic,x.it,0)}if(!picking&&edges){gl.disable(gl.BLEND);gl.depthMask(true);gl.uniform4fv(COL,new Float32Array([.08,.08,.08,.78]));for(const x of parts){gl.bindBuffer(gl.ARRAY_BUFFER,x.b);gl.vertexAttribPointer(A,3,gl.FLOAT,false,0,0);gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER,x.eb);gl.uniformMatrix4fv(MM,false,new Float32Array(x.matrix));gl.drawElements(gl.LINES,x.ec,x.it,0)}}}function pick(clientX,clientY){scene(true);const r=c.getBoundingClientRect(),d=Math.min(devicePixelRatio||1,2),x=Math.max(0,Math.min(c.width-1,Math.floor((clientX-r.left)*d))),y=Math.max(0,Math.min(c.height-1,Math.floor((r.bottom-clientY)*d))),px=new Uint8Array(4);gl.readPixels(x,y,1,1,gl.RGBA,gl.UNSIGNED_BYTE,px);selected=px[0]+(px[1]<<8)+(px[2]<<16);if(selected>parts.length)selected=0}function frame(){scene(false);requestAnimationFrame(frame)}c.oncontextmenu=e=>e.preventDefault();c.onpointerdown=e=>{drag=[e.clientX,e.clientY,e.button];moved=false;c.setPointerCapture(e.pointerId)};c.onpointermove=e=>{if(!drag)return;const dx=e.clientX-drag[0],dy=e.clientY-drag[1];if(Math.abs(dx)+Math.abs(dy)>2)moved=true;if(drag[2]===2||drag[2]===1){const s=dist*.00125,targetRight=[-Math.sin(yaw),0,Math.cos(yaw)];target[0]-=dx*s*targetRight[0];target[2]-=dx*s*targetRight[2];target[1]+=dy*s}else{yaw+=dx*.0034;pitch=Math.max(-1.18,Math.min(1.18,pitch+dy*.0034))}drag=[e.clientX,e.clientY,drag[2]]};c.onpointerup=e=>{if(drag&&!moved&&drag[2]===0)pick(e.clientX,e.clientY);drag=null};c.onpointercancel=()=>drag=null;c.onwheel=e=>{e.preventDefault();dist=Math.max(size*.06,Math.min(size*30,dist*Math.exp(e.deltaY*.001)))};const edgeBtn=document.getElementById('edges'),alphaBtn=document.getElementById('alpha');document.getElementById('home').onclick=home;document.getElementById('fit').onclick=fit;edgeBtn.onclick=()=>{edges=!edges;edgeBtn.classList.toggle('on',edges)};alphaBtn.onclick=()=>{transparent=!transparent;alphaBtn.classList.toggle('on',transparent)};document.getElementById('clearSel').onclick=()=>selected=0;document.addEventListener('keydown',e=>{if(e.key==='Escape')selected=0});home();requestAnimationFrame(frame)})();
</script></body></html>
""";

        return template
            .Replace("__TITLE__", safeTitle, StringComparison.Ordinal)
            .Replace("__STATS__", safeStats, StringComparison.Ordinal)
            .Replace("__PAYLOAD__", payload, StringComparison.Ordinal);
    }
}
