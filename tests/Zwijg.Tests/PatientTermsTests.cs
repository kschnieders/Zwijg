using Zwijg.Core.Pseudonymization;

namespace Zwijg.Tests;

public class PatientTermsTests
{
    private readonly Pseudonymizer _pseudonymizer = Pseudonymizer.CreateDefault();

    private async Task<(string Text, PseudonymMap Map)> Hide(string text, string patient)
    {
        var map = new PseudonymMap();
        var result = await _pseudonymizer.PseudonymizeAsync([text], map, secrets: PatientTerms.Expand(patient));
        return (result[0], map);
    }

    [Fact]
    public void Namen_und_Geburtsdatum_werden_zerlegt()
    {
        var terms = PatientTerms.Expand("Frau Kowalczyk-Nowak, Anna, geb. 12.03.1980");
        var names = terms.Where(t => t.Type == EntityType.Name).Select(t => t.Value).ToList();

        Assert.Equal(["Kowalczyk-Nowak", "Kowalczyk", "Nowak", "Anna"], names);
        Assert.Contains(terms, t => t.Type == EntityType.BirthDate && t.Value == "12.03.1980");
        Assert.Contains(terms, t => t.Value == "12.3.80");
        Assert.Contains(terms, t => t.Value == "1980-03-12");
        Assert.Contains(terms, t => t.Value == "12. März 1980");
        Assert.All(terms, t => Assert.True(t.WholeWord));
    }

    [Theory]
    [InlineData("Bitte Kowalczyk zurückrufen.")]
    [InlineData("KOWALCZYK, Anna")]
    [InlineData("Kowalczyks Befund ist da.")]
    [InlineData("geboren am 12. März 1980")]
    [InlineData("Geb.-Dat. 12.3.80")]
    [InlineData("DOB 1980-03-12")]
    public async Task Jede_Schreibweise_wird_versteckt(string text)
    {
        var (hidden, map) = await Hide(text, "Kowalczyk, Anna, 12.03.1980");

        Assert.DoesNotContain("owalczyk", hidden, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1980", hidden);
        Assert.DoesNotContain("12.3.80", hidden);
        // Zurück kommt die Schreibweise aus dem Patientenfeld, "KOWALCZYK" wird "Kowalczyk"
        Assert.Equal(text, map.Restore(hidden), ignoreCase: true);
    }

    [Fact]
    public async Task Nur_ganze_Woerter()
    {
        var (hidden, _) = await Hide("Annahme durch Anna, Johannahaus", "Anna Kowalczyk");

        Assert.Equal("Annahme durch [NAME_1], Johannahaus", hidden);
    }

    [Fact]
    public async Task Patient_mit_Diagnose_ist_hoch_sensibel()
    {
        var (_, map) = await Hide("Kowalczyk hat Diabetes.", "Kowalczyk");

        Assert.Equal(Sensitivity.High, map.MaxSensitivity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12.13.1980")]
    [InlineData("Herr Dr.")]
    public void Leere_oder_unsinnige_Eingaben_ergeben_nichts(string patient)
    {
        Assert.Empty(PatientTerms.Expand(patient));
    }
}
