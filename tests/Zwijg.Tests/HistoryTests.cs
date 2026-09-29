using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace Zwijg.Tests;

// Gespeicherte Unterhaltungen: verschlüsselt, nur für die Person selbst, mit Aufräumen
public class HistoryTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    private HttpClient Client(string key = "admin-key")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private async Task<string> NewUser(string name)
    {
        var res = await Client().PostAsJsonAsync("/admin/users", new { name, admin = false });
        return (await res.Content.ReadFromJsonAsync<JsonObject>())!["key"]!.GetValue<string>();
    }

    private static object Chat(string conversation, params string[] contents) => new
    {
        model = "auto",
        zwijg = new { conversation },
        messages = contents.Select((c, i) => new { role = i % 2 == 0 ? "user" : "assistant", content = c })
    };

    private static async Task<string> Send(HttpClient client, object body)
    {
        var res = await client.PostAsJsonAsync("/v1/chat/completions", body);
        res.EnsureSuccessStatusCode();
        return res.Headers.GetValues("X-Zwijg-Conversation").Single();
    }

    private async Task SetHistory(bool enabled = true, int max = 20, int days = 30, int pinned = 10) =>
        (await Client().PutAsJsonAsync("/admin/history", new { enabled, maxConversations = max, retentionDays = days, maxPinned = pinned }))
            .EnsureSuccessStatusCode();

    [Fact]
    public async Task Unterhaltung_wird_gespeichert_fortgesetzt_und_ist_verschluesselt()
    {
        await SetHistory();
        var me = Client(await NewUser("verlauf-a"));

        var id = await Send(me, Chat("new", "Herr Max Mustermann hat Fieber, was tun?"));
        var id2 = await Send(me, Chat(id, "Herr Max Mustermann hat Fieber, was tun?", "Echo: ...", "Und bei Kindern?"));
        Assert.Equal(id, id2);

        var list = await me.GetFromJsonAsync<JsonObject>("/v1/conversations");
        Assert.Equal(1, list!["total"]!.GetValue<int>());

        // Titel ohne Patientendaten, damit niemand über die Schulter Namen liest
        var title = list["items"]![0]!["title"]!.GetValue<string>();
        Assert.DoesNotContain("Mustermann", title);
        Assert.Contains("Fieber", title);

        // Die Person selbst sieht aber ihren echten Text
        var conv = await me.GetFromJsonAsync<JsonObject>($"/v1/conversations/{id}");
        var messages = conv!["messages"]!.AsArray();
        Assert.Equal(4, messages.Count);
        Assert.Contains("Mustermann", messages[0]!["content"]!.GetValue<string>());

        // In der Datei steht nichts im Klartext
        var raw = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(factory.DataDir, "history.db")));
        Assert.DoesNotContain("Mustermann", raw);
        Assert.DoesNotContain("Fieber", raw);
    }

    [Fact]
    public async Task Ohne_Feld_wird_nichts_gespeichert()
    {
        await SetHistory();
        var me = Client(await NewUser("verlauf-api"));

        var res = await me.PostAsJsonAsync("/v1/chat/completions", new { messages = new[] { new { role = "user", content = "Hallo" } } });
        res.EnsureSuccessStatusCode();

        Assert.False(res.Headers.Contains("X-Zwijg-Conversation"));
        Assert.Equal(0, (await me.GetFromJsonAsync<JsonObject>("/v1/conversations"))!["total"]!.GetValue<int>());
    }

    [Fact]
    public async Task Fremde_Unterhaltungen_sind_nicht_erreichbar()
    {
        await SetHistory();
        var owner = Client(await NewUser("verlauf-owner"));
        var other = Client(await NewUser("verlauf-other"));
        var id = await Send(owner, Chat("new", "Geheime Frage"));

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/v1/conversations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/v1/conversations/{id}")).StatusCode);

        // Auch nicht durch Fortsetzen mit fremder Id: dann entsteht eine eigene, neue Unterhaltung
        var hijack = await Send(other, Chat(id, "Geheime Frage", "x", "noch was"));
        Assert.NotEqual(id, hijack);
        var conv = await owner.GetFromJsonAsync<JsonObject>($"/v1/conversations/{id}");
        Assert.Equal(2, conv!["messages"]!.AsArray().Count);
    }

    [Fact]
    public async Task Anpinnen_Umbenennen_und_Grenzen()
    {
        await SetHistory(max: 2, pinned: 1);
        var me = Client(await NewUser("verlauf-pin"));

        var first = await Send(me, Chat("new", "Erste"));
        var pin = await me.PutAsJsonAsync($"/v1/conversations/{first}", new { pinned = true, title = "Wichtig" });
        pin.EnsureSuccessStatusCode();

        var second = await Send(me, Chat("new", "Zweite"));
        Assert.Equal(HttpStatusCode.BadRequest, (await me.PutAsJsonAsync($"/v1/conversations/{second}", new { pinned = true })).StatusCode);

        // Nicht angepinnte über der Grenze verschwinden, die angepinnte bleibt
        await Send(me, Chat("new", "Dritte"));
        await Send(me, Chat("new", "Vierte"));
        var list = await me.GetFromJsonAsync<JsonObject>("/v1/conversations");
        var titles = list!["items"]!.AsArray().Select(i => i!["title"]!.GetValue<string>()).ToList();

        Assert.Equal(3, list["total"]!.GetValue<int>());
        Assert.Equal("Wichtig", titles[0]);
        Assert.DoesNotContain("Zweite", titles);

        await SetHistory();
    }

    [Fact]
    public async Task Ausschalten_loescht_alles_und_speichert_nichts_mehr()
    {
        await SetHistory();
        var me = Client(await NewUser("verlauf-aus"));
        await Send(me, Chat("new", "Etwas"));

        await SetHistory(enabled: false);

        var res = await me.PostAsJsonAsync("/v1/chat/completions", Chat("new", "Noch was"));
        res.EnsureSuccessStatusCode();
        Assert.False(res.Headers.Contains("X-Zwijg-Conversation"));

        var stats = await Client().GetFromJsonAsync<JsonObject>("/admin/history");
        Assert.Equal(0, stats!["conversations"]!.GetValue<int>());

        await SetHistory();
    }
}
