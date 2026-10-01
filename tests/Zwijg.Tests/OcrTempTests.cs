using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Zwijg.Core.Ocr;

namespace Zwijg.Tests;

// Läuft nur unter Windows, dort sperrt eine offene Datei das Löschen
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Nur unter Windows sperren offene Dateien das Löschen";
    }
}

// Läuft nur unter Linux und macOS, dort zählen die Dateirechte des Ordners
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Unix-Dateirechte gibt es nur unter Linux und macOS";
    }
}

// Zwischenbilder der Texterkennung enthalten Patientendaten und dürfen nicht liegen bleiben.
// Gleiche Gruppe wie OcrTests, weil beide den Status-Cache der Texterkennung benutzen.
[Collection("Texterkennung")]
public class OcrTempTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("zwijg-test-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string MakeDir(string name, TimeSpan age)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "seite-1.pgm"), "Max Mustermann");
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow - age);
        return dir;
    }

    [Fact]
    public async Task Alte_Ordner_der_Texterkennung_werden_beim_Start_geloescht()
    {
        var old = MakeDir("zwijg-ocr-" + Guid.NewGuid().ToString("N"), TimeSpan.FromHours(3));
        var running = MakeDir("zwijg-ocr-" + Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(5));
        var foreign = MakeDir("zwijg-ocr-notizen", TimeSpan.FromHours(3));

        var deleted = await TesseractOcr.DeleteLeftoversAsync(new ListLogger(), _root);

        Assert.Equal(1, deleted);
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(running));
        Assert.True(Directory.Exists(foreign));
    }

    [WindowsFact]
    public async Task Kurz_gesperrte_Datei_wird_beim_zweiten_Versuch_geloescht()
    {
        var dir = MakeDir("zwijg-ocr-" + Guid.NewGuid().ToString("N"), TimeSpan.Zero);
        var locked = new FileStream(Path.Combine(dir, "seite-1.pgm"), FileMode.Open, FileAccess.Read, FileShare.None);
        _ = Task.Delay(100).ContinueWith(_ => locked.Dispose());

        var log = new ListLogger();
        Assert.True(await TesseractOcr.DeleteQuietlyAsync(dir, log));
        Assert.False(Directory.Exists(dir));
        Assert.Empty(log.Warnings);
    }

    [WindowsFact]
    public async Task Nicht_loeschbare_Zwischenbilder_landen_im_Log()
    {
        var dir = MakeDir("zwijg-ocr-" + Guid.NewGuid().ToString("N"), TimeSpan.Zero);
        var log = new ListLogger();
        using (new FileStream(Path.Combine(dir, "seite-1.pgm"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(await TesseractOcr.DeleteQuietlyAsync(dir, log));
        }

        Assert.Contains(log.Warnings, w => w.Contains(dir));
    }

    [WindowsFact]
    public async Task Kaputter_Logger_bricht_das_Loeschen_nicht_ab()
    {
        var dir = MakeDir("zwijg-ocr-" + Guid.NewGuid().ToString("N"), TimeSpan.Zero);
        using (new FileStream(Path.Combine(dir, "seite-1.pgm"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(await TesseractOcr.DeleteQuietlyAsync(dir, new ThrowingLogger()));
        }
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void Ordner_fuer_Zwischenbilder_gehoert_nur_dem_eigenen_Benutzer()
    {
        var dir = TesseractOcr.NewTempDir();
        try
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    [Fact]
    public async Task Status_wird_nach_einer_Minute_neu_geprueft_auch_bei_staendiger_Nutzung()
    {
        // Tesseract gibt es unter diesem Pfad nicht, die echte Prüfung meldet sofort "nicht gefunden"
        var o = new OcrOptions(Path.Combine(_root, "tesseract.exe"), null, "deu", 5);
        var stale = new OcrStatus(false, null, null, [], "Alter Eintrag");
        try
        {
            // Eintrag kurz vor Ablauf: Der erste Aufruf nutzt ihn noch
            TesseractOcr._lastCheck = new(o, stale, DateTime.UtcNow - TimeSpan.FromSeconds(59.5));
            Assert.Equal("Alter Eintrag", (await TesseractOcr.ReadImageAsync([1, 2, 3], ".png", o)).Error);

            // Der Treffer darf die Minute nicht verlängern
            await Task.Delay(1000);
            Assert.Equal("Tesseract wurde nicht gefunden", (await TesseractOcr.ReadImageAsync([1, 2, 3], ".png", o)).Error);
        }
        finally
        {
            TesseractOcr._lastCheck = null;
        }
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            throw new ObjectDisposedException("LoggerFactory");
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                lock (Warnings)
                    Warnings.Add(formatter(state, exception));
        }
    }
}
