using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Zwijg.Gateway.Settings;

namespace Zwijg.Tests;

// Vorlagen mit Variablen und direkt ausgeführte Vorlagen im Chat
public class TemplateTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    private HttpClient Client(string key = "admin-key")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private const string Absage =
        "Schreibe eine kurze Nachricht an {{Patient}}, dass der Termin am {{Absagetermin:termin}} ausfällt. " +
        "Grund: {{Grund:auswahl=Krankheit|Urlaub|Fortbildung}}. Neuer Termin: {{Neuer Termin:termin}}. {{Zusatz?:langtext}}";

    [Fact]
    public void Variablen_werden_gelesen()
    {
        var vars = TemplateVariables.Parse(Absage + " Nochmal {{Patient}}");

        Assert.Equal(5, vars.Count);
        Assert.Equal("text", vars[0].Type);
        Assert.Equal("termin", vars[1].Type);
        Assert.Equal(["Krankheit", "Urlaub", "Fortbildung"], vars[2].Options);
        Assert.True(vars[4].Optional);
        Assert.Equal("Zusatz", vars[4].Name);
    }

    [Theory]
    [InlineData("Hallo {{Name:farbe}}", "Unbekannter Typ")]
    [InlineData("{{Grund:auswahl=Nur eins}}", "mindestens zwei")]
    [InlineData("Hallo {{Name", "nicht richtig geschlossen")]
    public async Task Fehlerhafte_Variablen_werden_beim_Speichern_abgelehnt(string text, string message)
    {
        var res = await Client().PutAsJsonAsync("/admin/templates", new[] { new { title = "Test", text, mode = "Run", enabled = true } });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains(message, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Direkt_ausgefuehrte_Vorlage_zeigt_nur_die_Karte_und_speichert_sie()
    {
        (await Client().PutAsJsonAsync("/admin/templates", new[] { new { title = "Patientenabsage", text = Absage, mode = "Run", enabled = true } }))
            .EnsureSuccessStatusCode();

        var me = await Client().GetFromJsonAsync<JsonObject>("/v1/me");
        Assert.Equal("Run", me!["templates"]!.AsArray().Single(t => t!["title"]!.GetValue<string>() == "Patientenabsage")!["mode"]!.GetValue<string>());

        // Die Oberfläche setzt die Werte ein und schickt die Karte mit. Echo lehnt unbekannte Felder ab,
        // eine erfolgreiche Antwort beweist also, dass die Karte nicht an das Modell ging.
        var body = new
        {
            zwijg = new { conversation = "new" },
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = "Schreibe eine kurze Nachricht an Max Mustermann, dass der Termin am Freitag, 03.10.2026 um 10:30 Uhr ausfällt.",
                    zwijg_display = new
                    {
                        title = "Patientenabsage",
                        fields = new[] { new { label = "Patient", value = "Max Mustermann" }, new { label = "Absagetermin", value = "Freitag, 03.10.2026 um 10:30 Uhr" } }
                    }
                }
            }
        };

        var res = await Client().PostAsJsonAsync("/v1/chat/completions", body);
        res.EnsureSuccessStatusCode();
        var id = res.Headers.GetValues("X-Zwijg-Conversation").Single();

        var conv = await Client().GetFromJsonAsync<JsonObject>($"/v1/conversations/{id}");
        Assert.Equal("Patientenabsage", conv!["messages"]![0]!["display"]!["title"]!.GetValue<string>());

        // Titel kommt aus der Karte, aber ohne den Namen
        var title = conv["title"]!.GetValue<string>();
        Assert.StartsWith("Patientenabsage", title);
        Assert.DoesNotContain("Mustermann", title);
        Assert.Contains("03.10.2026", title);
    }
}
