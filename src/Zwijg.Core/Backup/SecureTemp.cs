namespace Zwijg.Core.Backup;

// Arbeitsordner für unverschlüsselte Zwischenstände, etwa bei Sicherung und Zurückspielen.
// Unter Linux darf nur der eigene Benutzer hinein, sonst könnten andere Benutzer des Servers mitlesen.
public static class SecureTemp
{
    public static string CreateDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path); // Der Temp Ordner gehört unter Windows ohnehin nur dem Benutzer
        else
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    public static void Delete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Bleibt etwas liegen, räumt CleanupStale es beim nächsten Start weg
        }
    }

    // Reste nach einem Absturz mitten in einer Sicherung. Nur ältere als eine Stunde, eine zweite Instanz
    // von Zwijg, etwa der Probestart beim Update, könnte gerade selbst einen Ordner benutzen.
    public static void CleanupStale(string prefix)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), prefix + "-*"))
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) > TimeSpan.FromHours(1))
                    Delete(dir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
