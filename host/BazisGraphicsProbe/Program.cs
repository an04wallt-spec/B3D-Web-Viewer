using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace BazisGraphicsProbe;

internal static class Program
{
    private const uint TH32CS_SNAPMODULE = 0x00000008;
    private const uint TH32CS_SNAPMODULE32 = 0x00000010;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            var process = FindBazisProcess();
            if (process is null)
            {
                MessageBox.Show(
                    "Не найден запущенный БАЗИС.\n\nОткройте модель в БАЗИС-24 и запустите Probe ещё раз.",
                    "BAZIS Graphics Probe",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var modules = EnumerateModules(process.Id);
            var graphics = DetectGraphics(modules);

            var report = new
            {
                time = DateTime.Now,
                process = new { process.Id, process.ProcessName, process.MainWindowTitle },
                graphics,
                graphicsRelatedModules = modules
                    .Where(IsGraphicsRelated)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                moduleCount = modules.Count
            };

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var reportPath = Path.Combine(desktop, "BAZIS-Graphics-Probe.json");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);

            var summary = graphics.Count == 0
                ? "Графический API однозначно не определён. Отчёт всё равно сохранён — пришлите его мне."
                : "Обнаружено: " + string.Join(", ", graphics);

            MessageBox.Show(
                summary + "\n\nОтчёт:\n" + reportPath,
                "BAZIS Graphics Probe",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "BAZIS Graphics Probe", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static Process? FindBazisProcess()
    {
        var all = Process.GetProcesses();

        var byWindow = all.FirstOrDefault(p => Safe(() =>
            (p.MainWindowTitle ?? "").Contains("БАЗИС", StringComparison.OrdinalIgnoreCase) ||
            (p.MainWindowTitle ?? "").Contains("BAZIS", StringComparison.OrdinalIgnoreCase), false));
        if (byWindow is not null) return byWindow;

        return all.FirstOrDefault(p => Safe(() =>
            p.ProcessName.Contains("bazis", StringComparison.OrdinalIgnoreCase) ||
            p.ProcessName.Contains("bmeb", StringComparison.OrdinalIgnoreCase), false));
    }

    private static List<string> EnumerateModules(int pid)
    {
        var result = new List<string>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, (uint)pid);
        if (snapshot == INVALID_HANDLE_VALUE)
            throw new InvalidOperationException("Windows не позволил получить список модулей процесса БАЗИС.");

        try
        {
            var entry = new MODULEENTRY32 { dwSize = (uint)Marshal.SizeOf<MODULEENTRY32>() };
            if (!Module32First(snapshot, ref entry))
                throw new InvalidOperationException("Не удалось прочитать модули процесса БАЗИС.");

            do
            {
                if (!string.IsNullOrWhiteSpace(entry.szExePath))
                    result.Add(entry.szExePath);
                else if (!string.IsNullOrWhiteSpace(entry.szModule))
                    result.Add(entry.szModule);
                entry.dwSize = (uint)Marshal.SizeOf<MODULEENTRY32>();
            }
            while (Module32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> DetectGraphics(IEnumerable<string> modules)
    {
        var names = modules.Select(Path.GetFileName).Where(x => x is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();

        if (names.Contains("opengl32.dll") || names.Any(n => n!.StartsWith("nvoglv", StringComparison.OrdinalIgnoreCase) || n.StartsWith("atio", StringComparison.OrdinalIgnoreCase)))
            found.Add("OpenGL");
        if (names.Contains("d3d9.dll")) found.Add("Direct3D 9");
        if (names.Contains("d3d10.dll") || names.Contains("d3d10_1.dll")) found.Add("Direct3D 10");
        if (names.Contains("d3d11.dll")) found.Add("Direct3D 11");
        if (names.Contains("d3d12.dll")) found.Add("Direct3D 12");
        if (names.Contains("dxgi.dll")) found.Add("DXGI");
        if (names.Contains("vulkan-1.dll")) found.Add("Vulkan");

        return found;
    }

    private static bool IsGraphicsRelated(string path)
    {
        var n = (Path.GetFileName(path) ?? path).ToLowerInvariant();
        return n.Contains("opengl") || n.Contains("d3d") || n.Contains("dxgi") || n.Contains("vulkan") ||
               n.Contains("nvogl") || n.Contains("nvd3d") || n.Contains("atid") || n.Contains("atio") ||
               n.Contains("intel") || n.Contains("ig4") || n.Contains("igd") || n.Contains("mesa");
    }

    private static T Safe<T>(Func<T> action, T fallback)
    {
        try { return action(); } catch { return fallback; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MODULEENTRY32
    {
        public uint dwSize;
        public uint th32ModuleID;
        public uint th32ProcessID;
        public uint GlblcntUsage;
        public uint ProccntUsage;
        public IntPtr modBaseAddr;
        public uint modBaseSize;
        public IntPtr hModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExePath;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Module32First(IntPtr hSnapshot, ref MODULEENTRY32 lpme);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Module32Next(IntPtr hSnapshot, ref MODULEENTRY32 lpme);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
