namespace Zwijg.Gateway;

// Startet eine andere Version als beim letzten Mal, wird der Datenordner vorher gesichert. Die neue Version stellt
// Einstellungen und Datenbanken beim Start auf ihr Format um, danach kann die alte sie nicht mehr öffnen.
// Mit der Sicherung geht es zurück, auch in Docker, wo kein Update Skript läuft.
public static class UpdateSnapshot
{
    public const string FolderName = "sicherungen";
    public const int Keep = 3;
    private const string Marker = ".version";

    // Nicht mitsichern: die Sicherungen selbst und die großen Sprachmodelle, die ein Update nicht ändert
    private static readonly string[] Skip = [FolderName, "models"];

    // Gibt den Ordner der neuen Sicherung zurück oder null, wenn keine nötig war
    public static string? CreateIfVersionChanged(string dataDir, string version, IEnumerable<string> extraFiles, bool skip, ILogger log)
    {
        var marker = Path.Combine(dataDir, Marker);
        var last = File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
        if (last == version)
            return null;

        string? target = null;

        // Bei einer neuen Installation gibt es noch nichts zu sichern
        if (!skip && File.Exists(Path.Combine(dataDir, "settings.json")))
        {
            var name = $"{DateTime.Now:yyyyMMdd-HHmmss}-vor-{Safe(version)}" + (last != null ? $"-von-{Safe(last)}" : "");
            target = Path.Combine(dataDir, FolderName, name);
            CopyDirectory(dataDir, target, top: true);

            // Liegt das Protokoll woanders, kommt es mit in die Sicherung
            foreach (var file in extraFiles.Where(f => File.Exists(f) && !Path.GetFullPath(f).StartsWith(Path.GetFullPath(dataDir) + Path.DirectorySeparatorChar)))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));

            Prune(Path.Combine(dataDir, FolderName));
            log.LogInformation("Neue Version {Version}, die Daten von {Last} sind vorher gesichert in {Path}", version, last ?? "unbekannt", target);
        }

        File.WriteAllText(marker, version);
        return target;
    }

    private static void CopyDirectory(string source, string target, bool top)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source))
        {
            if (top && Skip.Contains(Path.GetFileName(dir)))
                continue;
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)), top: false);
        }
    }

    // Die neuesten behalten, die Namen beginnen mit Datum und Uhrzeit
    private static void Prune(string folder)
    {
        foreach (var old in Directory.GetDirectories(folder).OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(Keep))
            Directory.Delete(old, recursive: true);
    }

    private static string Safe(string version) =>
        new(version.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_').ToArray());
}
