using System.Text.RegularExpressions;

namespace Zwijg.Core.Pseudonymization;

// Merkt sich pro Anfrage, welcher echte Wert zu welchem Platzhalter gehört.
// Lebt nur im Speicher und wird nie gespeichert oder geloggt.
public sealed class PseudonymMap
{
    private static readonly Regex PlaceholderPattern = new(
        @"\[\s*(?<label>[A-Z]+)_(?<n>\d+)\s*\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
        if (_byValue.TryGetValue(value, out var existing))
            return existing;

        label ??= EntityTypeInfo.Label(type);
        var n = _counter.GetValueOrDefault(label) + 1;
        _counter[label] = n;

        var placeholder = $"[{label}_{n}]";
        _byValue[value] = placeholder;
        _byPlaceholder[placeholder] = value;
        _typeCounts[type] = _typeCounts.GetValueOrDefault(type) + 1;

        return placeholder;
    }

    // Setzt die echten Werte wieder ein. Etwas tolerant, falls das Modell
    // Leerzeichen in die Klammern packt, z.B. "[ NAME_1 ]".
    public string Restore(string text)
    {
        if (_byPlaceholder.Count == 0 || string.IsNullOrEmpty(text))
            return text;

        return PlaceholderPattern.Replace(text, m =>
        {
            var key = $"[{m.Groups["label"].Value}_{m.Groups["n"].Value}]";
            return _byPlaceholder.TryGetValue(key, out var original) ? original : m.Value;
        });
    }

    public string Summary() =>
        string.Join(", ", _typeCounts.OrderBy(x => x.Key).Select(x => $"{EntityTypeInfo.Label(x.Key)}:{x.Value}")
            .Concat(_healthTerms.Count > 0 ? [$"GESUNDHEIT:{_healthTerms.Count}"] : []));
}
