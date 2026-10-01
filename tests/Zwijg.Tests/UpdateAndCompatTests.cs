using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zwijg.Core.Storage;
using Zwijg.Gateway;
using Zwijg.Gateway.Settings;

namespace Zwijg.Tests;

// Nachgebautes GitHub, damit die Tests nicht ins Internet gehen
public sealed class FakeGitHub : HttpMessageHandler
{
    public int Calls;
    public Func<HttpResponseMessage> Respond { get; set; } = () => new HttpResponseMessage(HttpStatusCode.NotFound);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(Respond());
    }

    public static HttpResponseMessage Release(string tag) => Releases((tag, "Neu: bessere Erkennung", false));

    // Wie die Liste unter /releases, neueste nicht unbedingt zuerst
    public static HttpResponseMessage Releases(params (string Tag, string Body, bool Draft)[] list) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(list.Select(r => new
        {
            tag_name = r.Tag,
            html_url = $"https://github.com/kschnieders/zwijg/releases/tag/{r.Tag}",
            body = r.Body,
            draft = r.Draft,
            prerelease = false,
            published_at = "2026-10-01T10:00:00Z",
        })),
    };
}

public class UpdateFactory : WebApplicationFactory<Program>
{
    public FakeGitHub GitHub { get; } = new();
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"zwijg-it-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Zwijg:ApiKeys:0:Key"] = "admin-key",
            ["Zwijg:ApiKeys:0:User"] = "admin",
            ["Zwijg:ApiKeys:0:Admin"] = "true",
            ["Zwijg:ApiKeys:1:Key"] = "user-key",
            ["Zwijg:ApiKeys:1:User"] = "empfang",
            ["Zwijg:Providers:Local:Type"] = "Echo",
            ["Zwijg:Providers:Local:Model"] = "echo-lokal",
            ["Zwijg:Audit:DatabasePath"] = Path.Combine(_dataDir, "audit.db"),
            ["Zwijg:SettingsPath"] = Path.Combine(_dataDir, "settings.json"),
        }));
        builder.ConfigureTestServices(services =>
            services.AddHttpClient(UpdateChecker.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => GitHub));
    }
}

public class UpdateAndCompatTests(UpdateFactory factory) : IClassFixture<UpdateFactory>
{
    private HttpClient Client(string key = "admin-key")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private Task<JsonObject?> Version(bool refresh = true) =>
        Client().GetFromJsonAsync<JsonObject>("/admin/version" + (refresh ? "?refresh=true" : ""));

