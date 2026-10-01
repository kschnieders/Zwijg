using System.Text.RegularExpressions;

namespace Zwijg.Core.Pseudonymization;

// Findet feste Formate wie Versichertennummer, Datum, IBAN, Telefon usw.
// Lieber einmal zu viel ersetzen als zu wenig, die Werte kommen in der Antwort ja zurück.
public sealed class RegexPiiDetector : IPiiDetector
{
    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // Eine Nummer nach einem Schlüsselwort: mindestens 4 Ziffern, gern mit Buchstaben davor
    // und Gruppen mit Leerzeichen, Bindestrich oder Schrägstrich (z.B. "123 456 78" oder "2024-123456")
    private const string IdentifierValue =
        @"(?<v>(?=[A-Za-z]{0,3}[\d\-/ ]*\d[\d\-/ ]*\d[\d\-/ ]*\d[\d\-/ ]*\d)[A-Za-z]{0,3}[\-]?\d+(?:[\-/ ]\d{2,})*[A-Za-z]?)(?![\w])";

    private const string Months = "Januar|Februar|März|April|Mai|Juni|Juli|August|September|Oktober|November|Dezember";

    // Trennzeichen nach dem Schlüsselwort. Atomar, damit lange Leerzeichenfolgen nicht quadratisch durchprobiert werden.
    private const string Separator = @"(?>[\s:=.#]*)";

    private static readonly (EntityType Type, Regex Pattern)[] Rules =
    [
        // Geburtsdatum mit Hinweis davor, z.B. "geb. 12.03.1980", "geb. 12/03/1980", "geb. am 1. März 1980"
        (EntityType.BirthDate, new Regex(
            @"(?i:\bgeb(?:\.|oren|urtsdatum)?(?:\s+am)?|\*)\s*:?\s*(?<v>\d{1,2}\.\s?\d{1,2}\.\s?(?:\d{4}|\d{2})|\d{1,2}/\d{1,2}/(?:\d{4}|\d{2})|\d{1,2}\.\s?(?:" + Months + @")\s+\d{4})(?!\d)", Opts, RegexLimits.MatchTimeout)),

        // Krankenversichertennummer (eGK): ein Buchstabe plus 9 Ziffern
        (EntityType.InsuranceNumber, new Regex(@"\b[A-Z]\d{9}\b", Opts, RegexLimits.MatchTimeout)),

        // Rentenversicherungsnummer, z.B. 12 150380 M 123
        (EntityType.InsuranceNumber, new Regex(@"\b\d{2}\s?\d{6}\s?[A-Z]\s?\d{3}\b", Opts, RegexLimits.MatchTimeout)),

        // Versichertennummer mit Kontext in jedem Format, auch vertippt:
        // "Versicheerungsnummer lautet 12345678", "Vers.-Nr.: 123 456 78", "KVNR A12345", "Mitgliedsnummer 4711-0815"
        (EntityType.InsuranceNumber, new Regex(
            @"(?i:\b(?:\w{0,30}(?:versi|kv|mitglied|kassen|egk)\w{0,30}(?:nummer|nr)|(?:versi\w*|vers\.?|kv|egk|mitglied\w*|kassen\w*)[\s.\-]*(?:nummer|nr\.?|no\.?)|kvnr|kvs|egk))" +
            Separator + @"(?i:lautet|ist|wäre)?(?>[\s:=#]*)" + IdentifierValue, Opts, RegexLimits.MatchTimeout)),

        // Sonstige Kennnummern mit Kontext: Patientennummer, Fallnummer, Aktenzeichen, ID ...
        (EntityType.Identifier, new Regex(
            @"(?i:\b(?:\w*nummer|\w+nr\.?|nr\.?|kennung|kennziffer|aktenzeichen|az\.?|id))" +
            Separator + @"(?i:lautet|ist)?(?>[\s:=#]*)" + IdentifierValue, Opts, RegexLimits.MatchTimeout)),

        // Lange Ziffernfolgen ohne Kontext. Ab 7 Ziffern ist das fast nie ein Messwert, aber oft eine Kennung.
        (EntityType.Identifier, new Regex(@"(?<![\d.,])\d{7,}(?![\d.,])", Opts, RegexLimits.MatchTimeout)),

        // Auch klein geschrieben, z.B. "de89 3704 0044 0532 0130 00"
        (EntityType.Iban, new Regex(@"(?i:\b[A-Z]{2}\d{2}(?:\s?[A-Z0-9]{4}){3,7}(?:\s?[A-Z0-9]{1,3})?\b)", Opts, RegexLimits.MatchTimeout)),

        // Vor dem @ höchstens 64 Zeichen, wie im Standard. Sonst wird "a.a.a.a..." quadratisch langsam.
        (EntityType.Email, new Regex(@"\b[A-Za-z0-9._%+-]{1,64}@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", Opts, RegexLimits.MatchTimeout)),

        // Auch mit Ländervorwahl wie "+31 6 1234 5678" oder "+49 (0) 5921 123456"
        (EntityType.Phone, new Regex(@"(?<![\w.,])(?:(?:\+|00)[1-9]\d{0,2}(?:\s?\(0\))?|0)\s?[1-9][\d\s/()-]{5,}\d\b", Opts, RegexLimits.MatchTimeout)),

        // Straße mit Hausnummer, z.B. "Hauptstraße 5a" oder "Lange Str. 12".
        // Der Name davor ist begrenzt, sonst wird "A.A.A.A..." quadratisch langsam.
        (EntityType.Address, new Regex(
            @"\b\p{Lu}[\p{L}.-]{0,50}?(?:\s?(?:[Ss]tra(?:ß|ss)e|[Ss]tr\.|[Ww]eg|[Aa]llee|[Pp]latz|[Gg]asse|[Rr]ing|[Dd]amm|[Uu]fer))\s+\d{1,4}(?:\s?[a-zA-Z]\b)?", Opts, RegexLimits.MatchTimeout)),

        // Straße ohne Hausnummer, nur zusammengesetzte Namen wie "Lindenstraße" oder "Kastanienallee"
        (EntityType.Address, new Regex(@"\b\p{Lu}[\p{L}-]{0,50}(?:straße|strasse|allee|gasse)\b", Opts, RegexLimits.MatchTimeout)),

        // PLZ mit Ort
        (EntityType.City, new Regex(@"\b\d{5}\s+\p{Lu}\p{Ll}+(?:[\s-]\p{Lu}\p{Ll}+)?\b", Opts, RegexLimits.MatchTimeout)),

        (EntityType.Date, new Regex(@"\b\d{1,2}\.\s?\d{1,2}\.\s?(?:\d{4}|\d{2})(?!\d)", Opts, RegexLimits.MatchTimeout)),
        (EntityType.Date, new Regex(@"\b\d{4}-\d{2}-\d{2}\b", Opts, RegexLimits.MatchTimeout)),
        (EntityType.Date, new Regex(
            @"\b\d{1,2}\.\s?(?:" + Months + @")\s+\d{4}\b", Opts, RegexLimits.MatchTimeout)),
    ];

    public Task<IReadOnlyList<PiiMatch>> DetectAsync(string text, CancellationToken ct = default)
    {
        var result = new List<PiiMatch>();

        foreach (var (type, pattern) in Rules)
        {
            foreach (Match m in pattern.Matches(text))
            {
                var g = m.Groups["v"].Success ? m.Groups["v"] : (Group)m;
                result.Add(new PiiMatch(type, g.Index, g.Length, g.Value));
            }
        }

        return Task.FromResult<IReadOnlyList<PiiMatch>>(result);
    }
}
