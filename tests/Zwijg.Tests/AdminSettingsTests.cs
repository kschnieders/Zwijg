using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Zwijg.Tests;

public class AdminSettingsTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    private HttpClient Client(string key = "admin-key")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private static object Connection(string name, string type, bool onPremise, string? apiKey = null, string? baseUrl = null) => new
    {
        name, type, onPremise, apiKey, baseUrl,
        preset = "custom", model = "test-modell", clearApiKey = false, timeoutSeconds = 30
    };

    private async Task<JsonObject> Settings() =>
        (await Client().GetFromJsonAsync<JsonObject>("/admin/settings"))!;

    [Fact]
    public async Task Api_Schluessel_wird_nie_im_Klartext_gezeigt_oder_gespeichert()
    {
        const string secret = "sk-geheim-1234567890";

        var res = await Client().PostAsJsonAsync("/admin/connections",
            Connection("Test Cloud", "OpenAI", false, secret, "https://api.example.com/v1"));
        res.EnsureSuccessStatusCode();

        var view = (await Client().GetStringAsync("/admin/settings"));
        Assert.DoesNotContain(secret, view);
        Assert.Contains("...7890", view);

        var file = await File.ReadAllTextAsync(Path.Combine(factory.DataDir, "settings.json"));
        Assert.DoesNotContain(secret, file);
    }

    [Fact]
    public async Task Cloud_Verbindung_darf_nicht_fuer_sensible_Daten_genutzt_werden()
    {
        await Client().PostAsJsonAsync("/admin/connections",
            Connection("Nur Cloud", "OpenAI", false, "x", "https://api.example.com/v1"));
        var id = (await Settings())["connections"]!.AsArray()
            .First(c => c!["name"]!.GetValue<string>() == "Nur Cloud")!["id"]!.GetValue<string>();

        var res = await Client().PutAsJsonAsync("/admin/routes", new { localConnectionId = id, cloudConnectionId = (string?)null });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Claude_kann_nicht_als_lokal_markiert_werden()
    {
        var res = await Client().PostAsJsonAsync("/admin/connections", Connection("Claude", "Anthropic", true, "x"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Neuer_Benutzer_bekommt_Schluessel_und_kann_wieder_entfernt_werden()
    {
        var created = await (await Client().PostAsJsonAsync("/admin/users", new { name = "praxis-test", admin = false }))
            .Content.ReadFromJsonAsync<JsonObject>();
        var key = created!["key"]!.GetValue<string>();

        var me = await Client(key).GetFromJsonAsync<JsonObject>("/v1/me");
        Assert.Equal("praxis-test", me!["name"]!.GetValue<string>());
        Assert.False(me["admin"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(key).GetAsync("/admin/settings")).StatusCode);

        var id = (await Settings())["users"]!.AsArray()
            .First(u => u!["name"]!.GetValue<string>() == "praxis-test")!["id"]!.GetValue<string>();
        (await Client().DeleteAsync($"/admin/users/{id}")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(key).GetAsync("/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Echo_Verbindung_laesst_sich_testen()
    {
        var local = (await Settings())["localConnectionId"]!.GetValue<string>();

        var result = await (await Client().PostAsync($"/admin/connections/{local}/test", null))
            .Content.ReadFromJsonAsync<JsonObject>();

        Assert.True(result!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Admin_kann_sich_nicht_selbst_loeschen()
    {
        var id = (await Settings())["users"]!.AsArray()
            .First(u => u!["name"]!.GetValue<string>() == "admin")!["id"]!.GetValue<string>();

        var res = await Client().DeleteAsync($"/admin/users/{id}");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