    [Theory]
    [InlineData("1.0.0", "0.9.9", true)]
    [InlineData("v1.10.0", "1.9.0", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("0.9.0", "1.0.0", false)]
    [InlineData("1.2.0-beta", "1.1.0", true)]
    [InlineData("kaputt", "1.0.0", false)]
    public void Versionen_vergleichen(string latest, string current, bool newer) =>
        Assert.Equal(newer, UpdateChecker.IsNewer(latest, current));

    [Fact]
    public async Task Neuere_Version_wird_gemeldet()
    {
        factory.GitHub.Respond = () => FakeGitHub.Release("v99.0.0");

        var v = await Version();

        Assert.True(v!["updateAvailable"]!.GetValue<bool>());
        Assert.Equal("99.0.0", v["latest"]!.GetValue<string>());
        Assert.Equal("Neu: bessere Erkennung", v["notes"]!.GetValue<string>());
        Assert.Equal(Endpoints.Version, v["current"]!.GetValue<string>());
    }

    [Fact]
    public async Task Alle_verpassten_Versionen_und_Sicherheitsupdates_werden_gemeldet()
    {
        factory.GitHub.Respond = () => FakeGitHub.Releases(
            ("v97.0.0", "### Neu\n\n- Diktieren", false),
            ("v99.0.0", "### Verbesserungen\n\n- Schneller", false),
            ("v98.0.0", "### Sicherheit\n\n- Lücke geschlossen", false),
            ("v100.0.0", "### Sicherheit\n\n- Entwurf", true),
            ("v0.0.1", "### Sicherheit\n\n- Uralt", false));

        var v = await Version();

        Assert.Equal("99.0.0", v!["latest"]!.GetValue<string>());
        Assert.True(v["security"]!.GetValue<bool>());
        var versions = v["releases"]!.AsArray().Select(r => r!["version"]!.GetValue<string>());
        Assert.Equal(["99.0.0", "98.0.0", "97.0.0"], versions);
        Assert.True(v["releases"]![1]!["security"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Ohne_Sicherheitsabschnitt_kein_Sicherheitsupdate()
    {
        factory.GitHub.Respond = () => FakeGitHub.Release("v99.0.0");

        var v = await Version();

        Assert.True(v!["updateAvailable"]!.GetValue<bool>());
        Assert.False(v["security"]!.GetValue<bool>());
    }

    [Fact]
    public void Abschnitt_einer_Version_aus_dem_Changelog()
    {
        const string md = "# Änderungen\n\n## 1.1.0 (2026-10-02)\n\n### Sicherheit\n\n- A\n\n## 1.0.0 (2026-09-29)\n\n### Neu\n\n- B\n";

        Assert.Equal("### Sicherheit\n\n- A", Changelog.Section(md, "1.1.0"));
        Assert.Equal("### Neu\n\n- B", Changelog.Section(md, "1.0.0"));
        Assert.Null(Changelog.Section(md, "1.0"));
        Assert.Null(Changelog.Section(md, "2.0.0"));
        Assert.True(Changelog.IsSecurity(Changelog.Section(md, "1.1.0")));
        Assert.False(Changelog.IsSecurity("Mehr Sicherheit beim Anmelden"));
    }

    [Fact]
    public async Task Changelog_der_installierten_Version_ohne_Anmeldung()
    {
        var res = await factory.CreateClient().GetFromJsonAsync<JsonObject>("/changelog");

        Assert.Equal(Endpoints.Version, res!["version"]!.GetValue<string>());
        Assert.True(File.Exists(Changelog.FilePath));
    }

    [Fact]
    public async Task Ohne_Veroeffentlichung_und_ohne_Netz_kein_Fehler_in_der_App()
    {
        factory.GitHub.Respond = () => new HttpResponseMessage(HttpStatusCode.NotFound);
        var none = await Version();
        Assert.False(none!["updateAvailable"]!.GetValue<bool>());
        Assert.Null(none["error"]);

        factory.GitHub.Respond = () => throw new HttpRequestException("kein Netz");
        var offline = await Version();
        Assert.False(offline!["updateAvailable"]!.GetValue<bool>());
        Assert.NotNull(offline["error"]);
    }

    [Fact]
    public async Task Nur_Admins_sehen_die_Version()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("user-key").GetAsync("/admin/version")).StatusCode);
    }

    [Fact]
    public async Task Ausgeschaltet_fragt_nur_auf_Knopfdruck()
    {
        (await Client().PutAsJsonAsync("/admin/version/check", new { enabled = false })).EnsureSuccessStatusCode();
        try
        {
            var before = factory.GitHub.Calls;
            var v = await Version(refresh: false);
            Assert.False(v!["enabled"]!.GetValue<bool>());
            Assert.Equal(before, factory.GitHub.Calls);

            await Version(refresh: true);
            Assert.Equal(before + 1, factory.GitHub.Calls);
        }
        finally
        {
            (await Client().PutAsJsonAsync("/admin/version/check", new { enabled = true })).EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task Streaming_kommt_als_ein_Block_im_OpenAI_Format()
    {
        var res = await Client("user-key").PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "auto",
            stream = true,
            messages = new[] { new { role = "user", content = "Herr Max Mustermann hat Fieber" } },
        });

        res.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType!.MediaType);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("\"object\":\"chat.completion.chunk\"", body);
        Assert.Contains("Mustermann", body);
        Assert.EndsWith("data: [DONE]\n\n", body);
    }

