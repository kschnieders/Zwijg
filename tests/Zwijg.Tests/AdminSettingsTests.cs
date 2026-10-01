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

    [Fact]
    public async Task Vorschau_mit_geaenderter_Adresse_schickt_den_gespeicherten_Schluessel_nicht_mit()
    {
        // Derselbe Beispielschlüssel wie oben, er steht in .gitleaks.toml auf der Liste der erlaubten
        const string secret = "sk-geheim-1234567890";
        using var listener = new HeaderListener();
        var saved = $"http://127.0.0.1:{listener.Port}/gespeichert/v1";
        var other = $"http://127.0.0.1:{listener.Port}/fremd/v1";

        (await Client().PostAsJsonAsync("/admin/connections", Connection("Vorschau Cloud", "OpenAI", false, secret, saved)))
            .EnsureSuccessStatusCode();
        var id = (await Settings())["connections"]!.AsArray()
            .First(c => c!["name"]!.GetValue<string>() == "Vorschau Cloud")!["id"]!.GetValue<string>();

        // Gleiche Adresse, nur mit Schrägstrich am Ende: der gespeicherte Schlüssel wird genommen
        (await Client().PostAsJsonAsync("/admin/connections/preview/test",
            new { id, connection = Connection("Vorschau Cloud", "OpenAI", false, null, saved + "/") })).EnsureSuccessStatusCode();
        Assert.Contains("Bearer " + secret, listener.Authorization.Last());

        // Andere Adresse: kein Schlüssel, sonst landet er beim fremden Server
        (await Client().PostAsJsonAsync("/admin/connections/preview/test",
            new { id, connection = Connection("Vorschau Cloud", "OpenAI", false, null, other) })).EnsureSuccessStatusCode();
        Assert.Equal(2, listener.Authorization.Count);
        Assert.Null(listener.Authorization.Last());

        // Der Test steht im Protokoll, mit der Adresse
        var audit = await Client().GetFromJsonAsync<JsonObject>("/admin/audit?action=admin&q=fremd");
        Assert.Contains(audit!["items"]!.AsArray(), e => e!["reason"]!.GetValue<string>().Contains(other));
    }

    [Fact]
    public async Task Gespeicherte_Verbindung_verliert_den_Schluessel_bei_neuer_Adresse_oder_neuem_Typ()
    {
        (await Client().PostAsJsonAsync("/admin/connections",
            Connection("Umzug Cloud", "OpenAI", false, "sk-umzug-1234567890", "https://api.example.com/v1"))).EnsureSuccessStatusCode();
        (await Client().PostAsJsonAsync("/admin/connections",
            Connection("Umzug Claude", "Anthropic", false, "sk-ant-umzug-0987654321"))).EnsureSuccessStatusCode();

        async Task<JsonObject> Named(string name) => (await Settings())["connections"]!.AsArray()
            .Select(c => c!.AsObject()).First(c => c["name"]!.GetValue<string>() == name);
        var cloud = (await Named("Umzug Cloud"))["id"]!.GetValue<string>();
        var claude = (await Named("Umzug Claude"))["id"]!.GetValue<string>();

        // Gleiche Adresse, nur mit Schrägstrich: Schlüssel bleibt
        (await Client().PutAsJsonAsync($"/admin/connections/{cloud}",
            Connection("Umzug Cloud", "OpenAI", false, null, "https://api.example.com/v1/"))).EnsureSuccessStatusCode();
        Assert.True((await Named("Umzug Cloud"))["hasApiKey"]!.GetValue<bool>());

        // Neue Adresse oder neuer Typ: Schlüssel muss neu eingegeben werden
        (await Client().PutAsJsonAsync($"/admin/connections/{cloud}",
            Connection("Umzug Cloud", "OpenAI", false, null, "https://fremd.example.org/v1"))).EnsureSuccessStatusCode();
        (await Client().PutAsJsonAsync($"/admin/connections/{claude}",
            Connection("Umzug Claude", "OpenAI", false, null, "https://fremd.example.org/v1"))).EnsureSuccessStatusCode();
        Assert.False((await Named("Umzug Cloud"))["hasApiKey"]!.GetValue<bool>());
        Assert.False((await Named("Umzug Claude"))["hasApiKey"]!.GetValue<bool>());

        // Der Umzug steht im Protokoll
        var reasons = await AdminReasons("fremd.example.org");
        Assert.Contains(reasons, r => r.Contains("Adresse https://api.example.com/v1/ → https://fremd.example.org/v1") && r.Contains("Schlüssel entfernt"));
        Assert.Contains(reasons, r => r.Contains("Typ Anthropic → OpenAI"));
    }

    [Fact]
    public async Task Zugangsdaten_in_der_Adresse_landen_nicht_im_Protokoll()
    {
        (await Client().PostAsJsonAsync("/admin/connections/preview/test", new
        {
            id = (string?)null,
            connection = Connection("Vorschau Geheim", "OpenAI", false, null, "https://admin:geheimpass@127.0.0.1:1/v1?api-key=geheimkey")
        })).EnsureSuccessStatusCode();

        var reasons = await AdminReasons("Vorschau Geheim");
        Assert.Contains(reasons, r => r.Contains("https://127.0.0.1:1/v1"));
        Assert.DoesNotContain(reasons, r => r.Contains("geheimpass") || r.Contains("geheimkey"));
    }

    // Texte der Admin Einträge, in denen der Suchbegriff vorkommt
    private async Task<List<string>> AdminReasons(string search)
    {
        var audit = await Client().GetFromJsonAsync<JsonObject>($"/admin/audit?action=admin&q={Uri.EscapeDataString(search)}");
        return audit!["items"]!.AsArray().Select(e => e!["reason"]!.GetValue<string>()).ToList();
    }

    // Nimmt Anfragen an und merkt sich den Authorization Header, antwortet wie ein OpenAI Server
    private sealed class HeaderListener : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _tcp = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public List<string?> Authorization { get; } = [];
        public int Port => ((IPEndPoint)_tcp.LocalEndpoint).Port;

        public HeaderListener()
        {
            _tcp.Start();
            _ = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _tcp.AcceptTcpClientAsync(_stop.Token);
                var stream = client.GetStream();
                var reader = new StreamReader(stream);

                string? auth = null;
                var length = 0;
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                {
                    if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                        auth = line;
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        length = int.Parse(line["Content-Length:".Length..].Trim());
                }
                var body = new char[length];
                if (length > 0)
                    await reader.ReadBlockAsync(body, 0, length);

                lock (Authorization)
                    Authorization.Add(auth);

                var json = """{"choices":[{"message":{"role":"assistant","content":"OK"}}]}""";
                var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {json.Length}\r\nConnection: close\r\n\r\n{json}";
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(response));
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _tcp.Stop();
        }
    }
}
