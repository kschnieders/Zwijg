using Zwijg.Core.Pseudonymization;

namespace Zwijg.Tests;

// Erkennung von Namen, Orten und Gesundheitsdaten. Jeweils mit Gegenproben,
// damit normaler medizinischer Text nicht zerstückelt wird.
public class DetectionTests
{
    private static async Task<(string Text, PseudonymMap Map)> Run(string input, Pseudonymizer? p = null)
    {
        var map = new PseudonymMap();
        var text = await (p ?? Pseudonymizer.CreateDefault()).PseudonymizeAsync(input, map);
        return (text, map);
    }

    [Fact]
    public async Task Beispiel_aus_der_Praxis()
    {
        var (text, map) = await Run("Max Mustermann von Lingen hat einen neuen Krankheit und ist schwer krank");

        Assert.DoesNotContain("Max", text);
        Assert.DoesNotContain("Mustermann", text);
        Assert.DoesNotContain("Lingen", text);
        Assert.Contains("krank", text);
        Assert.Equal(Sensitivity.High, map.MaxSensitivity);
    }

    [Theory]
    [InlineData("MUSTERMANN, Max, geb. 01.01.1970", "MUSTERMANN")]
    [InlineData("Name: Mustermann, Max", "Mustermann")]
    [InlineData("Rückfrage bei M. Mustermann", "Mustermann")]
    [InlineData("Die Tochter Lena kommt morgen mit.", "Lena")]
    [InlineData("Anna Maria Kowalczyk hat Fieber", "Kowalczyk")]
    [InlineData("Hans-Peter Brinkmann wurde operiert", "Brinkmann")]
    [InlineData("Herr von Weizsäcker kommt zur Kontrolle", "Weizsäcker")]
    [InlineData("Überweisung an Prof. Dr. med. Albers", "Albers")]
    [InlineData("Hallo Svenja, bitte Termin eintragen", "Svenja")]
    public async Task Findet_Namen(string input, string name)
    {
        var (text, _) = await Run(input);

        Assert.DoesNotContain(name, text);
    }

    [Theory]
    [InlineData("Die Patientin wohnt in Emlichheim.", "Emlichheim")]
    [InlineData("Er kommt aus Wietmarschen-Lohne.", "Wietmarschen-Lohne")]
    [InlineData("Wohnort: Kleinkleckersdorf", "Kleinkleckersdorf")]
    [InlineData("Umzug nach Osnabrück geplant", "Osnabrück")]
    [InlineData("Geboren in Bad Bentheim", "Bad Bentheim")]
    [InlineData("Die Familie lebt in Essen.", "Essen")]
    [InlineData("Termin in der Lindenstraße vereinbart", "Lindenstraße")]
    public async Task Findet_Orte(string input, string place)
    {
        var (text, _) = await Run(input);

        Assert.DoesNotContain(place, text);
    }

    [Theory]
    [InlineData("Vater: Herzinfarkt mit 60, Mutter: Diabetes Typ 2.")]
    [InlineData("Diagnose: Migräne, Anna-Lena-Syndrom ausgeschlossen.")]
    [InlineData("Bitte vor Ort Hilfe organisieren.")]
    [InlineData("Die Werte sind in Ordnung, keine Gefahr.")]
    [InlineData("Nach dem Essen treten Bauchschmerzen auf.")]
    [InlineData("Einnahme von Ibuprofen 400 mg bei Bedarf.")]
    [InlineData("Die Patientin sucht einen neuen Hausarzt in der Nähe.")]
    [InlineData("Heute Kontrolle, danach Rücksprache mit dem Labor.")]
    public async Task Laesst_medizinischen_Text_in_Ruhe(string input)
    {
        var (text, _) = await Run(input);

        Assert.DoesNotContain("[NAME_", text);
        Assert.DoesNotContain("[ORT_", text);
    }

