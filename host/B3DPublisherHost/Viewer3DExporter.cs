using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Microsoft.Win32;

namespace B3DPublisherHost;

internal static class Viewer3DExporter
{
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SaveTimeout = TimeSpan.FromSeconds(90);

    public static string ExportToTemporaryWrl(string b3dPath, string tempDirectory)
    {
        Directory.CreateDirectory(tempDirectory);
        var viewer = FindViewerExecutable()
            ?? throw new FileNotFoundException(
                "Не найден БАЗИС-Просмотр 3D (Viewer24.exe/ViewerX.exe). Он должен быть установлен вместе с БАЗИС-Мебельщик.");

        var wrlPath = Path.Combine(tempDirectory, "model.wrl");
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = viewer,
            Arguments = Quote(b3dPath),
            WorkingDirectory = Path.GetDirectoryName(b3dPath)!,
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("Не удалось запустить БАЗИС-Просмотр 3D.");

        var window = WaitForProcessWindow(process, WindowTimeout)
            ?? throw new TimeoutException("БАЗИС-Просмотр 3D запущен, но его окно не появилось.");

        TrySetFocus(window);
        if (!InvokeSave(window))
            throw new InvalidOperationException("В БАЗИС-Просмотр 3D не найдена штатная команда «Сохранить».");

        var dialog = WaitForSaveDialog(process.Id, SaveTimeout)
            ?? throw new TimeoutException("Не появился стандартный диалог сохранения БАЗИС-Просмотр 3D.");

        SelectWrlFileType(dialog);
        SetFileName(dialog, wrlPath);
        ConfirmSave(dialog);

        var actual = WaitForWrl(tempDirectory, SaveTimeout);
        if (actual is null)
            throw new TimeoutException("БАЗИС-Просмотр 3D не создал VRML-файл (.wrl) в ожидаемый срок.");
        return actual;
    }

