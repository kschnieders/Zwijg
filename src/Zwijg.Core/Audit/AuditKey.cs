using System.Security.Cryptography;

namespace Zwijg.Core.Audit;

// Geheimer Schlüssel für die Hashkette im Protokoll. Er liegt in einer eigenen Datei, nie in der Datenbank.
// Wer nur die Datenbank ändern kann, kann damit keine passenden Hashes mehr ausrechnen.
public static class AuditKey
{
    public const int Length = 32;

    // protect/unprotect verschlüsseln den Schlüssel in der Datei, z.B. mit Data Protection. Ohne steht er im Klartext drin.
    public static byte[] LoadOrCreate(string path, Func<string, string>? protect = null, Func<string, string>? unprotect = null)
    {
        if (File.Exists(path))
        {
            var text = File.ReadAllText(path).Trim();
            try
            {
                // Genau 32 Bytes. Ein verschlüsselter Schlüssel, der zufällig auch als Base64 durchgeht, fällt so auf.
                var key = Convert.FromBase64String(unprotect == null ? text : unprotect(text));
                if (key.Length == Length)
                    return key;
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                throw new InvalidOperationException($"Der Schlüssel für das Protokoll in {path} ist nicht lesbar.", ex);
            }

            throw new InvalidOperationException($"Der Schlüssel für das Protokoll in {path} hat nicht die richtige Länge.");
        }

        var created = RandomNumberGenerator.GetBytes(Length);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // CreateNew, damit nie ein vorhandener Schlüssel überschrieben wird. Unter Linux gleich nur für den eigenen Benutzer lesbar.
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        var plain = Convert.ToBase64String(created);
        var content = protect == null ? plain : protect(plain);
        using (var writer = new StreamWriter(path, options))
            writer.Write(content);

        return created;
    }
}
