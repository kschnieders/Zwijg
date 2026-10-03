using System.Text.RegularExpressions;
using Zwijg.Core.Audit;
using Zwijg.Gateway.Settings;

namespace Zwijg.Gateway.Backup;

// Password leer lassen, um das gespeicherte zu behalten
public sealed record BackupInput(bool Enabled, string? Directory, string? Time, bool External, string? Password);

public static partial class BackupEndpoints
{
    public static void MapBackup(this WebApplication app)
    {
        var admin = app.MapGroup("/admin");

        admin.MapGet("/backup", (SettingsStore store, BackupService backup) =>
        {
            var b = store.Current.Backup;
            return Results.Ok(new
            {
                settings = new { b.Enabled, b.Directory, b.Time, b.External, b.KeepDays, b.KeepWeeks, hasPassword = b.PasswordProtected != null },
                state = backup.State,
                status = backup.Status,
                sameDisk = backup.SameDisk(b.Directory),
                dataDir = backup.DataDir,
                files = backup.Files(),
            });
        });

        admin.MapPut("/backup", async (BackupInput input, HttpContext ctx, SettingsStore store, BackupService backup, IAuditLog audit) =>
        {
            var current = store.Current.Backup;
            var directory = string.IsNullOrWhiteSpace(input.Directory) ? null : input.Directory.Trim().Trim('"');
            var time = string.IsNullOrWhiteSpace(input.Time) ? "02:00" : input.Time.Trim();
            var password = string.IsNullOrEmpty(input.Password) ? null : input.Password;

            if (!TimePattern().IsMatch(time))
                return Results.BadRequest(new { error = "Uhrzeit bitte als HH:MM angeben, zum Beispiel 02:00" });
            if (password != null && password.Length < BackupService.MinPasswordLength)
                return Results.BadRequest(new { error = $"Das Passwort braucht mindestens {BackupService.MinPasswordLength} Zeichen" });

            if (input.Enabled)
            {
                if (directory == null)
                    return Results.BadRequest(new { error = "Bitte einen Zielordner angeben" });
                if (!Path.IsPathFullyQualified(directory))
                    return Results.BadRequest(new { error = "Bitte den vollständigen Pfad angeben, zum Beispiel D:\\Sicherung oder /mnt/nas/zwijg" });
                if (password == null && current.PasswordProtected == null)
                    return Results.BadRequest(new { error = "Bitte ein Passwort für die Verschlüsselung festlegen" });
                if (Unwritable(directory) is { } error)
                    return Results.BadRequest(new { error });
            }

            var text = !input.Enabled ? input.External ? "Sicherung: die Praxis sichert selbst" : "Sicherung ausgeschaltet"
                : password != null && current.PasswordProtected != null ? $"Sicherung eingestellt, neues Passwort, Ziel {directory}"
                : $"Sicherung eingestellt, Ziel {directory}";

            return await AdminSettingsEndpoints.ChangeAsync(ctx, store, audit, text, s =>
            {
                s.Backup.Enabled = input.Enabled;
                s.Backup.External = !input.Enabled && input.External;
                s.Backup.Directory = directory;
                s.Backup.Time = time;
                if (password != null)
                    s.Backup.PasswordProtected = backup.Protect(password);
            });
        });

        admin.MapPost("/backup/run", async (HttpContext ctx, BackupService backup, CancellationToken ct) =>
        {
            var status = await backup.RunAsync(ApiKeyMiddleware.GetUser(ctx).Name, ct);
            return status.LastError != null && status.LastAttempt > status.LastSuccess.GetValueOrDefault()
                ? Results.Json(new { error = status.LastError, status }, statusCode: 500)
                : Results.Ok(new { status, files = backup.Files() });
        });
    }

    // Ordner anlegen und einmal hineinschreiben, damit Fehler gleich beim Speichern auffallen und nicht erst nachts
    private static string? Unwritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".zwijg-schreibtest-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return $"In den Ordner {directory} kann Zwijg nicht schreiben: {ex.Message}";
        }
    }

    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d$")]
    private static partial Regex TimePattern();
}
