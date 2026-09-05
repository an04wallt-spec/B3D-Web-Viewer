using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Forms;

namespace B3DPublisherHost;

internal static class Program
{
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ExportTimeout = TimeSpan.FromMinutes(3);

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        try
        {
            var input = ResolveInput(args);
            if (input is null) return;

            var workDir = Path.Combine(
                Path.GetTempPath(),
                "B3D-Publisher",
                Path.GetFileNameWithoutExtension(input) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(workDir);
            var exported3ds = Path.Combine(workDir, "model.3ds");

            OpenWithBazis(input);
            var bazisWindow = WaitForBazisWindow(Path.GetFileNameWithoutExtension(input), UiTimeout);
            if (bazisWindow is null)
                throw new InvalidOperationException("Не найдено окно БАЗИС после открытия B3D.");

            bazisWindow.SetFocus();
            InvokeMenuItem(bazisWindow, "Файл");
            var exportItem = WaitForElement(
                AutomationElement.RootElement,
                e => IsMenuItem(e) && NameContains(e, "Экспорт"),
                TimeSpan.FromSeconds(10));
            if (exportItem is null)
                throw new InvalidOperationException("В меню БАЗИС не найдена команда «Экспорт».");
            InvokeElement(exportItem);

            var saveDialog = WaitForElement(
                AutomationElement.RootElement,
                e => IsWindow(e) && (NameContains(e, "Сохран") || NameContains(e, "Экспорт")),
                TimeSpan.FromSeconds(20));
            if (saveDialog is null)
                throw new InvalidOperationException("Не найден диалог экспорта/сохранения БАЗИС.");

            Select3dsFormat(saveDialog);
            SetFileName(saveDialog, exported3ds);
            ClickSave(saveDialog);

            WaitForStableFile(exported3ds, ExportTimeout);

            File.WriteAllText(
                Path.Combine(workDir, "handoff.txt"),
                "BAZIS_EXPORT_OK\r\n" + exported3ds + "\r\n",
                System.Text.Encoding.UTF8);

            MessageBox.Show(
                "БАЗИС сформировал готовую геометрию.\n\n" + exported3ds +
                "\n\nСледующий этап Publisher сможет забрать этот файл автоматически.",
                "B3D Publisher — экспорт готов",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "B3D Publisher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
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
        var psi = new ProcessStartInfo
        {
            FileName = b3dPath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(b3dPath)!
        };
        Process.Start(psi);
    }

    private static AutomationElement? WaitForBazisWindow(string modelName, TimeSpan timeout)
    {
        return WaitForElement(
            AutomationElement.RootElement,
            e => IsWindow(e) &&
                 (NameContains(e, modelName) || NameContains(e, "БАЗИС") || NameContains(e, "BAZIS")),
            timeout);
    }

    private static void InvokeMenuItem(AutomationElement root, string name)
    {
        var item = WaitForElement(root, e => IsMenuItem(e) && NameEquals(e, name), TimeSpan.FromSeconds(10));
        if (item is null) throw new InvalidOperationException($"Не найден пункт меню «{name}».");

        if (item.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expandPattern))
            ((ExpandCollapsePattern)expandPattern).Expand();
        else
            InvokeElement(item);
    }

    private static void Select3dsFormat(AutomationElement dialog)
    {
        var combos = dialog.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox));

        foreach (AutomationElement combo in combos)
        {
            try
            {
                if (combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ep))
                    ((ExpandCollapsePattern)ep).Expand();

                Thread.Sleep(250);
                var item = combo.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));

                var all = combo.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                foreach (AutomationElement candidate in all)
                {
                    if (!NameContains(candidate, "3ds")) continue;
                    if (candidate.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp))
                    {
                        ((SelectionItemPattern)sp).Select();
                        return;
                    }
                    InvokeElement(candidate);
                    return;
                }
            }
            catch { /* try next combo */ }
        }

        // Some standard Save As dialogs infer the format from the extension.
        // In that case SetFileName(model.3ds) below is sufficient.
    }

    private static void SetFileName(AutomationElement dialog, string path)
    {
        AutomationElement? edit = null;

        var byId = dialog.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "1001"));
        if (byId is not null && byId.Current.ControlType == ControlType.Edit)
            edit = byId;

        edit ??= dialog.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));

        if (edit is null || !edit.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
            throw new InvalidOperationException("Не найдено поле имени файла в диалоге экспорта.");

        ((ValuePattern)vp).SetValue(path);
    }

    private static void ClickSave(AutomationElement dialog)
    {
        var buttons = dialog.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

        foreach (AutomationElement button in buttons)
        {
            if (NameEquals(button, "Сохранить") || NameEquals(button, "Save"))
            {
                InvokeElement(button);
                return;
            }
        }

        throw new InvalidOperationException("Не найдена кнопка «Сохранить».");
    }

    private static void WaitForStableFile(string path, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        long lastSize = -1;
        var stableTicks = 0;

        while (sw.Elapsed < timeout)
        {
            if (File.Exists(path))
            {
                var size = new FileInfo(path).Length;
                if (size > 0 && size == lastSize)
                {
                    stableTicks++;
                    if (stableTicks >= 4) return;
                }
                else
                {
                    stableTicks = 0;
                    lastSize = size;
                }
            }
            Thread.Sleep(500);
        }

        throw new TimeoutException("БАЗИС не создал стабильный 3DS-файл за отведённое время.");
    }

    private static AutomationElement? WaitForElement(
        AutomationElement root,
        Func<AutomationElement, bool> predicate,
        TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                foreach (AutomationElement e in all)
                    if (predicate(e)) return e;
            }
            catch { /* UI tree may change while BAZIS opens dialogs */ }

            Thread.Sleep(200);
        }
        return null;
    }

    private static void InvokeElement(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var ip))
        {
            ((InvokePattern)ip).Invoke();
            return;
        }
        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp))
        {
            ((SelectionItemPattern)sp).Select();
            return;
        }
        throw new InvalidOperationException("Элемент интерфейса БАЗИС нельзя активировать через UI Automation.");
    }

    private static bool IsWindow(AutomationElement e) => e.Current.ControlType == ControlType.Window;
    private static bool IsMenuItem(AutomationElement e) => e.Current.ControlType == ControlType.MenuItem;

    private static bool NameEquals(AutomationElement e, string text) =>
        string.Equals(e.Current.Name?.Trim(), text, StringComparison.OrdinalIgnoreCase);

    private static bool NameContains(AutomationElement e, string text) =>
        (e.Current.Name ?? string.Empty).Contains(text, StringComparison.OrdinalIgnoreCase);
}
