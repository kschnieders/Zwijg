using System.Text.RegularExpressions;

namespace Zwijg.Core.Pseudonymization;

// Gesundheitsdaten sind nach Art. 9 DSGVO besonders geschützt.
// Sie werden nicht ersetzt, denn die KI soll ja über die Krankheit sprechen können.
// Aber zusammen mit einer Person macht das die Anfrage hoch sensibel.
// Die Begriffe stehen in Data/medizin.txt, hier kommen nur Endungen, Dosierungen und ICD Codes dazu.
public static class HealthTerms
{
    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    private static readonly Lazy<Regex> Terms = new(() => new Regex(@"\b(?:" + string.Join("|", LoadTerms()) + @")\b", Opts));

    // Krankheiten und Eingriffe an der Endung erkennen, z.B. Gastritis, Neuralgie, Neuropathie, Appendektomie.
    // Mit Ausnahmen wie Nostalgie oder Sympathie, und mindestens drei Buchstaben davor.
    private static readonly Regex MedicalEnding = new(
        @"\b\w{3,}(?:itis|itiden|(?<!nost)algien?|ämien?|[oyi]pathien?|plasien?|ektomien?|tomien?|stomien?|skopien?|urie|" +
        @"rrhoen?|rrhö|plegien?|paresen?|penien?|zytosen?|trophien?|sklerosen?|stenosen?|lithiasis|osen?)\b",
        Opts);

    // Wirkstoffe an der Endung erkennen, z.B. Ramipril, Candesartan, Bisoprolol, Simvastatin, Pantoprazol.
    // Mindestens drei Buchstaben davor, sonst wäre "April" ein Medikament.
    private static readonly Regex DrugEnding = new(
        @"\b\w{3,}(?:pril|sartan|olol|statin|prazol|cillin|mycin|oxacin|azepam|dipin|gliptin|gliflozin|xaban|parin|" +
        @"tidin|setron|triptan|lukast|afil|umab|izumab|ximab|tinib|semid|thiazid|dronat|ciclovir|conazol|zolam|oxetin|" +
        @"alopram|tralin|iapin|zapin|peridon|tadin|fibrat|glinid|glitazon|cyclin|profen|coxib|terol|sonid|ason|olon)\b",
        Opts);

    // Dosierungen wie "400 mg" oder "20 IE"
    private static readonly Regex Dosage = new(
        @"\b\d+(?:[.,]\d+)?\s?(?:mg|µg|mcg|ml|IE|I\.E\.)\b",
        Opts);

    // ICD-10 Codes wie J06.9 oder I10
    private static readonly Regex IcdCode = new(
        @"\b[A-TV-Z][0-9]{2}(?:\.[0-9]{1,2})?[GVAZLRB]?\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> Find(string text)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var regex in new[] { Terms.Value, MedicalEnding, DrugEnding, IcdCode, Dosage })
        {
            foreach (Match m in regex.Matches(text))
                found.Add(m.Value);
        }

        return found.ToList();
    }

    // "wort" genau, "wort*" am Anfang, "*wort*" irgendwo im Wort
    private static IEnumerable<string> LoadTerms()
    {
        using var stream = typeof(HealthTerms).Assembly.GetManifestResourceStream("Zwijg.medizin.txt")
            ?? throw new InvalidOperationException("Liste medizin fehlt");
        using var reader = new StreamReader(stream);

        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            if (line.TrimStart().StartsWith('#'))
                continue;
            foreach (var t in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                terms.Add(t);
        }

        // Längere zuerst, damit "herzinfarkt" vor "herz" versucht wird
        return terms.OrderByDescending(t => t.Length).Select(t =>
        {
            var start = t.StartsWith('*') ? @"\w*" : "";
            var end = t.EndsWith('*') ? @"\w*" : "";
            return start + Regex.Escape(t.Trim('*')) + end;
        });
    }
}
