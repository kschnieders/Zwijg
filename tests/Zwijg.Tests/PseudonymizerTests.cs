using Zwijg.Core.Pseudonymization;

namespace Zwijg.Tests;

public class PseudonymizerTests
{
    private readonly Pseudonymizer _pseudonymizer = Pseudonymizer.CreateDefault();

    private async Task<(string Text, PseudonymMap Map)> Run(string input)
    {
        var map = new PseudonymMap();
        var text = await _pseudonymizer.PseudonymizeAsync(input, map);
        return (text, map);
    }

    [Fact]
    public async Task Ersetzt_typische_Patientendaten()
    {
        var input = "Herr Max Mustermann, geb. 12.03.1980, KVNR A123456789, klagt über Kopfschmerzen.";

        var (text, map) = await Run(input);

        Assert.DoesNotContain("Max", text);
        Assert.DoesNotContain("Mustermann", text);
        Assert.DoesNotContain("12.03.1980", text);
        Assert.DoesNotContain("A123456789", text);
        Assert.Contains("[GEBURTSDATUM_1]", text);
        Assert.Contains("[VERSICHERTENNR_1]", text);
        Assert.Contains("Kopfschmerzen", text);
        Assert.Equal(Sensitivity.High, map.MaxSensitivity);
    }

    [Fact]
    public async Task Stellt_Original_wieder_her()
    {
        var input = "Frau Anna Schmidt (geb. 01.02.1975) wohnt in der Hauptstraße 5a, 12345 Berlin. Tel. 0171 2345678";

        var (text, map) = await Run(input);

        Assert.NotEqual(input, text);
        Assert.Equal(input, map.Restore(text));
    }

    [Fact]
    public async Task Nachname_bekommt_ueberall_denselben_Platzhalter()
    {
        var map = new PseudonymMap();
        var result = await _pseudonymizer.PseudonymizeAsync(
            ["Patientin: Lena Krüger, 34 Jahre", "Wie soll ich Frau Krüger weiter behandeln? Krüger hat Fieber."], map);

        Assert.DoesNotContain("Krüger", result[1]);
        var placeholder = result[0].Split(' ').First(w => w.StartsWith("[NAME_") && result[1].Contains(w.TrimEnd(',')));
        Assert.NotNull(placeholder);
    }

    [Fact]
    public async Task Erkennt_Email_Iban_und_Telefon()
    {
        var input = "Mail an max.muster@example.de, IBAN DE89 3704 0044 0532 0130 00, Rückruf unter +49 30 1234567";

        var (text, _) = await Run(input);

        Assert.Contains("[EMAIL_1]", text);
        Assert.Contains("[IBAN_1]", text);
        Assert.Contains("[TELEFON_1]", text);
    }

    [Fact]
    public async Task Laesst_normalen_medizinischen_Text_in_Ruhe()
    {
        var input = "Patientin klagt seit 3 Tagen über Husten. Therapie: Ibuprofen 400 mg, 3x täglich.";

        var (text, map) = await Run(input);

        Assert.Equal(input, text);
        Assert.Equal(0, map.Count);
    }

    [Fact]
    public async Task Arztname_nach_Dr_wird_ersetzt()
    {
        var (text, _) = await Run("Überweisung an Dr. med. Weber zur Abklärung.");

        Assert.DoesNotContain("Weber", text);
    }

    [Fact]
    public void Restore_ist_tolerant_bei_Leerzeichen()
    {
        var map = new PseudonymMap();
        map.GetOrAdd(EntityType.Name, "Mustermann");

        Assert.Equal("Herr Mustermann", map.Restore("Herr [ NAME_1 ]"));
    }
}
