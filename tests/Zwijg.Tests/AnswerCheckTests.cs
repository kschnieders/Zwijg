using Zwijg.Core.Safety;

namespace Zwijg.Tests;

public class AnswerCheckTests
{
    private static List<string> New(string question, string answer) =>
        AnswerCheck.Find([question], answer).Select(f => $"{f.Kind}: {f.Text}").ToList();

    [Fact]
    public void Erfundene_Angaben_werden_gefunden()
    {
        var found = New(
            "Bitte Arztbrief für Frau Mustermann, Diagnose Lungenentzündung.",
            "Therapie mit Amoxicillin 1000 mg 1-0-1 für 7 Tage, CRP 48 mg/l, Kalium 5,8 mmol/l, RR 140/90.");

        Assert.Equal(
            ["Wirkstoff: Amoxicillin", "Dosierung: 1000 mg", "Einnahme: 1-0-1", "Laborwert: 48 mg/l", "Laborwert: 5,8 mmol/l", "Laborwert: 140/90"],
            found);
    }

    [Fact]
    public void Was_schon_in_der_Frage_steht_ist_nicht_neu()
    {
        var found = New(
            "Patient nimmt Ramipril 5 mg 1-0-0, HbA1c 7,2 %, Amoxicillin 1000 3x täglich.",
            "Ramipril 5mg morgens (1 - 0 - 0) weiter, HbA1c 7,2 % ist erhöht. Amoxicillin 1 g 3 mal täglich.");

        Assert.Empty(found);
    }

    [Theory]
    [InlineData("1000 mg", "1 g")]
    [InlineData("0,5 mg", "500 µg")]
    [InlineData("2 Tabletten", "2 Tbl.")]
    [InlineData("1.000 mg", "1000mg")]
    [InlineData("20 IE", "20 I.E.")]
    public void Gleiche_Menge_anders_geschrieben(string question, string answer)
    {
        Assert.Empty(New("Dosis " + question, "Dosis " + answer));
    }

    [Fact]
    public void Geaenderte_Dosis_faellt_auf()
    {
        Assert.Equal(["Dosierung: 10 mg"], New("Ramipril 5 mg", "Ramipril 10 mg"));
    }

    [Fact]
    public void Alltagstext_ohne_Treffer()
    {
        Assert.Empty(New(
            "Bitte eine Absage für den Termin am 12.03. um 8:30 schreiben.",
            "Sehr geehrte Frau Mustermann, leider müssen wir Ihren Termin am 12.03.2026 um 8:30 Uhr absagen. Wir melden uns in 2 Tagen. 3 von 10 Plätzen sind frei, also 30 %."));
    }

    [Fact]
    public void Fundstellen_passen_zum_Text()
    {
        const string answer = "Neu: Metformin 500 mg.";

        var found = AnswerCheck.Find(["Diabetes"], answer);

        Assert.All(found, f => Assert.Equal(f.Text, answer.Substring(f.Start, f.Length)));
        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void Riesige_und_boesartige_Antworten_bleiben_schnell()
    {
        string[] answers =
        [
            "Kalium " + new string(' ', 200_000) + "x",
            new string('1', 200_000) + " mg",
            string.Concat(Enumerable.Repeat("1-", 100_000)),
            string.Concat(Enumerable.Repeat("RR ", 70_000)),
            string.Concat(Enumerable.Repeat("1.000.", 40_000)) + "mg",
            string.Concat(Enumerable.Repeat("Amoxicillin ", 20_000)),
        ];

        foreach (var answer in answers)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            AnswerCheck.Find([answer], answer);
            Assert.True(watch.ElapsedMilliseconds < 3000, $"{watch.ElapsedMilliseconds} ms für {answer[..20]}");
        }
    }

    [Fact]
    public void Gruppenbegriffe_sind_keine_Wirkstoffe()
    {
        Assert.Empty(New("Frage", "Ein Antibiotikum oder ein Antidepressivum wäre möglich, auch die Pille."));
    }
}
