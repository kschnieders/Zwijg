using System.Text.RegularExpressions;

namespace Zwijg.Core.Pseudonymization;

// Namen per Regeln finden:
// 1. Kontext: Wörter nach "Herr", "Frau", "Patientin", "Name:", "Dr.", "geb.", "Eheleute" usw.
// 2. Bekannte Vornamen aus der Liste, plus das großgeschriebene Wort danach als Nachname
// 3. Bekannte Nachnamen aus der Liste, auch allein, plus ein unbekanntes Wort davor als Vorname
// 4. "Nachname, Vorname" und "NACHNAME, Vorname" wie in Arztbriefen
// 5. Initiale mit Nachname, z.B. "M. Mustermann"
// 6. Listen wie "Patienten: Meyer, Schulze" und Terminlisten wie "8:30 Kaminski"
// Jedes Namenswort wird einzeln gemeldet, damit "Herr Mustermann" später
// denselben Platzhalter bekommt wie der Nachname in "Max Mustermann".
public sealed class NameDetector : IPiiDetector
{
    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // Auch mit Apostroph wie in O'Neill oder D'Angelo
    private const string Part = @"\p{Lu}(?:['’]\p{Lu})?\p{Ll}+";
    private const string Word = $@"{Part}(?:-{Part})?";
    private const string Particle = @"(?:(?:von|van|de|der|zu|vom|ten|ter)\s+)?";

    private const string AfterContext = @"(?:(?:Dr|Prof)\.\s*(?:med\.\s*)?)*" + $@"(?<name>{Particle}{Word}(?:\s+{Particle}{Word})?)";

    private static readonly Regex ContextPattern = new(
        @"\b(?:Herrn?|Frau|Hr\.|Fr\.|Frl\.|Patient(?:in)?|Pat\.|Patientenname|Name|Nachname|Vorname|Geburtsname|" +
        @"Versicherte[rn]?|Mitglied|Betreuer(?:in)?|Bewohner(?:in)?|Klient(?:in)?|Familie|Fam\.|Hallo|Liebe[r]?|" +
        @"Eheleute|Ehepaar|geb\.|geborene[rn]?)" +
        @"\s*:?\s+" + AfterContext,
        Opts);

    // "Herr und Frau Becker", "Frau und Herrn Becker"
    private static readonly Regex CouplePattern = new(
        @"\b(?:Herrn?|Frau)\s+und\s+(?:Herrn?|Frau)\s+" + AfterContext,
        Opts);

    // Ohne Doppelpunkt, sonst wird bei "Vater: Herzinfarkt" die Krankheit zum Namen
    private static readonly Regex FamilyPattern = new(
        @"\b(?:Kind|Sohn|Tochter|Ehemann|Ehefrau|Mutter|Vater|Bruder|Schwester|Oma|Opa|Enkel(?:in)?|Angehörige[rn]?)" +
        @"\s+" + AfterContext,
        Opts);

    private static readonly Regex DoctorPattern = new(
        $@"\b(?:(?:Dr|Prof|Dipl\.-Med)\.\s*(?:(?:med|dent|rer\. nat|phil)\.\s*)?)+(?<name>{Word}(?:\s+{Word})?)",
        Opts);

    // "Patienten: Meyer, Schulze und Nowak"
    private static readonly Regex LabelListPattern = new(
        $@"\b(?:Patienten|Patientinnen|Namen|Teilnehmer(?:innen)?|Anwesend|Besucher)\s*:\s*" +
        $@"(?<list>{Word}(?:\s*(?:,|;|/|&|\bund\b)\s*{Word})*)",
        Opts);

    // Terminlisten: "8:30 Kaminski", "10:15 Uhr Brinkhoff"
    private static readonly Regex TimePattern = new(
        $@"(?<![\d:.])\d{{1,2}}[:.]\d{{2}}(?:\s*Uhr)?\s*[-\u2013:]?\s*(?<name>{Word})\b(?!-)",
        Opts);

    // Typische Endungen von Nachnamen, nur in Terminlisten genutzt
    private static readonly Regex SurnameEnding = new(
        @"(?:mann|ski|ska|sky|cki|cka|wicz|czyk|czak|enko|chuk|tschuk|owa|ova|ow|ov|ev|eva|oglu|oğlu|poulos|poulou|" +
        @"akis|idis|iadis|ović|ovic|ević|evic|ić|hoff|meier|meyer|maier|mayer|huber|bauer|brink|kamp|haus|horst)$",
        Opts);

