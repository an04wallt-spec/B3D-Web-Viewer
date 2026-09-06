using System.Diagnostics;
using System.Text;
using System.Windows.Forms;

namespace B3DPublisherHost;

internal static class Program
{
    private const string PayloadFormatMarker = "local-view-bazis-viewer3d-wrl-1";

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string? tempDirectory = null;
        try
        {
            var input = ResolveInput(args);
            if (input is null) return;

            // Production route:
            // B3D -> official BAZIS Viewer3D -> temporary VRML -> one local HTML.
            // Viewer3D performs the B3D interpretation and final mesh export.
            // The Publisher never reconstructs B3D geometry and never uses scripts,
            // OBJ/3DS/DAE, WebViewer cloud, or license bypasses.
            tempDirectory = Path.Combine(Path.GetTempPath(), "B3DPublisher", Guid.NewGuid().ToString("N"));
            var wrl = Viewer3DExporter.ExportToTemporaryWrl(input, tempDirectory);
            var model = VrmlParser.Parse(wrl);

            var output = GetExpectedOutputPath(input);
            OfflineHtmlPublisher.Publish(model, input, output);
            ValidatePublishedHtml(output);

            var info = new FileInfo(output);
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(output))).ToLowerInvariant();
            Process.Start(new ProcessStartInfo { FileName = Path.GetDirectoryName(output)!, UseShellExecute = true });
            MessageBox.Show(
                "Готово.\n\n" + output + "\n\n" +
                $"Треугольников: {model.TriangleCount:N0}\n" +
                $"Размер HTML: {info.Length:N0} байт\nSHA-256: {sha}\n\n" +
                "Геометрия получена штатным БАЗИС-Просмотр 3D. WRL был только временным файлом и удалён. " +
                "Клиенту передаётся один полностью локальный HTML.",
                "B3D Publisher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "B3D Publisher", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempDirectory))
            {
                try { Directory.Delete(tempDirectory, true); } catch { }
            }
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
            throw new InvalidDataException("Созданный HTML не прошёл проверку автономности/целостности Viewer3D-публикации.");
    }
}
