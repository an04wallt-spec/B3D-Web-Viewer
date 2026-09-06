using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace B3DPublisherHost;

internal static class B3DHandlerProbe
{
    private const string ThumbnailHandler = "{E357FCCD-A995-4576-B01F-234630154E96}";
    private const string PreviewHandler = "{8895B1C6-B41F-4C1C-A562-0D564250836F}";
    private const string PropertySheetHandler = "PropertySheetHandlers";

    private enum AssocStr
    {
        Command = 1, Executable = 2, FriendlyDocName = 3, FriendlyAppName = 4,
        NoOpen = 5, ShellNewValue = 6, DdeCommand = 7, DdeIfExec = 8,
        DdeApplication = 9, DdeTopic = 10, InfoTip = 11, QuickTip = 12,
        TileInfo = 13, ContentType = 14, DefaultIcon = 15, ShellExtension = 16,
        DropTarget = 17, DelegateExecute = 18, SupportedUriProtocols = 19,
        ProgId = 20, AppId = 21, AppPublisher = 22, AppIconReference = 23, Max = 24
    }

    [DllImport("Shlwapi.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern uint AssocQueryString(uint flags, AssocStr str, string pszAssoc,
        string? pszExtra, StringBuilder? pszOut, ref uint pcchOut);

    public static string BuildReport()
    {
        var lines = new List<string>
        {
            "B3D Publisher — Windows .b3d handler probe",
            $"Windows: {Environment.OSVersion}",
            $"Process: {(Environment.Is64BitProcess ? "64" : "32")}-bit",
            "",
            "Effective association (AssocQueryString):"
        };

        AddAssoc(lines, "ProgID", AssocStr.ProgId);
        AddAssoc(lines, "Executable", AssocStr.Executable);
        AddAssoc(lines, "Command", AssocStr.Command);
        AddAssoc(lines, "Friendly app", AssocStr.FriendlyAppName);
        AddAssoc(lines, "Content type", AssocStr.ContentType);
        AddAssoc(lines, "DelegateExecute", AssocStr.DelegateExecute);
        AddAssoc(lines, "Shell extension", AssocStr.ShellExtension);

        var progId = QueryAssoc(AssocStr.ProgId);

        lines.Add("");
        lines.Add("Per-user association:");
        DumpRegistryTree(lines, Registry.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.b3d", 1, 4);

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            lines.Add("");
            lines.Add($"HKCR ({(view == RegistryView.Registry64 ? "64" : "32")}-bit view):");
            using var classes = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
            DumpRegistryTree(lines, classes, @".b3d", 1, 5);
            if (!string.IsNullOrWhiteSpace(progId))
            {
                lines.Add($"  ProgID {progId}:");
                DumpRegistryTree(lines, classes, progId, 2, 5);
            }

            ProbeKnownShellHandlers(lines, classes, ".b3d", progId, view);
        }

        lines.Add("");
        lines.Add("COM resolution:");
        var clsids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectLikelyClsids(Registry.ClassesRoot, @".b3d", clsids);
        if (!string.IsNullOrWhiteSpace(progId)) CollectLikelyClsids(Registry.ClassesRoot, progId, clsids);
        foreach (var clsid in clsids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add($"  CLSID {clsid}");
            DumpComClass(lines, clsid);
        }
        if (clsids.Count == 0) lines.Add("  <no CLSIDs referenced by .b3d/ProgID>");

        lines.Add("");
        lines.Add("What matters:");
        lines.Add("- Thumbnail handler = Explorer miniature provider only; it is not proof of a reusable 3D mesh decoder.");
        lines.Add("- Preview handler = Explorer preview-pane COM object; inspect its InprocServer32/LocalServer32.");
        lines.Add("- Open command/DelegateExecute = application dispatch path used when the file is opened.");
        lines.Add("- A BAZIS DLL/EXE in any of these paths is the concrete component to inspect next.");
        lines.Add("- This probe is read-only: it does not register, patch, inject into, or bypass any BAZIS component.");
        return string.Join(Environment.NewLine, lines);
    }

    public static string SaveReport(string report)
    {
        var dir = Path.Combine(Path.GetTempPath(), "B3DPublisher");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "B3D-handler-probe.txt");
        File.WriteAllText(path, report, new UTF8Encoding(false));
        return path;
    }