    // "frau schmidt", "herr yilmaz": klein geschrieben nur, wenn der Name in einer Liste steht
    private static readonly Regex LowercasePattern = new(
        @"\b(?i:herrn?|frau|hr\.|fr\.)\s+(?<name>\p{Ll}{2,}(?:-\p{Ll}{2,})?)\b(?:\s+(?<second>\p{Ll}{2,}(?:-\p{Ll}{2,})?)\b)?",
        Opts);

    // "MUSTERMANN, Max" überall, "Mustermann, Max" nur am Zeilenanfang oder nach einem Doppelpunkt,
    // sonst würde bei "Migräne, Anna klagt..." die Krankheit zum Nachnamen
    private static readonly Regex LastFirstPattern = new(
        $@"(?:(?<=^|[:;(]\s*)(?<last>{Word})|\b(?<last>\p{{Lu}}{{3,}}(?:-\p{{Lu}}{{3,}})?)),\s*(?<first>{Word})\b(?!-)",
        Opts | RegexOptions.Multiline);

    // "M. Mustermann", aber nicht mitten in Abkürzungen wie "z. B."
    private static readonly Regex InitialPattern = new(
        $@"(?<![\p{{L}}.])\p{{Lu}}\.\s?(?<last>\p{{Lu}}\p{{Ll}}{{2,}}(?:-\p{{Lu}}\p{{Ll}}+)?)\b",
        Opts);

    // Nicht Teil eines längeren Wortes wie "Anna-Lena-Syndrom"
    private static readonly Regex CapitalizedWord = new($@"(?<!-)\b{Word}\b(?!-)", Opts);
    private static readonly Regex ParticleThenWord = new($@"\G\s+{Particle}(?<w>{Word})\b", Opts);
    // Beide suchen rückwärts ab einer Stelle, sonst wird es bei langen Texten mit vielen Namen langsam
    private static readonly Regex WordBefore = new($@"(?<!-)\b(?<w>{Word})[ \t]+$", Opts | RegexOptions.RightToLeft);
    private static readonly Regex ArticleBefore = new(@"\b(?:der|die|das|den|dem|des|ein|eine|einem|einen|einer)\s+$",
        Opts | RegexOptions.IgnoreCase | RegexOptions.RightToLeft);

    // Wörter, die typisch für Hauptwörter sind und deshalb kein unbekannter Vorname sein sollten
    private static readonly Regex NounEnding = new(
        @"(?:ung|heit|keit|schaft|ion|tät|nis|tum|ment|ismus|ik|arzt|ärztin|ist|istin|eur|ant|ent|ler|ner|ger|ter|chen|lein|ei|ur|us)$",
        Opts);

    // Was in Terminlisten statt eines Namens hinter der Uhrzeit stehen kann
    private static readonly HashSet<string> AppointmentWord = new(StringComparer.OrdinalIgnoreCase)
    {
        "Kontrolle", "Nachkontrolle", "Wundkontrolle", "Nachsorge", "Vorsorge", "Ultraschall", "Sono", "Sonografie",
        "Röntgen", "Verbandswechsel", "Fäden", "Gespräch", "Beratung", "Aufklärung", "Physio", "Physiotherapie",
        "Akupunktur", "Spritze", "Infusion", "Check", "Checkup", "Gesundheitscheck", "Screening", "Hautkrebsscreening",
        "Abstrich", "Blutentnahme", "Blutabnahme", "Impfen", "Impfung", "Grippeimpfung", "Auffrischung", "Allergietest",
        "Lungenfunktion", "Spirometrie", "Belastungs", "Langzeit", "Holter", "Ergometrie", "Audiometrie", "Sehtest",
        "Hörtest", "Wiegen", "Messen", "Befundbesprechung", "Besprechung", "Teamsitzung", "Mittagspause", "Pause",
        "Mittag", "Frühstück", "Feierabend", "Akutsprechstunde", "Sprechstunde", "Telefonsprechstunde", "Videosprechstunde",
        "Hausbesuch", "Hausbesuche", "Notfall", "Termin", "Termine", "Frei", "Offen", "Reserviert", "Blockiert", "Neu",
        "Neupatient", "Neupatientin", "Rezept", "Rezepte", "Überweisung", "Anruf", "Rückruf", "Labor", "Post", "Fax",
    };

