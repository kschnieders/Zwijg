using System.Text.RegularExpressions;

namespace Zwijg.Core.Pseudonymization;

// Merkt sich pro Anfrage, welcher echte Wert zu welchem Platzhalter gehört.
// Lebt nur im Speicher und wird nie gespeichert oder geloggt.
public sealed class PseudonymMap
{
    private static readonly Regex PlaceholderPattern = new(
        @"\[\s*(?<label>[A-Z]+)_(?<n>\d+)\s*\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Link- und Bildadressen: Markdown Ziele und Referenzen, <...> Links, Adressen mit :// oder //,
    // HTML Attribute mit Adressen und CSS url(). Die Liste ist nicht vollständig, der eigentliche Schutz
    // gegen nachgeladene Adressen liegt beim Client (z.B. CSP oder Anzeige als reiner Text).
    private static readonly Regex LinkPattern = new(
        @"\]\([ \t]*(?:<[^>\n]*>|[^)\s]*)" +
        @"|^[ ]{0,3}\[[^\]\n]+\]:(?>[ \t]*)(?:\r?\n(?>[ \t]*))?(?:<[^>\n]*>|\S+)" +
        @"|<[a-z][a-z0-9+.-]{0,63}:[^<>\s]*>" +
        @"|\b[a-z][a-z0-9+.-]{0,63}://[^\s<>""'`]*" +
        @"|(?<![\w:/])//[^\s<>""'`]*" +
        @"|\b(?:src|srcset|href|poster|background|data|action|formaction|ping|cite|longdesc|content)\s*=\s*(?:""[^""]*""|'[^']*'|[^\s>]*)" +
        @"|\burl\([^()]*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline,
        TimeSpan.FromMilliseconds(500));

    // Größere Nummern erreicht der Zähler nie, sie müssen nicht reserviert werden
    private const int MaxReserved = 1_000_000;

    private readonly Dictionary<string, string> _byValue = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _byPlaceholder = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _counter = new(StringComparer.Ordinal);
    private readonly Dictionary<EntityType, int> _typeCounts = [];

    public int Count => _byValue.Count;

    public IReadOnlyDictionary<EntityType, int> TypeCounts => _typeCounts;

    private readonly HashSet<string> _healthTerms = new(StringComparer.OrdinalIgnoreCase);

    // Gesundheitsbegriffe, die im Text vorkommen. Werden nicht ersetzt, erhöhen aber die Sensibilität.
    public IReadOnlyCollection<string> HealthTerms => _healthTerms;

    public void AddHealthTerms(IEnumerable<string> terms) => _healthTerms.UnionWith(terms);

    // Eine Person zusammen mit Gesundheitsdaten ist immer hoch sensibel (Art. 9 DSGVO).
    // Gesundheitsdaten ohne erkannte Person sind mindestens niedrig.
    public Sensitivity MaxSensitivity
    {
        get
        {
            var max = _typeCounts.Count == 0 ? Sensitivity.None : _typeCounts.Keys.Max(EntityTypeInfo.SensitivityOf);
            if (_healthTerms.Count == 0)
                return max;

            var identifiesPerson = _typeCounts.Keys.Any(t => t is not EntityType.Date);
            return identifiesPerson ? Sensitivity.High : (Sensitivity)Math.Max((int)max, (int)Sensitivity.Low);
        }
    }

    public string GetOrAdd(EntityType type, string value, string? label = null)
    {
        // Jeder Wert zählt einmal für die Sensibilität, auch wenn er per Seed aus einer früheren Runde kommt.
        // Sonst wäre "Herr Mustermann hat Diabetes" in der zweiten Runde keine Person mit Gesundheitsdaten mehr.
        if (_counted.Add(value))
            _typeCounts[type] = _typeCounts.GetValueOrDefault(type) + 1;

        if (_byValue.TryGetValue(value, out var existing))
            return existing;

        label ??= EntityTypeInfo.Label(type);
        var n = _counter.GetValueOrDefault(label) + 1;
        _counter[label] = n;

        var placeholder = $"[{label}_{n}]";
        _byValue[value] = placeholder;
        _byPlaceholder[placeholder] = value;

        return placeholder;
    }

    private readonly HashSet<string> _counted = new(StringComparer.Ordinal);

    // Alle Platzhalter mit ihrem echten Wert, z.B. für "Text schützen", wo das Zurücksetzen im Browser passiert
    public IReadOnlyDictionary<string, string> Entries => _byPlaceholder;

    // Übernimmt eine Zuordnung aus einer früheren Runde, damit derselbe Name wieder denselben Platzhalter bekommt
    // und neue Namen nicht dieselbe Nummer erhalten. Ungültige oder doppelte Einträge werden ignoriert.
    public void Seed(string placeholder, string value)
    {
        var m = PlaceholderPattern.Match(placeholder);
        if (!m.Success || m.Value != placeholder || string.IsNullOrEmpty(value)
            || _byPlaceholder.ContainsKey(placeholder) || _byValue.ContainsKey(value))
            return;

        var label = m.Groups["label"].Value;
        var n = int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture);
        _counter[label] = Math.Max(_counter.GetValueOrDefault(label), n);
        _byValue[value] = placeholder;
        _byPlaceholder[placeholder] = value;
    }

