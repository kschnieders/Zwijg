using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Zwijg.Core.Pseudonymization;

namespace Zwijg.Tests;

// Was jemand im Chat selbst als geheim markiert, darf nie an das Modell, in den Titel oder ins Protokoll
public class SecretTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    private HttpClient Client(string key = "admin-key")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private static object Chat(string conversation, object[] secrets, params string[] contents) => new
    {
        model = "auto",
        zwijg = new { conversation, secrets },
        messages = contents.Select((c, i) => new { role = i % 2 == 0 ? "user" : "assistant", content = c }),
    };

    [Fact]
    public async Task Geheimnis_wird_ueberall_ersetzt_und_in_der_Antwort_wieder_eingesetzt()
    {
        var map = new PseudonymMap();
        var result = await Pseudonymizer.CreateDefault().PseudonymizeAsync(
            ["Das Projekt Nordlicht startet morgen.", "Wer leitet projekt nordlicht?"], map,
            secrets: [new SecretTerm("Projekt Nordlicht", "GEHEIM")]);

        Assert.Equal("Das [GEHEIM_1] startet morgen.", result[0]);
        Assert.Equal("Wer leitet [GEHEIM_1]?", result[1]);
        Assert.Equal("Projekt Nordlicht läuft", map.Restore("[GEHEIM_1] läuft"));
    }

    [Fact]
    public async Task Geheimnis_hat_Vorrang_vor_der_automatischen_Erkennung()
    {
        // "Max Mustermann" allein wäre ein Name. Markiert ist aber mehr, und davon darf nichts übrig bleiben.
        var map = new PseudonymMap();
        var result = await Pseudonymizer.CreateDefault().PseudonymizeAsync(
            "Termin bei Max Mustermann Consulting GmbH", map,
            secrets: [new SecretTerm("Mustermann Consulting GmbH", "FIRMA")]);

        Assert.DoesNotContain("Consulting", result);
        Assert.Contains("[FIRMA_1]", result);
    }

    [Fact]
    public async Task Geheimnis_geht_nicht_an_das_Modell_und_nicht_in_Titel_oder_Protokoll()
    {
        (await Client().PutAsJsonAsync("/admin/history", new { enabled = true, maxConversations = 20, retentionDays = 30, maxPinned = 10 }))
            .EnsureSuccessStatusCode();
        object[] secrets = [new { value = "Kontonummer 4711", label = "NUMMER" }];

        var res = await Client("user-key").PostAsJsonAsync("/v1/chat/completions",
            Chat("new", secrets, "Bitte Kontonummer 4711 an die Buchhaltung melden"));
        res.EnsureSuccessStatusCode();
        var id = res.Headers.GetValues("X-Zwijg-Conversation").Single();

        // Die Antwort zeigt den echten Text wieder
        var answer = (await res.Content.ReadFromJsonAsync<JsonObject>())!["choices"]![0]!["message"]!["content"]!.GetValue<string>();
        Assert.Contains("Kontonummer 4711", answer);

        // Im Protokoll steht nur, was an das Modell ging
        var audit = await Client().GetFromJsonAsync<JsonObject>("/admin/audit?q=Buchhaltung");
        var prompt = audit!["items"]![0]!["prompt"]!.GetValue<string>();
        Assert.Contains("[NUMMER_1]", prompt);
        Assert.DoesNotContain("4711", prompt);

        // Titel in der Seitenleiste ohne Geheimnis, beim Laden kommen die Geheimnisse mit
        var conv = await Client("user-key").GetFromJsonAsync<JsonObject>($"/v1/conversations/{id}");
        Assert.DoesNotContain("4711", conv!["title"]!.GetValue<string>());
        Assert.Equal("Kontonummer 4711", conv["secrets"]![0]!["value"]!.GetValue<string>());
        Assert.Equal("NUMMER", conv["secrets"]![0]!["label"]!.GetValue<string>());
    }

    [Fact]
    public async Task Vorschau_zeigt_Geheimnis_als_Platzhalter_und_falsche_Bezeichnung_wird_GEHEIM()
    {
        var res = await Client("user-key").PostAsJsonAsync("/v1/check", new
        {
            text = "Codewort Seestern nicht weitersagen",
            secrets = new[] { new { value = "Seestern", label = "<script>" } },
        });

        var check = await res.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("Codewort [GEHEIM_1] nicht weitersagen", check!["pseudonymized"]!.GetValue<string>());
    }
}
