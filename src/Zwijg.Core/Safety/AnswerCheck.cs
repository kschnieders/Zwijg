using System.Globalization;
using System.Text.RegularExpressions;
using Zwijg.Core.Pseudonymization;

namespace Zwijg.Core.Safety;

// Eine Stelle in der Antwort, die so nicht in der Frage stand. Key fasst gleiche Angaben zusammen,
// z.B. "1 g" und "1000 mg".
public sealed record AnswerFinding(string Kind, string Text, int Start, int Length, string Key);

// Sucht in der Antwort nach Wirkstoffen, Dosierungen, Einnahmeschemata und Laborwerten, die in der Frage
// nicht vorkamen. Modelle erfinden solche Angaben manchmal. Das ersetzt keine fachliche Prüfung,
// lenkt den Blick aber auf die Stellen, die man vor dem Übernehmen prüfen sollte.
public static class AnswerCheck
{
    public const string Drug = "Wirkstoff";
    public const string Dose = "Dosierung";
    public const string Schedule = "Einnahme";
    public const string Lab = "Laborwert";

    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    // 1.000 als Tausender, 2,5 und 2.5 als Kommazahl
    private const string Number = @"(?<n>\d{1,3}(?:\.\d{3})+(?![\d,])|\d+(?:[.,]\d+)?)";

    private static readonly Lazy<Regex> DrugNames = new(() => new Regex(
        @"\b(?:" + string.Join("|", Section("Wirkstoffe").OrderByDescending(t => t.Length).Select(DrugPattern)) + @")\b",
        Opts, RegexLimits.MatchTimeout));

    private static readonly Lazy<Regex> LabNamed = new(() => new Regex(
        @"\b(?:" + string.Join("|", Section("Labor").OrderByDescending(t => t.Length).Select(Regex.Escape)) + @")\b" +
        @"(?>[^\d\n]{0,20})" + Number + @"\s?(?<u>" + LabUnits + @"|%)?",
        Opts, RegexLimits.MatchTimeout));

    private const string LabUnits =
        @"mg/dl|mg/l|g/dl|g/l|[mµμnp]?mol/l|U/l|IU/l|mU/l|[µμ]U/ml|ng/ml|pg/ml|[µμ]g/l|ng/l|/[µμ]l|/nl|G/l|T/l|mmHg|ml/min(?:/1[.,]73\s?m²)?";

    // Laborwert mit Einheit, auch ohne Namen davor, z.B. "48 mg/l"
    private static readonly Regex LabWithUnit = new(
        @"(?<![\w.,])" + Number + @"\s?(?<u>" + LabUnits + @")(?![\w/])", Opts, RegexLimits.MatchTimeout);

    // Blutdruck wie "140/90 mmHg" oder "RR 140/90"
    private static readonly Regex BloodPressure = new(
        @"(?:\b(?:RR|Blutdruck)\b(?>[^\d\n]{0,15}))(?<n>\d{2,3}/\d{2,3})(?!\d)|(?<![\d/])(?<n>\d{2,3}/\d{2,3})\s?mmHg\b",
        Opts, RegexLimits.MatchTimeout);

    // Dosierung wie "400 mg", "1 g", "20 I.E.", "2 Tabletten". Nicht "mg/dl", das ist ein Laborwert.
    private static readonly Regex Dosage = new(
        @"(?<![\w.,])" + Number + @"\s?(?<u>mg|g|[µμ]g|mcg|ug|ml|IE|I\.E\.|Tropfen|Hübe|Hub|Sprühstöße|Sprühstoß|Tabletten|Tablette|Tbl\.?|Kapseln|Kapsel|Beutel|Ampullen|Ampulle|Pflaster)(?![\w/])",
        Opts, RegexLimits.MatchTimeout);

    // Einnahmeschema wie "1-0-1" oder "1-0-0-1", und "3x täglich"
    private static readonly Regex Scheme = new(
        @"(?<![\d.,\-])[0-4½](?:\s?-\s?[0-4½]){2,3}(?![\d\-])|\b\d\s?(?:x|×|mal)\s?(?:täglich|tgl\.?|pro Tag|am Tag)",
        Opts, RegexLimits.MatchTimeout);

    private static readonly Regex AnyNumber = new(@"\d{1,3}(?:\.\d{3})+(?![\d,])|\d+(?:[.,]\d+)?", RegexOptions.Compiled, RegexLimits.MatchTimeout);

    public static IReadOnlyList<AnswerFinding> Find(IEnumerable<string> question, string answer)
    {
        var asked = string.Join("\n", question);
        var knownKeys = Scan(asked).Select(f => f.Key).ToHashSet();
        var knownNumbers = AnyNumber.Matches(asked).Select(m => NumberKey(m.Value)).OfType<string>().ToHashSet();

        return Scan(answer).Where(f => !Known(f, knownKeys, knownNumbers)).ToList();
    }

    private static bool Known(AnswerFinding f, HashSet<string> keys, HashSet<string> numbers)
    {
        if (keys.Contains(f.Key))
            return true;

        // Steht die Zahl schon in der Frage, nur anders geschrieben ("Amoxicillin 1000, 3x"), ist sie nicht neu
        return f.Kind is Dose or Lab && f.Key.Split(' ')[0] is var n && numbers.Contains(n);
    }

