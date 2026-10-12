namespace Zwijg.Gateway;

// Prüft beim Start, ob Zwijg in seine Datenordner schreiben darf. Sonst läuft es scheinbar normal,
// aber jede Anfrage endet mit "Interner Fehler". Typischer Fall: ein altes Docker Volume, das noch root gehört.
public static class DataDirectoryCheck
{
    // Ordner müssen neue Dateien zulassen, vorhandene Dateien müssen sich ändern lassen
    public static void EnsureWritable(IEnumerable<string> directories, IEnumerable<string> files)
    {
        foreach (var dir in directories.Select(Path.GetFullPath).Distinct())
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, $".schreibtest-{Guid.NewGuid():N}");
                File.WriteAllBytes(probe, []);
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                throw NotWritable(dir, ex);
            }
        }

        foreach (var file in files.Where(File.Exists))
        {
            try
            {
                using var _ = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                throw NotWritable(file, ex);
            }
        }
    }

    // Unter Linux und macOS darf nur der Benutzer von Zwijg hinein. Ohne Zertifikat liegen in keys die Schlüssel im Klartext.
    public static void RestrictToOwner(IEnumerable<string> directories, ILogger logger)
    {
        if (OperatingSystem.IsWindows())
            return;

        var shared = new[] { Path.GetTempPath(), "/" }.Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)));
        foreach (var dir in directories.Select(d => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d))).Distinct())
        {
            if (shared.Contains(dir) || !Directory.Exists(dir))
                continue;
            try
            {
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                logger.LogWarning("Rechte von {Dir} ließen sich nicht einschränken. Bitte chmod 700 setzen.", dir);
            }
        }
    }

    private static InvalidOperationException NotWritable(string path, Exception inner) => new(
        $"Zwijg darf {path} nicht ändern. Der Benutzer, unter dem Zwijg läuft, braucht Schreibrechte auf den Datenordner und seine Dateien. " +
        "In Docker gehört ein älteres Volume meist noch root, dann einmalig: " +
        "docker compose run --rm --no-deps --user root --entrypoint chown zwijg -R 1654 /app/data (siehe docs/betrieb.md, Umstieg).",
        inner);
}
