using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Zwijg.Tests;

// Schreibt mit, was beim Cloud Anbieter ankommt, und antwortet wie OpenAI
public sealed class RecordingHandler : HttpMessageHandler
{
    public List<string> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
        lock (Bodies) Bodies.Add(body);

        const string answer = """{"id":"x","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"Erledigt"},"finish_reason":"stop"}]}""";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
    }

    // Nur was ab Stelle "from" ankam, damit sich Tests nicht gegenseitig stören
    public string Since(int from)
    {
        lock (Bodies) return string.Join("\n", Bodies.Skip(from));
    }
}

public sealed class ForwardingFactory : WebApplicationFactory<Program>
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"zwijg-fw-{Guid.NewGuid():N}");
    public RecordingHandler Cloud { get; } = new();

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
            ["Zwijg:Providers:Cloud:Type"] = "OpenAI",
            ["Zwijg:Providers:Cloud:BaseUrl"] = "http://cloud.invalid/v1",
            ["Zwijg:Providers:Cloud:ApiKey"] = "sk-test",
            ["Zwijg:Providers:Cloud:Model"] = "cloud-modell",
            ["Zwijg:Audit:DatabasePath"] = Path.Combine(_dataDir, "audit.db"),
            ["Zwijg:SettingsPath"] = Path.Combine(_dataDir, "settings.json"),
        }));
        builder.ConfigureTestServices(services =>
            services.AddHttpClient("provider").ConfigurePrimaryHttpMessageHandler(() => Cloud));
    }
}

