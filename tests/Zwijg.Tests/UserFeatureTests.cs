using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Zwijg.Tests;

// Rechte pro Benutzer, Sperren, Tageslimit und Benachrichtigungen. Alles muss der Server durchsetzen.
public class UserFeatureTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    private HttpClient Client(string key = "admin-key")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    // Legt einen Benutzer an und gibt Id und Schlüssel zurück
    private async Task<(string Id, string Key)> CreateUser(string name, object? rights = null)
    {
        var res = await Client().PostAsJsonAsync("/admin/users", rights ?? new { name, admin = false });
        res.EnsureSuccessStatusCode();
        var key = (await res.Content.ReadFromJsonAsync<JsonObject>())!["key"]!.GetValue<string>();

        var settings = await Client().GetFromJsonAsync<JsonObject>("/admin/settings");
        var id = settings!["users"]!.AsArray().First(u => u!["name"]!.GetValue<string>() == name)!["id"]!.GetValue<string>();
        return (id, key);
    }

    private static object Chat(string content) => new { model = "auto", messages = new[] { new { role = "user", content } } };

    [Fact]
    public async Task Gesperrter_Benutzer_kommt_nicht_mehr_rein()
    {
        var (id, key) = await CreateUser("sperr-test");
        Assert.Equal(HttpStatusCode.OK, (await Client(key).GetAsync("/v1/me")).StatusCode);

        (await Client().PutAsJsonAsync($"/admin/users/{id}", new { name = "sperr-test", admin = false, active = false }))
            .EnsureSuccessStatusCode();

        var res = await Client(key).PostAsJsonAsync("/v1/chat/completions", Chat("Hallo"));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Contains("gesperrt", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Admin_kann_sich_nicht_selbst_sperren()
    {
        var me = await Client().GetFromJsonAsync<JsonObject>("/v1/me");

        var res = await Client().PutAsJsonAsync($"/admin/users/{me!["id"]}", new { name = "admin", admin = true, active = false });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Nur_lokal_gilt_auch_fuer_harmlose_Fragen()
    {
        var (_, key) = await CreateUser("lokal-test", new { name = "lokal-test", admin = false, cloudAllowed = false });

        var res = await Client(key).PostAsJsonAsync("/v1/chat/completions", Chat("Was ist die Standarddosis von Ibuprofen?"));

        res.EnsureSuccessStatusCode();
        Assert.Equal("Local", res.Headers.GetValues("X-Zwijg-Route").Single());
    }

    [Fact]
    public async Task Ohne_Dokumentrecht_kein_Upload()
    {
        var (_, key) = await CreateUser("doc-test", new { name = "doc-test", admin = false, canUseDocuments = false });

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("Test"u8.ToArray()), "file", "a.txt");
        form.Add(new StringContent("Zusammenfassen"), "question");

        var res = await Client(key).PostAsync("/v1/documents/ask", form);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Tageslimit_greift()
    {
        var (_, key) = await CreateUser("limit-test", new { name = "limit-test", admin = false, dailyLimit = 2 });

        (await Client(key).PostAsJsonAsync("/v1/chat/completions", Chat("Eins"))).EnsureSuccessStatusCode();
        (await Client(key).PostAsJsonAsync("/v1/chat/completions", Chat("Zwei"))).EnsureSuccessStatusCode();
        var third = await Client(key).PostAsJsonAsync("/v1/chat/completions", Chat("Drei"));

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        var me = await Client(key).GetFromJsonAsync<JsonObject>("/v1/me");
        Assert.Equal(2, me!["usedToday"]!.GetValue<int>());
    }

    [Fact]
    public async Task Benachrichtigung_nur_fuer_ausgewaehlte_und_wegklickbar()
    {
        var (targetId, targetKey) = await CreateUser("hinweis-ziel");
        var (_, otherKey) = await CreateUser("hinweis-andere");

        (await Client().PostAsJsonAsync("/admin/announcements", new
        {
            title = "Bitte beachten", message = "Keine Befunde von Kindern hochladen.", level = "Warning",
            audience = "Selected", userIds = new[] { targetId }, dismissible = true, enabled = true
        })).EnsureSuccessStatusCode();

        var target = await Client(targetKey).GetFromJsonAsync<JsonObject>("/v1/me");
        var other = await Client(otherKey).GetFromJsonAsync<JsonObject>("/v1/me");
        var shown = target!["announcements"]!.AsArray();
        Assert.Contains(shown, a => a!["title"]!.GetValue<string>() == "Bitte beachten");
        Assert.DoesNotContain(other!["announcements"]!.AsArray(), a => a!["title"]!.GetValue<string>() == "Bitte beachten");

        var id = shown.First(a => a!["title"]!.GetValue<string>() == "Bitte beachten")!["id"]!.GetValue<string>();
        (await Client(targetKey).PostAsync($"/v1/announcements/{id}/dismiss", null)).EnsureSuccessStatusCode();

        var after = await Client(targetKey).GetFromJsonAsync<JsonObject>("/v1/me");
        Assert.DoesNotContain(after!["announcements"]!.AsArray(), a => a!["id"]!.GetValue<string>() == id);
    }

    [Fact]
    public async Task Pflicht_Hinweis_laesst_sich_nicht_wegklicken()
    {
        (await Client().PostAsJsonAsync("/admin/announcements", new
        {
            title = "Wichtig", message = "Wartung heute Abend.", level = "Critical",
            audience = "All", dismissible = false, enabled = true
        })).EnsureSuccessStatusCode();

        var me = await Client().GetFromJsonAsync<JsonObject>("/v1/me");
        var id = me!["announcements"]!.AsArray().First(a => a!["title"]!.GetValue<string>() == "Wichtig")!["id"]!.GetValue<string>();

        var res = await Client().PostAsync($"/v1/announcements/{id}/dismiss", null);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Uebersicht_zaehlt_Anfragen()
    {
        (await Client().PostAsJsonAsync("/v1/chat/completions", Chat("Statistik Test"))).EnsureSuccessStatusCode();

        var stats = await Client().GetFromJsonAsync<JsonObject>("/admin/stats");

        Assert.True(stats!["today"]!["requests"]!.GetValue<int>() >= 1);
        Assert.Equal(14, stats["days"]!.AsArray().Count);
    }
}
