using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Forms;

namespace B3DPublisherHost;

internal static class Program
{
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(90);
    private static readonly ConcurrentDictionary<string, byte> Changed = new(StringComparer.OrdinalIgnoreCase);

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        try
        {
            var input = ResolveInput(args);
            if (input is null) return;

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var captureDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "B3D-Native-Capture_" + stamp);
            Directory.CreateDirectory(captureDir);

            var webViewerDll = FindWebViewerDll();
            WriteStatus(captureDir, new
            {
                input,
                started = DateTime.Now,
                webViewerDll,
                mode = "BAZIS native WebViewer black-box capture",
                note = "No B3D reconstruction and no interchange export are used."
            });

            OpenWithBazis(input);
            var bazisWindow = WaitForBazisWindow(Path.GetFileNameWithoutExtension(input), UiTimeout);
            if (bazisWindow is null)
                throw new InvalidOperationException("Не найдено окно БАЗИС после открытия B3D.");

            var roots = BuildWatchRoots();
            using var watchers = new WatcherSet(roots, captureDir);
            watchers.Start();

            // Some BAZIS windows/providers reject UI Automation SetFocus().
            // Focus is not required for searching/invoking descendants, so it must never be fatal.
            TrySetFocus(bazisWindow);
            var invoked = TryInvokeNativeWebViewer(bazisWindow);

