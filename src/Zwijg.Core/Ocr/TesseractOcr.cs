using System.Diagnostics;
using System.Text;
using Docnet.Core;
using Docnet.Core.Models;

namespace Zwijg.Core.Ocr;

// TesseractPath leer: automatisch suchen. TessdataDir leer: Sprachdaten von Tesseract selbst.
public sealed record OcrOptions(string? TesseractPath, string? TessdataDir, string Languages, int MaxPages);

public sealed record OcrResult(string Text, int Pages, int PagesRead, string? Error);

public sealed record OcrStatus(bool Available, string? Path, string? Version, IReadOnlyList<string> Languages, string? Problem);

// Texterkennung für eingescannte PDFs und Fotos. Seiten werden mit PDFium als Bild gerendert
// (klappt mit JPEG, Fax und JBIG2), dann liest Tesseract den Text. Alles lokal.
// Die Zwischenbilder enthalten Patientendaten und liegen nur kurz in einem eigenen Ordner.
public static class TesseractOcr
{
    // Etwa 250 dpi, gut für Tesseract und noch nicht zu groß im Speicher
    private const double RenderScale = 3.5;
    private static readonly TimeSpan PageTimeout = TimeSpan.FromMinutes(2);

    // PDFium ist nicht für mehrere Threads gleichzeitig gebaut
    private static readonly SemaphoreSlim RenderLock = new(1, 1);

    public static async Task<OcrStatus> CheckAsync(OcrOptions o, CancellationToken ct = default)
    {
        var exe = Find(o.TesseractPath);
        if (exe == null)
            return new(false, null, null, [], "Tesseract wurde nicht gefunden");

        var version = await RunAsync(exe, ["--version"], ct);
        if (version.ExitCode != 0)
            return new(false, exe, null, [], "Tesseract lässt sich nicht starten");

        var list = await RunAsync(exe, WithTessdata(o, ["--list-langs"]), ct);
        var languages = list.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.Contains(' ') && !l.EndsWith(':')).ToList();
        var missing = o.Languages.Split('+').Where(l => !languages.Contains(l)).ToList();
        var firstLine = version.Output.Split('\n')[0].Trim();

        return missing.Count == 0
            ? new(true, exe, firstLine, languages, null)
            : new(false, exe, firstLine, languages, $"Sprachdaten fehlen: {string.Join(", ", missing)}");
    }

    // Fehlt eine Sprache, liest Tesseract still mit den übrigen weiter und meldet trotzdem Erfolg.
    // Deshalb vorher prüfen. Das Ergebnis gilt eine Minute, damit nicht jede Datei Tesseract dreimal startet.
    private static (OcrOptions Options, OcrStatus Status, DateTime At)? _lastCheck;

    private static async Task<(string? Exe, string? Error)> ReadyAsync(OcrOptions o, CancellationToken ct)
    {
        var last = _lastCheck;
        var status = last is { } l && l.Options == o && DateTime.UtcNow - l.At < TimeSpan.FromMinutes(1)
            ? l.Status
            : await CheckAsync(o, ct);
        _lastCheck = (o, status, DateTime.UtcNow);
        return status.Available ? (status.Path, null) : (null, status.Problem ?? "Tesseract ist nicht bereit");
    }

    public static async Task<OcrResult> ReadPdfAsync(byte[] pdf, OcrOptions o, CancellationToken ct = default)
    {
        var (exe, problem) = await ReadyAsync(o, ct);
        if (exe == null)
            return new("", 0, 0, problem);

        var dir = NewTempDir();
        try
        {
            var files = new List<string>();
            int pageCount;
            await RenderLock.WaitAsync(ct);
            try
            {
                using var doc = DocLib.Instance.GetDocReader(pdf, new PageDimensions(RenderScale));
                pageCount = doc.GetPageCount();
                for (var i = 0; i < Math.Min(pageCount, o.MaxPages); i++)
                {
                    ct.ThrowIfCancellationRequested();
                    using var page = doc.GetPageReader(i);
                    var file = Path.Combine(dir, $"seite-{i + 1}.pgm");
                    WriteGrayscale(file, page.GetImage(), page.GetPageWidth(), page.GetPageHeight());
                    files.Add(file);
                }
            }
            finally
            {
                RenderLock.Release();
            }

            var texts = new List<string>();
            foreach (var file in files)
            {
                var (text, error) = await RecognizeAsync(exe, file, o, ct);
                if (error != null)
                    return new("", pageCount, texts.Count, error);
                texts.Add(text);
            }

            return new(string.Join("\n\n", texts).Trim(), pageCount, texts.Count, null);
        }
        finally
        {
            DeleteQuietly(dir);
        }
    }

    // Fotos und Scans als Bilddatei, z.B. .jpg, .png oder .tif
    public static async Task<OcrResult> ReadImageAsync(byte[] image, string extension, OcrOptions o, CancellationToken ct = default)
    {
        var (exe, problem) = await ReadyAsync(o, ct);
        if (exe == null)
            return new("", 1, 0, problem);

        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "bild" + extension.ToLowerInvariant());
            await File.WriteAllBytesAsync(file, image, ct);
            var (text, error) = await RecognizeAsync(exe, file, o, ct);
            return new(text.Trim(), 1, error == null ? 1 : 0, error);
        }
        finally
        {
            DeleteQuietly(dir);
        }
    }

    private static async Task<(string Text, string? Error)> RecognizeAsync(string exe, string file, OcrOptions o, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PageTimeout);
        try
        {
            var r = await RunAsync(exe, WithTessdata(o, [file, "stdout", "-l", o.Languages, "--psm", "3"]), timeout.Token);
            return r.ExitCode == 0 ? (r.Output, null) : ("", "Tesseract konnte die Seite nicht lesen");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ("", "Die Texterkennung hat zu lange gedauert");
        }
    }

    private static List<string> WithTessdata(OcrOptions o, List<string> args)
    {
        if (!string.IsNullOrWhiteSpace(o.TessdataDir))
            args.InsertRange(0, ["--tessdata-dir", o.TessdataDir]);
        return args;
    }

    // PDFium liefert BGRA mit durchsichtigem Hintergrund. Für Tesseract auf Weiß legen und in Graustufen speichern (PGM).
    public static void WriteGrayscale(string file, byte[] bgra, int width, int height)
    {
        var gray = new byte[width * height];
        for (int i = 0, p = 0; i < gray.Length; i++, p += 4)
        {
            var lum = (bgra[p] * 29 + bgra[p + 1] * 150 + bgra[p + 2] * 77) >> 8;
            var alpha = bgra[p + 3];
            gray[i] = (byte)((lum * alpha + 255 * (255 - alpha)) / 255);
        }

        using var fs = File.Create(file);
        var header = Encoding.ASCII.GetBytes($"P5\n{width} {height}\n255\n");
        fs.Write(header);
        fs.Write(gray);
    }

    // Eingestellter Pfad, sonst Suchpfad, sonst der übliche Ort unter Windows
    public static string? Find(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured) ? configured : null;

        var name = OperatingSystem.IsWindows() ? "tesseract.exe" : "tesseract";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir.Trim('"'), name);
            if (dir.Length > 0 && File.Exists(candidate))
                return candidate;
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\Programs" })
            {
                var candidate = Path.Combine(root, "Tesseract-OCR", name);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string exe, List<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Tesseract startet nicht");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var errors = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        // Ältere Versionen schreiben die Sprachenliste auf den Fehlerkanal
        var text = await output;
        return (process.ExitCode, text.Length > 0 ? text : await errors);
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zwijg-ocr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteQuietly(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
