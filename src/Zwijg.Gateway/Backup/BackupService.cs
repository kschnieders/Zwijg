using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Zwijg.Core.Audit;
using Zwijg.Core.Backup;
using Zwijg.Gateway.Settings;

namespace Zwijg.Gateway.Backup;

public sealed record BackupStatus(
    DateTimeOffset? LastSuccess = null,
    DateTimeOffset? LastAttempt = null,
    string? LastError = null,
    string? LastFile = null,
    long? LastSize = null);

public sealed record BackupFile(string Name, long Size, DateTimeOffset Created);

// Sichert einmal am Tag den Datenordner verschlüsselt in einen Ordner nach Wahl.
// Läuft im Hintergrund, holt eine verpasste Sicherung nach und räumt alte auf.
public sealed partial class BackupService(
    SettingsStore settings,
    IOptions<GatewayOptions> options,
    IDataProtectionProvider dataProtection,
    IAuditLog audit,
    ILogger<BackupService> logger) : BackgroundService
{
    public const int MinPasswordLength = 10;
    private const string Prefix = "zwijg-sicherung-";

    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly IDataProtector _protector = dataProtection.CreateProtector("Zwijg.Backup.Password");

    public string DataDir => Path.GetDirectoryName(Path.GetFullPath(options.Value.SettingsPath))!;
    private string AuditPath => Path.GetFullPath(options.Value.Audit.DatabasePath);
    private string StatusPath => Path.Combine(DataDir, "sicherung-status.json");

    public BackupStatus Status
    {
        get
        {
            try
            {
                return File.Exists(StatusPath)
                    ? JsonSerializer.Deserialize<BackupStatus>(File.ReadAllText(StatusPath), JsonSerializerOptions.Web) ?? new()
                    : new();
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                return new();
            }
        }
    }

    // ok, off, external (sichert anders), pending (eingerichtet, noch keine), failed, stale (zu alt)
    public string State
    {
        get
        {
            var b = settings.Current.Backup;
            if (b.External)
                return "external";
            if (!b.Enabled)
                return "off";

            var s = Status;
            if (s.LastError != null && (s.LastSuccess == null || s.LastAttempt > s.LastSuccess))
                return "failed";
            if (s.LastSuccess == null)
                return "pending";
            return DateTimeOffset.Now - s.LastSuccess > TimeSpan.FromDays(2) ? "stale" : "ok";
        }
    }

    public string Protect(string password) => _protector.Protect(password);

    // Liegt das Ziel auf derselben Platte wie die Daten, hilft die Sicherung bei einem Plattenausfall nicht
    public bool SameDisk(string? directory) =>
        OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(directory)
        && string.Equals(Path.GetPathRoot(Path.GetFullPath(directory)), Path.GetPathRoot(DataDir), StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<BackupFile> Files()
    {
        var dir = settings.Current.Backup.Directory;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return [];
        return Directory.GetFiles(dir, Prefix + "*" + BackupArchive.Extension)
            .Select(f => (File: f, Time: TimeOf(Path.GetFileName(f))))
            .Where(f => f.Time != null)
            .OrderByDescending(f => f.Time)
            .Select(f => new BackupFile(Path.GetFileName(f.File), new FileInfo(f.File).Length, f.Time!.Value))
            .ToList();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Nach dem Start erst kurz warten, dann jede Minute schauen, ob eine Sicherung fällig ist
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            do
            {
                var b = settings.Current.Backup;
                if (b.Enabled && !b.External && IsDue(DateTimeOffset.Now, b.Time, Status.LastSuccess, Status.LastAttempt))
                    await RunAsync("Zwijg", stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Zwijg wird beendet
        }
    }

    // Fällig, wenn die letzte geplante Zeit vorbei ist und seitdem keine Sicherung geklappt hat.
    // So wird auch nachgeholt, wenn der Rechner zur geplanten Zeit aus war.
    // Nach einem Fehler erst eine Stunde später wieder versuchen, nicht jede Minute.
    public static bool IsDue(DateTimeOffset now, string time, DateTimeOffset? lastSuccess, DateTimeOffset? lastAttempt)
    {
        if (!TimeSpan.TryParseExact(time, @"hh\:mm", CultureInfo.InvariantCulture, out var at))
            at = TimeSpan.FromHours(2);
        var today = new DateTimeOffset(now.Date + at, now.Offset);
        var lastPlanned = now >= today ? today : today.AddDays(-1);

        if (lastSuccess >= lastPlanned)
            return false;
        return lastAttempt == null || now - lastAttempt >= TimeSpan.FromHours(1);
    }

    // Eine Sicherung anlegen. Gibt den neuen Stand zurück, Fehler stehen darin, nicht als Ausnahme.
    public async Task<BackupStatus> RunAsync(string by, CancellationToken ct)
    {
        await _running.WaitAsync(ct);
        var started = DateTimeOffset.Now;
        try
        {
            var b = settings.Current.Backup;
            if (string.IsNullOrWhiteSpace(b.Directory) || b.PasswordProtected == null)
                throw new InvalidOperationException("Sicherung ist nicht eingerichtet, Zielordner oder Passwort fehlt.");

            var password = _protector.Unprotect(b.PasswordProtected);
            Directory.CreateDirectory(b.Directory);
            var name = $"{Prefix}{started:yyyy-MM-dd-HHmmss}{BackupArchive.Extension}";
            var target = Path.Combine(b.Directory, name);

            var work = Path.Combine(Path.GetTempPath(), $"zwijg-sicherung-{Guid.NewGuid():N}");
            try
            {
                var files = await Task.Run(() => Snapshot(work), ct);
                await Task.Run(() => BackupArchive.Create(target, files, password), ct);
            }
            finally
            {
                if (Directory.Exists(work))
                    Directory.Delete(work, recursive: true);
            }

            var size = new FileInfo(target).Length;
            foreach (var old in ToDelete(Files().Select(f => (f.Name, f.Created)), DateTimeOffset.Now, b.KeepDays, b.KeepWeeks))
                File.Delete(Path.Combine(b.Directory, old));

            var status = new BackupStatus(started, started, null, name, size);
            SaveStatus(status);
            await WriteAuditAsync(by, $"Sicherung erstellt: {name} ({size / 1024.0 / 1024.0:0.0} MB)", ct);
            logger.LogInformation("Sicherung erstellt: {File}", target);
            return status;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = ex switch
            {
                UnauthorizedAccessException => "Keine Schreibrechte im Zielordner.",
                System.Security.Cryptography.CryptographicException => "Das gespeicherte Passwort lässt sich nicht mehr lesen. Bitte neu eintragen.",
                _ => ex.Message,
            };
            var status = Status with { LastAttempt = started, LastError = message };
            SaveStatus(status);
            await WriteAuditAsync(by, "Sicherung fehlgeschlagen: " + message, ct);
            logger.LogError(ex, "Sicherung fehlgeschlagen");
            return status;
        }
        finally
        {
            _running.Release();
        }
    }

    // Datenbanken im laufenden Betrieb sauber kopieren, den Rest einfach so.
    // Namen mit "data/" kommen in den Datenordner, "audit/" zum Protokoll.
    private Dictionary<string, string> Snapshot(string work)
    {
        Directory.CreateDirectory(work);
        var files = new Dictionary<string, string>();

        void Copy(string name, string path)
        {
            if (!File.Exists(path))
                return;
            var copy = Path.Combine(work, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = File.Create(copy))
                input.CopyTo(output);
            files[name] = copy;
        }

        void Database(string name, string path)
        {
            if (!File.Exists(path))
                return;
            var copy = Path.Combine(work, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            using (var source = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False"))
            using (var target = new SqliteConnection($"Data Source={copy};Pooling=False"))
            {
                source.Open();
                target.Open();
                source.BackupDatabase(target);
            }
            files[name] = copy;
        }

        Copy("data/settings.json", Path.Combine(DataDir, "settings.json"));
        Copy("data/.version", Path.Combine(DataDir, ".version"));
        Database("data/history.db", Path.Combine(DataDir, "history.db"));
        var keys = Path.Combine(DataDir, "keys");
        if (Directory.Exists(keys))
        {
            foreach (var key in Directory.GetFiles(keys))
                Copy("data/keys/" + Path.GetFileName(key), key);
        }

        Database("audit/audit.db", AuditPath);
        Copy("audit/audit.db.key", AuditPath + ".key");
        Copy("audit/audit.db.kopf", AuditPath + ".kopf");

        var info = Path.Combine(work, "zwijg-sicherung.json");
        File.WriteAllText(info, JsonSerializer.Serialize(new { version = Endpoints.Version, created = DateTimeOffset.Now }, JsonSerializerOptions.Web));
        files["zwijg-sicherung.json"] = info;
        return files;
    }

    // Behalten: die neueste je Tag der letzten Tage und die neueste je Woche der letzten Wochen
    public static IEnumerable<string> ToDelete(IEnumerable<(string Name, DateTimeOffset Created)> files, DateTimeOffset now, int days, int weeks)
    {
        var list = files.OrderByDescending(f => f.Created).ToList();
        var keep = new HashSet<string>();

        foreach (var day in list.Where(f => f.Created.Date > now.Date.AddDays(-days)).GroupBy(f => f.Created.Date))
            keep.Add(day.First().Name);

        foreach (var week in list.Where(f => f.Created.Date > now.Date.AddDays(-7 * weeks))
                     .GroupBy(f => ISOWeek.GetYear(f.Created.Date) * 100 + ISOWeek.GetWeekOfYear(f.Created.Date)))
            keep.Add(week.First().Name);

        // Die neueste bleibt immer, auch wenn sie sehr alt ist
        if (list.Count > 0)
            keep.Add(list[0].Name);

        return list.Where(f => !keep.Contains(f.Name)).Select(f => f.Name);
    }

    private static DateTimeOffset? TimeOf(string name) =>
        FileName().Match(name) is { Success: true } m
        && DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t)
            ? new DateTimeOffset(t)
            : null;

    [GeneratedRegex(@"^zwijg-sicherung-(\d{4}-\d{2}-\d{2}-\d{6})\.zwijg$")]
    private static partial Regex FileName();

    private void SaveStatus(BackupStatus status)
    {
        try
        {
            File.WriteAllText(StatusPath, JsonSerializer.Serialize(status, JsonSerializerOptions.Web));
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Stand der Sicherung nicht gespeichert");
        }
    }

    private async Task WriteAuditAsync(string by, string text, CancellationToken ct)
    {
        try
        {
            await audit.WriteAsync(new AuditEntry { User = by, Action = "admin", Reason = text }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Sicherung nicht im Protokoll vermerkt");
        }
    }
}
