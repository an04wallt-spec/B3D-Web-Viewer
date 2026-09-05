using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace BazisWebViewerBridgeProbe;

internal static class Program
{
    private static readonly string[] Needles =
    {
        "WebViewer.dll", "InitializeWebViewerDLL", "FinalizeWebViewerDLL", "UploadLibrary", "UploadModelFromStream",
        "GetStreamFromLibEvent", "FGetStreamFromLibEvent", "TGetStreamFromLibEvent", "TMemoryStream", "TStream",
        "CFRN", ".cfrn", "file.json", "models-desktop", "api-viewer", "viewer.bazissoft.ru", "cloud.bazissoft.ru",
        "WebViewerConnector", "WebViewerConverter", "WebViewerAPI"
    };

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            var bazis = FindBazis();
            var root = bazis?.MainModule?.FileName is string exe
                ? Path.GetDirectoryName(exe)
                : @"C:\Program Files (x86)\BazisSoft\Bazis";

            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                MessageBox.Show("Не найдена папка БАЗИС. Запустите БАЗИС-24 и повторите.", "BAZIS WebViewer Bridge Probe",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var files = Directory.EnumerateFiles(root, "*.*", SearchOption.TopDirectoryOnly)
                .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            var hits = new List<object>();
            foreach (var file in files)
            {
                byte[] bytes;
                try { bytes = File.ReadAllBytes(file); }
                catch { continue; }

                var ascii = Encoding.ASCII.GetString(bytes);
                var unicode = Encoding.Unicode.GetString(bytes);
                var found = Needles.Where(n => ascii.Contains(n, StringComparison.OrdinalIgnoreCase) || unicode.Contains(n, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (found.Length == 0) continue;

                string sha;
                using (var h = SHA256.Create()) sha = Convert.ToHexString(h.ComputeHash(bytes)).ToLowerInvariant();
                var vi = FileVersionInfo.GetVersionInfo(file);
                hits.Add(new
                {
                    file = Path.GetFileName(file),
                    path = file,
                    size = bytes.LongLength,
                    version = vi.FileVersion,
                    product = vi.ProductName,
                    sha256 = sha,
                    hits = found
                });
            }

            object? processInfo = null;
            if (bazis is not null)
            {
                var modules = new List<object>();
                try
                {
                    foreach (ProcessModule m in bazis.Modules)
                    {
                        var name = Path.GetFileName(m.FileName);
                        if (name.Contains("viewer", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("cloud", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("kernel", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("model", StringComparison.OrdinalIgnoreCase))
                            modules.Add(new { name, path = m.FileName });
                    }
                }
                catch { }
                processInfo = new { bazis.Id, bazis.ProcessName, bazis.MainWindowTitle, modules };
            }

            var report = new
            {
                time = DateTime.Now,
                installRoot = root,
                process = processInfo,
                scannedFiles = files.Length,
                matches = hits
            };

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var outPath = Path.Combine(desktop, "BAZIS-WebViewer-Bridge-Probe.json");
            File.WriteAllText(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);

            MessageBox.Show($"Готово. Найдено файлов с признаками WebViewer/CFRN: {hits.Count}.\n\nОтчёт:\n{outPath}",
                "BAZIS WebViewer Bridge Probe", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "BAZIS WebViewer Bridge Probe", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static Process? FindBazis()
    {
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.ProcessName.Contains("bazis", StringComparison.OrdinalIgnoreCase) ||
                    (p.MainWindowTitle ?? "").Contains("БАЗИС", StringComparison.OrdinalIgnoreCase)) return p;
            }
            catch { }
        }
        return null;
    }
}
