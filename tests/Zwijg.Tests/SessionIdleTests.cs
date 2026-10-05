using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Zwijg.Tests;

// Uhr, die der Test vorstellen kann
public sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class IdleFactory : WebApplicationFactory<Program>
{
    public TestClock Clock { get; } = new();
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"zwijg-idle-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Zwijg:ApiKeys:0:Key"] = "admin-key",
            ["Zwijg:ApiKeys:0:User"] = "admin",
            ["Zwijg:ApiKeys:0:Admin"] = "true",
            ["Zwijg:Providers:Local:Type"] = "Echo",
            ["Zwijg:Audit:DatabasePath"] = Path.Combine(_dataDir, "audit.db"),
            ["Zwijg:SettingsPath"] = Path.Combine(_dataDir, "settings.json"),
        }));
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Clock));
    }
}

// Abmelden nach Inaktivität gilt auch auf dem Server, nicht nur im offenen Browser Tab
public class SessionIdleTests(IdleFactory factory) : IClassFixture<IdleFactory>
{
    private async Task<HttpClient> LoggedIn()
    {
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-key");
        var name = "empfang" + Guid.NewGuid().ToString("N")[..6];
        (await admin.PostAsJsonAsync("/admin/users", new { name, admin = false, username = name, password = "Startpasswort-123", mustChangePassword = false })).EnsureSuccessStatusCode();

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "zwijg");
        (await client.PostAsJsonAsync("/auth/login", new { username = name, password = "Startpasswort-123", remember = true })).EnsureSuccessStatusCode();
        return client;
    }

    private static Task<HttpResponseMessage> Background(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/me");
        request.Headers.Add("X-Zwijg-Hintergrund", "1");
        return client.SendAsync(request);
    }

    [Fact]
    public async Task Ohne_Eingabe_endet_die_Sitzung_auch_mit_angemeldet_bleiben()
    {
        var client = await LoggedIn();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/me")).StatusCode);

        // Nur die Abfrage im Hintergrund, 31 Minuten lang
        for (var i = 0; i < 31; i++)
        {
            factory.Clock.Now += TimeSpan.FromMinutes(1);
            await Background(client);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Wer_arbeitet_bleibt_angemeldet()
    {
        var client = await LoggedIn();
        for (var i = 0; i < 6; i++)
        {
            factory.Clock.Now += TimeSpan.FromMinutes(20);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/me")).StatusCode);
        }
    }
}