    private static List<AnswerFinding> Scan(string text)
    {
        var found = new List<AnswerFinding>();

        foreach (Match m in DrugNames.Value.Matches(text))
            found.Add(new(Drug, m.Value, m.Index, m.Length, "wirkstoff " + m.Value.ToLowerInvariant()));
        foreach (Match m in HealthTerms.DrugEnding.Matches(text))
            found.Add(new(Drug, m.Value, m.Index, m.Length, "wirkstoff " + m.Value.ToLowerInvariant()));

        foreach (Match m in LabNamed.Value.Matches(text))
            AddValue(found, Lab, text, m.Groups["n"], m.Groups["u"]);
        foreach (Match m in LabWithUnit.Matches(text))
            AddValue(found, Lab, text, m.Groups["n"], m.Groups["u"]);
        foreach (Match m in BloodPressure.Matches(text))
            found.Add(new(Lab, m.Groups["n"].Value, m.Groups["n"].Index, m.Groups["n"].Length, m.Groups["n"].Value + " mmhg"));

        foreach (Match m in Dosage.Matches(text))
            AddValue(found, Dose, text, m.Groups["n"], m.Groups["u"]);
        // "3x täglich" und "3 mal tgl." sind dasselbe, bei "1 - 0 - 1" zählen nur die Ziffern
        foreach (Match m in Scheme.Matches(text))
        {
            var key = char.IsDigit(m.Value[0]) && !m.Value.Contains('-') ? m.Value[0] + "x" : Regex.Replace(m.Value, @"\s", "");
            found.Add(new(Schedule, m.Value, m.Index, m.Length, "schema " + key));
        }

        // Überlappende Treffer: der erste und längste gewinnt, z.B. "Kalium 5,8 mmol/l" nur einmal
        var kept = new List<AnswerFinding>();
        foreach (var f in found.OrderBy(f => f.Start).ThenByDescending(f => f.Length))
        {
            if (kept.Count == 0 || f.Start >= kept[^1].Start + kept[^1].Length)
                kept.Add(f);
        }
        return kept;
    }

    private static void AddValue(List<AnswerFinding> found, string kind, string text, Group number, Group unit)
    {
        if (NumberKey(number.Value) is not { } n)
            return;

        var end = unit.Success ? unit.Index + unit.Length : number.Index + number.Length;
        var (value, canonical) = Canonical(decimal.Parse(n, CultureInfo.InvariantCulture), unit.Success ? unit.Value : "");
        found.Add(new(kind, text[number.Index..end], number.Index, end - number.Index,
            $"{value.ToString("0.######", CultureInfo.InvariantCulture)} {canonical}".Trim()));
    }

    // Gleiche Mengen gleich schreiben: 1 g = 1000 mg, 500 µg = 0,5 mg, "Tabletten" = "Tbl"
    private static (decimal, string) Canonical(decimal value, string unit)
    {
        var u = unit.ToLowerInvariant().Replace('μ', 'µ').Replace(" ", "");
        return u switch
        {
            "g" => (value * 1000, "mg"),
            "µg" or "mcg" or "ug" => (value / 1000, "mg"),
            "i.e." => (value, "ie"),
            "tabletten" or "tablette" or "tbl" or "tbl." => (value, "tbl"),
            "kapseln" => (value, "kapsel"),
            "hübe" or "sprühstöße" or "sprühstoß" => (value, "hub"),
            "ampullen" => (value, "ampulle"),
            _ => (value, u),
        };
    }

    // Zahl als Text ohne Tausenderpunkte und mit Punkt als Komma, z.B. "1.000" wird "1000", "2,5" wird "2.5"
    private static string? NumberKey(string raw)
    {
        var s = Regex.IsMatch(raw, @"^\d{1,3}(?:\.\d{3})+$") ? raw.Replace(".", "") : raw.Replace(',', '.');
        return decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d)
            ? d.ToString("0.######", CultureInfo.InvariantCulture)
            : null;
    }

    // Einträge eines Abschnitts aus medizin.txt. Mit * am Ende sind es Gruppen wie "antibiotik*",
    // die gehören nicht dazu, außer Insulin.
    private static IEnumerable<string> Section(string heading)
    {
        using var stream = typeof(HealthTerms).Assembly.GetManifestResourceStream("Zwijg.medizin.txt")
            ?? throw new InvalidOperationException("Liste medizin fehlt");
        using var reader = new StreamReader(stream);

        var inSection = false;
        var terms = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            if (line.TrimStart().StartsWith('#'))
            {
                inSection = line.TrimStart('#', ' ').StartsWith(heading, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection)
                continue;
            foreach (var t in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!t.Contains('*') || t == "insulin*")
                    terms.Add(t);
            }
        }
        return terms.Where(t => t != "pille").Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string DrugPattern(string term) =>
        term.EndsWith('*') ? Regex.Escape(term.TrimEnd('*')) + @"\w*" : Regex.Escape(term);
}
