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

// Modell, das sich eine Dosierung ausdenkt
public sealed class InventingModel : HttpMessageHandler
{
    public const string Answer = "Empfehlung: Amoxicillin 1000 mg 1-0-1 für sieben Tage.";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["id"] = "x",
            ["object"] = "chat.completion",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = Answer },
                ["finish_reason"] = "stop",
            }),
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        });
    }
}

public sealed class InventingFactory : WebApplicationFactory<Program>
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"zwijg-check-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Zwijg:ApiKeys:0:Key"] = "admin-key",
            ["Zwijg:ApiKeys:0:User"] = "admin",
            ["Zwijg:ApiKeys:0:Admin"] = "true",
            ["Zwijg:Providers:Local:Type"] = "OpenAI",
            ["Zwijg:Providers:Local:BaseUrl"] = "http://lokal.invalid/v1",
            ["Zwijg:Providers:Local:Model"] = "lokal",
            ["Zwijg:Routing:Mode"] = "LocalOnly",
            ["Zwijg:Audit:DatabasePath"] = Path.Combine(_dataDir, "audit.db"),
            ["Zwijg:SettingsPath"] = Path.Combine(_dataDir, "settings.json"),
        }));
        builder.ConfigureTestServices(services =>
            services.AddHttpClient("provider").ConfigurePrimaryHttpMessageHandler(() => new InventingModel()));
    }
}

public class AnswerCheckEndpointTests(InventingFactory factory) : IClassFixture<InventingFactory>
{
    private HttpClient Client(bool webUi)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-key");
        if (webUi)
            client.DefaultRequestHeaders.Add("X-Requested-With", "zwijg");
        return client;
    }

    private static Task<HttpResponseMessage> Ask(HttpClient client) =>
        client.PostAsJsonAsync("/v1/chat/completions",
            new { model = "auto", messages = new[] { new { role = "user", content = "Was hilft bei einer Lungenentzündung?" } } });

    private static string Content(JsonObject body) => body["choices"]![0]!["message"]!["content"]!.GetValue<string>();

    [Fact]
    public async Task Andere_Programme_bekommen_den_Hinweis_als_Text()
    {
        var res = await Ask(Client(webUi: false));
        var body = (await res.Content.ReadFromJsonAsync<JsonObject>())!;

        Assert.Equal("3", res.Headers.GetValues("X-Zwijg-Check").Single());
        Assert.StartsWith(InventingModel.Answer, Content(body));
        Assert.Contains("Hinweis von Zwijg", Content(body));
        Assert.Contains("Amoxicillin, 1000 mg, 1-0-1", Content(body));
        Assert.Equal(3, body["zwijg_check"]!.AsArray().Count);
    }

    [Fact]
    public async Task Die_Oberflaeche_bekommt_nur_die_Fundstellen()
    {
        var res = await Ask(Client(webUi: true));
        var body = (await res.Content.ReadFromJsonAsync<JsonObject>())!;

        Assert.Equal(InventingModel.Answer, Content(body));
        var first = body["zwijg_check"]![0]!;
        Assert.Equal("Wirkstoff", first["kind"]!.GetValue<string>());
        Assert.Equal("Amoxicillin", InventingModel.Answer.Substring(first["start"]!.GetValue<int>(), first["length"]!.GetValue<int>()));
    }

    [Fact]
    public async Task Abgeschaltet_kein_Hinweis()
    {
        var admin = Client(webUi: true);
        var settings = (await admin.GetFromJsonAsync<JsonObject>("/admin/settings"))!["instructions"]!.AsObject();
        settings["checkAnswers"] = false;
        (await admin.PutAsJsonAsync("/admin/instructions", settings)).EnsureSuccessStatusCode();
        try
        {
            var res = await Ask(Client(webUi: false));
            var body = (await res.Content.ReadFromJsonAsync<JsonObject>())!;

            Assert.Equal(InventingModel.Answer, Content(body));
            Assert.Null(body["zwijg_check"]);
            Assert.False(res.Headers.Contains("X-Zwijg-Check"));
        }
        finally
        {
            settings["checkAnswers"] = true;
            (await admin.PutAsJsonAsync("/admin/instructions", settings)).EnsureSuccessStatusCode();
        }
    }
}
