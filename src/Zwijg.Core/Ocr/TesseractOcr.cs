using System.Diagnostics;
using System.Text;
using Docnet.Core;
using Docnet.Core.Models;
using Docnet.Core.Readers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

    // Höchstens rund 50 Megapixel pro Seite (A4 braucht etwa 9). Eine riesige Seite wird kleiner gerendert,
    // sonst belegt schon eine winzige PDF mit großer Seite Gigabytes an Speicher.
    public const double MaxPixelsPerPage = 50_000_000;
    private static readonly TimeSpan PageTimeout = TimeSpan.FromMinutes(2);

    // PDFium ist nicht für mehrere Threads gleichzeitig gebaut
    private static readonly SemaphoreSlim RenderLock = new(1, 1);

    // Setzt das Gateway beim Start
    public static ILogger Log { get; set; } = NullLogger.Instance;

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
    // Als Klasse gespeichert, damit parallele Anfragen nie einen halb geschriebenen Eintrag lesen.
    internal sealed record LastCheck(OcrOptions Options, OcrStatus Status, DateTime At);

    internal static LastCheck? _lastCheck;

    private static async Task<(string? Exe, string? Error)> ReadyAsync(OcrOptions o, CancellationToken ct)
    {
        var last = _lastCheck;
        OcrStatus status;
        if (last != null && last.Options == o && DateTime.UtcNow - last.At < TimeSpan.FromMinutes(1))
            status = last.Status;
        else
        {
            status = await CheckAsync(o, ct);
            _lastCheck = new(o, status, DateTime.UtcNow);
        }
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
            List<string> files;
            int pageCount;
            await RenderLock.WaitAsync(ct);
            try
            {
                files = RenderPages(pdf, o.MaxPages, dir, out pageCount, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new("", 0, 0, "Die Seiten der PDF ließen sich nicht als Bild lesen");
            }
            finally
            {
                RenderLock.Release();
            }

            var texts = new List<string>();
            foreach (var file in files)
            {
                // Zeigt dem Aufräumen beim Start einer zweiten Instanz, dass der Ordner noch benutzt wird
                try { Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                var (text, error) = await RecognizeAsync(exe, file, o, ct);
                if (error != null)
                    return new("", pageCount, texts.Count, error);
                texts.Add(text);
            }

            return new(string.Join("\n\n", texts).Trim(), pageCount, texts.Count, null);
        }
        finally
        {
            await DeleteQuietlyAsync(dir, Log);
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
            await DeleteQuietlyAsync(dir, Log);
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

    // Skalierung pro Seite: normal etwa 250 dpi, bei riesigen Seiten so viel kleiner, dass das Bild ins Budget passt
    public static double ScaleFor(double widthPt, double heightPt)
    {
        var area = widthPt * heightPt;
        return area > 0 ? Math.Min(RenderScale, Math.Sqrt(MaxPixelsPerPage / area)) : RenderScale;
    }

    // Seitengrößen in Punkt lesen, ohne etwas zu rendern, und daraus die Skalierung der ersten maxPages Seiten
    public static IReadOnlyList<double> PageScales(byte[] pdf, int maxPages, out int pageCount)
    {
        using var probe = DocLib.Instance.GetDocReader(pdf, new PageDimensions(1.0));
        pageCount = probe.GetPageCount();

        var scales = new List<double>();
        for (var i = 0; i < Math.Min(pageCount, maxPages); i++)
        {
            using var page = probe.GetPageReader(i);
            scales.Add(ScaleFor(page.GetPageWidth(), page.GetPageHeight()));
        }

        return scales;
    }

    // Rendert die ersten maxPages Seiten als Graustufenbild in dir. Docnet serialisiert PDFium selbst,
    // RenderLock sorgt nur dafür, dass nicht mehrere große Seitenbilder gleichzeitig im Speicher liegen.
    public static List<string> RenderPages(byte[] pdf, int maxPages, string dir, out int pageCount, CancellationToken ct = default)
    {
        var files = new List<string>();
        var scales = PageScales(pdf, maxPages, out pageCount);
        IDocReader? doc = null;
        var docScale = 0.0;
        try
        {
            for (var i = 0; i < scales.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                // Docnet skaliert pro Dokument. Bei anderer Seitengröße einen neuen Leser öffnen,
                // aber nur einen zur Zeit, sonst liegt die PDF mehrfach im Speicher.
                if (doc == null || scales[i] != docScale)
                {
                    doc?.Dispose();
                    doc = DocLib.Instance.GetDocReader(pdf, new PageDimensions(scales[i]));
                    docScale = scales[i];
                }

                using var page = doc.GetPageReader(i);
                var file = Path.Combine(dir, $"seite-{i + 1}.pgm");
                WriteGrayscale(file, page.GetImage(), page.GetPageWidth(), page.GetPageHeight());
                files.Add(file);
            }
        }
        finally
        {
            doc?.Dispose();
        }

        return files;
    }

    // PDFium liefert BGRA mit durchsichtigem Hintergrund. Für Tesseract auf Weiß legen und in Graustufen speichern (PGM).
    public static void WriteGrayscale(string file, byte[] bgra, int width, int height)
    {
        var pixels = checked(width * height);
        if (bgra.Length < checked(pixels * 4L))
            throw new ArgumentException("Bild ist kleiner als angegeben", nameof(bgra));

        var gray = new byte[pixels];
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

    private const string TempPrefix = "zwijg-ocr-";

    // Unter Linux sonst 0755 und auf einem gemeinsamen /tmp für andere Benutzer lesbar
    internal static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), TempPrefix + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(dir);
        else
            Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return dir;
    }

    // Nach einem Absturz oder harten Neustart bleiben Zwischenbilder liegen. Beim Start aufräumen.
    // Nur eigene Ordner, die älter als eine Stunde sind, damit ein zweiter laufender Zwijg nicht gestört wird.
    public static async Task<int> DeleteLeftoversAsync(ILogger log, string? tempDir = null)
    {
        List<DirectoryInfo> dirs;
        try
        {
            dirs = new DirectoryInfo(tempDir ?? Path.GetTempPath()).EnumerateDirectories(TempPrefix + "*").ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log.LogWarning("Temp-Ordner ließ sich nicht nach alten Zwischenbildern durchsuchen: {Error}", e.Message);
            return 0;
        }

        var deleted = 0;
        foreach (var dir in dirs)
        {
            var ours = dir.Name.Length == TempPrefix.Length + 32 && dir.Name[TempPrefix.Length..].All(char.IsAsciiHexDigitLower);
            // Verknüpfungen nie folgen, die könnten sonst woanders hinzeigen
            if (!ours || dir.LinkTarget != null || DateTime.UtcNow - dir.LastWriteTimeUtc < TimeSpan.FromHours(1))
                continue;
            if (await DeleteQuietlyAsync(dir.FullName, log))
                deleted++;
        }

        if (deleted > 0)
            log.LogInformation("{Count} alte Ordner der Texterkennung gelöscht", deleted);
        return deleted;
    }

    // Unter Windows hält Tesseract oder ein Virenscanner die Datei manchmal noch kurz offen. Deshalb mehrmals versuchen.
    internal static async Task<bool> DeleteQuietlyAsync(string dir, ILogger log)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt == 3)
                {
                    // Ein kaputter Logger darf das Ergebnis der Texterkennung nicht ersetzen
                    try
                    {
                        log.LogWarning("Zwischenbilder der Texterkennung ließen sich nicht löschen, nächster Versuch beim Start: {Dir} ({Error})",
                            dir, e.GetType().Name);
                    }
                    catch (Exception) { }
                    return false;
                }
            }

            await Task.Delay(attempt * 200);
        }
    }
}
