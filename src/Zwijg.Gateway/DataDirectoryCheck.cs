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

    private static InvalidOperationException NotWritable(string path, Exception inner) => new(
        $"Zwijg darf {path} nicht ändern. Der Benutzer, unter dem Zwijg läuft, braucht Schreibrechte auf den Datenordner und seine Dateien. " +
        "In Docker gehört ein älteres Volume meist noch root, dann einmalig: " +
        "docker compose run --rm --no-deps --user root --entrypoint chown zwijg -R 1654 /app/data (siehe docs/betrieb.md, Umstieg).",
        inner);
}
