namespace Zwijg.Core.Pseudonymization;

// Listen liegen als Textdateien unter Data und werden beim ersten Zugriff geladen.
internal static class WordLists
{
    public static HashSet<string> Load(string name, out HashSet<string> needsContext)
    {
        var all = new HashSet<string>(StringComparer.Ordinal);
        needsContext = new HashSet<string>(StringComparer.Ordinal);

        using var stream = typeof(WordLists).Assembly.GetManifestResourceStream($"Zwijg.{name}.txt")
            ?? throw new InvalidOperationException($"Liste {name} fehlt");
        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (line.StartsWith('~'))
            {
                line = line[1..];
                needsContext.Add(line);
            }

            all.Add(line);
        }

        return all;
    }
}

public static class FirstNames
{
    private static readonly Lazy<HashSet<string>> Names = new(() => WordLists.Load("vornamen", out _));

    public static bool Contains(string word) => Names.Value.Contains(word);
}

public static class Surnames
{
    private static readonly Lazy<(HashSet<string> All, HashSet<string> NeedsContext)> Data = new(() =>
    {
        var all = WordLists.Load("nachnamen", out var needsContext);
        return (all, needsContext);
    });

    public static bool Contains(string word) => Data.Value.All.Contains(word);

    // Namen wie Koch oder Wolf sind auch normale Wörter und zählen nur mit Zusammenhang
    public static bool NeedsContext(string word) => Data.Value.NeedsContext.Contains(word);
}

public static class Places
{
    // orte.txt ist handgepflegt und hat Vorrang, orte-alle.txt enthält alle Orte in Deutschland
    private static readonly Lazy<(HashSet<string> All, HashSet<string> NeedsContext, string[] Standalone)> Data = new(() =>
    {
        var curated = WordLists.Load("orte", out var curatedContext);
        var all = WordLists.Load("orte-alle", out var allContext);

        var needsContext = new HashSet<string>(curatedContext, StringComparer.Ordinal);
        needsContext.UnionWith(allContext.Where(p => !curated.Contains(p)));
        all.UnionWith(curated);

        return (all, needsContext, all.Where(p => !needsContext.Contains(p)).ToArray());
    });

    // Orte, die immer erkannt werden, auch ohne "in" oder "aus" davor
    public static IReadOnlyCollection<string> Standalone => Data.Value.Standalone;

    public static bool Contains(string word) => Data.Value.All.Contains(word);

    // Orte wie Essen oder Halle sind auch normale Wörter, kleine Dörfer heißen oft wie Nachnamen
    public static bool NeedsContext(string word) => Data.Value.NeedsContext.Contains(word);

    // Für die Namenserkennung: nur eindeutige Orte, sonst wäre jeder Nachname, der auch ein Dorf ist, kein Name mehr
    public static bool IsWellKnown(string word) => Contains(word) && !NeedsContext(word);
}