    private static void ProbeKnownShellHandlers(List<string> lines, RegistryKey classes, string extension,
        string? progId, RegistryView view)
    {
        lines.Add($"  Known shell roles ({(view == RegistryView.Registry64 ? "64" : "32")}-bit):");
        foreach (var (label, guid) in new[] { ("Thumbnail", ThumbnailHandler), ("Preview", PreviewHandler) })
        {
            var candidates = new List<string>
            {
                $@"{extension}\ShellEx\{guid}",
                $@"SystemFileAssociations\{extension}\ShellEx\{guid}"
            };
            if (!string.IsNullOrWhiteSpace(progId)) candidates.Add($@"{progId}\ShellEx\{guid}");
            foreach (var path in candidates)
            {
                var value = ReadDefault(classes, path);
                if (string.IsNullOrWhiteSpace(value)) continue;
                lines.Add($"    {label}: {path} -> {value}");
                if (Guid.TryParse(value, out var g)) DumpComClass(lines, g.ToString("B"), 3, view);
            }
        }

        foreach (var basePath in new[] { extension, progId }.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var path = $@"{basePath}\ShellEx\{PropertySheetHandler}";
            using var key = classes.OpenSubKey(path);
            if (key is null) continue;
            foreach (var sub in key.GetSubKeyNames()) lines.Add($"    PropertySheet: {path}\\{sub}");
        }
    }

    private static string? ReadDefault(RegistryKey root, string path)
    {
        try { using var key = root.OpenSubKey(path); return key?.GetValue(null)?.ToString(); }
        catch { return null; }
    }

    private static void DumpComClass(List<string> lines, string clsid, int indent = 1, RegistryView? view = null)
    {
        try
        {
            using var classes = view.HasValue
                ? RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view.Value)
                : Registry.ClassesRoot;
            var p = $@"CLSID\{clsid}";
            using var key = classes.OpenSubKey(p);
            if (key is null) { lines.Add(new string(' ', indent * 2) + "<CLSID not registered in this view>"); return; }
            var name = key.GetValue(null)?.ToString();
            if (!string.IsNullOrWhiteSpace(name)) lines.Add(new string(' ', indent * 2) + $"Name = {name}");
            foreach (var server in new[] { "InprocServer32", "LocalServer32" })
            {
                using var sk = key.OpenSubKey(server);
                var value = sk?.GetValue(null, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
                if (!string.IsNullOrWhiteSpace(value)) lines.Add(new string(' ', indent * 2) + $"{server} = {value}");
            }
            using var appId = key.OpenSubKey("");
            var aid = key.GetValue("AppID")?.ToString();
            if (!string.IsNullOrWhiteSpace(aid)) lines.Add(new string(' ', indent * 2) + $"AppID = {aid}");
        }
        catch (Exception ex) { lines.Add(new string(' ', indent * 2) + $"<COM read failed: {ex.Message}>"); }
    }

    private static void AddAssoc(List<string> lines, string label, AssocStr value)
        => lines.Add($"  {label}: {QueryAssoc(value) ?? "<none>"}");

    private static string? QueryAssoc(AssocStr value)
    {
        uint length = 0;
        _ = AssocQueryString(0, value, ".b3d", null, null, ref length);
        if (length == 0 || length > 32768) return null;
        var sb = new StringBuilder((int)length);
        return AssocQueryString(0, value, ".b3d", null, sb, ref length) == 0 ? sb.ToString() : null;
    }

    private static void DumpRegistryTree(List<string> lines, RegistryKey root, string path, int indent, int depth)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            if (key is null) { lines.Add(new string(' ', indent * 2) + "<not present>"); return; }
            DumpOpenedKey(lines, key, indent, depth);
        }
        catch (Exception ex) { lines.Add(new string(' ', indent * 2) + $"<registry read failed: {ex.Message}>"); }
    }

    private static void DumpOpenedKey(List<string> lines, RegistryKey key, int indent, int depth)
    {
        var prefix = new string(' ', indent * 2);
        foreach (var valueName in key.GetValueNames())
        {
            object? value; try { value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames); } catch { continue; }
            lines.Add($"{prefix}{(string.IsNullOrEmpty(valueName) ? "(Default)" : valueName)} = {FormatValue(value)}");
        }
        if (depth <= 0) return;
        foreach (var subName in key.GetSubKeyNames())
        {
            lines.Add($"{prefix}[{subName}]");
            try { using var sub = key.OpenSubKey(subName); if (sub is not null) DumpOpenedKey(lines, sub, indent + 1, depth - 1); } catch { }
        }
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "<null>", string s => s, string[] a => string.Join("; ", a),
        byte[] b => Convert.ToHexString(b), _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""
    };

    private static void CollectLikelyClsids(RegistryKey root, string path, HashSet<string> output)
    {
        try { using var key = root.OpenSubKey(path); if (key is not null) CollectLikelyClsids(key, output, 0); } catch { }
    }

    private static void CollectLikelyClsids(RegistryKey key, HashSet<string> output, int depth)
    {
        if (depth > 7) return;
        foreach (var valueName in key.GetValueNames())
        {
            var text = key.GetValue(valueName)?.ToString();
            if (Guid.TryParse(text, out var guid)) output.Add(guid.ToString("B"));
        }
        foreach (var subName in key.GetSubKeyNames())
        {
            if (Guid.TryParse(subName, out var guid)) output.Add(guid.ToString("B"));
            try { using var sub = key.OpenSubKey(subName); if (sub is not null) CollectLikelyClsids(sub, output, depth + 1); } catch { }
        }
    }
}
