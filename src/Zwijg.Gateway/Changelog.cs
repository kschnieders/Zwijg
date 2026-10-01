using System.Text.RegularExpressions;

namespace Zwijg.Gateway;

// Liest CHANGELOG.md. Jede Version hat einen Abschnitt "## 1.2.0 (Datum)" mit Unterabschnitten wie "### Sicherheit".
public static partial class Changelog
{
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md");

    // Text unter der Überschrift der Version, ohne die Überschrift selbst. null, wenn es die Version nicht gibt.
    public static string? Section(string markdown, string version)
    {
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var start = Array.FindIndex(lines, l => VersionHeading().Match(l) is { Success: true } m && m.Groups[1].Value == version);
        if (start < 0)
            return null;

        var end = Array.FindIndex(lines, start + 1, l => l.StartsWith("## "));
        var body = lines[(start + 1)..(end < 0 ? lines.Length : end)];
        return string.Join("\n", body).Trim();
    }

    public static string? ForInstalledVersion()
    {
        try
        {
            return File.Exists(FilePath) ? Section(File.ReadAllText(FilePath), Endpoints.Version) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    // Ein Abschnitt "### Sicherheit" heißt: Diese Version schließt Lücken und sollte bald eingespielt werden
    public static bool IsSecurity(string? notes) => notes != null && SecurityHeading().IsMatch(notes);

    [GeneratedRegex(@"^##\s+v?(\d+\.\d+\.\d+)\b")]
    private static partial Regex VersionHeading();

    [GeneratedRegex(@"^#{2,4}\s+Sicherheit\b", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex SecurityHeading();
}