    private static string? FindViewerExecutable()
    {
        var names = new[] { "Viewer24.exe", "Viewer2024.exe", "Viewer.exe" };

        foreach (var name in names)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + name);
                var value = key?.GetValue(null) as string;
                if (!string.IsNullOrWhiteSpace(value) && File.Exists(value)) return value;
            }
            catch { }
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + name);
                var value = key?.GetValue(null) as string;
                if (!string.IsNullOrWhiteSpace(value) && File.Exists(value)) return value;
            }
            catch { }
        }

        // A running BAZIS process gives us the most reliable installation directory.
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var file = p.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(file)) continue;
                var dir = Path.GetDirectoryName(file)!;
                var exe = Path.GetFileName(file);
                if (!exe.Contains("baz", StringComparison.OrdinalIgnoreCase) &&
                    !dir.Contains("baz", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var name in names)
                {
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) return candidate;
                }
                var versioned = Directory.EnumerateFiles(dir, "Viewer*.exe", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(x => !Path.GetFileName(x).Contains("2D", StringComparison.OrdinalIgnoreCase));
                if (versioned is not null) return versioned;
            }
            catch { }
        }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 }.Where(Directory.Exists))
        {
            try
            {
                foreach (var top in Directory.EnumerateDirectories(root)
                             .Where(d => Path.GetFileName(d).Contains("Baz", StringComparison.OrdinalIgnoreCase)))
                {
                    var found = FindViewerBelow(top, 0, 4);
                    if (found is not null) return found;
                }
            }
            catch { }
        }
        return null;
    }

    private static string? FindViewerBelow(string directory, int depth, int maxDepth)
    {
        try
        {
            var files = Directory.EnumerateFiles(directory, "Viewer*.exe", SearchOption.TopDirectoryOnly)
                .Where(x => !Path.GetFileName(x).Contains("2D", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => Path.GetFileName(x).Contains("24", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (files.Count > 0) return files[0];
            if (depth >= maxDepth) return null;
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var found = FindViewerBelow(child, depth + 1, maxDepth);
                if (found is not null) return found;
            }
        }
        catch { }
        return null;
    }

    private static AutomationElement? WaitForProcessWindow(Process process, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                process.Refresh();
                if (process.HasExited) throw new InvalidOperationException("БАЗИС-Просмотр 3D завершился до загрузки модели.");
                if (process.MainWindowHandle != IntPtr.Zero)
                    return AutomationElement.FromHandle(process.MainWindowHandle);

                var windows = AutomationElement.RootElement.FindAll(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
                foreach (AutomationElement w in windows)
                    if (w.Current.ProcessId == process.Id) return w;
            }
            catch (InvalidOperationException) { throw; }
            catch { }
            Thread.Sleep(250);
        }
        return null;
    }

    private static bool InvokeSave(AutomationElement window)
    {
        // Viewer3D documentation defines a toolbar command named «Сохранить».
        foreach (var expected in new[] { "Сохранить", "Save", "Сохранение" })
        {
            try
            {
                var all = window.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                foreach (AutomationElement e in all)
                {
                    var name = (e.Current.Name ?? string.Empty).Trim();
                    if (!name.Equals(expected, StringComparison.OrdinalIgnoreCase) &&
                        !name.StartsWith(expected, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!e.Current.IsEnabled) continue;
                    if (e.TryGetCurrentPattern(InvokePattern.Pattern, out var inv))
                    {
                        ((InvokePattern)inv).Invoke();
                        return true;
                    }
                }
            }
            catch { }
        }

        // Standard Save shortcut is a safe fallback for the Viewer3D document window.
        try
        {
            window.SetFocus();
            SendKeys.SendWait("^s");
            return true;
        }
        catch { return false; }
    }

    private static AutomationElement? WaitForSaveDialog(int processId, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                var windows = AutomationElement.RootElement.FindAll(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
                foreach (AutomationElement w in windows)
                {
                    if (w.Current.ProcessId != processId) continue;
                    var name = w.Current.Name ?? string.Empty;
                    if (name.Contains("Сохран", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Save", StringComparison.OrdinalIgnoreCase))
                        return w;

                    // Modern common dialogs may expose no useful title; a filename edit is enough.
                    if (FindByAutomationId(w, "1001") is not null) return w;
                }
            }
            catch { }
            Thread.Sleep(200);
        }
        return null;
    }

    private static void SelectWrlFileType(AutomationElement dialog)
    {
        // Standard Windows common-dialog file type combo.
        var combo = FindByAutomationId(dialog, "1136");
        if (combo is null)
        {
            try
            {
                var combos = dialog.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox));
                foreach (AutomationElement c in combos)
                {
                    var n = c.Current.Name ?? string.Empty;
                    if (n.Contains("тип", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("type", StringComparison.OrdinalIgnoreCase)) { combo = c; break; }
                }
            }
            catch { }
        }
        if (combo is null) return;

        try
        {
            if (combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ex))
                ((ExpandCollapsePattern)ex).Expand();
            Thread.Sleep(150);

            var items = combo.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            foreach (AutomationElement item in items)
            {
                var n = item.Current.Name ?? string.Empty;
                if (!n.Contains("VRML", StringComparison.OrdinalIgnoreCase) &&
                    !n.Contains("wrl", StringComparison.OrdinalIgnoreCase)) continue;
                if (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sel))
                {
                    ((SelectionItemPattern)sel).Select();
                    return;
                }
                if (item.TryGetCurrentPattern(InvokePattern.Pattern, out var inv))
                {
                    ((InvokePattern)inv).Invoke();
                    return;
                }
            }
        }
        catch { }
    }

    private static void SetFileName(AutomationElement dialog, string filePath)
    {
        var edit = FindByAutomationId(dialog, "1001");
        if (edit is null)
        {
            var edits = dialog.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            if (edits.Count > 0) edit = edits[edits.Count - 1];
        }
        if (edit is null || !edit.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
            throw new InvalidOperationException("Не найдено поле имени файла в диалоге сохранения Viewer3D.");
        ((ValuePattern)value).SetValue(filePath);
    }

    private static void ConfirmSave(AutomationElement dialog)
    {
        var button = FindByAutomationId(dialog, "1");
        if (button is not null && button.TryGetCurrentPattern(InvokePattern.Pattern, out var inv))
        {
            ((InvokePattern)inv).Invoke();
            return;
        }
        foreach (var expected in new[] { "Сохранить", "Save" })
        {
            var b = FindNamedButton(dialog, expected);
            if (b is not null && b.TryGetCurrentPattern(InvokePattern.Pattern, out inv))
            {
                ((InvokePattern)inv).Invoke();
                return;
            }
        }
        throw new InvalidOperationException("Не найдена кнопка сохранения в стандартном диалоге Viewer3D.");
    }

    private static string? WaitForWrl(string directory, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                var files = Directory.EnumerateFiles(directory, "*.wrl", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(File.GetLastWriteTimeUtc).ToList();
                foreach (var f in files)
                {
                    var info = new FileInfo(f);
                    if (info.Length < 128) continue;
                    using var s = File.Open(f, FileMode.Open, FileAccess.Read, FileShare.None);
                    return f;
                }
            }
            catch { }
            Thread.Sleep(250);
        }
        return null;
    }

    private static AutomationElement? FindByAutomationId(AutomationElement root, string id)
    {
        try
        {
            return root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, id));
        }
        catch { return null; }
    }

    private static AutomationElement? FindNamedButton(AutomationElement root, string text)
    {
        try
        {
            var buttons = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement b in buttons)
                if ((b.Current.Name ?? string.Empty).Contains(text, StringComparison.OrdinalIgnoreCase)) return b;
        }
        catch { }
        return null;
    }

    private static void TrySetFocus(AutomationElement element)
    {
        try { element.SetFocus(); } catch { }
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
}