    [Fact]
    public async Task Weboberflaeche_hat_Sicherheitsheader()
    {
        var res = await factory.CreateClient().GetAsync("/");
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("script-src 'self'", res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
    }

    // Einstellungen: alte Dateien werden umgestellt und gesichert, neuere werden gelesen und nicht zerstört

    private static SettingsStore Store(string path) => new(
        Options.Create(new GatewayOptions { SettingsPath = path }),
        new EphemeralDataProtectionProvider(),
        NullLogger<SettingsStore>.Instance);

    private static string SettingsFile(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"zwijg-compat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, json);
        return path;
    }

    private const string OldSettings = """
        { "users": [ { "id": "a1", "name": "Dr. Anna Weber", "keyHash": "ABC", "admin": true } ] }
        """;

    [Fact]
    public void Alte_Einstellungen_werden_umgestellt_und_vorher_gesichert()
    {
        var path = SettingsFile(OldSettings);

        var store = Store(path);

        Assert.Equal(SettingsStore.CurrentSchema, store.Current.SchemaVersion);
        Assert.Equal("anna.weber", store.Current.Users.Single().Username);
        Assert.True(File.Exists(path + ".v0.bak"));
        Assert.Equal(OldSettings, File.ReadAllText(path + ".v0.bak"));
    }

    [Fact]
    public void Fremde_Vorlagen_Ids_aus_alten_Einstellungen_werden_ersetzt()
    {
        var path = SettingsFile("""
            { "schemaVersion": 1,
              "users": [ { "id": "a1", "name": "admin", "username": "admin", "keyHash": "ABC", "admin": true } ],
              "templates": [ { "id": "x\"><a href=\"https://example.com\">Hier klicken</a>", "title": "Alt", "text": "Hallo" },
                             { "id": "0123456789", "title": "Gut", "text": "Hallo" } ] }
            """);

        var store = Store(path);

        Assert.All(store.Current.Templates, t => Assert.Matches("^[0-9a-f]{10}$", t.Id));
        Assert.Equal("0123456789", store.Current.Templates[1].Id);
        Assert.DoesNotContain("example.com", File.ReadAllText(path));
    }

    [Fact]
    public void Einstellungen_einer_neueren_Version_lassen_sich_lesen()
    {
        var path = SettingsFile("""
            { "schemaVersion": 999, "feldAusDerZukunft": { "x": 1 },
              "users": [ { "id": "a1", "name": "admin", "username": "admin", "keyHash": "ABC", "admin": true } ] }
            """);

        var store = Store(path);

        Assert.Equal("admin", store.Current.Users.Single().Name);
        // Nichts wurde zurückgeschrieben, die Datei bleibt für die neuere Version intakt
        Assert.Contains("feldAusDerZukunft", File.ReadAllText(path));
    }

    // Datenbanken: jeder Schritt läuft genau einmal

    [Fact]
    public void Datenbank_Schritte_laufen_genau_einmal()
    {
        var file = Path.Combine(Path.GetTempPath(), $"zwijg-schema-{Guid.NewGuid():N}.db");
        using var con = new SqliteConnection($"Data Source={file};Pooling=False");
        con.Open();

        const string step1 = "CREATE TABLE IF NOT EXISTS T (Id INTEGER PRIMARY KEY)";
        const string step2 = "ALTER TABLE T ADD COLUMN Name TEXT";

        Assert.Equal(0, SqliteSchema.Migrate(con, step1));
        Assert.Equal(1, SqliteSchema.Migrate(con, step1, step2));
        // Ein zweites ALTER würde scheitern, also darf Schritt 2 nicht noch einmal laufen
        Assert.Equal(2, SqliteSchema.Migrate(con, step1, step2));
    }
}