    // Wörter, die nach einem Vornamen oder Titel stehen können, aber keine Namen sind
    private static readonly HashSet<string> NotANameWord = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dr", "Doktor", "Prof", "Professor", "Kollege", "Kollegin", "Patient", "Patientin",
        "Der", "Die", "Das", "Den", "Dem", "Des", "Ein", "Eine", "Einen", "Einem", "Und", "Oder", "Mit", "Von", "Vom",
        "Hat", "Ist", "War", "Wird", "Wurde", "Klagt", "Kommt", "Kam", "Leidet", "Zeigt", "Seit", "Heute", "Gestern",
        "Morgen", "Aktuell", "Bitte", "Danke", "Er", "Sie", "Es", "Wir", "Ich", "Ihr", "Sein", "Seine", "Ihre",
        "Danach", "Dann", "Daher", "Deshalb", "Aber", "Auch", "Noch", "Nun", "Jetzt", "Hier", "Dort", "Wie", "Was",
        "Wann", "Warum", "Welche", "Welcher", "Diagnose", "Befund", "Anamnese", "Therapie", "Medikation",
        "Beschwerden", "Termin", "Labor", "Station", "Praxis", "Klinik", "Kasse", "Krankenkasse", "Versicherung",
        "Januar", "Februar", "März", "April", "Mai", "Juni", "Juli", "August", "September", "Oktober", "November",
        "Dezember", "Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag", "Sonntag",
        "Sehr", "Geehrte", "Geehrter", "Guten", "Tag", "Grüße", "Gruß", "Team", "Uhr", "Pause", "Mittag", "Frühstück",
        "Arzt", "Ärztin", "Hausarzt", "Hausärztin", "Facharzt", "Fachärztin", "Zahnarzt", "Zahnärztin", "Oberarzt",
        "Oberärztin", "Chefarzt", "Chefärztin", "Pfleger", "Pflegerin", "Therapeut", "Therapeutin", "Betreuer", "Betreuerin",
        "Impfung", "Blutabnahme", "Sprechstunde", "Besprechung", "Hausbesuch", "Notfall", "Vertretung", "Urlaub",
        // Nach Personen benannte Krankheiten, Zeichen und Skalen sind keine Patientennamen
        "Morbus", "Parkinson", "Alzheimer", "Crohn", "Hashimoto", "Basedow", "Bechterew", "Cushing", "Addison",
        "Down", "Asperger", "Tourette", "Hodgkin", "Raynaud", "Sjögren", "Wilson", "Lyme", "Paget", "Menière",
        "Meniere", "Quincke", "Kussmaul", "Glasgow", "Apgar", "Wernicke", "Korsakoff", "Guillain", "Barré",
        "Horner", "Babinski", "Lasègue", "Lasegue", "Romberg", "Murphy", "McBurney", "Virchow", "Dupuytren",
        "Scheuermann", "Perthes", "Osgood", "Schlatter", "Baker", "Bell", "Duchenne", "Huntington", "Marfan",
        "Kawasaki", "Kaposi", "Karnofsky", "Barthel", "Norton", "Braden", "Tinel", "Phalen", "Lachman", "Trendelenburg",
    };

    public Task<IReadOnlyList<PiiMatch>> DetectAsync(string text, CancellationToken ct = default)
    {
        var result = new List<PiiMatch>();

        AddNameGroup(result, ContextPattern.Matches(text), "name");
        AddNameGroup(result, CouplePattern.Matches(text), "name");
        AddNameGroup(result, FamilyPattern.Matches(text), "name");
        AddNameGroup(result, DoctorPattern.Matches(text), "name");

        foreach (Match m in LabelListPattern.Matches(text))
        {
            var list = m.Groups["list"];
            foreach (Match w in CapitalizedWord.Matches(list.Value))
            {
                if (IsPlainWord(w.Value))
                    continue;
                result.Add(new PiiMatch(EntityType.Name, list.Index + w.Index, w.Length, w.Value));
            }
        }

        // In Terminlisten steht hinter der Uhrzeit fast immer ein Name oder eine Terminart.
        // Terminarten und typische Hauptwörter werden aussortiert, der Rest gilt als Name.
        foreach (Match m in TimePattern.Matches(text))
        {
            var g = m.Groups["name"];
            if (IsPlainWord(g.Value) || AppointmentWord.Contains(g.Value))
                continue;
            // Unbekannte Wörter auf -en sind meist Tätigkeiten wie "Aufstehen" oder "Mittagessen"
            var unknownButPlausible = !NounEnding.IsMatch(g.Value) && !g.Value.EndsWith("en", StringComparison.Ordinal);
            if (Surnames.Contains(g.Value) || IsFirstName(g.Value) || SurnameEnding.IsMatch(g.Value) || unknownButPlausible)
                Add(result, g);
        }

        foreach (Match m in LowercasePattern.Matches(text))
        {
            foreach (var g in new[] { m.Groups["name"], m.Groups["second"] })
            {
                if (!g.Success || NotANameWord.Contains(g.Value))
                    break;
                var title = char.ToUpperInvariant(g.Value[0]) + g.Value[1..];
                if (!Surnames.Contains(title) && !IsFirstName(title))
                    break;
                Add(result, g);
            }
        }

        foreach (Match m in LastFirstPattern.Matches(text))
        {
            var first = m.Groups["first"];
            var last = m.Groups["last"];
            if (IsFirstName(first.Value) && !NotANameWord.Contains(last.Value) && HealthTerms.Find(last.Value).Count == 0)
            {
                Add(result, last);
                Add(result, first);
            }
        }

        foreach (Match m in InitialPattern.Matches(text))
        {
            if (!NotANameWord.Contains(m.Groups["last"].Value) && !Places.IsWellKnown(m.Groups["last"].Value))
                Add(result, m.Groups["last"]);
        }

        FindByFirstName(text, result);
        FindBySurname(text, result);
        return Task.FromResult<IReadOnlyList<PiiMatch>>(result);
    }

    // Vornamen aus der Liste, auch mehrere hintereinander ("Anna Maria"), dann den Nachnamen
    private static void FindByFirstName(string text, List<PiiMatch> result)
    {
        foreach (Match m in CapitalizedWord.Matches(text))
        {
            if (!IsFirstName(m.Value) || NotANameWord.Contains(m.Value))
                continue;

            result.Add(new PiiMatch(EntityType.Name, m.Index, m.Length, m.Value));

            var pos = m.Index + m.Length;
            for (var i = 0; i < 2; i++)
            {
                var next = ParticleThenWord.Match(text, pos);
                if (!next.Success)
                    break;

                var w = next.Groups["w"];
                if (NotANameWord.Contains(w.Value) || Places.IsWellKnown(w.Value))
                    break;

                result.Add(new PiiMatch(EntityType.Name, w.Index, w.Length, w.Value));
                pos = w.Index + w.Length;

                // Nach einem Nachnamen ist Schluss, nach einem weiteren Vornamen geht es weiter
                if (!IsFirstName(w.Value))
                    break;
            }
        }
    }

    // Eindeutige Nachnamen aus der Liste auch ohne Zusammenhang, dazu ein unbekannter Vorname davor ("Kübra Aydın")
    private static void FindBySurname(string text, List<PiiMatch> result)
    {
        foreach (Match m in CapitalizedWord.Matches(text))
        {
            if (!Surnames.Contains(m.Value) || Surnames.NeedsContext(m.Value) || NotANameWord.Contains(m.Value))
                continue;

            result.Add(new PiiMatch(EntityType.Name, m.Index, m.Length, m.Value));

            var before = WordBefore.Match(text, 0, m.Index);
            if (!before.Success)
                continue;

            var w = before.Groups["w"];
            if (!IsPlainWord(w.Value) && !NounEnding.IsMatch(w.Value) && !ArticleBefore.Match(text, 0, w.Index).Success)
                result.Add(new PiiMatch(EntityType.Name, w.Index, w.Length, w.Value));
        }
    }

    // Wörter, die sicher kein Name sind: Füllwörter, Rollen, Krankheiten, Orte
    private static bool IsPlainWord(string word) =>
        NotANameWord.Contains(word) || Places.IsWellKnown(word) || HealthTerms.Find(word).Count > 0;

    private static bool IsFirstName(string word) =>
        FirstNames.Contains(word) || (word.Contains('-') && word.Split('-').Any(FirstNames.Contains));

    private static void AddNameGroup(List<PiiMatch> result, MatchCollection matches, string group)
    {
        foreach (Match m in matches)
        {
            var g = m.Groups[group];
            foreach (Match w in CapitalizedWord.Matches(g.Value))
            {
                if (NotANameWord.Contains(w.Value))
                    break;

                result.Add(new PiiMatch(EntityType.Name, g.Index + w.Index, w.Length, w.Value));
            }
        }
    }

    private static void Add(List<PiiMatch> result, Group g) =>
        result.Add(new PiiMatch(EntityType.Name, g.Index, g.Length, g.Value));
}
