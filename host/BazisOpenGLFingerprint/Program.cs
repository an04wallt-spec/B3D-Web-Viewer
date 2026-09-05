using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace BazisOpenGLFingerprint;

internal static class Program
{
    private static readonly Dictionary<string, string[]> Families = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ImmediateMode"] = new[] { "glBegin", "glEnd", "glVertex2", "glVertex3", "glVertex4", "glNormal3", "glTexCoord2", "glColor3", "glColor4" },
        ["DisplayLists"] = new[] { "glNewList", "glEndList", "glCallList", "glCallLists", "glGenLists" },
        ["ClientArrays"] = new[] { "glVertexPointer", "glNormalPointer", "glTexCoordPointer", "glColorPointer", "glEnableClientState", "glDrawArrays", "glDrawElements" },
        ["VBO"] = new[] { "glGenBuffers", "glBindBuffer", "glBufferData", "glBufferSubData", "glMapBuffer", "glMapBufferRange" },
        ["Shaders"] = new[] { "glCreateShader", "glShaderSource", "glCompileShader", "glCreateProgram", "glAttachShader", "glLinkProgram", "glUseProgram", "glGetUniformLocation" },
        ["FBO"] = new[] { "glGenFramebuffers", "glBindFramebuffer", "glFramebufferTexture", "glCheckFramebufferStatus" },
        ["VAO"] = new[] { "glGenVertexArrays", "glBindVertexArray" },
        ["Instancing"] = new[] { "glDrawArraysInstanced", "glDrawElementsInstanced", "glVertexAttribDivisor" },
        ["ModernAttribs"] = new[] { "glVertexAttribPointer", "glEnableVertexAttribArray", "glDisableVertexAttribArray" },
        ["Textures"] = new[] { "glBindTexture", "glTexImage2D", "glTexSubImage2D", "glCompressedTexImage2D", "glActiveTexture" },
        ["WGLContext"] = new[] { "wglCreateContext", "wglCreateContextAttribsARB", "wglMakeCurrent", "wglGetProcAddress", "SwapBuffers" }
    };

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            var process = FindBazisProcess();
            var kernelPath = FindKernelPath(process);
            if (kernelPath is null || !File.Exists(kernelPath))
            {
                MessageBox.Show("Не найден LibKernel3D.dll. Откройте БАЗИС-24 и запустите Probe ещё раз.", "BAZIS OpenGL Fingerprint", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var bytes = File.ReadAllBytes(kernelPath);
            var ascii = Encoding.ASCII.GetString(bytes);
            var unicode = Encoding.Unicode.GetString(bytes);

            var familyHits = new Dictionary<string, string[]>();
            var allHits = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (family, funcs) in Families)
            {
                var hits = funcs.Where(f => ContainsSymbol(ascii, unicode, f)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                familyHits[family] = hits;
                foreach (var h in hits) allHits.Add(h);
            }

            var glLike = Regex.Matches(ascii, @"\b(?:wgl|gl)[A-Za-z0-9_]{3,80}\b")
                .Select(m => m.Value)
                .Where(s => s.StartsWith("gl", StringComparison.Ordinal) || s.StartsWith("wgl", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .Take(2000)
                .ToArray();

            var classification = Classify(familyHits);
            var report = new
            {
                time = DateTime.Now,
                process = process is null ? null : new { process.Id, process.ProcessName, process.MainWindowTitle },
                kernel3D = new { path = kernelPath, size = bytes.LongLength, version = FileVersionInfo.GetVersionInfo(kernelPath).FileVersion },
                classification,
                families = familyHits,
                allKnownHits = allHits.ToArray(),
                discoveredGLSymbols = glLike
            };

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var reportPath = Path.Combine(desktop, "BAZIS-OpenGL-Fingerprint.json");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);

            MessageBox.Show(classification + "\n\nОтчёт:\n" + reportPath, "BAZIS OpenGL Fingerprint", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "BAZIS OpenGL Fingerprint", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static bool ContainsSymbol(string ascii, string unicode, string symbol) =>
        ascii.Contains(symbol, StringComparison.Ordinal) || unicode.Contains(symbol, StringComparison.Ordinal);

    private static string Classify(Dictionary<string, string[]> h)
    {
        bool Has(string k) => h.TryGetValue(k, out var a) && a.Length > 0;
        if (Has("Shaders") || Has("VBO") || Has("VAO") || Has("ModernAttribs"))
        {
            if (Has("ImmediateMode") || Has("DisplayLists") || Has("ClientArrays"))
                return "Hybrid / compatibility-style OpenGL: legacy fixed-function/client-array calls coexist with newer buffer/shader-era calls.";
            return "Modern-style OpenGL symbols detected; capture tool must support buffer/shader based rendering.";
        }
        if (Has("ImmediateMode") || Has("DisplayLists") || Has("ClientArrays"))
            return "Legacy / compatibility-style OpenGL strongly indicated (fixed-function, display-list or client-array path).";
        return "OpenGL family could not be classified from LibKernel3D.dll symbols alone.";
    }

    private static Process? FindBazisProcess()
    {
        var all = Process.GetProcesses();
        return all.FirstOrDefault(p => Safe(() =>
            (p.MainWindowTitle ?? "").Contains("БАЗИС", StringComparison.OrdinalIgnoreCase) ||
            (p.MainWindowTitle ?? "").Contains("BAZIS", StringComparison.OrdinalIgnoreCase) ||
            p.ProcessName.Contains("bazis", StringComparison.OrdinalIgnoreCase), false));
    }

    private static string? FindKernelPath(Process? process)
    {
        if (process is not null)
        {
            try
            {
                foreach (ProcessModule m in process.Modules)
                    if (string.Equals(Path.GetFileName(m.FileName), "LibKernel3D.dll", StringComparison.OrdinalIgnoreCase))
                        return m.FileName;
            }
            catch { }
        }

        var candidates = new[]
        {
            @"C:\Program Files (x86)\BazisSoft\Bazis\LibKernel3D.dll",
            @"C:\Program Files\BazisSoft\Bazis\LibKernel3D.dll"
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static T Safe<T>(Func<T> f, T fallback)
    {
        try { return f(); } catch { return fallback; }
    }
}
