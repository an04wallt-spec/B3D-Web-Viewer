using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace B3DPublisherHost;

internal sealed record VrmlPart(
    string Name,
    float[] Positions,
    float[] Normals,
    float[] TexCoords,
    uint[] Indices,
    float[] Color,
    string? TexturePath);

internal sealed record VrmlModel(IReadOnlyList<VrmlPart> Parts, int TriangleCount);

internal static class VrmlParser
{
    public static VrmlModel Parse(string wrlPath)
    {
        var text = File.ReadAllText(wrlPath, DetectEncoding(wrlPath));
        if (!text.Contains("#VRML", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Viewer3D создал файл, который не похож на VRML.");

        var parts = new List<VrmlPart>();
        var shapeSpans = FindNodeBlocks(text, "Shape");
        var shapeNumber = 0;
        foreach (var span in shapeSpans)
        {
            shapeNumber++;
            var shape = text.Substring(span.Start, span.Length);
            var geometry = FindFirstNodeBlock(shape, "IndexedFaceSet");
            if (geometry is null) continue;

            var coordNode = FindFirstNodeBlock(geometry, "Coordinate");
            if (coordNode is null) continue;
            var points = ReadFloatArrayField(coordNode, "point");
            if (points.Count < 9 || points.Count % 3 != 0) continue;

            var coordIndex = ReadIntArrayField(geometry, "coordIndex");
            if (coordIndex.Count == 0) continue;

            var texNode = FindFirstNodeBlock(geometry, "TextureCoordinate");
            var texPoints = texNode is null ? new List<float>() : ReadFloatArrayField(texNode, "point");
            var texIndex = ReadIntArrayField(geometry, "texCoordIndex");

            var normalNode = FindFirstNodeBlock(geometry, "Normal");
            var normalPoints = normalNode is null ? new List<float>() : ReadFloatArrayField(normalNode, "vector");
            var normalIndex = ReadIntArrayField(geometry, "normalIndex");
            var normalPerVertex = ReadBoolField(geometry, "normalPerVertex", true);
            var ccw = ReadBoolField(geometry, "ccw", true);

            var material = FindFirstNodeBlock(shape, "Material");
            var color = material is null ? new[] { 0.72f, 0.70f, 0.66f } : ReadVec3Field(material, "diffuseColor", 0.72f, 0.70f, 0.66f);
            var imageTexture = FindFirstNodeBlock(shape, "ImageTexture");
            var texture = imageTexture is null ? null : ReadUrlField(imageTexture);
            texture = ResolveTexturePath(wrlPath, texture);

            var triangles = TriangulateFaces(coordIndex);
            if (triangles.Count == 0) continue;
            var texFaces = texIndex.Count > 0 ? SplitFaces(texIndex) : new List<int[]>();
            var normalFaces = normalIndex.Count > 0 ? SplitFaces(normalIndex) : new List<int[]>();

            var packedPositions = new List<float>(triangles.Count * 9);
            var packedNormals = new List<float>(triangles.Count * 9);
            var packedTex = new List<float>(triangles.Count * 6);
            var packedIndices = new List<uint>(triangles.Count * 3);
            var vertexMap = new Dictionary<VertexKey, uint>();

            foreach (var tri in triangles)
            {
                var faceIndex = tri.FaceIndex;
                var a = GetPoint(points, tri.A);
                var b = GetPoint(points, tri.B);
                var c = GetPoint(points, tri.C);
                var faceNormal = FaceNormal(a, b, c, ccw);

                foreach (var corner in new[] { (Index: tri.A, Corner: tri.CornerA), (Index: tri.B, Corner: tri.CornerB), (Index: tri.C, Corner: tri.CornerC) })
                {
                    var p = GetPoint(points, corner.Index);
                    var uvIndex = ResolveMappedIndex(texFaces, faceIndex, corner.Corner, corner.Index);
                    var uv = GetVec2(texPoints, uvIndex);

                    float[] n;
                    if (normalPoints.Count >= 3)
                    {
                        int ni;
                        if (normalFaces.Count > faceIndex)
                        {
                            ni = normalPerVertex
                                ? ResolveMappedIndex(normalFaces, faceIndex, corner.Corner, corner.Index)
                                : (normalFaces[faceIndex].Length > 0 ? normalFaces[faceIndex][0] : -1);
                        }
                        else ni = normalPerVertex ? corner.Index : faceIndex;
                        n = ni >= 0 && (ni * 3 + 2) < normalPoints.Count ? GetPoint(normalPoints, ni) : faceNormal;
                    }
                    else n = faceNormal;

                    var key = new VertexKey(p[0], p[1], p[2], n[0], n[1], n[2], uv[0], uv[1]);
                    if (!vertexMap.TryGetValue(key, out var packedIndex))
                    {
                        packedIndex = (uint)(packedPositions.Count / 3);
                        vertexMap.Add(key, packedIndex);
                        packedPositions.AddRange(p);
                        packedNormals.AddRange(n);
                        packedTex.AddRange(uv);
                    }
                    packedIndices.Add(packedIndex);
                }
            }

            if (packedIndices.Count == 0) continue;
            parts.Add(new VrmlPart(
                "Поверхность " + shapeNumber,
                packedPositions.ToArray(),
                packedNormals.ToArray(),
                packedTex.ToArray(),
                packedIndices.ToArray(),
                color,
                texture));
        }

        if (parts.Count == 0)
            throw new InvalidDataException("В официальном VRML Viewer3D не найдено IndexedFaceSet-геометрии.");
        return new VrmlModel(parts, parts.Sum(p => p.Indices.Length / 3));
    }

    private static Encoding DetectEncoding(string path)
    {
        using var fs = File.OpenRead(path);
        var bom = new byte[4];
        var n = fs.Read(bom, 0, bom.Length);
        if (n >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) return Encoding.UTF8;
        if (n >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) return Encoding.Unicode;
        try { return Encoding.GetEncoding(1251); }
        catch { return Encoding.UTF8; }
    }

    private static IReadOnlyList<(int Start, int Length)> FindNodeBlocks(string text, string nodeName)
    {
        var result = new List<(int, int)>();
        var rx = new Regex(@"\b" + Regex.Escape(nodeName) + @"\s*\{", RegexOptions.IgnoreCase);
        foreach (Match m in rx.Matches(text))
        {
            var brace = text.IndexOf('{', m.Index + m.Length - 1);
            if (brace < 0) continue;
            var end = FindMatchingBrace(text, brace);
            if (end > brace) result.Add((m.Index, end - m.Index + 1));
        }
        return result;
    }

    private static string? FindFirstNodeBlock(string text, string nodeName)
    {
        var span = FindNodeBlocks(text, nodeName).FirstOrDefault();
        return span.Length > 0 ? text.Substring(span.Start, span.Length) : null;
    }

    private static int FindMatchingBrace(string text, int open)
    {
        var depth = 0;
        var quoted = false;
        var escaped = false;
        for (var i = open; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (escaped) { escaped = false; continue; }
                if (ch == '\\') { escaped = true; continue; }
                if (ch == '"') quoted = false;
                continue;
            }
            if (ch == '"') { quoted = true; continue; }
            if (ch == '#')
            {
                while (i + 1 < text.Length && text[i + 1] != '\n' && text[i + 1] != '\r') i++;
                continue;
            }
            if (ch == '{') depth++;
            else if (ch == '}' && --depth == 0) return i;
        }
        return -1;
    }

    private static string? ReadBracketField(string block, string field)
    {
        var m = Regex.Match(block, @"\b" + Regex.Escape(field) + @"\s*\[", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var open = block.IndexOf('[', m.Index + m.Length - 1);
        var depth = 0;
        var quoted = false;
        for (var i = open; i < block.Length; i++)
        {
            var ch = block[i];
            if (ch == '"') quoted = !quoted;
            if (quoted) continue;
            if (ch == '[') depth++;
            else if (ch == ']' && --depth == 0) return block.Substring(open + 1, i - open - 1);
        }
        return null;
    }

    private static List<float> ReadFloatArrayField(string block, string field)
    {
        var body = ReadBracketField(block, field);
        if (body is null) return new List<float>();
        return NumberRegex().Matches(body).Select(m => ParseFloat(m.Value)).ToList();
    }

    private static List<int> ReadIntArrayField(string block, string field)
    {
        var body = ReadBracketField(block, field);
        if (body is null) return new List<int>();
        return Regex.Matches(body, @"[-+]?\d+")
            .Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
    }

    private static float[] ReadVec3Field(string block, string field, float x, float y, float z)
    {
        var m = Regex.Match(block, @"\b" + Regex.Escape(field) + @"\s+(?<a>[-+0-9.eE]+)\s+(?<b>[-+0-9.eE]+)\s+(?<c>[-+0-9.eE]+)", RegexOptions.IgnoreCase);
        return m.Success ? new[] { ParseFloat(m.Groups["a"].Value), ParseFloat(m.Groups["b"].Value), ParseFloat(m.Groups["c"].Value) } : new[] { x, y, z };
    }

    private static bool ReadBoolField(string block, string field, bool fallback)
    {
        var m = Regex.Match(block, @"\b" + Regex.Escape(field) + @"\s+(TRUE|FALSE)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Equals("TRUE", StringComparison.OrdinalIgnoreCase) : fallback;
    }

    private static string? ReadUrlField(string block)
    {
        var m = Regex.Match(block, "\\burl\\s+(?:\\[\\s*)?\"(?<u>[^\"]+)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["u"].Value : null;
    }

    private static string? ResolveTexturePath(string wrlPath, string? texture)
    {
        if (string.IsNullOrWhiteSpace(texture)) return null;
        texture = Uri.UnescapeDataString(texture.Replace('/', Path.DirectorySeparatorChar));
        if (Path.IsPathRooted(texture) && File.Exists(texture)) return texture;
        var candidate = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(wrlPath)!, texture));
        return File.Exists(candidate) ? candidate : null;
    }

    private static List<int[]> SplitFaces(List<int> source)
    {
        var faces = new List<int[]>();
        var current = new List<int>();
        foreach (var i in source)
        {
            if (i == -1)
            {
                if (current.Count > 0) faces.Add(current.ToArray());
                current.Clear();
            }
            else current.Add(i);
        }
        if (current.Count > 0) faces.Add(current.ToArray());
        return faces;
    }

    private sealed record TriangleRef(int FaceIndex, int A, int B, int C, int CornerA, int CornerB, int CornerC);

    private static List<TriangleRef> TriangulateFaces(List<int> indices)
    {
        var faces = SplitFaces(indices);
        var tris = new List<TriangleRef>();
        for (var fi = 0; fi < faces.Count; fi++)
        {
            var f = faces[fi];
            if (f.Length < 3) continue;
            for (var i = 1; i + 1 < f.Length; i++)
                tris.Add(new TriangleRef(fi, f[0], f[i], f[i + 1], 0, i, i + 1));
        }
        return tris;
    }

    private static int ResolveMappedIndex(List<int[]> faces, int face, int corner, int fallback)
    {
        if (face < 0 || face >= faces.Count) return fallback;
        var f = faces[face];
        return corner >= 0 && corner < f.Length ? f[corner] : fallback;
    }

    private static float[] GetPoint(IReadOnlyList<float> values, int index)
    {
        var i = index * 3;
        if (index < 0 || i + 2 >= values.Count) return new[] { 0f, 0f, 0f };
        return new[] { values[i], values[i + 1], values[i + 2] };
    }

    private static float[] GetVec2(IReadOnlyList<float> values, int index)
    {
        var i = index * 2;
        if (index < 0 || i + 1 >= values.Count) return new[] { 0f, 0f };
        return new[] { values[i], values[i + 1] };
    }

    private static float[] FaceNormal(float[] a, float[] b, float[] c, bool ccw)
    {
        var ux = b[0] - a[0]; var uy = b[1] - a[1]; var uz = b[2] - a[2];
        var vx = c[0] - a[0]; var vy = c[1] - a[1]; var vz = c[2] - a[2];
        var x = uy * vz - uz * vy;
        var y = uz * vx - ux * vz;
        var z = ux * vy - uy * vx;
        if (!ccw) { x = -x; y = -y; z = -z; }
        var len = MathF.Sqrt(x * x + y * y + z * z);
        return len > 1e-12f ? new[] { x / len, y / len, z / len } : new[] { 0f, 0f, 1f };
    }

    private static float ParseFloat(string value) => float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    private static Regex NumberRegex() => new(@"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.Compiled);

    private readonly record struct VertexKey(float Px, float Py, float Pz, float Nx, float Ny, float Nz, float U, float V);
}
