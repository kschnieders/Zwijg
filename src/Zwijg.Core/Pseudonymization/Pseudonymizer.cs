using System.Text;
using System.Text.RegularExpressions;

namespace Zwijg.Core.Pseudonymization;

// Text, den jemand im Chat selbst als geheim markiert hat, mit der Bezeichnung für den Platzhalter (z.B. GEHEIM)
public sealed record SecretTerm(string Value, string Label);

public sealed class Pseudonymizer(IEnumerable<IPiiDetector> detectors, Func<IReadOnlyCollection<string>>? ignoredWords = null)
{
    private readonly IPiiDetector[] _detectors = detectors.ToArray();

    public static Pseudonymizer CreateDefault() =>
        new([new RegexPiiDetector(), new NameDetector(), new PlaceDetector()]);

    public async Task<string> PseudonymizeAsync(string text, PseudonymMap map, CancellationToken ct = default,
        IReadOnlyList<SecretTerm>? secrets = null)
    {
        var result = await PseudonymizeAsync([text], map, ct, secrets);
        return result[0];
    }

    // Alle Texte einer Anfrage zusammen verarbeiten (z.B. der ganze Chatverlauf),
    // damit derselbe Name überall denselben Platzhalter bekommt.
    public async Task<IReadOnlyList<string>> PseudonymizeAsync(
        IReadOnlyList<string> texts, PseudonymMap map, CancellationToken ct = default, IReadOnlyList<SecretTerm>? secrets = null)
    {
        var ignored = new HashSet<string>(ignoredWords?.Invoke() ?? [], StringComparer.OrdinalIgnoreCase);
        var matchesPerText = new List<List<PiiMatch>>();
        var known = new Dictionary<string, EntityType>(StringComparer.Ordinal);

        foreach (var text in texts)
        {
            var found = new List<PiiMatch>();
            foreach (var detector in _detectors)
                found.AddRange(await detector.DetectAsync(text, ct));

            found.RemoveAll(m => ignored.Contains(m.Value));

            foreach (var m in found.Where(m => m.Type is EntityType.Name or EntityType.City))
                known.TryAdd(m.Value, m.Type);

            matchesPerText.Add(found);
            map.AddHealthTerms(HealthTerms.Find(text));
        }

        // Einmal erkannte Namen und Orte auch an allen anderen Stellen ersetzen,
        // z.B. wenn später nur noch "Mustermann" ohne "Herr" davor steht, auch als "Mustermanns".
        if (known.Count > 0)
        {
            var anyKnown = new Regex(
                @"\b(?<n>" + string.Join("|", known.Keys.OrderByDescending(n => n.Length).Select(Regex.Escape)) + @")(?:s|')?\b",
                RegexOptions.CultureInvariant);

            for (var i = 0; i < texts.Count; i++)
            {
                foreach (Match m in anyKnown.Matches(texts[i]))
                {
                    var n = m.Groups["n"];
                    matchesPerText[i].Add(new PiiMatch(known[n.Value], n.Index, n.Length, n.Value));
                }
            }
        }

        // Selbst markierte Geheimnisse haben Vorrang: jede Fundstelle wird ersetzt,
        // und automatische Treffer, die sie berühren, fallen weg, damit kein Rest davon übrig bleibt.
        if (secrets is { Count: > 0 })
        {
            for (var i = 0; i < texts.Count; i++)
            {
                var hidden = FindSecrets(texts[i], secrets);
                if (hidden.Count == 0)
                    continue;

                matchesPerText[i].RemoveAll(m => hidden.Any(h => m.Start < h.End && h.Start < m.End));
                matchesPerText[i].AddRange(hidden);
            }
        }

        var output = new List<string>(texts.Count);
        for (var i = 0; i < texts.Count; i++)
            output.Add(Replace(texts[i], ResolveOverlaps(matchesPerText[i]), map));

        return output;
    }

    // Alle Fundstellen, egal ob groß oder klein geschrieben. Längere Geheimnisse zuerst,
    // damit "Projekt Nordlicht" nicht von "Nordlicht" zerteilt wird.
    private static List<PiiMatch> FindSecrets(string text, IReadOnlyList<SecretTerm> secrets)
    {
        var result = new List<PiiMatch>();
        foreach (var secret in secrets.Where(s => s.Value.Trim().Length >= 2).OrderByDescending(s => s.Value.Length))
        {
            var value = secret.Value.Trim();
            for (var pos = text.IndexOf(value, StringComparison.OrdinalIgnoreCase); pos >= 0;
                 pos = text.IndexOf(value, pos + value.Length, StringComparison.OrdinalIgnoreCase))
            {
                if (result.Any(r => pos < r.End && r.Start < pos + value.Length))
                    continue;

                // Der Platzhalter gehört zum Geheimnis selbst, nicht zur Schreibweise an dieser Stelle
                result.Add(new PiiMatch(EntityType.Custom, pos, value.Length, value, secret.Label));
            }
        }

        return result;
    }

    // Überlappende Treffer: der früheste gewinnt, bei gleichem Start der längste.
    private static List<PiiMatch> ResolveOverlaps(List<PiiMatch> matches)
    {
        var kept = new List<PiiMatch>();
        var end = -1;

        foreach (var m in matches.OrderBy(m => m.Start).ThenByDescending(m => m.Length).ThenBy(m => m.Type))
        {
            if (m.Start < end || m.Length == 0)
                continue;

            kept.Add(m);
            end = m.End;
        }

        return kept;
    }

    private static string Replace(string text, List<PiiMatch> matches, PseudonymMap map)
    {
        if (matches.Count == 0)
            return text;

        var sb = new StringBuilder(text.Length);
        var pos = 0;

        foreach (var m in matches)
        {
            sb.Append(text, pos, m.Start - pos);
            sb.Append(map.GetOrAdd(m.Type, m.Value, m.Label));
            pos = m.End;
        }

        sb.Append(text, pos, text.Length - pos);
        return sb.ToString();
    }
}
