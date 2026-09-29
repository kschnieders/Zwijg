using System.Text.RegularExpressions;

namespace Zwijg.Core.Pseudonymization;

// Orte finden, auch ohne Postleitzahl:
// 1. Eindeutige Orte aus der Liste immer, zum Beispiel Nordhorn oder Frankfurt am Main
// 2. Alle Orte in Deutschland nach "in", "aus", "von", "nach", "bei", "Nähe" usw., auch mehrteilige wie "Halle (Saale)"
// 3. "Landkreis Emsland", "Kreis Borken", "Samtgemeinde Uelsen"
// 4. Nach "in", "aus" usw. ein Wort mit typischer Ortsendung (-horn, -burg, -hausen ...)
// 5. Nach "wohnt in", "Wohnort:", "geboren in" usw. einfach das nächste großgeschriebene Wort
public sealed class PlaceDetector : IPiiDetector
{
    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private const string Word = @"\p{Lu}[\p{Ll}ß]+(?:-\p{Lu}[\p{Ll}ß]+)*";

    // Bis zu vier Teile wie in "Neustadt an der Weinstraße", "Frankfurt am Main", "Halle (Saale)"
    private const string Phrase =
        $@"(?:Bad\s+|Sankt\s+|St\.\s+)?{Word}" +
        $@"(?:(?:\s+(?:am|an\s+der|an\s+dem|im|in\s+der|ob\s+der|bei|vor\s+der|auf\s+dem|zu|unter|ob)\s+|\s+){Word}){{0,3}}" +
        @"(?:\s*\([^()\n]{2,30}\))?";

    private static readonly Regex StrongContext = new(
        // "Ort" nur mit Doppelpunkt, sonst wird aus "vor Ort Hilfe" ein Ort
        @"(?:\b(?:Wohnort|Ort|Geburtsort|Heimatort)\s*:|\b(?:wohnhaft(?:\s+in)?|wohnt\s+in|lebt\s+in|wohnend\s+in|" +
        @"geboren\s+in|stammt\s+aus|kommt\s+aus|zugezogen\s+aus|umgezogen\s+nach))\s+" +
        $@"(?<place>(?:Bad\s+|Sankt\s+|St\.\s+)?{Word}(?:\s+(?:am|an\s+der|im|in\s+der|ob\s+der)\s+{Word})?)",
        Opts);

    private static readonly Regex WeakContext = new(
        $@"\b(?:in|aus|von|nach|bei|Raum|Region|Umgebung\s+von|Nähe(?:\s+von)?|nahe)\s+(?<place>{Phrase})",
        Opts);

    // "Landkreis Grafschaft Bentheim", "Kreis Steinfurt", "LK Emsland", "Ortsteil Laar"
    private static readonly Regex DistrictContext = new(
        $@"\b(?:Landkreis|Kreis|Lkr\.|LK|Stadtkreis|Regierungsbezirk|Samtgemeinde|Verbandsgemeinde|Gemeinde|Stadt|Amt|" +
        $@"Ortsteil|OT|Stadtteil|Ortschaft|Bauerschaft|Dorf)\s+(?<place>{Phrase})",
        Opts);

    private static readonly Regex PlaceSuffix = new(
        @"(?:burg|berg|dorf|hausen|heim|feld|felde|stadt|stedt|hagen|ingen|rode|furt|bach|brück|brücken|kirchen|" +
        @"hafen|haven|münde|walde|born|horn|hofen|leben|lar|loh|ow|itz|witz|beck|büttel|fleth|siel|kamp)$",
        Opts);

