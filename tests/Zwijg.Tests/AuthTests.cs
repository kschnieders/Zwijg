using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Zwijg.Tests;

// Anmeldung mit Benutzername und Passwort, Sitzungen und ihre Sicherheitsregeln
public class AuthTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    private HttpClient Admin()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-key");
        return client;
    }

    // Eigener Client pro "Browser", damit jeder seine eigenen Cookies hat
    private HttpClient Browser(bool withHeader = true)
    {
        var client = factory.CreateClient();
        if (withHeader)
            client.DefaultRequestHeaders.Add("X-Requested-With", "zwijg");
        return client;
    }

    private async Task<string> CreateUser(string username, string password, bool mustChange = false)
    {
        (await Admin().PostAsJsonAsync("/admin/users", new { name = username, username, admin = false, password, mustChangePassword = mustChange }))
            .EnsureSuccessStatusCode();
        var settings = await Admin().GetFromJsonAsync<JsonObject>("/admin/settings");
        return settings!["users"]!.AsArray().First(u => u!["username"]!.GetValue<string>() == username)!["id"]!.GetValue<string>();
    }

    private static Task<HttpResponseMessage> Login(HttpClient c, string username, string password, bool remember = false) =>
        c.PostAsJsonAsync("/auth/login", new { username, password, remember });

    [Fact]
    public async Task Anmelden_Abmelden_und_falsches_Passwort()
    {
        await CreateUser("login.test", "Sommer-Regen-2026");
        var b = Browser();

        Assert.Equal(HttpStatusCode.Unauthorized, (await b.GetAsync("/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(b, "login.test", "falsch-falsch")).StatusCode);

        (await Login(b, "LOGIN.TEST", "Sommer-Regen-2026")).EnsureSuccessStatusCode();
        var me = await b.GetFromJsonAsync<JsonObject>("/v1/me");
        Assert.Equal("login.test", me!["username"]!.GetValue<string>());
        Assert.True(me["viaSession"]!.GetValue<bool>());

        (await b.PostAsync("/auth/logout", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.GetAsync("/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Aendernde_Anfragen_brauchen_die_Kennung()
    {
        await CreateUser("csrf.test", "Sommer-Regen-2026");
        var noHeader = Browser(withHeader: false);
        (await Login(noHeader, "csrf.test", "Sommer-Regen-2026")).EnsureSuccessStatusCode();

        var res = await noHeader.PostAsJsonAsync("/v1/check", new { text = "Hallo" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        noHeader.DefaultRequestHeaders.Add("X-Requested-With", "zwijg");
        (await noHeader.PostAsJsonAsync("/v1/check", new { text = "Hallo" })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Startpasswort_muss_zuerst_geaendert_werden()
    {
        await CreateUser("start.test", "Start-Passwort-1", mustChange: true);
        var b = Browser();
        var login = await (await Login(b, "start.test", "Start-Passwort-1")).Content.ReadFromJsonAsync<JsonObject>();
        Assert.True(login!["mustChangePassword"]!.GetValue<bool>());

        Assert.Equal(HttpStatusCode.Forbidden, (await b.PostAsJsonAsync("/v1/check", new { text = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.PostAsJsonAsync("/v1/account/password", new { @new = "kurz" })).StatusCode);

        (await b.PostAsJsonAsync("/v1/account/password", new { @new = "Mein-neues-Passwort-7" })).EnsureSuccessStatusCode();
        (await b.PostAsJsonAsync("/v1/check", new { text = "x" })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Passwortwechsel_meldet_andere_Sitzungen_ab()
    {
        await CreateUser("zwei.geraete", "Erstes-Passwort-1");
        var pc = Browser();
        var laptop = Browser();
        (await Login(pc, "zwei.geraete", "Erstes-Passwort-1")).EnsureSuccessStatusCode();
        (await Login(laptop, "zwei.geraete", "Erstes-Passwort-1")).EnsureSuccessStatusCode();

        (await pc.PostAsJsonAsync("/v1/account/password", new { current = "Erstes-Passwort-1", @new = "Zweites-Passwort-2" })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await pc.GetAsync("/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await laptop.GetAsync("/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Sperre_beendet_laufende_Sitzung_sofort()
    {
        var id = await CreateUser("sperr.login", "Sommer-Regen-2026");
        var b = Browser();
        (await Login(b, "sperr.login", "Sommer-Regen-2026")).EnsureSuccessStatusCode();

        (await Admin().PutAsJsonAsync($"/admin/users/{id}", new { name = "sperr.login", admin = false, active = false })).EnsureSuccessStatusCode();

        Assert.NotEqual(HttpStatusCode.OK, (await b.GetAsync("/v1/me")).StatusCode);
        var again = await Login(Browser(), "sperr.login", "Sommer-Regen-2026");
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
    }

    [Fact]
    public async Task Durchprobieren_wird_gebremst()
    {
        await CreateUser("brute.test", "Sommer-Regen-2026");
        var b = Browser();

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(b, "brute.test", $"falsch-{i}-xxxxx")).StatusCode);

        // Selbst das richtige Passwort hilft während der Sperre nicht
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Login(b, "brute.test", "Sommer-Regen-2026")).StatusCode);
    }

    [Fact]
    public async Task Zuruecksetzen_liefert_Startpasswort_und_Passwort_steht_nirgends_im_Klartext()
    {
        var id = await CreateUser("reset.test", "Geheimes-Passwort-99");

        var reset = await (await Admin().PostAsync($"/admin/users/{id}/password-reset", null)).Content.ReadFromJsonAsync<JsonObject>();
        var temp = reset!["password"]!.GetValue<string>();

        var b = Browser();
        var login = await (await Login(b, "reset.test", temp)).Content.ReadFromJsonAsync<JsonObject>();
        Assert.True(login!["mustChangePassword"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(Browser(), "reset.test", "Geheimes-Passwort-99")).StatusCode);

        var file = await File.ReadAllTextAsync(Path.Combine(factory.DataDir, "settings.json"));
        Assert.DoesNotContain(temp, file);
        Assert.DoesNotContain("Geheimes-Passwort-99", file);
        Assert.Contains("pbkdf2-sha256$", file);
    }

    [Fact]
    public async Task Zugangsschluessel_funktioniert_weiter()
    {
        var res = await Admin().GetAsync("/v1/me");
        res.EnsureSuccessStatusCode();
        Assert.False((await res.Content.ReadFromJsonAsync<JsonObject>())!["viaSession"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Status_und_Version_ohne_Anmeldung()
    {
        // Das Über Fenster zeigt die Version auch auf der Anmeldeseite
        var health = await factory.CreateClient().GetFromJsonAsync<JsonObject>("/health");
        Assert.Equal("ok", health!["status"]!.GetValue<string>());
        Assert.Matches(@"^\d+\.\d+\.\d+", health["version"]!.GetValue<string>());
    }
}
