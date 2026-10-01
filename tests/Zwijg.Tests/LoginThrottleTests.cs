using System.Collections;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Zwijg.Gateway;

namespace Zwijg.Tests;

// Die Bremse für Fehlversuche darf selbst nicht zum Speicherfresser werden
public class LoginThrottleTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 3, 12, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static int Tracked(LoginThrottle throttle) =>
        ((ICollection)typeof(LoginThrottle).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(throttle)!).Count;

    [Fact]
    public async Task Ueberlanger_Benutzername_wird_sofort_abgewiesen_und_nicht_gemerkt()
    {
        var throttle = factory.Services.GetRequiredService<LoginThrottle>();
        var before = Tracked(throttle);

        var res = await factory.CreateClient().PostAsJsonAsync("/auth/login",
            new { username = new string('a', 2_000_000), password = "falsch-falsch", remember = false });

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal(before, Tracked(throttle));
    }

    [Fact]
    public void Alte_Eintraege_werden_aufgeraeumt()
    {
        var time = new ManualTime();
        var throttle = new LoginThrottle(time);

        for (var i = 0; i < 1000; i++)
            throttle.Failed($"benutzer{i}|127.0.0.1");
        Assert.Equal(1000, Tracked(throttle));

        // Nach einer Stunde ohne Fehlversuch braucht es die Einträge nicht mehr
        time.Now += TimeSpan.FromHours(1);
        throttle.Failed("neu|127.0.0.1");

        Assert.Equal(1, Tracked(throttle));
    }

    [Fact]
    public void Zaehler_beginnt_nach_ruhiger_Zeit_von_vorn()
    {
        var time = new ManualTime();
        var throttle = new LoginThrottle(time);
        const string key = "max.mustermann|127.0.0.1";

        for (var i = 0; i < 5; i++)
            throttle.Failed(key);
        Assert.NotNull(throttle.LockedFor(key));

        // Lange nach Ende der Sperre zählt ein einzelner Fehlversuch wieder als erster
        time.Now += TimeSpan.FromHours(1);
        Assert.Null(throttle.LockedFor(key));
        throttle.Failed(key);
        Assert.Null(throttle.LockedFor(key));
    }

    [Fact]
    public void Wiederholte_Fehlversuche_verlaengern_die_Sperre_weiter()
    {
        var time = new ManualTime();
        var throttle = new LoginThrottle(time);
        const string key = "max.mustermann|127.0.0.1";

        for (var i = 0; i < 5; i++)
            throttle.Failed(key);
        var first = throttle.LockedFor(key)!.Value;

        // Direkt nach der Sperre geht es mit längerer Sperre weiter, nicht mit 5 freien Versuchen
        time.Now += first + TimeSpan.FromSeconds(1);
        throttle.Failed(key);

        Assert.True(throttle.LockedFor(key) > first);
    }
}
