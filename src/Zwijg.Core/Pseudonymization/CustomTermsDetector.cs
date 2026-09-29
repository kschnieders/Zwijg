using System.Text.RegularExpressions;

namespace Zwijg.Core.Pseudonymization;

// Eigene Listen der Praxis, z.B. Namen von Ärzten und Mitarbeitern oder Orte aus der Umgebung.
// Die Listen kommen über eine Funktion, damit Änderungen in der Oberfläche sofort gelten.
public sealed class CustomTermsDetector(Func<(IReadOnlyCollection<string> Names, IReadOnlyCollection<string> Places)> lists)
    : IPiiDetector
{
    private Cache? _cache;

    public Task<IReadOnlyList<PiiMatch>> DetectAsync(string text, CancellationToken ct = default)
    {
        var (names, places) = GetPatterns();
        var result = new List<PiiMatch>();

        if (names != null)
        {
            foreach (Match m in names.Matches(text))
                result.Add(new PiiMatch(EntityType.Name, m.Index, m.Length, m.Value));
        }

        if (places != null)
        {
            foreach (Match m in places.Matches(text))
                result.Add(new PiiMatch(EntityType.City, m.Index, m.Length, m.Value));
        }

        return Task.FromResult<IReadOnlyList<PiiMatch>>(result);
    }

    // Regex nur neu bauen, wenn sich die Listen geändert haben
    private (Regex? Names, Regex? Places) GetPatterns()
    {
        var (names, places) = lists();
        var hash = HashCode.Combine(string.Join('\n', names), string.Join('\n', places));

        var cache = _cache;
        if (cache == null || cache.Hash != hash)
        {
            cache = new Cache(hash, Build(SplitWords(names)), Build(places));
            _cache = cache;
        }

        return (cache.Names, cache.Places);
    }

    private sealed record Cache(int Hash, Regex? Names, Regex? Places);

    // Namen auch einzeln finden: "Anna Weber" soll auch "Frau Weber" treffen
    private static IEnumerable<string> SplitWords(IEnumerable<string> names) =>
        names.SelectMany(n => n.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(w => w.Length >= 2);

    private static Regex? Build(IEnumerable<string> terms)
    {
        var list = terms.Select(t => t.Trim()).Where(t => t.Length >= 2).Distinct().OrderByDescending(t => t.Length).ToList();
        if (list.Count == 0)
            return null;

        return new Regex(@"\b(?:" + string.Join("|", list.Select(Regex.Escape)) + @")\b",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }
}
