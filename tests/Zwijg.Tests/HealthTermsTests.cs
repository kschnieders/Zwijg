using Zwijg.Core.Pseudonymization;

namespace Zwijg.Tests;

// Gesundheitsdaten müssen erkannt werden, damit Person plus Krankheit als hoch sensibel gilt
public class HealthTermsTests
{
    [Theory]
    [InlineData("Z.n. Appendektomie, jetzt Gastritis", "Appendektomie")]
    [InlineData("Neu aufgetretene Polyneuropathie beider Füße", "Polyneuropathie")]
    [InlineData("Verdacht auf Mammakarzinom rechts", "Mammakarzinom")]
    [InlineData("Ulcus cruris am linken Unterschenkel", "Ulcus")]
    [InlineData("Commotio cerebri nach Sturz", "Commotio")]
    [InlineData("Fraktur des Radius, Gips für sechs Wochen", "Fraktur")]
    [InlineData("Lungenembolie vor zwei Jahren", "Lungenembolie")]
    [InlineData("Thrombozytopenie unklarer Ursache", "Thrombozytopenie")]
    [InlineData("Hämaturie seit gestern", "Hämaturie")]
    [InlineData("Trigeminusneuralgie links", "Trigeminusneuralgie")]
    [InlineData("Sie nimmt Quetiapin und Sertralin", "Quetiapin")]
    [InlineData("Umstellung auf Empagliflozin", "Empagliflozin")]
    [InlineData("Seit Ozempic 8 kg abgenommen", "Ozempic")]
    [InlineData("Bandscheibenvorfall L4/L5", "Bandscheibenvorfall")]
    [InlineData("Kreuzbandriss beim Fußball", "Kreuzbandriss")]
    [InlineData("KHK und pAVK bekannt", "KHK")]
    [InlineData("Tinnitus seit dem Konzert", "Tinnitus")]
    [InlineData("Gürtelrose am Rücken", "Gürtelrose")]
    [InlineData("Er hat Kopfweh und Bauchweh", "Kopfweh")]
    [InlineData("Die Schilddrüse ist vergrößert", "Schilddrüse")]
    public void Fachbegriffe_werden_erkannt(string text, string term) =>
        Assert.Contains(HealthTerms.Find(text), t => t.Equals(term, StringComparison.OrdinalIgnoreCase));

    [Theory]
    [InlineData("Herzliche Grüße aus dem Team")]
    [InlineData("Die Organisation des Sommerfests läuft")]
    [InlineData("Wir fahren nach Darmstadt zur Messe")]
    [InlineData("Eine Analyse der Zahlen folgt")]
    [InlineData("Er sucht noch einen Parkplatz")]
    [InlineData("Mit großer Sympathie und etwas Nostalgie")]
    [InlineData("Die Fotografie hängt im Flur")]
    [InlineData("Im April beginnt die Saison")]
    [InlineData("Bitte die Tür leise schließen")]
    [InlineData("Wir können uns das leisten")]
    public void Alltagssaetze_sind_keine_Gesundheitsdaten(string text) =>
        Assert.Empty(HealthTerms.Find(text));
}