    // Platzhalter, die schon in der Eingabe stehen (z.B. aus "Text schützen"), belegen ihre Nummer.
    // Sonst bekäme ein neuer Name dieselbe Nummer und beim Zurücksetzen würden Patienten verwechselt.
    public void Reserve(string text)
    {
        foreach (Match m in PlaceholderPattern.Matches(text))
        {
            if (!int.TryParse(m.Groups["n"].Value, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var n) || n > MaxReserved)
                continue;

            var label = m.Groups["label"].Value;
            _counter[label] = Math.Max(_counter.GetValueOrDefault(label), n);
        }
    }

    // Setzt die echten Werte wieder ein. Etwas tolerant, falls das Modell
    // Leerzeichen in die Klammern packt, z.B. "[ NAME_1 ]".
    // In Links und Bildadressen bleibt der Platzhalter stehen. Sonst könnte das Modell echte Daten in eine
    // Adresse schreiben, die der Client beim Anzeigen lädt, z.B. ![x](https://example.com/?p=[NAME_1]).
    public string Restore(string text) => Restore(text, out _);

    // skipped ist true, wenn die Link-Erkennung zu lange brauchte und deshalb nichts eingesetzt wurde
    public string Restore(string text, out bool skipped)
    {
        skipped = false;
        if (_byPlaceholder.Count == 0 || string.IsNullOrEmpty(text))
            return text;

        MatchCollection links;
        try
        {
            links = LinkPattern.Matches(text);

            // Matches sucht erst bei Bedarf. Count erzwingt die Suche hier, damit ein Timeout im catch landet.
            _ = links.Count;
        }
        catch (RegexMatchTimeoutException)
        {
            // Lieber gar nichts einsetzen als echte Daten in einer Adresse
            skipped = true;
            return text;
        }

        // Replace geht von vorn nach hinten, also reicht ein Zeiger durch die Links
        var next = 0;
        return PlaceholderPattern.Replace(text, m =>
        {
            while (next < links.Count && links[next].Index + links[next].Length <= m.Index)
                next++;
            if (next < links.Count && links[next].Index < m.Index + m.Length)
                return m.Value;

            var key = $"[{m.Groups["label"].Value}_{m.Groups["n"].Value}]";
            return _byPlaceholder.TryGetValue(key, out var original) ? original : m.Value;
        });
    }

    public string Summary() =>
        string.Join(", ", _typeCounts.OrderBy(x => x.Key).Select(x => $"{EntityTypeInfo.Label(x.Key)}:{x.Value}")
            .Concat(_healthTerms.Count > 0 ? [$"GESUNDHEIT:{_healthTerms.Count}"] : []));
}
