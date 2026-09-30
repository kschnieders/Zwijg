using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Zwijg.Core.Pseudonymization;

namespace Zwijg.Tests;

// "Text schützen": geschützte Fassung zum Kopieren, Zuordnung nur für den Browser, dieselben Regeln wie für die Cloud
public class ProtectTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    private HttpClient Client(string key = "user-key")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private async Task<(HttpStatusCode Status, JsonObject Body)> Protect(object body, string key = "user-key")
    {
        var res = await Client(key).PostAsJsonAsync("/v1/protect", body);
        return (res.StatusCode, (await res.Content.ReadFromJsonAsync<JsonObject>())!);
    }

    private static string Value(JsonObject body, string placeholder) =>
        body["mapping"]!.AsArray().First(e => e!["placeholder"]!.GetValue<string>() == placeholder)!["value"]!.GetValue<string>();

    [Fact]
    public void Zuordnung_aus_frueherer_Runde_wird_weitergezaehlt()
    {
        var map = new PseudonymMap();
        map.Seed("[NAME_1]", "Mustermann");
        map.Seed("[NAME_7]", "Max");
        map.Seed("kaputt", "egal");

        Assert.Equal("[NAME_1]", map.GetOrAdd(EntityType.Name, "Mustermann"));
        Assert.Equal("[NAME_8]", map.GetOrAdd(EntityType.Name, "Erika"));
        Assert.Equal(3, map.Entries.Count);
    }

    [Fact]
    public async Task Text_wird_geschuetzt_und_Namen_behalten_ihren_Platzhalter()
    {
        var (status, first) = await Protect(new { text = "Bitte einen Termin für Herrn Max Mustermann am Montag vorschlagen." });

        Assert.Equal(HttpStatusCode.OK, status);
        var text = first["protected"]!.GetValue<string>();
        Assert.DoesNotContain("Mustermann", text);
        Assert.Equal("Mustermann", Value(first, "[NAME_2]"));

        // Zweite Runde mit der Zuordnung aus der ersten
        var (_, second) = await Protect(new
        {
            text = "Herr Mustermann und Frau Erika Beispiel kommen zusammen.",
            known = first["mapping"],
        });

        var text2 = second["protected"]!.GetValue<string>();
        Assert.Contains("[NAME_2]", text2);
        Assert.DoesNotContain("[NAME_1] und", text2);
        Assert.Equal("Erika", Value(second, "[NAME_3]"));
        Assert.Equal("Mustermann", Value(second, "[NAME_2]"));
    }

    [Fact]
    public async Task Person_mit_Diagnose_darf_standardmaessig_nicht_raus()
    {
        var (status, body) = await Protect(new { text = "Herr Max Mustermann hat Diabetes und braucht eine Überweisung." });

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("zu sensibel", body["error"]!.GetValue<string>());

        var audit = await Client("admin-key").GetFromJsonAsync<JsonObject>("/admin/audit?action=protect&status=Blocked");
        Assert.NotEmpty(audit!["items"]!.AsArray());
    }

    [Fact]
    public async Task Person_mit_Diagnose_bleibt_auch_in_spaeteren_Runden_gesperrt()
    {
        // Erste Runde ohne Diagnose ist erlaubt, die Zuordnung wandert in die zweite
        var (_, first) = await Protect(new { text = "Termin für Herrn Max Mustermann am Montag." });

        var (status, body) = await Protect(new
        {
            text = "Herr Max Mustermann hat Diabetes, bitte Überweisung schreiben.",
            known = first["mapping"],
        });

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("zu sensibel", body["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Wer_nicht_in_die_Cloud_darf_kann_nichts_nach_aussen_geben()
    {
        var created = await (await Client("admin-key").PostAsJsonAsync("/admin/users",
            new { name = "nur.lokal", username = "nur.lokal", admin = false, cloudAllowed = false })).Content.ReadFromJsonAsync<JsonObject>();

        var (status, _) = await Protect(new { text = "Termin am Montag bestätigen." }, created!["key"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task Leerer_Text_wird_abgelehnt()
    {
        var (status, _) = await Protect(new { text = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }
}
