using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Windows.Forms;

namespace BazisApiImportProbe;

internal static class Program
{
    private static readonly string[] ApiNeedles =
    {
        "wglCreateContext","wglCreateContextAttribsARB","wglMakeCurrent","SwapBuffers",
        "D3D11CreateDevice","D3D11CreateDeviceAndSwapChain","CreateDXGIFactory","CreateDXGIFactory1","CreateDXGIFactory2",
        "Direct3DCreate9","Direct3DCreate9Ex","vkCreateInstance"
    };

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            var process = FindBazisProcess();
            if (process is null)
            {
                MessageBox.Show("БАЗИС не найден. Открой модель в БАЗИС-24 и запусти Probe ещё раз.", "BAZIS API Import Probe");
                return;
            }

            var modules = new List<string>();
            foreach (ProcessModule m in process.Modules)
            {
                try { if (File.Exists(m.FileName)) modules.Add(m.FileName); } catch { }
            }

            var hits = new List<object>();
            foreach (var path in modules.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var h in ScanFile(path)) hits.Add(h);
            }

            var grouped = hits
                .GroupBy(x => ((dynamic)x).api as string ?? "")
                .ToDictionary(g => g.Key, g => g.Count());

            var report = new
            {
                time = DateTime.Now,
                process = new { process.Id, process.ProcessName, process.MainWindowTitle },
                scannedModules = modules.Count,
                apiSummary = grouped,
                hits
            };

            var outPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "BAZIS-API-Import-Probe.json");
            File.WriteAllText(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            MessageBox.Show($"Готово. Найдено совпадений: {hits.Count}\n\nОтчёт:\n{outPath}", "BAZIS API Import Probe");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "BAZIS API Import Probe", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static IEnumerable<object> ScanFile(string path)
    {
        var result = new List<object>();
        try
        {
            using var fs = File.OpenRead(path);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata && pe.PEHeaders.PEHeader is null) return result;

            fs.Position = 0;
            using var br = new BinaryReader(fs);
            var bytes = br.ReadBytes((int)Math.Min(fs.Length, 128L * 1024 * 1024));
            var text = System.Text.Encoding.ASCII.GetString(bytes);

            foreach (var api in ApiNeedles)
            {
                if (text.Contains(api, StringComparison.Ordinal))
                    result.Add(new { module = path, api });
            }
        }
        catch { }
        return result;
    }

    private static Process? FindBazisProcess()
    {
        var all = Process.GetProcesses();
        return all.FirstOrDefault(p => Safe(() =>
            (p.MainWindowTitle ?? "").Contains("БАЗИС", StringComparison.OrdinalIgnoreCase) ||
            (p.MainWindowTitle ?? "").Contains("BAZIS", StringComparison.OrdinalIgnoreCase), false))
            ?? all.FirstOrDefault(p => Safe(() => p.ProcessName.Contains("bazis", StringComparison.OrdinalIgnoreCase), false));
    }

    private static T Safe<T>(Func<T> f, T fallback) { try { return f(); } catch { return fallback; } }
}
