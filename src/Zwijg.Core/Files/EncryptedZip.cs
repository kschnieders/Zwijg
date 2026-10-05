using ICSharpCode.SharpZipLib.Zip;

namespace Zwijg.Core.Files;

// Verschlüsselte ZIP Dateien zum Verschicken, mit AES-256. Lassen sich mit 7-Zip, WinRAR und auf dem Mac öffnen.
// Damit auch die Dateinamen geschützt sind, liegt im verschlüsselten ZIP nur ein inneres ZIP "dokumente.zip".
// Die Dateien gehen direkt beim Lesen hinein, es liegt nie eine unverschlüsselte Kopie auf der Platte.
public sealed class EncryptedZipWriter : IDisposable
{
    public const string InnerName = "dokumente.zip";

    private readonly ZipOutputStream _outer;
    private readonly ZipOutputStream _inner;
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

    public long Bytes { get; private set; }
    public int Count => _names.Count;

    public EncryptedZipWriter(Stream output, string password)
    {
        _outer = new ZipOutputStream(output) { IsStreamOwner = false, Password = password };
        // Röntgenbilder und PDFs lassen sich kaum packen, außen also nicht noch einmal
        _outer.SetLevel(0);
        _outer.PutNextEntry(new ZipEntry(InnerName) { AESKeySize = 256, DateTime = DateTime.Now });
        _inner = new ZipOutputStream(_outer) { IsStreamOwner = false };
    }

    // Gibt den Namen zurück, unter dem die Datei im Archiv liegt. Gleiche Namen bekommen eine Nummer.
    public string Add(string fileName, Stream content, long maxTotalBytes)
    {
        var name = UniqueName(SafeName(fileName));
        _inner.PutNextEntry(new ZipEntry(name) { DateTime = DateTime.Now });

        var buffer = new byte[81920];
        int read;
        while ((read = content.Read(buffer, 0, buffer.Length)) > 0)
        {
            Bytes += read;
            if (Bytes > maxTotalBytes)
                throw new FileTooLargeException();
            _inner.Write(buffer, 0, read);
        }
        _inner.CloseEntry();
        return name;
    }

    public void Finish()
    {
        _inner.Finish();
        _outer.CloseEntry();
        _outer.Finish();
    }

    public void Dispose()
    {
        _inner.Dispose();
        _outer.Dispose();
    }

    // Nur der Dateiname, keine Ordner und keine Steuerzeichen
    public static string SafeName(string fileName)
    {
        var name = Path.GetFileName(fileName.Replace('\\', '/').Split('/').Last());
        name = new string(name.Where(c => !char.IsControl(c) && c is not ':' and not '*' and not '?' and not '"' and not '<' and not '>' and not '|').ToArray()).Trim(' ', '.');
        return name.Length == 0 ? "datei" : name.Length > 150 ? name[..150] : name;
    }

    private string UniqueName(string name)
    {
        var candidate = name;
        for (var n = 2; !_names.Add(candidate); n++)
            candidate = $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}";
        return candidate;
    }
}

public sealed class FileTooLargeException() : Exception("Die Dateien sind zusammen zu groß.");

public sealed class WrongZipPasswordException() : Exception("Das Kennwort ist falsch.");

// Verschlüsselte ZIP wieder öffnen, auch solche aus 7-Zip oder WinRAR
public static class EncryptedZipReader
{
    // Was herauskommt: eine einzelne Datei unter ihrem Namen, sonst ein normales ZIP
    public sealed record Result(string FileName, Func<Stream, CancellationToken, Task> WriteTo);

    // input muss vollständig vorliegen (Datei oder Speicher). Prüft das Kennwort, bevor etwas geschrieben wird.
    public static Result Open(Stream input, string password)
    {
        var zip = new ZipFile(input, leaveOpen: true) { Password = password };
        var entries = zip.Cast<ZipEntry>().Where(e => e.IsFile).ToList();
        if (entries.Count == 0)
            throw new InvalidDataException("Im Archiv sind keine Dateien.");

        // Kennwort gleich am ersten Eintrag prüfen, ein Fehler kommt sonst erst mitten im Herunterladen
        try
        {
            using var probe = zip.GetInputStream(entries[0]);
            probe.ReadByte();
        }
        catch (ZipException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
                                      || ex.Message.Contains("passwort", StringComparison.OrdinalIgnoreCase)
                                      || ex.Message.Contains("AES", StringComparison.OrdinalIgnoreCase))
        {
            throw new WrongZipPasswordException();
        }

        // Aus Zwijg: genau ein inneres ZIP, das kommt so wie es ist heraus
        if (entries.Count == 1)
        {
            var only = entries[0];
            var name = EncryptedZipWriter.SafeName(only.Name);
            return new Result(name, async (output, ct) =>
            {
                await using var stream = zip.GetInputStream(only);
                await stream.CopyToAsync(output, ct);
            });
        }

        // Mehrere Dateien, etwa aus 7-Zip: als normales ZIP ohne Kennwort
        return new Result(EncryptedZipWriter.InnerName, (output, ct) =>
        {
            using var plain = new ZipOutputStream(output) { IsStreamOwner = false };
            // Ordner fallen weg, gleiche Namen bekommen eine Nummer
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                var name = EncryptedZipWriter.SafeName(entry.Name);
                for (var n = 2; !used.Add(name); n++)
                    name = $"{Path.GetFileNameWithoutExtension(EncryptedZipWriter.SafeName(entry.Name))} ({n}){Path.GetExtension(entry.Name)}";
                plain.PutNextEntry(new ZipEntry(name) { DateTime = entry.DateTime });
                using var stream = zip.GetInputStream(entry);
                stream.CopyTo(plain);
                plain.CloseEntry();
            }
            plain.Finish();
            return Task.CompletedTask;
        });
    }
}