            if (!invoked)
            {
                MessageBox.Show(
                    "Наблюдение за штатной конвертацией БАЗИС запущено.\n\n" +
                    "В БАЗИСе вызовите обычную команду «Веб-Просмотр / Отправить модель».\n" +
                    "Это НЕ экспорт в 3DS/OBJ/DAE — мы фиксируем только то, что сам БАЗИС создаёт для своего WebViewer.\n\n" +
                    "После завершения передачи нажмите OK здесь.",
                    "B3D Native Capture",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(
                    "Штатная команда WebViewer в БАЗИСе запущена автоматически.\n\n" +
                    "Дождитесь окончания операции в БАЗИСе и нажмите OK здесь.",
                    "B3D Native Capture",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }

            Thread.Sleep(1500);
            watchers.Stop();

            var copied = CopyCapturedFiles(captureDir);
            var report = new
            {
                input,
                finished = DateTime.Now,
                webViewerDll,
                commandInvokedAutomatically = invoked,
                watchRoots = roots,
                changedCount = Changed.Count,
                copiedCount = copied.Count,
                copied
            };
            File.WriteAllText(
                Path.Combine(captureDir, "capture-report.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                System.Text.Encoding.UTF8);

            Process.Start(new ProcessStartInfo { FileName = captureDir, UseShellExecute = true });
            MessageBox.Show(
                $"Захват закончен.\n\nНайдено изменённых файлов: {Changed.Count}\nСкопировано кандидатов: {copied.Count}\n\nПапка открыта на рабочем столе.",
                "B3D Native Capture",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "B3D Native Capture", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string? ResolveInput(string[] args)
    {
        if (args.Length > 0)
        {
            var p = Path.GetFullPath(args[0]);
            ValidateInput(p);
            return p;
        }

        using var dlg = new OpenFileDialog
        {
            Title = "Выберите модель БАЗИС",
            Filter = "Модель БАЗИС (*.b3d)|*.b3d",
            Multiselect = false,
            CheckFileExists = true
        };
        if (dlg.ShowDialog() != DialogResult.OK) return null;
        ValidateInput(dlg.FileName);
        return dlg.FileName;
    }

    private static void ValidateInput(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("B3D-файл не найден.", path);
        if (!string.Equals(Path.GetExtension(path), ".b3d", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Нужен файл .b3d.");
    }

    private static void OpenWithBazis(string b3dPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = b3dPath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(b3dPath)!
        });
    }

    private static string? FindWebViewerDll()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BazisSoft", "Bazis", "WebViewer.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "BazisSoft", "Bazis", "WebViewer.dll")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static List<string> BuildWatchRoots()
    {
        var list = new List<string>();
        AddRoot(list, Path.GetTempPath());
        AddRoot(list, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BazisSoft"));
        AddRoot(list, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BazisSoft"));
        AddRoot(list, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Bazis"));
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddRoot(List<string> list, string path)
    {
        try { if (Directory.Exists(path)) list.Add(Path.GetFullPath(path)); } catch { }
    }

    private static void TrySetFocus(AutomationElement element)
    {
        try { element.SetFocus(); }
        catch (InvalidOperationException) { }
        catch (ElementNotAvailableException) { }
    }

    private static bool TryInvokeNativeWebViewer(AutomationElement bazisWindow)
    {
        try
        {
            var all = bazisWindow.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            foreach (AutomationElement e in all)
            {
                var name = e.Current.Name ?? string.Empty;
                var hit = name.Contains("Веб-Просмотр", StringComparison.OrdinalIgnoreCase) ||
                          name.Contains("Веб просмотр", StringComparison.OrdinalIgnoreCase) ||
                          name.Contains("WebViewer", StringComparison.OrdinalIgnoreCase) ||
                          (name.Contains("Веб", StringComparison.OrdinalIgnoreCase) && name.Contains("Просмотр", StringComparison.OrdinalIgnoreCase));
                if (!hit) continue;

                if (e.TryGetCurrentPattern(InvokePattern.Pattern, out var ip))
                {
                    ((InvokePattern)ip).Invoke();
                    return true;
                }
                if (e.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ep))
                {
                    ((ExpandCollapsePattern)ep).Expand();
                    Thread.Sleep(300);
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    private static AutomationElement? WaitForBazisWindow(string modelName, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                var windows = AutomationElement.RootElement.FindAll(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
                foreach (AutomationElement e in windows)
                {
                    var name = e.Current.Name ?? string.Empty;
                    if (name.Contains(modelName, StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("БАЗИС", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("BAZIS", StringComparison.OrdinalIgnoreCase))
                        return e;
                }
            }
            catch { }
            Thread.Sleep(250);
        }
        return null;
    }

    private static List<object> CopyCapturedFiles(string captureDir)
    {
        var copied = new List<object>();
        var payloadDir = Path.Combine(captureDir, "payload-candidates");
        Directory.CreateDirectory(payloadDir);
        long total = 0;
        var index = 0;

        foreach (var source in Changed.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(source)) continue;
                if (source.StartsWith(captureDir, StringComparison.OrdinalIgnoreCase)) continue;

                var fi = new FileInfo(source);
                if (fi.Length < 256 || fi.Length > 512L * 1024 * 1024) continue;
                if (total + fi.Length > 1024L * 1024 * 1024) break;

                var ext = Path.GetExtension(source);
                var safe = MakeSafeFileName(Path.GetFileNameWithoutExtension(source));
                var dest = Path.Combine(payloadDir, $"{index++:D4}_{safe}{ext}");
                File.Copy(source, dest, true);
                total += fi.Length;

                copied.Add(new
                {
                    source,
                    copy = dest,
                    size = fi.Length,
                    modified = fi.LastWriteTime
                });
            }
            catch { }
        }
        return copied;
    }

    private static string MakeSafeFileName(string name)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        if (name.Length > 80) name = name[..80];
        return string.IsNullOrWhiteSpace(name) ? "file" : name;
    }

    private static void WriteStatus(string captureDir, object obj)
    {
        File.WriteAllText(
            Path.Combine(captureDir, "start-info.json"),
            JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }),
            System.Text.Encoding.UTF8);
    }

    private sealed class WatcherSet : IDisposable
    {
        private readonly List<FileSystemWatcher> _watchers = new();
        private readonly string _captureDir;

        public WatcherSet(IEnumerable<string> roots, string captureDir)
        {
            _captureDir = captureDir;
            foreach (var root in roots)
            {
                try
                {
                    var w = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                        EnableRaisingEvents = false
                    };
                    w.Created += OnChanged;
                    w.Changed += OnChanged;
                    w.Renamed += OnRenamed;
                    _watchers.Add(w);
                }
                catch { }
            }
        }

        public void Start()
        {
            Changed.Clear();
            foreach (var w in _watchers) w.EnableRaisingEvents = true;
        }

        public void Stop()
        {
            foreach (var w in _watchers) w.EnableRaisingEvents = false;
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            if (e.FullPath.StartsWith(_captureDir, StringComparison.OrdinalIgnoreCase)) return;
            Changed.TryAdd(e.FullPath, 0);
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            if (e.FullPath.StartsWith(_captureDir, StringComparison.OrdinalIgnoreCase)) return;
            Changed.TryAdd(e.FullPath, 0);
        }

        public void Dispose()
        {
            foreach (var w in _watchers) w.Dispose();
        }
    }
}
