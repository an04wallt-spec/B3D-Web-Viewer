using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Windows.Automation;
using System.Windows.Forms;

namespace B3DPublisherHost;

internal static class Program
{
    private const string ScriptResourceSuffix = "Bazis24FinalMeshPublisher.js";
    private const string InstalledScriptName = "Local View B3D Publisher.js";
    private const string PayloadFormatMarker = "local-view-bazis24-final-mesh-2";
    private static readonly TimeSpan BazisWindowTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(180);

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            var input = ResolveInput(args);
            if (input is null) return;

            // Production route is deliberately narrow:
            // BAZIS final TTriMesh -> official Script API -> one local HTML.
            // No B3D parsing/reconstruction, no OBJ/3DS/DAE, no WebViewer cloud.
            var installedScripts = InstallOfficialMeshBridge();
            if (installedScripts.Count == 0)
                throw new InvalidOperationException("Не удалось установить официальный mesh-bridge в папку скриптов БАЗИС.");

            var output = GetExpectedOutputPath(input);
            var previousWrite = File.Exists(output) ? File.GetLastWriteTimeUtc(output) : DateTime.MinValue;
            var started = DateTime.UtcNow;

            OpenWithBazis(input);
            var window = WaitForBazisWindow(Path.GetFileNameWithoutExtension(input), BazisWindowTimeout)
                ?? throw new InvalidOperationException("Не найдено окно БАЗИС после открытия модели.");

            TrySetFocus(window);
            if (!TryInvokePublisherScript(window, TimeSpan.FromSeconds(12)))
            {
                throw new InvalidOperationException(
                    "БАЗИС не предоставил доступ к команде «Local View B3D Publisher» в меню скриптов. " +
                    "Publisher не обходит ограничения лицензии и не переключается на реконструкцию B3D или облачный WebViewer.");
            }

            if (!WaitForPublishedHtml(output, previousWrite, started, PublishTimeout))
                throw new TimeoutException("БАЗИС не создал итоговый HTML в ожидаемый срок.");

