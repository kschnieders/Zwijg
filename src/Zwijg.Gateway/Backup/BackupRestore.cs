using Zwijg.Core.Backup;

namespace Zwijg.Gateway.Backup;

// Sicherung zurückspielen, bei beendetem Zwijg:
//   Zwijg.Gateway.exe --zurueckspielen D:\Sicherung\zwijg-sicherung-2026-10-04-020000.zwijg
// Das Passwort wird abgefragt oder kommt aus ZWIJG_SICHERUNG_PASSWORT.
// Der bisherige Stand wird vorher beiseitegelegt, es geht also nichts verloren.
public static class BackupRestore
{
    public const string Argument = "--zurueckspielen";

    public static string? RequestedFile(string[] args)
    {
        var i = Array.FindIndex(args, a => a.Equals(Argument, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : i >= 0 ? "" : null;
    }

    // Für die Kommandozeile: prüft, fragt nach dem Passwort und meldet in einfachen Worten
    public static int RunFromCommandLine(string file, IConfiguration config)
    {
        var gateway = config.GetSection("Zwijg").Get<GatewayOptions>() ?? new GatewayOptions();
        var dataDir = Path.GetDirectoryName(Path.GetFullPath(gateway.SettingsPath))!;

        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        {
            Console.Error.WriteLine($"Sicherungsdatei nicht gefunden: {file}");
            Console.Error.WriteLine($"Aufruf: Zwijg.Gateway {Argument} <Pfad zur .zwijg Datei>");
            return 1;
        }

        // Läuft Zwijg noch, würde es die Dateien gleich wieder überschreiben
        if (StartupChecks.FindBusyUrl(config["urls"]) is { } busy)
        {
            Console.Error.WriteLine($"Zwijg läuft noch ({StartupChecks.Shown(busy)}). Bitte zuerst beenden und dann noch einmal.");
            return 1;
        }

        var password = Environment.GetEnvironmentVariable("ZWIJG_SICHERUNG_PASSWORT") ?? ReadPassword();
        try
        {
            var aside = Restore(file, password, dataDir, Path.GetFullPath(gateway.Audit.DatabasePath));
            Console.WriteLine();
            Console.WriteLine($"Fertig. Die Sicherung {Path.GetFileName(file)} ist zurückgespielt, Zwijg kann wieder gestartet werden.");
            Console.WriteLine($"Der Stand davor liegt in {aside}");
            return 0;
        }
        catch (BackupArchive.WrongPasswordException ex)
        {
            Console.Error.WriteLine(ex.Message + " Es wurde nichts geändert.");
            return 1;
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine(ex.Message + " Es wurde nichts geändert.");
            return 1;
        }
    }

    // Packt erst vollständig aus und prüft, dann legt es den bisherigen Stand beiseite und spielt zurück.
    // Gibt den Ordner mit dem bisherigen Stand zurück.
    public static string Restore(string file, string password, string dataDir, string auditPath)
    {
        var temp = SecureTemp.CreateDirectory("zwijg-zurueck");
        var work = Path.Combine(temp, "dateien");
        try
        {
            BackupArchive.Extract(file, work, password, temp);
            if (!File.Exists(Path.Combine(work, "zwijg-sicherung.json")) || !File.Exists(Path.Combine(work, "data", "settings.json")))
                throw new InvalidDataException("In der Datei fehlen die Einstellungen, das ist keine vollständige Sicherung.");

            // Bisherigen Stand beiseitelegen. Sprachmodelle und Update Sicherungen bleiben, wo sie sind.
            var aside = Path.Combine(Path.GetDirectoryName(dataDir)!, $"{Path.GetFileName(dataDir)}-vor-wiederherstellung-{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.CreateDirectory(aside);
            if (Directory.Exists(dataDir))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(dataDir))
                {
                    var name = Path.GetFileName(entry);
                    if (name is "models" or UpdateSnapshot.FolderName)
                        continue;
                    var to = Path.Combine(aside, name);
                    if (Directory.Exists(entry)) Directory.Move(entry, to);
                    else File.Move(entry, to);
                }
            }
            foreach (var path in new[] { auditPath, auditPath + ".key", auditPath + ".kopf" })
            {
                if (File.Exists(path))
                    File.Move(path, Path.Combine(aside, "protokoll-" + Path.GetFileName(path)));
            }

            // Zurückspielen
            CopyDirectory(Path.Combine(work, "data"), dataDir);
            var audit = Path.Combine(work, "audit");
            Directory.CreateDirectory(Path.GetDirectoryName(auditPath)!);
            foreach (var (name, target) in new[] { ("audit.db", auditPath), ("audit.db.key", auditPath + ".key"), ("audit.db.kopf", auditPath + ".kopf") })
            {
                if (File.Exists(Path.Combine(audit, name)))
                    File.Copy(Path.Combine(audit, name), target, overwrite: true);
            }
            return aside;
        }
        finally
        {
            SecureTemp.Delete(temp);
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    // Passwort ohne Anzeige einlesen
    private static string ReadPassword()
    {
        Console.Write("Passwort der Sicherung: ");
        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? "";

        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Count > 0) chars.RemoveAt(chars.Count - 1);
                continue;
            }
            if (!char.IsControl(key.KeyChar))
                chars.Add(key.KeyChar);
        }
        Console.WriteLine();
        return new string(chars.ToArray());
    }
}
