using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Forms;

namespace B3DPublisherHost;

internal static class Program
{
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(90);
    private static readonly ConcurrentDictionary<string, byte> Changed = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] NativeBridgeTerms =
    {
        "WebViewer", "UploadModelFromStream", "InitializeWebViewerDLL", "FinalizeWebViewerDLL",
        "GetStreamFromLibEvent", "TGetStreamFromLibEvent", "FGetStreamFromLibEvent",
        "LibKernel3D", "CFRN", "cfrn", "TStream", "TMemoryStream",
        "mesh", "Mesh", "triangle", "Triangle", "triang", "Triang", "vertex", "Vertex",
        "facet", "Facet", "tessell", "Tessell", "polygon", "Polygon", "polyhedron", "Polyhedron",
        "Export", "export", "Save", "save", "Serialize", "serialize", "Model", "model"
    };

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
            var bazisRoot = FindBazisRoot(webViewerDll);
            var discovery = DiscoverNativeBridge(bazisRoot);
            File.WriteAllText(
                Path.Combine(captureDir, "native-bridge-discovery.json"),
                JsonSerializer.Serialize(discovery, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);

            WriteStatus(captureDir, new
            {
                input,
                started = DateTime.Now,
                webViewerDll,
                bazisRoot,
                mode = "BAZIS native final-geometry bridge discovery + WebViewer black-box capture",
                note = "No B3D geometry reconstruction and no OBJ/3DS/DAE route are used. The discovery stage only inventories public PE metadata and embedded identifiers from the installed BAZIS binaries."
            });

            OpenWithBazis(input);
            var bazisWindow = WaitForBazisWindow(Path.GetFileNameWithoutExtension(input), UiTimeout);
            if (bazisWindow is null)
                throw new InvalidOperationException("Не найдено окно БАЗИС после открытия B3D.");

            var roots = BuildWatchRoots();
            using var watchers = new WatcherSet(roots, captureDir);
            watchers.Start();

            TrySetFocus(bazisWindow);
            var invoked = TryInvokeNativeWebViewer(bazisWindow);

            if (!invoked)
            {
                MessageBox.Show(
                    "Publisher уже собрал карту штатных модулей БАЗИС и начал наблюдение за локальными файлами.\n\n" +
                    "Если команда «Веб-Просмотр» у вас доступна — вызовите её обычным способом. Если она заблокирована техсопровождением, просто нажмите OK: отчёт native-bridge-discovery.json всё равно уже создан.\n\n" +
                    "Никакого OBJ/3DS/DAE и реконструкции B3D здесь нет.",
                    "B3D Publisher",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(
                    "Штатная команда WebViewer в БАЗИСе запущена автоматически.\n\n" +
                    "Дождитесь окончания операции в БАЗИСе и нажмите OK здесь.",
                    "B3D Publisher",
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
                bazisRoot,
                commandInvokedAutomatically = invoked,
                nativeDiscoveryFile = Path.Combine(captureDir, "native-bridge-discovery.json"),
                nativeDiscoveryModules = discovery.modules.Count,
                nativeDiscoveryHighValueHits = discovery.highValueHits,
                watchRoots = roots,
                changedCount = Changed.Count,
                copiedCount = copied.Count,
                copied
            };
            File.WriteAllText(
                Path.Combine(captureDir, "capture-report.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);

            Process.Start(new ProcessStartInfo { FileName = captureDir, UseShellExecute = true });
            MessageBox.Show(
                $"Этап Publisher завершён.\n\nКандидатов штатного моста: {discovery.highValueHits.Count}\nИзменённых файлов: {Changed.Count}\nСкопировано файлов: {copied.Count}\n\nПапка открыта на рабочем столе.",
                "B3D Publisher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "B3D Publisher", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!(process.ProcessName.Contains("bazis", StringComparison.OrdinalIgnoreCase) ||
                      (process.MainWindowTitle ?? "").Contains("БАЗИС", StringComparison.OrdinalIgnoreCase))) continue;
                foreach (ProcessModule m in process.Modules)
                    if (string.Equals(Path.GetFileName(m.FileName), "WebViewer.dll", StringComparison.OrdinalIgnoreCase))
                        return m.FileName;
            }
            catch { }
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BazisSoft", "Bazis", "WebViewer.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "BazisSoft", "Bazis", "WebViewer.dll")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindBazisRoot(string? webViewerDll)
    {
        if (!string.IsNullOrWhiteSpace(webViewerDll)) return Path.GetDirectoryName(webViewerDll);
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "BazisSoft", "Bazis"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BazisSoft", "Bazis")
        };
        return candidates.FirstOrDefault(Directory.Exists);
    }

    private sealed record NativeModuleHit(string path, long size, string? version, List<string> identifiers, List<string> references);
    private sealed record NativeBridgeDiscovery(DateTime time, string? bazisRoot, List<NativeModuleHit> modules, List<object> highValueHits);

    private static NativeBridgeDiscovery DiscoverNativeBridge(string? bazisRoot)
    {
        var modules = new List<NativeModuleHit>();
        var high = new List<object>();
        if (string.IsNullOrWhiteSpace(bazisRoot) || !Directory.Exists(bazisRoot))
            return new NativeBridgeDiscovery(DateTime.Now, bazisRoot, modules, high);

        var files = Directory.EnumerateFiles(bazisRoot, "*.*", SearchOption.TopDirectoryOnly)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var path in files)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Length <= 0 || fi.Length > 512L * 1024 * 1024) continue;
                var bytes = File.ReadAllBytes(path);
                var ascii = Encoding.ASCII.GetString(bytes);
                var unicode = Encoding.Unicode.GetString(bytes);

                var ids = NativeBridgeTerms
                    .Where(term => ascii.Contains(term, StringComparison.Ordinal) || unicode.Contains(term, StringComparison.Ordinal))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (ids.Count == 0) continue;

                var refs = ExtractInterestingIdentifiers(ascii)
                    .Concat(ExtractInterestingIdentifiers(unicode))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .Take(400)
                    .ToList();

                var ver = FileVersionInfo.GetVersionInfo(path).FileVersion;
                modules.Add(new NativeModuleHit(path, fi.Length, ver, ids, refs));

                var exact = ids.Where(x =>
                    x.Equals("UploadModelFromStream", StringComparison.OrdinalIgnoreCase) ||
                    x.Equals("InitializeWebViewerDLL", StringComparison.OrdinalIgnoreCase) ||
                    x.Equals("GetStreamFromLibEvent", StringComparison.OrdinalIgnoreCase) ||
                    x.Equals("LibKernel3D", StringComparison.OrdinalIgnoreCase) ||
                    x.Equals("CFRN", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (exact.Length > 0)
                    high.Add(new { module = path, exact, related = refs.Take(80).ToArray() });
            }
            catch { }
        }

        modules = modules.OrderByDescending(m => Score(m.identifiers)).ThenBy(m => m.path, StringComparer.OrdinalIgnoreCase).ToList();
        return new NativeBridgeDiscovery(DateTime.Now, bazisRoot, modules, high);
    }

    private static int Score(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var score = set.Count;
        foreach (var key in new[] { "UploadModelFromStream", "GetStreamFromLibEvent", "InitializeWebViewerDLL", "LibKernel3D", "CFRN", "mesh", "triangle", "tessell" })
            if (set.Contains(key)) score += 20;
        return score;
    }

    private static IEnumerable<string> ExtractInterestingIdentifiers(string text)
    {
        var rx = new Regex(@"[A-Za-z_][A-Za-z0-9_:.@?$<>~-]{4,120}", RegexOptions.Compiled);
        foreach (Match m in rx.Matches(text))
        {
            var s = m.Value;
            if (NativeBridgeTerms.Any(t => s.Contains(t, StringComparison.OrdinalIgnoreCase))) yield return s;
        }
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
            Encoding.UTF8);
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
