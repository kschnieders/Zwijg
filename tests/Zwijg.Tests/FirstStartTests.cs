using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zwijg.Gateway;
using Zwijg.Gateway.Settings;

namespace Zwijg.Tests;

// Erster Start ohne konfigurierten Admin: Zwijg legt einen an und zeigt den Startschlüssel
public sealed class FirstStartFactory : WebApplicationFactory<Program>
{
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), $"zwijg-first-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Zwijg:Providers:Local:Type"] = "Echo",
            ["Zwijg:Audit:DatabasePath"] = Path.Combine(DataDir, "audit.db"),
            ["Zwijg:SettingsPath"] = Path.Combine(DataDir, "settings.json"),
        }));
    }
}

public class FirstStartTests(FirstStartFactory factory) : IClassFixture<FirstStartFactory>
{
    private static SettingsStore Store(string dir) => new(
        Options.Create(new GatewayOptions { SettingsPath = Path.Combine(dir, "settings.json") }),
        new EphemeralDataProtectionProvider(),
        NullLogger<SettingsStore>.Instance);

    [Fact]
    public void Startschluessel_kommt_bei_jedem_Start_neu_bis_er_benutzt_wurde()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"zwijg-start-{Guid.NewGuid():N}");

        var first = Store(dir);
        var key1 = first.StartKeyToShow;
        Assert.NotNull(key1);
        var admin = first.Current.Users.Single();
        Assert.True(admin.Admin && admin.StartKey);
        Assert.Equal(SettingsStore.HashKey(key1!), admin.KeyHash);

        // Erster Start abgebrochen, zweiter Start: neuer Schlüssel, der alte gilt nicht mehr
        var second = Store(dir);
        var key2 = second.StartKeyToShow;
        Assert.NotNull(key2);
        Assert.NotEqual(key1, key2);
        Assert.Equal(SettingsStore.HashKey(key2!), second.Current.Users.Single().KeyHash);

        // Nach der ersten Anmeldung bleibt er
        second.StartKeyUsed(second.Current.Users.Single().Id);
        var third = Store(dir);
        Assert.Null(third.StartKeyToShow);
        Assert.Equal(SettingsStore.HashKey(key2!), third.Current.Users.Single().KeyHash);
    }

    [Fact]
    public async Task Anmeldung_mit_dem_Startschluessel_beendet_die_Erneuerung()
    {
        var store = factory.Services.GetRequiredService<SettingsStore>();
        var key = store.StartKeyToShow;
        Assert.NotNull(key);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/me")).StatusCode);

        Assert.False(store.Current.Users.Single().StartKey);
    }

    [Fact]
    public void Belegter_Port_wird_erkannt()
    {
        var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        try
        {
            var port = ((IPEndPoint)blocker.LocalEndpoint).Port;
            var url = $"http://localhost:{port}";

            Assert.Equal(url, StartupChecks.FindBusyUrl($"http://zwijg.praxis:81;{url}"));
            var message = StartupChecks.BusyMessage(url);
            Assert.Contains($"Port {port} ist schon belegt", message);
            Assert.Contains($"http://localhost:{port}", message);
            Assert.Contains("\"Urls\"", message);
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public void Freier_Port_und_Anzeige_der_Adresse()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        Assert.Null(StartupChecks.FindBusyUrl($"http://127.0.0.1:{port}"));
        Assert.Equal("http://localhost:5050", StartupChecks.Shown("http://0.0.0.0:5050"));
        Assert.Equal("http://localhost:5050", StartupChecks.Shown("http://[::]:5050"));
        Assert.Equal("http://localhost:8080", StartupChecks.Shown("http://+:8080"));

        var banner = StartupChecks.ReadyBanner("http://127.0.0.1:5050", "zw_abc");
        Assert.Contains("http://localhost:5050", banner);
        Assert.Contains("zw_abc", banner);
        Assert.DoesNotContain("Startschlüssel", StartupChecks.ReadyBanner("http://localhost:5050", null));
    }
}