public class ForwardingTests(ForwardingFactory factory) : IClassFixture<ForwardingFactory>
{
    private HttpClient Client()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "user-key");
        return client;
    }

    private Task<HttpResponseMessage> Send(string json) =>
        Client().PostAsync("/v1/chat/completions", new StringContent(json, Encoding.UTF8, "application/json"));

    [Theory]
    // Tool Aufruf des Modells mit Patientendaten in den Argumenten
    [InlineData("""{"model":"auto","messages":[{"role":"user","content":"Bitte Termin eintragen"},{"role":"assistant","content":null,"tool_calls":[{"id":"c1","type":"function","function":{"name":"termin","arguments":"{\"patient\":\"Max Mustermann\"}"}}]},{"role":"user","content":"Danke"}]}""")]
    // Tool Beschreibung mit Patientendaten
    [InlineData("""{"model":"auto","messages":[{"role":"user","content":"Bitte Termin eintragen"}],"tools":[{"type":"function","function":{"name":"termin","description":"Termin für Max Mustermann"}}]}""")]
    [InlineData("""{"model":"auto","messages":[{"role":"user","content":"Bitte Termin eintragen"}],"functions":[{"name":"termin","description":"Termin für Max Mustermann"}]}""")]
    [InlineData("""{"model":"auto","messages":[{"role":"user","content":"Bitte Brief schreiben"}],"prediction":{"type":"content","content":"Sehr geehrter Herr Max Mustermann"}}""")]
    [InlineData("""{"model":"auto","messages":[{"role":"user","content":"Bitte Brief schreiben"}],"response_format":{"type":"json_schema","json_schema":{"name":"brief","description":"Brief an Max Mustermann","schema":{}}}}""")]
    public async Task Felder_ausserhalb_der_Nachrichten_werden_abgelehnt(string json)
    {
        var before = factory.Cloud.Bodies.Count;

        var res = await Send(json);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("nicht unterstützt", await res.Content.ReadAsStringAsync());
        Assert.Equal(before, factory.Cloud.Bodies.Count);
    }

    [Theory]
    // Name des Sprechers in der Nachricht
    [InlineData("""{"model":"auto","messages":[{"role":"user","name":"Max Mustermann","content":"Bitte kurz zusammenfassen: Termin am Montag"}]}""")]
    // Metadaten der Anfrage
    [InlineData("""{"model":"auto","messages":[{"role":"user","content":"Bitte kurz zusammenfassen: Termin am Montag"}],"metadata":{"patient":"Max Mustermann"}}""")]
    public async Task Unbekannte_Felder_gehen_nicht_an_den_Anbieter(string json)
    {
        var before = factory.Cloud.Bodies.Count;

        var res = await Send(json);

        res.EnsureSuccessStatusCode();
        Assert.Equal("Cloud", res.Headers.GetValues("X-Zwijg-Route").Single());
        Assert.DoesNotContain("Mustermann", factory.Cloud.Since(before));
    }

    [Theory]
    [InlineData("""["Max Mustermann"]""")]
    [InlineData("\"Max Mustermann\"")]
    public async Task Stop_wird_wie_die_Nachrichten_geschuetzt(string stop)
    {
        var before = factory.Cloud.Bodies.Count;

        var res = await Send($$"""{"model":"auto","messages":[{"role":"user","content":"Was ist Ibuprofen?"}],"stop":{{stop}}}""");

        res.EnsureSuccessStatusCode();
        Assert.DoesNotContain("Mustermann", factory.Cloud.Since(before));
    }

    [Theory]
    [InlineData("Max Mustermann")]
    [InlineData("User")]
    [InlineData("tool")]
    [InlineData("function")]
    [InlineData("")]
    public async Task Nur_bekannte_Rollen_sind_erlaubt(string role)
    {
        var before = factory.Cloud.Bodies.Count;

        var res = await Send($$"""{"model":"auto","messages":[{"role":"{{role}}","content":"Was ist Ibuprofen?"}]}""");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("role muss system, developer, user, assistant sein", await res.Content.ReadAsStringAsync());
        Assert.Equal(before, factory.Cloud.Bodies.Count);
    }

    [Fact]
    public async Task Leere_Tool_Liste_stoert_nicht()
    {
        var res = await Send("""{"model":"auto","messages":[{"role":"user","content":"Was ist Ibuprofen?"}],"tools":[]}""");

        res.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Developer_Rolle_geht_als_System_raus()
    {
        var before = factory.Cloud.Bodies.Count;

        var res = await Send("""{"model":"auto","messages":[{"role":"developer","content":"Antworte kurz."},{"role":"user","content":"Was ist Ibuprofen?"}]}""");

        res.EnsureSuccessStatusCode();
        var roles = JsonNode.Parse(factory.Cloud.Bodies[before])!["messages"]!.AsArray().Select(m => m!["role"]!.GetValue<string>());
        Assert.Equal(["system", "user"], roles);
    }

    [Fact]
    public async Task Grosser_Seed_ist_erlaubt()
    {
        var before = factory.Cloud.Bodies.Count;

        var res = await Send("""{"model":"auto","messages":[{"role":"user","content":"Was ist Ibuprofen?"}],"seed":10000000000}""");

        res.EnsureSuccessStatusCode();
        Assert.Equal(10000000000L, JsonNode.Parse(factory.Cloud.Bodies[before])!["seed"]!.GetValue<long>());
    }

    [Fact]
    public async Task Erlaubte_Einstellungen_werden_weitergegeben()
    {
        var before = factory.Cloud.Bodies.Count;

        var res = await Send("""{"model":"auto","messages":[{"role":"user","content":"Was ist Ibuprofen?"}],"temperature":0.3,"max_tokens":50,"stop":["ENDE"],"response_format":{"type":"json_object"}}""");

        res.EnsureSuccessStatusCode();
        var sent = JsonNode.Parse(factory.Cloud.Bodies[before])!.AsObject();
        Assert.Equal(0.3, sent["temperature"]!.GetValue<double>());
        Assert.Equal(50, sent["max_tokens"]!.GetValue<int>());
        Assert.Equal("ENDE", sent["stop"]![0]!.GetValue<string>());
        Assert.Equal("json_object", sent["response_format"]!["type"]!.GetValue<string>());
        Assert.Equal("cloud-modell", sent["model"]!.GetValue<string>());
        Assert.All(sent["messages"]!.AsArray(), m => Assert.Equal(["role", "content"], m!.AsObject().Select(p => p.Key)));
    }

    [Theory]
    [InlineData("""{"model":"auto","messages":[{"role":1,"content":"Hallo"}]}""")]
    [InlineData("""{"model":"auto","messages":[{"role":{"x":1},"content":"Hallo"}]}""")]
    [InlineData("""{"model":"auto","messages":[{"role":"user","content":[{"type":1,"text":"Hallo"}]}]}""")]
    [InlineData("""{"model":"auto","messages":[{"role":"user","content":[{"type":"text","text":{"x":1}}]}]}""")]
    [InlineData("""{"model":"auto","messages":[{"role":"user","content":[null]}]}""")]
    [InlineData("""{"model":"auto","messages":[null]}""")]
    [InlineData("""{"model":"auto","messages":["x"]}""")]
    [InlineData("""{"model":1,"messages":[{"role":"user","content":"Hallo"}]}""")]
    [InlineData("""{"model":"auto","messages":[{"role":"user","content":"Hallo"}],"max_tokens":"viel"}""")]
    [InlineData("""{"model":"auto","messages":[{"role":"user","content":"Hallo"}],"temperature":[1]}""")]
    [InlineData("""{"model":"auto","messages":[{"role":"user","content":"Hallo"}],"max_tokens":1.5}""")]
    [InlineData("""{"model":"auto","messages":[{"role":"user","content":"Hallo"}],"max_tokens":1e12}""")]
    [InlineData("""{"model":"auto","messages":[{"role":"user","content":"Hallo"}],"n":1.5}""")]
    [InlineData("""{"model":"auto","messages":[{"role":"user","content":"Hallo"}],"seed":1e30}""")]
    public async Task Ungueltige_Form_gibt_400_statt_500(string json)
    {
        var res = await Send(json);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Verlauf_ohne_Nutzernachricht_verliert_die_Antwort_nicht()
    {
        var res = await Send("""{"model":"auto","messages":[{"role":"system","content":"Sag Hallo"}],"zwijg":{"conversation":"new"}}""");

        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("Erledigt", json!["choices"]![0]!["message"]!["content"]!.GetValue<string>());
    }
}

// Eigene Instanz, weil die Schutzregeln hier für die ganze Klasse gelten
public class RoleRuleTests(ForwardingFactory factory) : IClassFixture<ForwardingFactory>
{
    private HttpClient Client(string key = "user-key")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private async Task<HttpResponseMessage> Send(string role, string content)
    {
        var rules = new object[]
        {
            new { name = "Passwörter", patterns = "Passwort", action = "Block", isRegex = false, caseSensitive = false, label = (string?)null, message = "Bitte keine Zugangsdaten eingeben.", enabled = true },
            new { name = "Besonders sensibel", patterns = "HIV", action = "LocalOnly", isRegex = false, caseSensitive = false, label = (string?)null, message = (string?)null, enabled = true },
        };
        (await Client("admin-key").PutAsJsonAsync("/admin/rules", rules)).EnsureSuccessStatusCode();

        var json = $$"""{"model":"auto","messages":[{"role":"{{role}}","content":"{{content}}"},{"role":"user","content":"Wie geht es weiter?"}]}""";
        return await Client().PostAsync("/v1/chat/completions", new StringContent(json, Encoding.UTF8, "application/json"));
    }

    [Theory]
    [InlineData("system")]
    [InlineData("user")]
    public async Task Block_Regel_gilt_auch_fuer_Systemnachrichten(string role)
    {
        var before = factory.Cloud.Bodies.Count;

        var res = await Send(role, "Mein Passwort ist geheim123");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.DoesNotContain("geheim123", factory.Cloud.Since(before));
    }

    [Theory]
    [InlineData("system")]
    [InlineData("user")]
    [InlineData("assistant")]
    public async Task Nur_lokal_Regel_gilt_fuer_alle_Rollen(string role)
    {
        var before = factory.Cloud.Bodies.Count;

        var res = await Send(role, "Patient hat HIV.");

        res.EnsureSuccessStatusCode();
        Assert.Equal("Local", res.Headers.GetValues("X-Zwijg-Route").Single());
        Assert.DoesNotContain("HIV", factory.Cloud.Since(before));
    }

    [Fact]
    public async Task Gesperrtes_Wort_in_einer_Antwort_blockiert_das_Gespraech_nicht()
    {
        var res = await Send("assistant", "Bitte nie das Passwort weitergeben.");

        res.EnsureSuccessStatusCode();
    }
}
