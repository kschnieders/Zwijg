using System.Globalization;
using System.Text.RegularExpressions;

namespace Zwijg.Core.Pseudonymization;

// Patient für eine Unterhaltung, zum Beispiel "Kowalczyk, Anna, 12.03.1980".
// Daraus werden Begriffe, die Zwijg in der ganzen Unterhaltung versteckt: jeder Namensteil für sich
// und das Geburtsdatum in den üblichen Schreibweisen. So findet Zwijg auch "Kowalczyk" allein im Satz.
public static partial class PatientTerms
{
    public const int MaxLength = 200;

    private static readonly string[] Months =
        ["Januar", "Februar", "März", "April", "Mai", "Juni", "Juli", "August", "September", "Oktober", "November", "Dezember"];

    // Wörter, die beim Eintippen oft dabei sind, aber kein Name sind
    private static readonly HashSet<string> NotAName = new(StringComparer.OrdinalIgnoreCase)
    {
        "herr", "frau", "dr", "prof", "geb", "geboren", "am", "von", "van", "de", "der", "den", "und", "patient", "patientin",
    };

    public static IReadOnlyList<SecretTerm> Expand(string? patient)
    {
        if (string.IsNullOrWhiteSpace(patient))
            return [];
        var text = patient.Length > MaxLength ? patient[..MaxLength] : patient;

        var terms = new List<SecretTerm>();

        // Geburtsdatum herausnehmen, der Rest sind Namen
        foreach (Match m in DatePattern().Matches(text))
        {
            if (ToDate(m) is { } date)
                terms.AddRange(DateForms(date).Select(d => new SecretTerm(d, "GEBURTSDATUM", EntityType.BirthDate, WholeWord: true)));
        }
        var rest = DatePattern().Replace(text, " ");

        foreach (var word in NameSplit().Split(rest))
        {
            var name = word.Trim('.', '-', '\'');
            if (name.Length < 2 || !name.Any(char.IsLetter) || name.Any(char.IsDigit) || NotAName.Contains(name))
                continue;

            terms.Add(new SecretTerm(name, "NAME", EntityType.Name, WholeWord: true));

            // Doppelnamen auch einzeln, "Müller-Lüdenscheidt" und "Müller"
            if (name.Contains('-'))
            {
                foreach (var part in name.Split('-').Where(p => p.Length >= 2))
                    terms.Add(new SecretTerm(part, "NAME", EntityType.Name, WholeWord: true));
            }
        }

        return terms.DistinctBy(t => t.Value, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static DateTime? ToDate(Match m)
    {
        int day, month, year;
        if (m.Groups["iy"].Success)
        {
            year = int.Parse(m.Groups["iy"].Value, CultureInfo.InvariantCulture);
            month = int.Parse(m.Groups["im"].Value, CultureInfo.InvariantCulture);
            day = int.Parse(m.Groups["id"].Value, CultureInfo.InvariantCulture);
        }
        else
        {
            day = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture);
            month = m.Groups["mn"].Success
                ? Array.FindIndex(Months, x => x.Equals(m.Groups["mn"].Value, StringComparison.OrdinalIgnoreCase)) + 1
                : int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
            year = int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);

            // Zweistellig: 80 ist 1980, 05 ist 2005
            if (year < 100)
                year += year > DateTime.Now.Year % 100 ? 1900 : 2000;
        }

        return month is >= 1 and <= 12 && day >= 1 && year is >= 1900 and <= 2100 && day <= DateTime.DaysInMonth(year, month)
            ? new DateTime(year, month, day)
            : null;
    }

    // Die Schreibweisen, die in Briefen und Befunden vorkommen
    private static IEnumerable<string> DateForms(DateTime d)
    {
        var inv = CultureInfo.InvariantCulture;
        var month = Months[d.Month - 1];
        yield return d.ToString("dd.MM.yyyy", inv);
        yield return d.ToString("d.M.yyyy", inv);
        yield return d.ToString("dd.MM.yy", inv);
        yield return d.ToString("d.M.yy", inv);
        yield return d.ToString("yyyy-MM-dd", inv);
        yield return $"{d.Day}. {month} {d.Year}";
        yield return $"{d.Day:00}. {month} {d.Year}";
        yield return $"{d.Day}.{month} {d.Year}";
    }

    [GeneratedRegex(@"(?<!\d)(?:(?<iy>\d{4})-(?<im>\d{2})-(?<id>\d{2})|(?<d>\d{1,2})\.\s?(?:(?<m>\d{1,2})\.|(?<mn>Januar|Februar|März|April|Mai|Juni|Juli|August|September|Oktober|November|Dezember))\s?(?<y>\d{4}|\d{2}))(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"[\s,;/]+")]
    private static partial Regex NameSplit();
}