    // Wörter, die nach einer Präposition eher keine Orte sind, auch wenn ein Dorf so heißt
    private static readonly HashSet<string> NotAPlace = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dingen", "Umfeld", "Vorfeld", "Rahmen", "Gebrauch", "Anfang", "Verlauf", "Bedarf", "Bereich", "Folge",
        "Richtung", "Abstand", "Tabletten", "Tropfen", "Rücken", "Brücke", "Stadt", "Heim", "Pflegeheim",
        "Altenheim", "Kinderheim", "Krankenhaus", "Klinik", "Praxis", "Urlaub", "Ruhe", "Absprache",
        "Rücksprache", "Behandlung", "Therapie", "Untersuchung", "Gegenwart", "Zukunft", "Vergangenheit",
        "Deutschland", "Europa", "Ausland", "Inland", "Bett", "Hause", "Arbeit", "Schule", "Kita", "Kindergarten",
        "Notaufnahme", "Station", "Reha", "Kur", "Anbetracht", "Bezug", "Kombination", "Abhängigkeit", "Ordnung",
        "Berg", "Burg", "Dorf", "Feld", "Hafen", "Horn", "Leben", "Kirchen", "Bach", "Hof", "Gefahr", "Schwangerschaft",
        "Zentrum", "Mitte", "Nord", "Süd", "Ost", "West", "Altstadt", "Innenstadt", "Siedlung", "Mühle",
        "Narkose", "Belastung", "Ruhelage", "Rückenlage", "Bauchlage", "Seitenlage", "Fieber", "Übelkeit", "Schmerzen",
        "Angst", "Hand", "Sicht", "Absicht", "Gabe", "Einnahme", "Rezept", "Termin", "Anruf", "Rückruf", "Bedarfsfall",
    };

    private static readonly Lazy<Regex> KnownPlaces = new(() => new Regex(
        @"\b(?:" + string.Join("|", Places.Standalone.OrderByDescending(p => p.Length).Select(Regex.Escape)) + @")\b",
        Opts));

    public Task<IReadOnlyList<PiiMatch>> DetectAsync(string text, CancellationToken ct = default)
    {
        var result = new List<PiiMatch>();

        foreach (Match m in StrongContext.Matches(text))
            Add(result, m.Groups["place"].Index, m.Groups["place"].Value);

        foreach (Match m in DistrictContext.Matches(text))
        {
            // "Landkreis Emsland" steht so in der Liste, "Kreis Borken" nur als "Borken"
            var g = m.Groups["place"];
            if (LongestKnown(g.Value) is { } known)
                Add(result, g.Index, known);
        }

        foreach (Match m in WeakContext.Matches(text))
        {
            var g = m.Groups["place"];
            if (LongestKnown(g.Value) is { } known)
            {
                Add(result, g.Index, known);
                continue;
            }

            // Unbekannter Ort mit typischer Endung, nur das erste Wort
            var first = Regex.Match(g.Value, $"^(?:Bad\\s+)?{Word}").Value;
            if (PlaceSuffix.IsMatch(first))
                Add(result, g.Index, first);
        }

        foreach (Match m in KnownPlaces.Value.Matches(text))
            result.Add(new PiiMatch(EntityType.City, m.Index, m.Length, m.Value));

        return Task.FromResult<IReadOnlyList<PiiMatch>>(result);
    }

    // Den längsten Anfang, der ein bekannter Ort ist: "Frankfurt am Main hat" wird zu "Frankfurt am Main"
    private static string? LongestKnown(string phrase)
    {
        var candidate = phrase;
        while (true)
        {
            if (!NotAPlace.Contains(candidate) && Places.Contains(candidate))
                return candidate;

            var cut = candidate.LastIndexOfAny([' ', '(']);
            if (cut <= 0)
                return null;
            candidate = candidate[..cut].TrimEnd();

            // Verbindungswörter am Ende abschneiden, "Frankfurt am" ist kein Ort
            candidate = Regex.Replace(candidate, @"\s+(?:am|an|der|dem|im|in|ob|bei|vor|auf|zu|unter)$", "");
        }
    }

    private static void Add(List<PiiMatch> result, int index, string value)
    {
        if (!NotAPlace.Contains(value))
            result.Add(new PiiMatch(EntityType.City, index, value.Length, value));
    }
}
