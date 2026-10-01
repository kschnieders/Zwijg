using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Zwijg.Tests;

public class DictationEndpointTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    private static readonly byte[] Speech = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "diktat.wav"));

    private HttpClient Client(string key = "admin-key")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private async Task<(HttpStatusCode Status, JsonObject Body)> Transcribe(byte[] audio, string fileName = "diktat.wav")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", fileName);
        var res = await Client("user-key").PostAsync("/v1/audio/transcriptions", form);
        return (res.StatusCode, (await res.Content.ReadFromJsonAsync<JsonObject>())!);
    }

    private Task<HttpResponseMessage> UseModel(string? dir, string model = "base") =>
        Client().PutAsJsonAsync("/admin/dictation", new { enabled = true, model, modelDir = dir, threads = 0, language = "de" });

    [Fact]
    public async Task Ohne_Modell_gibt_es_einen_klaren_Hinweis()
    {
        (await UseModel(Path.Combine(Path.GetTempPath(), "zwijg-ohne-modell"))).EnsureSuccessStatusCode();

        var (status, body) = await Transcribe(Speech);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Contains("Sprachmodell", body["error"]!.GetValue<string>());

        var me = await Client("user-key").GetFromJsonAsync<JsonObject>("/v1/me");
        Assert.False(me!["dictation"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Nur_Admins_duerfen_Modelle_laden()
    {
        var res = await Client("user-key").PostAsJsonAsync("/admin/dictation/download", new { model = "base" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        var unknown = await Client().PostAsJsonAsync("/admin/dictation/download", new { model = "gibt-es-nicht" });
        Assert.Equal(HttpStatusCode.Conflict, unknown.StatusCode);
    }

    [Fact]
    public async Task Unbekanntes_Modell_wird_beim_Speichern_abgelehnt()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await UseModel(null, "riesig")).StatusCode);
    }

    [WhisperFact]
    public async Task Diktat_ueber_die_Schnittstelle_und_nur_Metadaten_im_Protokoll()
    {
        (await UseModel(Path.GetDirectoryName(WhisperFactAttribute.ModelPath))).EnsureSuccessStatusCode();
        var me = await Client("user-key").GetFromJsonAsync<JsonObject>("/v1/me");
        Assert.True(me!["dictation"]!.GetValue<bool>());

        var (status, body) = await Transcribe(Speech);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Mustermann", body["text"]!.GetValue<string>());

        var audit = await Client().GetFromJsonAsync<JsonObject>("/admin/audit?action=dictation");
        var entry = audit!["items"]![0]!;
        Assert.Equal("Ok", entry["status"]!.GetValue<string>());
        Assert.Null(entry["prompt"]);

        // Kein WAV
        var (bad, _) = await Transcribe("OggS kein wav"u8.ToArray(), "diktat.ogg");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, bad);
    }
}
