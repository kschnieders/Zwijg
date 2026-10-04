using System.Text;
using System.Text.RegularExpressions;

namespace Zwijg.Core.Pseudonymization;

// Text, den jemand im Chat selbst als geheim markiert hat, mit der Bezeichnung für den Platzhalter (z.B. GEHEIM)
// Type: wie der Wert zählt, z.B. Name beim Patientenfeld. WholeWord: nur ganze Wörter, "Anna" nicht in "Annahme".
public sealed record SecretTerm(string Value, string Label, EntityType Type = EntityType.Custom, bool WholeWord = false);

public sealed class TextTooLongException() : Exception(Pseudonymizer.TooLongMessage);

public sealed class Pseudonymizer(IEnumerable<IPiiDetector> detectors, Func<IReadOnlyCollection<string>>? ignoredWords = null)
{
    // Höchstens so viele Zeichen pro Anfrage, alle Texte zusammen. Die Erkennung braucht rund 5 Sekunden
    // pro Million Zeichen, und mehr passt ohnehin in kaum ein Modell. Rund 150 Seiten Arztbrief.
    public const int MaxTextLength = 500_000;
    public const string TooLongMessage = "Der Text ist zu lang für die Prüfung, höchstens 500.000 Zeichen";

    private readonly IPiiDetector[] _detectors = detectors.ToArray();

    public static bool TooLong(IEnumerable<string> texts) => texts.Sum(t => (long)t.Length) > MaxTextLength;

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
        // Die Aufrufer prüfen das vorher mit einer eigenen Meldung, das hier ist nur die Absicherung
        if (TooLong(texts))
            throw new TextTooLongException();

        var ignored = new HashSet<string>(ignoredWords?.Invoke() ?? [], StringComparer.OrdinalIgnoreCase);
        var matchesPerText = new List<List<PiiMatch>>();
        var known = new Dictionary<string, EntityType>(StringComparer.Ordinal);

        foreach (var text in texts)
        {
            map.Reserve(text);

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
        // Auch in Großbuchstaben oder mit großem Anfangsbuchstaben, z.B. "MUSTERMANN" im Briefkopf, mit demselben Platzhalter.
        // Klein geschrieben nicht, sonst träfe "Herr Klein" auch jedes "klein". Aus "KOCH" wird kein "Koch",
        // wenn der Name auch ein normales Wort ist, sonst träfe es auch "Der Koch der Klinik".
        if (known.Count > 0)
        {
            var spellings = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var k in known.Keys)
            {
                var title = char.ToUpperInvariant(k[0]) + k[1..].ToLowerInvariant();
                foreach (var v in new[] { k, k.ToUpperInvariant(), title })
                {
                    if (v == title && v != k && Surnames.NeedsContext(v))
                        continue;
                    spellings.TryAdd(v, k);
                }
            }

            var anyKnown = new Regex(
                @"\b(?<n>" + string.Join("|", spellings.Keys.OrderByDescending(n => n.Length).Select(Regex.Escape)) + @")(?:s|S|')?\b",
                RegexOptions.CultureInvariant);

            for (var i = 0; i < texts.Count; i++)
            {
                // Direkte Treffer in anderer Schreibweise bekommen denselben Wert wie das erste Vorkommen
                matchesPerText[i] = matchesPerText[i]
                    .Select(m => m.Type is EntityType.Name or EntityType.City && spellings.TryGetValue(m.Value, out var first)
                        ? m with { Value = first } : m)
                    .ToList();

                foreach (Match m in anyKnown.Matches(texts[i]))
                {
                    var n = m.Groups["n"];
                    var first = spellings[n.Value];
                    matchesPerText[i].Add(new PiiMatch(known[first], n.Index, n.Length, first));
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
            output.Add(Replace(texts[i], ResolveOverlaps(matchesPerText[i], texts[i]), map));

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
                if (secret.WholeWord && !IsWholeWord(text, pos, value.Length))
                    continue;

                // Der Platzhalter gehört zum Geheimnis selbst, nicht zur Schreibweise an dieser Stelle
                result.Add(new PiiMatch(secret.Type, pos, value.Length, value, secret.Type == EntityType.Custom ? secret.Label : null));
            }
        }

        return result;
    }

    // Davor und dahinter kein Buchstabe und keine Ziffer. Ein "s" dahinter ist erlaubt, "Kowalczyks Befund".
    private static bool IsWholeWord(string text, int start, int length)
    {
        var end = start + length;
        if (start > 0 && char.IsLetterOrDigit(text[start - 1]))
            return false;
        if (end >= text.Length || !char.IsLetterOrDigit(text[end]))
            return true;
        return text[end] == 's' && (end + 1 >= text.Length || !char.IsLetterOrDigit(text[end + 1]));
    }

    // Überlappende Treffer werden zu einem zusammengefasst, damit kein Rest wie ".03.1980" stehen bleibt.
    // Der Typ mit der höchsten Sensibilität gewinnt, bei Gleichstand der frühere oder längere.
    private static List<PiiMatch> ResolveOverlaps(List<PiiMatch> matches, string text)
    {
        var kept = new List<PiiMatch>();

        foreach (var m in matches.Where(m => m.Length > 0).OrderBy(m => m.Start).ThenByDescending(m => m.Length).ThenBy(m => m.Type))
        {
            if (kept.Count == 0 || m.Start >= kept[^1].End)
            {
                kept.Add(m);
                continue;
            }

            var last = kept[^1];
            var winner = EntityTypeInfo.SensitivityOf(m.Type) > EntityTypeInfo.SensitivityOf(last.Type) ? m : last;
            kept[^1] = m.End > last.End
                ? new PiiMatch(winner.Type, last.Start, m.End - last.Start, text[last.Start..m.End], winner.Label)
                : last with { Type = winner.Type, Label = winner.Label };
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