    [Theory]
    [InlineData("Nachricht für Max Mustermann: Die KVS oder Versicheerungsnummer lautet 12345678", "12345678", "[VERSICHERTENNR_")]
    [InlineData("Versichertennummer: 123 456 78", "123 456 78", "[VERSICHERTENNR_")]
    [InlineData("Vers.-Nr. 4711-0815", "4711-0815", "[VERSICHERTENNR_")]
    [InlineData("KVNR X12345678", "X12345678", "[VERSICHERTENNR_")]
    [InlineData("Mitgliedsnummer ist 99887766", "99887766", "[VERSICHERTENNR_")]
    [InlineData("Krankenversicherungsnr.: AB-445566", "AB-445566", "[VERSICHERTENNR_")]
    [InlineData("Patientennummer 20231", "20231", "[NUMMER_")]
    [InlineData("Fallnummer 2024-123456 bitte anlegen", "2024-123456", "[NUMMER_")]
    [InlineData("Aktenzeichen: AZ4711/22", "AZ4711/22", "[NUMMER_")]
    [InlineData("Bitte 88776655 zurückrufen", "88776655", "[NUMMER_")]
    public async Task Findet_Nummern_in_jedem_Format(string input, string number, string placeholder)
    {
        var (text, _) = await Run(input);

        Assert.DoesNotContain(number, text);
        Assert.Contains(placeholder, text);
    }

    [Theory]
    [InlineData("Ibuprofen 400 mg dreimal täglich")]
    [InlineData("Blutdruck 160/95, Puls 88")]
    [InlineData("Er hat 10000 IE Vitamin D verschrieben bekommen")]
    [InlineData("Leukozyten 12500 pro Mikroliter")]
    [InlineData("Die Versicherung zahlt seit 2019")]
    [InlineData("Termin im Jahr 2026 um 14:30")]
    [InlineData("Hausnummer 5 fehlt")]
    public async Task Laesst_Messwerte_und_Jahreszahlen_in_Ruhe(string input)
    {
        var (text, _) = await Run(input);

        Assert.DoesNotContain("[NUMMER_", text);
        Assert.DoesNotContain("[VERSICHERTENNR_", text);
    }

    [Fact]
    public async Task Gesundheit_ohne_Person_ist_niedrig()
    {
        var (_, map) = await Run("Welche Therapie hilft bei Migräne mit Übelkeit?");

        Assert.Equal(Sensitivity.Low, map.MaxSensitivity);
        Assert.Contains(map.HealthTerms, t => t.Equals("Migräne", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Person_mit_Gesundheit_ist_hoch()
    {
        var (_, map) = await Run("Frau Brinkmann hat Diagnose J06.9");

        Assert.Equal(Sensitivity.High, map.MaxSensitivity);
        Assert.Contains("J06.9", map.HealthTerms);
    }

    [Theory]
    [InlineData("Herr Brinkmann: Blutdruck 160/95", "Blutdruck")]
    [InlineData("Frau Brinkmann nimmt Ramipril", "Ramipril")]
    [InlineData("Frau Brinkmann bekommt 400 mg", "400 mg")]
    [InlineData("Herr Brinkmann, Candesartan und Bisoprolol", "Bisoprolol")]
    public async Task Messwerte_und_Medikamente_sind_Gesundheitsdaten(string input, string term)
    {
        var (_, map) = await Run(input);

        Assert.Contains(map.HealthTerms, t => t.Equals(term, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Sensitivity.High, map.MaxSensitivity);
    }

    [Fact]
    public async Task April_ist_kein_Medikament()
    {
        var (_, map) = await Run("Termin im April");

        Assert.Empty(map.HealthTerms);
    }

    [Fact]
    public async Task Ort_wird_ueberall_ersetzt_wenn_einmal_erkannt()
    {
        var (text, _) = await Run("Sie wohnt in Emlichheim. Emlichheim liegt an der Grenze.");

        Assert.DoesNotContain("Emlichheim", text);
    }

    [Fact]
    public async Task Eigene_Listen_und_Ausnahmen()
    {
        var p = new Pseudonymizer(
            [new NameDetector(), new CustomTermsDetector(() => (["Dr. Oltmanns", "Hilde Zwartjes"], ["Gildehaus"]))],
            () => ["Max"]);

        var (text, _) = await Run("Oltmanns und Frau Zwartjes aus Gildehaus. Max. Dosis beachten.", p);

        Assert.DoesNotContain("Oltmanns", text);
        Assert.DoesNotContain("Zwartjes", text);
        Assert.DoesNotContain("Gildehaus", text);
        Assert.Contains("Max. Dosis", text);
    }
}
