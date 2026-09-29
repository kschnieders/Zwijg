using System.Diagnostics;
using System.Text;
using Zwijg.Core.Pseudonymization;

namespace Zwijg.Tests;

// Lange Dokumente mit vielen Namen dürfen nicht spürbar langsamer werden
public class DetectionSpeedTests
{
    [Fact]
    public async Task Langer_Text_mit_vielen_Namen_bleibt_schnell()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 2000; i++)
            sb.Append("Kübra Aydın und Herr Schmidt kamen um 8:30 zu Kaminski, danach Befund für Müller. ");
        var text = sb.ToString();

        // Beim ersten Aufruf werden alle Listen geladen (rund 60.000 Orte, Namen, Fachbegriffe)
        var pseudonymizer = Pseudonymizer.CreateDefault();
        var cold = Stopwatch.StartNew();
        await pseudonymizer.PseudonymizeAsync("Aufwärmen mit Max Mustermann aus Lingen, Diagnose Gastritis", new PseudonymMap());
        cold.Stop();
        Assert.True(cold.Elapsed < TimeSpan.FromSeconds(10), $"Erster Aufruf dauerte {cold.Elapsed.TotalSeconds:0.0} s");
        Console.WriteLine($"Erster Aufruf: {cold.ElapsedMilliseconds} ms");

        var watch = Stopwatch.StartNew();
        var result = await pseudonymizer.PseudonymizeAsync(text, new PseudonymMap());
        watch.Stop();

        Assert.DoesNotContain("Aydın", result);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Dauerte {watch.Elapsed.TotalSeconds:0.0} s für {text.Length} Zeichen");
    }
}