            ValidatePublishedHtml(output);
            var info = new FileInfo(output);
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(output))).ToLowerInvariant();

            // Production deliverable is intentionally one file only: the local HTML.
            // The checksum is shown to the operator but no receipt/sidecar file is written.
            Process.Start(new ProcessStartInfo { FileName = Path.GetDirectoryName(output)!, UseShellExecute = true });
            MessageBox.Show(
                "Готово.\n\n" + output + "\n\n" +
                $"Размер: {info.Length:N0} байт\nSHA-256: {sha}\n\n" +
                "HTML полностью локальный; клиенту ничего устанавливать не требуется.",
                "B3D Publisher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "B3D Publisher", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

    private static string GetExpectedOutputPath(string input)
    {
        var dir = Path.GetDirectoryName(input)!;
        var name = Path.GetFileNameWithoutExtension(input);
        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        return Path.Combine(dir, name + "_просмотр.html");
    }

    private static List<string> InstallOfficialMeshBridge()
    {
        var script = ReadEmbeddedScript();
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var destinations = new List<string>();

        // BAZIS documentation uses Documents\BazisX\Scripts. Prefer Bazis24,
        // but also update existing versioned BAZIS script folders without
        // requiring a separate installer.
        var roots = new List<string> { Path.Combine(docs, "Bazis24") };
        try
        {
            roots.AddRange(Directory.EnumerateDirectories(docs, "Bazis*", SearchOption.TopDirectoryOnly));
        }
        catch { }

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var scripts = Path.Combine(root, "Scripts");
                if (!Directory.Exists(root) && !root.EndsWith("Bazis24", StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(scripts);
                var dest = Path.Combine(scripts, InstalledScriptName);
                File.WriteAllText(dest, script, new UTF8Encoding(false));
                destinations.Add(dest);
            }
            catch { }
        }
        return destinations;
    }

    private static string ReadEmbeddedScript()
    {
        var asm = Assembly.GetExecutingAssembly();
        var resource = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(ScriptResourceSuffix, StringComparison.OrdinalIgnoreCase));
        if (resource is null) throw new InvalidOperationException("В B3D-Publisher.exe отсутствует встроенный mesh-bridge.");
        using var stream = asm.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Не удалось открыть встроенный mesh-bridge.");
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return reader.ReadToEnd();
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

    private static void TrySetFocus(AutomationElement element)
    {
        try { element.SetFocus(); } catch { }
    }

    private static bool TryInvokePublisherScript(AutomationElement bazisWindow, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        var expandedScriptsMenu = false;
        while (sw.Elapsed < timeout)
        {
            try
            {
                // First try visible descendants of the BAZIS main window.
                if (TryInvokeNamedElement(bazisWindow, InstalledScriptName) ||
                    TryInvokeNamedElement(bazisWindow, Path.GetFileNameWithoutExtension(InstalledScriptName)))
                    return true;

                if (!expandedScriptsMenu)
                {
                    var descendants = bazisWindow.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                    foreach (AutomationElement e in descendants)
                    {
                        var name = e.Current.Name ?? string.Empty;
                        if (!name.Equals("Скрипты", StringComparison.OrdinalIgnoreCase) &&
                            !name.Contains("Скрипт", StringComparison.OrdinalIgnoreCase)) continue;

                        if (e.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand))
                        {
                            ((ExpandCollapsePattern)expand).Expand();
                            expandedScriptsMenu = true;
                            break;
                        }
                        if (e.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
                        {
                            ((InvokePattern)invoke).Invoke();
                            expandedScriptsMenu = true;
                            break;
                        }
                    }
                }

                // Popup menu items are often children of the desktop, not the BAZIS window.
                if (TryInvokeNamedElement(AutomationElement.RootElement, InstalledScriptName) ||
                    TryInvokeNamedElement(AutomationElement.RootElement, Path.GetFileNameWithoutExtension(InstalledScriptName)))
                    return true;
            }
            catch { }
            Thread.Sleep(350);
        }
        return false;
    }

    private static bool TryInvokeNamedElement(AutomationElement root, string expected)
    {
        try
        {
            var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            foreach (AutomationElement e in all)
            {
                var name = (e.Current.Name ?? string.Empty).Trim();
                if (!name.Equals(expected, StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains(expected, StringComparison.OrdinalIgnoreCase)) continue;
                if (!e.Current.IsEnabled) continue;
                if (e.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
                {
                    ((InvokePattern)invoke).Invoke();
                    return true;
                }
                if (e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select))
                {
                    ((SelectionItemPattern)select).Select();
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    private static bool WaitForPublishedHtml(string output, DateTime oldWrite, DateTime started, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                if (File.Exists(output))
                {
                    var fi = new FileInfo(output);
                    if (fi.Length > 1024 && fi.LastWriteTimeUtc > oldWrite && fi.LastWriteTimeUtc >= started.AddSeconds(-2))
                    {
                        // Wait until the writer has released the file.
                        using var s = File.Open(output, FileMode.Open, FileAccess.Read, FileShare.None);
                        return true;
                    }
                }
            }
            catch { }
            Thread.Sleep(300);
        }
        return false;
    }

    private static void ValidatePublishedHtml(string output)
    {
        var html = File.ReadAllText(output, Encoding.UTF8);
        if (!html.Contains("<!doctype html>", StringComparison.OrdinalIgnoreCase) ||
            !html.Contains("Local View B3D", StringComparison.Ordinal) ||
            !html.Contains("<canvas", StringComparison.OrdinalIgnoreCase) ||
            !html.Contains("<script id=\"data\" type=\"application/json\">", StringComparison.OrdinalIgnoreCase) ||
            !html.Contains(PayloadFormatMarker, StringComparison.Ordinal) ||
            !html.Contains("Снять выделение", StringComparison.Ordinal) ||
            html.Contains("<script src=", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Созданный HTML не прошёл проверку автономности/целостности финальной mesh-публикации.");
    }
}
