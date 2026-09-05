using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace B3DPublisherHost;

internal static class NativePeImportScanner
{
    private sealed record ImportEntry(string dll, List<string> functions);
    private sealed record ModuleImports(string path, long size, string? version, List<ImportEntry> imports, List<string> bridgeFunctions);

    private static readonly string[] BridgeNames =
    {
        "UploadModelFromStream",
        "InitializeWebViewerDLL",
        "FinalizeWebViewerDLL",
        "UploadLibrary",
        "GetStreamFromLibEvent"
    };

    [ModuleInitializer]
    internal static void RunAtPublisherStartup()
    {
        try
        {
            var root = FindBazisRoot();
            if (root is null) return;

            var modules = new List<ModuleImports>();
            foreach (var path in Directory.EnumerateFiles(root, "*.*", SearchOption.TopDirectoryOnly)
                         .Where(IsPeCandidate))
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Length <= 0 || fi.Length > 512L * 1024 * 1024) continue;
                    var imports = ReadImports(path);
                    if (imports.Count == 0) continue;

                    var bridge = imports
                        .Where(x => x.dll.Equals("WebViewer.dll", StringComparison.OrdinalIgnoreCase))
                        .SelectMany(x => x.functions)
                        .Where(f => BridgeNames.Any(b => f.Contains(b, StringComparison.OrdinalIgnoreCase)))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    var referencesWebViewer = imports.Any(x => x.dll.Equals("WebViewer.dll", StringComparison.OrdinalIgnoreCase));
                    var referencesKernel = imports.Any(x => x.dll.Equals("LibKernel3D.dll", StringComparison.OrdinalIgnoreCase));
                    if (!referencesWebViewer && !referencesKernel && bridge.Count == 0) continue;

                    modules.Add(new ModuleImports(
                        path,
                        fi.Length,
                        FileVersionInfo.GetVersionInfo(path).FileVersion,
                        imports.Where(x =>
                                x.dll.Equals("WebViewer.dll", StringComparison.OrdinalIgnoreCase) ||
                                x.dll.Equals("LibKernel3D.dll", StringComparison.OrdinalIgnoreCase))
                            .ToList(),
                        bridge));
                }
                catch { }
            }

            var directWebViewerCallers = modules
                .Where(m => m.imports.Any(i => i.dll.Equals("WebViewer.dll", StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(m => m.bridgeFunctions.Count)
                .ThenBy(m => m.path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var reportPath = Path.Combine(desktop, "BAZIS-Publisher-PE-Imports.json");
            var report = new
            {
                time = DateTime.Now,
                bazisRoot = root,
                method = "PE import-table inspection only; no process modification, no license logic and no B3D geometry parsing",
                directWebViewerCallerCount = directWebViewerCallers.Count,
                directWebViewerCallers,
                kernelAndViewerRelatedModules = modules
            };
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        }
        catch { }
    }

    private static bool IsPeCandidate(string p) =>
        p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
        p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
        p.EndsWith(".bpl", StringComparison.OrdinalIgnoreCase);

    private static string? FindBazisRoot()
    {
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!(process.ProcessName.Contains("bazis", StringComparison.OrdinalIgnoreCase) ||
                      (process.MainWindowTitle ?? "").Contains("БАЗИС", StringComparison.OrdinalIgnoreCase))) continue;
                var main = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(main)) return Path.GetDirectoryName(main);
            }
            catch { }
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "BazisSoft", "Bazis"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BazisSoft", "Bazis")
        };
        return candidates.FirstOrDefault(Directory.Exists);
    }

    private sealed record Section(uint va, uint virtualSize, uint rawPtr, uint rawSize);

    private static List<ImportEntry> ReadImports(string path)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length < 0x100 || ReadU16(b, 0) != 0x5A4D) return new();
        var pe = checked((int)ReadU32(b, 0x3C));
        if (pe < 0 || pe + 0x100 > b.Length || ReadU32(b, pe) != 0x00004550) return new();

        var coff = pe + 4;
        var sectionCount = ReadU16(b, coff + 2);
        var optionalSize = ReadU16(b, coff + 16);
        var opt = coff + 20;
        if (opt + optionalSize > b.Length) return new();
        var magic = ReadU16(b, opt);
        var pe32Plus = magic == 0x20B;
        if (!pe32Plus && magic != 0x10B) return new();

        var dataDir = opt + (pe32Plus ? 112 : 96);
        if (dataDir + 16 > opt + optionalSize) return new();
        var importRva = ReadU32(b, dataDir + 8);
        if (importRva == 0) return new();

        var sections = new List<Section>();
        var sh = opt + optionalSize;
        for (var i = 0; i < sectionCount; i++)
        {
            var o = sh + i * 40;
            if (o + 40 > b.Length) break;
            sections.Add(new Section(
                ReadU32(b, o + 12),
                ReadU32(b, o + 8),
                ReadU32(b, o + 20),
                ReadU32(b, o + 16)));
        }

        int Rva(uint rva)
        {
            foreach (var s in sections)
            {
                var span = Math.Max(s.virtualSize, s.rawSize);
                if (rva >= s.va && rva < s.va + span)
                {
                    var off = (long)s.rawPtr + (rva - s.va);
                    return off >= 0 && off < b.Length ? (int)off : -1;
                }
            }
            return rva < b.Length ? (int)rva : -1;
        }

        var result = new List<ImportEntry>();
        var desc = Rva(importRva);
        if (desc < 0) return result;

        for (var di = 0; di < 4096; di++, desc += 20)
        {
            if (desc < 0 || desc + 20 > b.Length) break;
            var oft = ReadU32(b, desc);
            var nameRva = ReadU32(b, desc + 12);
            var ft = ReadU32(b, desc + 16);
            if (oft == 0 && nameRva == 0 && ft == 0) break;

            var nameOff = Rva(nameRva);
            var dll = ReadAsciiZ(b, nameOff, 260);
            if (string.IsNullOrWhiteSpace(dll)) continue;

            var funcs = new List<string>();
            var thunkRva = oft != 0 ? oft : ft;
            var thunk = Rva(thunkRva);
            if (thunk >= 0)
            {
                var step = pe32Plus ? 8 : 4;
                for (var ti = 0; ti < 65536; ti++, thunk += step)
                {
                    if (thunk < 0 || thunk + step > b.Length) break;
                    ulong val = pe32Plus ? ReadU64(b, thunk) : ReadU32(b, thunk);
                    if (val == 0) break;
                    var ordinalMask = pe32Plus ? 0x8000000000000000UL : 0x80000000UL;
                    if ((val & ordinalMask) != 0)
                    {
                        funcs.Add("#" + (val & 0xFFFF));
                        continue;
                    }
                    var hn = Rva((uint)val);
                    if (hn < 0 || hn + 2 >= b.Length) continue;
                    var fn = ReadAsciiZ(b, hn + 2, 512);
                    if (!string.IsNullOrWhiteSpace(fn)) funcs.Add(fn);
                }
            }

            result.Add(new ImportEntry(dll, funcs.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        }
        return result;
    }

    private static ushort ReadU16(byte[] b, int o) =>
        o >= 0 && o + 2 <= b.Length ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o, 2)) : (ushort)0;

    private static uint ReadU32(byte[] b, int o) =>
        o >= 0 && o + 4 <= b.Length ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4)) : 0;

    private static ulong ReadU64(byte[] b, int o) =>
        o >= 0 && o + 8 <= b.Length ? BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o, 8)) : 0;

    private static string ReadAsciiZ(byte[] b, int o, int max)
    {
        if (o < 0 || o >= b.Length) return string.Empty;
        var end = o;
        var limit = Math.Min(b.Length, o + max);
        while (end < limit && b[end] != 0) end++;
        return end > o ? Encoding.ASCII.GetString(b, o, end - o) : string.Empty;
    }
}
