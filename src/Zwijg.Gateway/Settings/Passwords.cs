using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Zwijg.Gateway.Settings;

// Passwörter nur als PBKDF2 Hash mit Salt. Format: pbkdf2-sha256$iterationen$salt$hash
public static partial class Passwords
{
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public const int MinLength = 10;

    // Für unbekannte Benutzer trotzdem einmal hashen, damit man an der Antwortzeit nichts erkennt
    private static readonly string Dummy = Hash("zwijg-dummy-passwort");

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        var parts = (stored ?? Dummy).Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations))
            return false;

        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return stored != null && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    // Lange Passwörter statt komplizierter Regeln, so wie es das BSI inzwischen auch empfiehlt
    public static string? Problem(string password, string username)
    {
        if (password.Length < MinLength)
            return $"Das Passwort braucht mindestens {MinLength} Zeichen";
        if (password.Length > 200)
            return "Das Passwort ist zu lang";
        if (password.Contains(username, StringComparison.OrdinalIgnoreCase) && username.Length >= 3)
            return "Das Passwort darf den Benutzernamen nicht enthalten";
        if (password.Distinct().Count() < 4)
            return "Das Passwort ist zu einfach";
        return null;
    }

    // Startpasswort, gut abzulesen und vorzulesen: keine 0/O oder 1/l
    public static string Generate()
    {
        const string chars = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var groups = Enumerable.Range(0, 3).Select(_ =>
            new string(Enumerable.Range(0, 4).Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)]).ToArray()));
        return string.Join("-", groups);
    }

    // Benutzername aus dem Anzeigenamen, z.B. "Dr. Anna Weber" wird "anna.weber"
    public static string UsernameFrom(string name)
    {
        var s = name.ToLowerInvariant()
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss")
            .Replace("dr.", "").Replace("prof.", "");
        s = NotAllowed().Replace(s.Trim(), ".").Trim('.');
        return s.Length >= 2 ? s[..Math.Min(40, s.Length)] : "benutzer";
    }

    public static readonly Regex UsernamePattern = new("^[a-z0-9][a-z0-9._-]{1,39}$", RegexOptions.Compiled);

    [GeneratedRegex(@"[^a-z0-9._-]+")]
    private static partial Regex NotAllowed();
}
