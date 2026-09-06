using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace B3DPublisherHost;

internal static class B3DHandlerProbe
{
    private enum AssocStr
    {
        Command = 1,
        Executable = 2,
        FriendlyDocName = 3,
        FriendlyAppName = 4,
        NoOpen = 5,
        ShellNewValue = 6,
        DdeCommand = 7,
        DdeIfExec = 8,
        DdeApplication = 9,
        DdeTopic = 10,
        InfoTip = 11,
        QuickTip = 12,
        TileInfo = 13,
        ContentType = 14,
        DefaultIcon = 15,
        ShellExtension = 16,
        DropTarget = 17,
        DelegateExecute = 18,
        SupportedUriProtocols = 19,
        ProgId = 20,
        AppId = 21,
        AppPublisher = 22,
        AppIconReference = 23,
        Max = 24
    }

    [DllImport("Shlwapi.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern uint AssocQueryString(
        uint flags,
        AssocStr str,
        string pszAssoc,
        string? pszExtra,
        StringBuilder? pszOut,
        ref uint pcchOut);

    public static string BuildReport()
    {
        var lines = new List<string>
        {
            "B3D Publisher — Windows .b3d handler probe",
            $"Windows: {Environment.OSVersion}",
            $"64-bit process: {Environment.Is64BitProcess}",
            $"Machine: {Environment.MachineName}",
            "",
            "Effective association (Windows AssocQueryString):"
        };

        AddAssoc(lines, "ProgID", AssocStr.ProgId);
        AddAssoc(lines, "Executable", AssocStr.Executable);
        AddAssoc(lines, "Command", AssocStr.Command);
        AddAssoc(lines, "Friendly app", AssocStr.FriendlyAppName);
        AddAssoc(lines, "Friendly document", AssocStr.FriendlyDocName);
        AddAssoc(lines, "Content type", AssocStr.ContentType);
        AddAssoc(lines, "DelegateExecute", AssocStr.DelegateExecute);
        AddAssoc(lines, "Shell extension", AssocStr.ShellExtension);

        lines.Add("");
        lines.Add("HKCU Explorer FileExts\\.b3d:");
        DumpRegistryTree(lines, Registry.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.b3d", 0, 3);

        lines.Add("");
        lines.Add("Merged HKCR\\.b3d:");
        DumpRegistryTree(lines, Registry.ClassesRoot, @".b3d", 0, 4);

        var progId = QueryAssoc(AssocStr.ProgId);
        if (!string.IsNullOrWhiteSpace(progId))
        {
            lines.Add("");
            lines.Add($"Merged HKCR\\{progId}:");
            DumpRegistryTree(lines, Registry.ClassesRoot, progId, 0, 5);
        }

        var clsids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectClsids(Registry.ClassesRoot, @".b3d", clsids);
        if (!string.IsNullOrWhiteSpace(progId)) CollectClsids(Registry.ClassesRoot, progId, clsids);

        if (clsids.Count > 0)
        {
            lines.Add("");
            lines.Add("Referenced COM classes:");
            foreach (var clsid in clsids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                lines.Add($"CLSID {clsid}");
                DumpRegistryTree(lines, Registry.ClassesRoot, $@"CLSID\{clsid}", 1, 4);
            }
        }

        lines.Add("");
        lines.Add("Interpretation rule:");
        lines.Add("- If Executable/Command points to Microsoft 3D Viewer, the file is reaching that app directly.");
        lines.Add("- If .b3d or its ProgID references a COM CLSID/DLL, that registered handler is the likely decoder/preview bridge.");
        lines.Add("- If the executable is a BAZIS component, Windows is only dispatching the file to BAZIS, not parsing B3D itself.");
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

    private static void AddAssoc(List<string> lines, string label, AssocStr value)
        => lines.Add($"  {label}: {QueryAssoc(value) ?? "<none>"}");

    private static string? QueryAssoc(AssocStr value)
    {
        uint length = 0;
        _ = AssocQueryString(0, value, ".b3d", null, null, ref length);
        if (length == 0 || length > 32768) return null;
        var sb = new StringBuilder((int)length);
        var result = AssocQueryString(0, value, ".b3d", null, sb, ref length);
        return result == 0 ? sb.ToString() : null;
    }

    private static void DumpRegistryTree(
        List<string> lines,
        RegistryKey root,
        string path,
        int indent,
        int remainingDepth)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            if (key is null)
            {
                lines.Add(new string(' ', indent * 2) + "<not present>");
                return;
            }
            DumpOpenedKey(lines, key, indent, remainingDepth);
        }
        catch (Exception ex)
        {
            lines.Add(new string(' ', indent * 2) + $"<registry read failed: {ex.Message}>");
        }
    }

    private static void DumpOpenedKey(List<string> lines, RegistryKey key, int indent, int remainingDepth)
    {
        var prefix = new string(' ', indent * 2);
        foreach (var valueName in key.GetValueNames())
        {
            object? value;
            try { value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames); }
            catch { continue; }
            var name = string.IsNullOrEmpty(valueName) ? "(Default)" : valueName;
            lines.Add($"{prefix}{name} = {FormatValue(value)}");
        }

        if (remainingDepth <= 0) return;
        foreach (var subName in key.GetSubKeyNames())
        {
            lines.Add($"{prefix}[{subName}]");
            try
            {
                using var sub = key.OpenSubKey(subName);
                if (sub is not null) DumpOpenedKey(lines, sub, indent + 1, remainingDepth - 1);
            }
            catch { }
        }
    }

    private static string FormatValue(object? value)
        => value switch
        {
            null => "<null>",
            string s => s,
            string[] a => string.Join("; ", a),
            byte[] b => Convert.ToHexString(b),
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""
        };

    private static void CollectClsids(RegistryKey root, string path, HashSet<string> output)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            if (key is null) return;
            CollectClsids(key, output, 0);
        }
        catch { }
    }

    private static void CollectClsids(RegistryKey key, HashSet<string> output, int depth)
    {
        if (depth > 6) return;
        foreach (var valueName in key.GetValueNames())
        {
            var text = key.GetValue(valueName)?.ToString();
            if (Guid.TryParse(text, out var guid)) output.Add(guid.ToString("B"));
        }
        foreach (var subName in key.GetSubKeyNames())
        {
            if (Guid.TryParse(subName, out var guid)) output.Add(guid.ToString("B"));
            try
            {
                using var sub = key.OpenSubKey(subName);
                if (sub is not null) CollectClsids(sub, output, depth + 1);
            }
            catch { }
        }
    }
}
