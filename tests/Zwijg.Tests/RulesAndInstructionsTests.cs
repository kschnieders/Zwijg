using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Zwijg.Core.Security;

namespace Zwijg.Tests;

// Eigene Schutzregeln, globale und persönliche Anweisungen, Vorlagen
public class RulesAndInstructionsTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    private HttpClient Client(string key = "admin-key")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private static object Chat(string content) => new { model = "auto", messages = new[] { new { role = "user", content } } };

    private static object Rule(string name, string patterns, string action, bool regex = false, string? label = null, string? message = null) =>
        new { name, patterns, action, isRegex = regex, caseSensitive = false, label, message, enabled = true };

    private async Task SetRules(params object[] rules) =>
        (await Client().PutAsJsonAsync("/admin/rules", rules)).EnsureSuccessStatusCode();

    private static async Task<string> Answer(HttpResponseMessage res)
    {
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonObject>();
        return json!["choices"]![0]!["message"]!["content"]!.GetValue<string>();
    }

    [Fact]
    public async Task Schutzregeln_ersetzen_blockieren_und_halten_lokal()
    {
        await SetRules(
            Rule("Patientennummer", @"P-\d{5}", "Replace", regex: true, label: "PATIENTENNR"),
            Rule("Passwörter", "Passwort\nKennwort", "Block", message: "Bitte keine Zugangsdaten eingeben."),
            Rule("Besonders sensibel", "HIV", "LocalOnly"));

        // Ersetzen: die KI sieht nur den Platzhalter, die Antwort bekommt den echten Wert zurück
        var check = await (await Client().PostAsJsonAsync("/v1/check", new { text = "Befund zu P-12345 liegt vor" }))
            .Content.ReadFromJsonAsync<JsonObject>();
        Assert.Contains("[PATIENTENNR_1]", check!["pseudonymized"]!.GetValue<string>());
        Assert.Contains("P-12345", await Answer(await Client().PostAsJsonAsync("/v1/chat/completions", Chat("Befund zu P-12345"))));

        // Blockieren mit eigener Meldung, der gefundene Text kommt nicht zurück
        var blocked = await Client().PostAsJsonAsync("/v1/chat/completions", Chat("Mein Passwort ist geheim123"));
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        var body = await blocked.Content.ReadAsStringAsync();
        Assert.Contains("Bitte keine Zugangsdaten", body);
        Assert.DoesNotContain("geheim123", body);

        // Nur lokal, auch bei einer sonst harmlosen Frage. "Archiv" darf nicht treffen.
        var hiv = await Client().PostAsJsonAsync("/v1/chat/completions", Chat("Wie wird HIV übertragen?"));
        Assert.Equal("Local", hiv.Headers.GetValues("X-Zwijg-Route").Single());
        var archiv = await Client().PostAsJsonAsync("/v1/chat/completions", Chat("Wo finde ich das Archiv?"));
        Assert.Equal("Cloud", archiv.Headers.GetValues("X-Zwijg-Route").Single());

        await SetRules();
    }

    [Theory]
    [InlineData("(unclosed", true)]
    [InlineData("P-\\d+", false)]
    public async Task Kaputte_Muster_werden_beim_Speichern_abgelehnt(string pattern, bool regexBroken)
    {
        var res = await Client().PutAsJsonAsync("/admin/rules", new[] { Rule("Test", pattern, "Replace", regex: true, label: "TEST") });

        Assert.Equal(regexBroken ? HttpStatusCode.BadRequest : HttpStatusCode.OK, res.StatusCode);
        await SetRules();
    }

    [Fact]
    public async Task Falscher_Platzhaltername_wird_abgelehnt()
    {
        var res = await Client().PutAsJsonAsync("/admin/rules", new[] { Rule("Test", "abc", "Replace", label: "patient nr 1") });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Gefaehrliches_Muster_legt_den_Server_nicht_lahm()
    {
        await SetRules(Rule("Falle", "(a+)+$", "Warn", regex: true));

        var watch = Stopwatch.StartNew();
        var res = await Client().PostAsJsonAsync("/v1/check", new { text = new string('a', 40) + "!" });

        res.EnsureSuccessStatusCode();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Dauerte {watch.Elapsed}");
        await SetRules();
    }

    [Fact]
    public async Task Anweisungen_Hinweis_und_persoenliche_Anweisung_kommen_an()
    {
        (await Client().PutAsJsonAsync("/admin/instructions", new
        {
            enabled = true, language = "de", addressing = "du", tone = "friendly", length = "short",
            format = "bullets", audience = "patients", customText = "Verweise bei Notfällen auf die 112.",
            responseFooter = "KI Antwort, bitte fachlich prüfen."
        })).EnsureSuccessStatusCode();

        var settings = await Client().GetFromJsonAsync<JsonObject>("/admin/settings");
        var me = settings!["users"]!.AsArray().First(u => u!["name"]!.GetValue<string>() == "empfang")!;
        (await Client().PutAsJsonAsync($"/admin/users/{me["id"]}", new
        {
            name = "empfang", admin = false, instructions = "Schlage immer einen Termin vor."
        })).EnsureSuccessStatusCode();

        // Echo zeigt mit /system, was ein echtes Modell als Anweisung bekommen hätte
        var system = await Answer(await Client("user-key").PostAsJsonAsync("/v1/chat/completions", Chat("/system")));
        Assert.Contains("\"du\"", system);
        Assert.Contains("einfache Sprache", system);
        Assert.Contains("112", system);
        Assert.Contains("Schlage immer einen Termin vor", system);
        Assert.EndsWith("KI Antwort, bitte fachlich prüfen.", system);

        // Ein anderer Benutzer bekommt die persönliche Anweisung nicht
        var adminSystem = await Answer(await Client().PostAsJsonAsync("/v1/chat/completions", Chat("/system")));
        Assert.DoesNotContain("Termin vor", adminSystem);

        (await Client().PutAsJsonAsync("/admin/instructions", new { enabled = false, responseFooter = "" })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Unbekannter_Antwortstil_wird_abgelehnt()
    {
        var res = await Client().PutAsJsonAsync("/admin/instructions", new { enabled = true, tone = "wütend" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Vorlagen_erscheinen_nur_wenn_aktiv()
    {
        (await Client().PutAsJsonAsync("/admin/templates", new[]
        {
            new { title = "Arztbrief", text = "Entwirf einen kurzen Arztbrief zu:", enabled = true },
            new { title = "Versteckt", text = "Nicht zeigen", enabled = false },
        })).EnsureSuccessStatusCode();

        var me = await Client("user-key").GetFromJsonAsync<JsonObject>("/v1/me");
        var titles = me!["templates"]!.AsArray().Select(t => t!["title"]!.GetValue<string>()).ToList();

        Assert.Contains("Arztbrief", titles);
        Assert.DoesNotContain("Versteckt", titles);
    }

    [Theory]
    [InlineData("HIV positiv", true)]
    [InlineData("im Archiv", false)]
    [InlineData("hiv", true)]
    public void Woerter_nur_als_ganze_Woerter(string text, bool hit)
    {
        var rule = new ProtectionRule { Name = "t", Patterns = "HIV", Action = RuleAction.LocalOnly };

        Assert.Equal(hit, RuleEngine.Find(text, [rule]).Count > 0);
    }
}
