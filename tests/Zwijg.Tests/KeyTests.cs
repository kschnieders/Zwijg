using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zwijg.Gateway;
using Zwijg.Gateway.Settings;

namespace Zwijg.Tests;

// Früher standen feste Schlüssel im Repo. Die dürfen nirgends mehr funktionieren.
public class PublicKeyFactory : WebApplicationFactory<Program>
{
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), $"zwijg-it-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Zwijg:ApiKeys:0:Key"] = "admin-key",
            ["Zwijg:ApiKeys:0:User"] = "admin",
            ["Zwijg:ApiKeys:0:Admin"] = "true",
            ["Zwijg:ApiKeys:1:Key"] = "dev-praxis-admin",
            ["Zwijg:ApiKeys:1:User"] = "alter admin",
            ["Zwijg:ApiKeys:1:Admin"] = "true",
            ["Zwijg:Providers:Local:Type"] = "Echo",
            ["Zwijg:Providers:Local:Model"] = "echo-lokal",
            ["Zwijg:Audit:DatabasePath"] = Path.Combine(DataDir, "audit.db"),
            ["Zwijg:SettingsPath"] = Path.Combine(DataDir, "settings.json"),
        }));
    }
}

public class KeyTests(PublicKeyFactory factory) : IClassFixture<PublicKeyFactory>
{
    private HttpClient Client(string key)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private static SettingsStore Store(string path, params ApiKeyOptions[] keys) => new(
        Options.Create(new GatewayOptions { SettingsPath = path, ApiKeys = [.. keys] }),
        new EphemeralDataProtectionProvider(),
        NullLogger<SettingsStore>.Instance);

    private static string TempSettings() => Path.Combine(Path.GetTempPath(), $"zwijg-keys-{Guid.NewGuid():N}", "settings.json");

    [Fact]
    public async Task Oeffentlicher_Schluessel_aus_der_Konfiguration_ist_gesperrt()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client("dev-praxis-admin").GetAsync("/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client("admin-key").GetAsync("/v1/me")).StatusCode);
    }

    [Fact]
    public void Nur_oeffentlicher_Schluessel_ergibt_neuen_Startschluessel()
    {
        var store = Store(TempSettings(), new ApiKeyOptions { Key = "dev-praxis-admin", User = "admin", Admin = true });

        var admin = Assert.Single(store.Current.Users);
        Assert.NotEqual("", admin.KeyHash);
        Assert.NotEqual(SettingsStore.HashKey("dev-praxis-admin"), admin.KeyHash);
    }

    [Fact]
    public void Oeffentlicher_Schluessel_in_alten_Einstellungen_wird_beim_Start_gesperrt()
    {
        var path = TempSettings();
        Store(path,
            new ApiKeyOptions { Key = "admin-key-lang-und-zufaellig", User = "admin", Admin = true },
            new ApiKeyOptions { Key = "empfang-key-lang-und-zufaellig", User = "empfang" });

        // So sah eine Installation aus, die einmal mit den alten Entwicklerschlüsseln gestartet wurde
        var json = File.ReadAllText(path).Replace(
            SettingsStore.HashKey("empfang-key-lang-und-zufaellig"), SettingsStore.HashKey("dev-praxis-empfang"));
        File.WriteAllText(path, json);

        var store = Store(path);
        var empfang = store.Current.Users.Single(u => u.Name == "empfang");
        Assert.Equal("", empfang.KeyHash);
        Assert.DoesNotContain(SettingsStore.HashKey("dev-praxis-empfang"), File.ReadAllText(path));

        // Der Admin hatte einen eigenen Schlüssel und behält ihn
        Assert.Equal(SettingsStore.HashKey("admin-key-lang-und-zufaellig"), store.Current.Users.Single(u => u.Name == "admin").KeyHash);
    }

    [Fact]
    public async Task Schluessel_entfernen_geht_nur_mit_Passwort()
    {
        var admin = Client("admin-key");

        // Mit Passwort: danach kein Zugang mehr per Schlüssel, aber per Passwort
        var created = await (await admin.PostAsJsonAsync("/admin/users",
            new { name = "ohne.schluessel", username = "ohne.schluessel", admin = false, password = "Sommer-Regen-2026" })).Content.ReadFromJsonAsync<JsonObject>();
        var key = created!["key"]!.GetValue<string>();
        var id = await IdOf(admin, "ohne.schluessel");
        Assert.Equal(HttpStatusCode.OK, (await Client(key).GetAsync("/v1/me")).StatusCode);

        (await admin.DeleteAsync($"/admin/users/{id}/key")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(key).GetAsync("/v1/me")).StatusCode);
        var browser = factory.CreateClient();
        browser.DefaultRequestHeaders.Add("X-Requested-With", "zwijg");
        (await browser.PostAsJsonAsync("/auth/login", new { username = "ohne.schluessel", password = "Sommer-Regen-2026" })).EnsureSuccessStatusCode();

        // Ohne Passwort: abgelehnt, sonst käme die Person nie wieder rein
        await admin.PostAsJsonAsync("/admin/users", new { name = "nur.schluessel", username = "nur.schluessel", admin = false });
        var id2 = await IdOf(admin, "nur.schluessel");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync($"/admin/users/{id2}/key")).StatusCode);
    }

    private static async Task<string> IdOf(HttpClient admin, string username)
    {
        var settings = await admin.GetFromJsonAsync<JsonObject>("/admin/settings");
        return settings!["users"]!.AsArray().First(u => u!["username"]!.GetValue<string>() == username)!["id"]!.GetValue<string>();
    }
}
