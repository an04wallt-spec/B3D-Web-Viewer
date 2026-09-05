using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace BazisDxgiProbe;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            var process = FindBazisProcess();
            if (process is null)
            {
                MessageBox.Show("Не найден запущенный БАЗИС.\n\nОткройте модель в БАЗИС-24 и запустите Probe ещё раз.", "BAZIS DXGI Probe", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var result = Capture(process.Id, TimeSpan.FromSeconds(12));
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var reportPath = Path.Combine(desktop, "BAZIS-DXGI-Probe.json");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                time = DateTime.Now,
                process = new { process.Id, process.ProcessName, process.MainWindowTitle },
                captureSeconds = 12,
                dxgiConfirmed = result.DxgiEvents > 0,
                presentLikeEvents = result.PresentEvents,
                dxgiEvents = result.DxgiEvents,
                d3d11Events = result.D3D11Events,
                providersEnabled = result.ProvidersEnabled,
                providerErrors = result.ProviderErrors,
                topEvents = result.EventCounts.OrderByDescending(x => x.Value).Take(40).ToDictionary(x => x.Key, x => x.Value)
            }, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);

            var text = result.DxgiEvents > 0
                ? $"DXGI подтверждён для Bazis.exe.\nDXGI-событий: {result.DxgiEvents}\nPresent-подобных: {result.PresentEvents}"
                : "DXGI-события для Bazis.exe за время теста не обнаружены.";

            MessageBox.Show(text + "\n\nОтчёт:\n" + reportPath, "BAZIS DXGI Probe", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show("Windows не разрешил открыть ETW-сессию.\n\nЗапустите BAZIS-DXGI-Probe.exe от имени администратора и повторите тест.", "BAZIS DXGI Probe", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "BAZIS DXGI Probe", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static ProbeResult Capture(int pid, TimeSpan duration)
    {
        var counts = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var providerErrors = new List<string>();
        var enabled = new List<string>();
        long dxgi = 0, d3d11 = 0, present = 0;
        var sessionName = "B3D_BAZIS_DXGI_" + Guid.NewGuid().ToString("N");

        using var session = new TraceEventSession(sessionName, null);
        session.StopOnDispose = true;

        foreach (var provider in new[] { "Microsoft-Windows-DXGI", "Microsoft-Windows-Direct3D11", "Microsoft-Windows-D3D11" })
        {
            try
            {
                var ok = session.EnableProvider(provider, TraceEventLevel.Verbose, ulong.MaxValue);
                if (ok) enabled.Add(provider);
            }
            catch (Exception ex)
            {
                providerErrors.Add(provider + ": " + ex.Message);
            }
        }

        session.Source.Dynamic.All += data =>
        {
            if (data.ProcessID != pid) return;
            var provider = data.ProviderName ?? data.ProviderGuid.ToString();
            var name = data.EventName ?? ("Event" + data.ID);
            var key = provider + "/" + name;
            counts.AddOrUpdate(key, 1, (_, n) => n + 1);
            if (provider.Contains("DXGI", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref dxgi);
            if (provider.Contains("D3D11", StringComparison.OrdinalIgnoreCase) || provider.Contains("Direct3D11", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref d3d11);
            if (name.Contains("Present", StringComparison.OrdinalIgnoreCase) || name.Contains("SwapChain", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref present);
        };

        MessageBox.Show(
            "Сейчас начнётся 12-секундный тест.\n\nПосле нажатия OK сразу покрутите модель в окне БАЗИСа несколько секунд.\nНичего больше делать не нужно.",
            "BAZIS DXGI Probe",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);

        using var cts = new CancellationTokenSource(duration);
        var processing = Task.Run(() =>
        {
            try { session.Source.Process(); } catch { }
        });

        cts.Token.WaitHandle.WaitOne();
        session.Stop();
        try { processing.Wait(TimeSpan.FromSeconds(2)); } catch { }

        return new ProbeResult((int)dxgi, (int)d3d11, (int)present, counts, enabled, providerErrors);
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

    private static T Safe<T>(Func<T> f, T fallback)
    {
        try { return f(); } catch { return fallback; }
    }

    private sealed record ProbeResult(
        int DxgiEvents,
        int D3D11Events,
        int PresentEvents,
        ConcurrentDictionary<string, int> EventCounts,
        List<string> ProvidersEnabled,
        List<string> ProviderErrors);
}
