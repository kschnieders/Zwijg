using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Zwijg.Gateway;

namespace Zwijg.Tests;

// Kleine Modelle wechseln manchmal ins Chinesische. Zwijg fragt dann nach oder räumt auf.
public class LanguageGuardTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    [Theory]
    [InlineData("Wie nehme ich das?", "Bitte nach Anweisung einnehmen.", false)]
    [InlineData("Wie nehme ich das?", "Bitte 请按照药说明服用 einnehmen.", true)]
    [InlineData("请翻译: Guten Tag", "你好", false)]
    public void Erkennt_fremde_Schrift_nur_wenn_die_Frage_keine_hatte(string question, string answer, bool expected)
    {
        Assert.Equal(expected, LanguageGuard.HasUnexpectedScript(question, answer));
    }

    [Fact]
    public void Entfernt_fremde_Stuecke_und_haengt_Hinweis_an()
    {
        var result = LanguageGuard.Strip("Ibuprofen 400 mg zum Mund. 请按照药说明服用，并在必要时保持充足水分。\n\n我们安排了您明日上午12:00的随访检查。\n\nViele Grüße");

        Assert.False(LanguageGuard.HasUnexpectedScript("x", result));
        Assert.Contains("Ibuprofen 400 mg zum Mund.", result);
        Assert.Contains("Viele Grüße", result);
        Assert.EndsWith(LanguageGuard.RemovedNote, result);
    }

    [Fact]
    public async Task Zweiter_Versuch_liefert_saubere_Antwort_und_wird_protokolliert()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-key");

        var res = await client.PostAsJsonAsync("/v1/chat/completions",
            new { messages = new[] { new { role = "user", content = "/drift" } } });
        res.EnsureSuccessStatusCode();
        var answer = (await res.Content.ReadFromJsonAsync<JsonObject>())!["choices"]![0]!["message"]!["content"]!.GetValue<string>();

        Assert.Equal("Die Antwort ist jetzt vollständig auf Deutsch.", answer);

        var audit = await client.GetFromJsonAsync<JsonObject>("/admin/audit?q=Sprachkorrektur&limit=1");
        Assert.Equal(1, audit!["total"]!.GetValue<int>());
    }
}
