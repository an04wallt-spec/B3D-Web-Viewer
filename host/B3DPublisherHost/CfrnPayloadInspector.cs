using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace B3DPublisherHost;

internal static class CfrnPayloadInspector
{
    [ModuleInitializer]
    internal static void Register()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { InspectLatestCapture(); } catch { }
        };
    }

    private static void InspectLatestCapture()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!Directory.Exists(desktop)) return;

        var capture = Directory.EnumerateDirectories(desktop, "B3D-Native-Capture_*", SearchOption.TopDirectoryOnly)
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (capture is null) return;

        var payload = Path.Combine(capture, "payload-candidates");
        if (!Directory.Exists(payload)) return;

        var candidates = new List<object>();
        foreach (var path in Directory.EnumerateFiles(payload, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Length < 4 || fi.Length > 1024L * 1024 * 1024) continue;

                using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                Span<byte> sig = stackalloc byte[4];
                if (fs.Read(sig) != 4 || sig[0] != (byte)'P' || sig[1] != (byte)'K') continue;
                fs.Position = 0;

                using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
                var jsonEntry = zip.Entries.FirstOrDefault(e => e.FullName.Equals("file.json", StringComparison.OrdinalIgnoreCase));
                if (jsonEntry is null) continue;

                using var js = jsonEntry.Open();
                using var doc = JsonDocument.Parse(js);
                var root = doc.RootElement;

                var entries = zip.Entries.Select(e => new { e.FullName, e.Length, e.CompressedLength }).ToArray();
                var textures = zip.Entries.Count(e => e.FullName.StartsWith("textures/", StringComparison.OrdinalIgnoreCase) && !e.FullName.EndsWith("/"));
                var models = zip.Entries.Count(e => e.FullName.StartsWith("models/", StringComparison.OrdinalIgnoreCase) && !e.FullName.EndsWith("/"));

                var table = root.TryGetProperty("table", out var t) && t.ValueKind == JsonValueKind.Object ? t : default;
                var objects = CountArray(table, "objects");
                var materials = CountArray(table, "materials");
                var triangles = CountArray(table, "triangles");
                var holes = CountArray(table, "holes");

                var objectTypes = new Dictionary<int, int>();
                var grooveObjects = 0;
                var inlineGeometrySignals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (table.ValueKind == JsonValueKind.Object && table.TryGetProperty("objects", out var objArray) && objArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var obj in objArray.EnumerateArray())
                    {
                        if (obj.ValueKind != JsonValueKind.Object) continue;
                        if (obj.TryGetProperty("objType", out var ot) && ot.TryGetInt32(out var type))
                            objectTypes[type] = objectTypes.TryGetValue(type, out var n) ? n + 1 : 1;
                        if (obj.TryGetProperty("grooves", out var grooves) && grooves.ValueKind == JsonValueKind.Array && grooves.GetArrayLength() > 0)
                            grooveObjects++;
                        foreach (var p in obj.EnumerateObject())
                        {
                            var name = p.Name;
                            if (name.Contains("vert", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("tri", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("mesh", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("normal", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("index", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("contour", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("trajectory", StringComparison.OrdinalIgnoreCase))
                                inlineGeometrySignals.Add(name);
                        }
                    }
                }

                var triShape = DescribeArrayElement(table, "triangles");
                candidates.Add(new
                {
                    file = path,
                    size = fi.Length,
                    format = "CFRN-compatible ZIP (contains file.json)",
                    entryCount = entries.Length,
                    models,
                    textures,
                    objects,
                    materials,
                    triangles,
                    holes,
                    grooveObjects,
                    objectTypes,
                    triangleElementShape = triShape,
                    inlineGeometrySignals = inlineGeometrySignals.OrderBy(x => x).ToArray(),
                    entries
                });
            }
            catch { }
        }

        var reportPath = Path.Combine(capture, "cfrn-payload-report.json");
        var report = new
        {
            time = DateTime.Now,
            capture,
            method = "ZIP/CFRN container inspection only; no B3D geometry reconstruction and no license bypass",
            cfrnCandidateCount = candidates.Count,
            candidates
        };
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
    }

    private static int CountArray(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object) return 0;
        return parent.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.GetArrayLength() : 0;
    }

    private static object? DescribeArrayElement(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array || a.GetArrayLength() == 0)
            return null;
        var first = a[0];
        if (first.ValueKind == JsonValueKind.Object)
            return new { kind = "object", keys = first.EnumerateObject().Select(p => p.Name).ToArray() };
        if (first.ValueKind == JsonValueKind.Array)
            return new { kind = "array", length = first.GetArrayLength() };
        return new { kind = first.ValueKind.ToString(), sample = first.ToString() };
    }
}
