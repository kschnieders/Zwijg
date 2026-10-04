using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Zwijg.Tests;

public sealed class PatientFactory : WebApplicationFactory<Program>
{
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), $"zwijg-patient-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Zwijg:ApiKeys:0:Key"] = "user-key",
            ["Zwijg:ApiKeys:0:User"] = "empfang",
            ["Zwijg:ApiKeys:1:Key"] = "admin-key",
            ["Zwijg:ApiKeys:1:User"] = "admin",
            ["Zwijg:ApiKeys:1:Admin"] = "true",
            ["Zwijg:Providers:Local:Type"] = "Echo",
            ["Zwijg:Audit:DatabasePath"] = Path.Combine(DataDir, "audit.db"),
            ["Zwijg:SettingsPath"] = Path.Combine(DataDir, "settings.json"),
        }));
    }
}

// Das Patientenfeld: versteckt den Namen in der ganzen Unterhaltung und liegt nirgends im Klartext
public class PatientFieldTests(PatientFactory factory) : IClassFixture<PatientFactory>
{
    private const string Patient = "Kowalczyk, Anna, 12.03.1980";

    private HttpClient Client()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "user-key");
        return client;
    }

    [Fact]
    public async Task Vorschau_versteckt_den_Patienten_ohne_Hinweis_im_Satz()
    {
        var res = await Client().PostAsJsonAsync("/v1/check", new { text = "Bitte kowalczyk zurückrufen, geb. 12.3.80, Diabetes", patient = Patient });
        var body = (await res.Content.ReadFromJsonAsync<JsonObject>())!;
        var text = body["pseudonymized"]!.GetValue<string>();
        Assert.Contains("[NAME_1]", text);

        Assert.DoesNotContain("owalczyk", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("12.3.80", text);
        Assert.Equal("High", body["sensitivity"]!.GetValue<string>());
    }

    [Fact]
    public async Task Patient_steht_in_keiner_Datei_im_Klartext()
    {
        var client = Client();
        var res = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "auto",
            messages = new[] { new { role = "user", content = "Bitte Kowalczyk wegen Diabetes zurückrufen, geb. 12.03.1980." } },
            zwijg = new { conversation = "new", patient = Patient },
        });
        res.EnsureSuccessStatusCode();
        var id = res.Headers.GetValues("X-Zwijg-Conversation").Single();

        // Die Antwort enthält den echten Namen wieder, beim Modell kam er nicht an
        var answer = (await res.Content.ReadFromJsonAsync<JsonObject>())!["choices"]![0]!["message"]!["content"]!.GetValue<string>();
        Assert.Contains("Kowalczyk", answer);

        // Beim Öffnen der Unterhaltung ist das Feld wieder da
        var conv = await client.GetFromJsonAsync<JsonObject>($"/v1/conversations/{id}");
        Assert.Equal(Patient, conv!["patient"]!.GetValue<string>());
        Assert.DoesNotContain("owalczyk", conv["title"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);

        // Jede Datei im Datenordner: Einstellungen, Verlauf, Protokoll, Schlüssel
        var files = Directory.GetFiles(factory.DataDir, "*", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            byte[] bytes;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var copy = new MemoryStream())
            {
                stream.CopyTo(copy);
                bytes = copy.ToArray();
            }

            foreach (var secret in new[] { "Kowalczyk", "kowalczyk", "12.03.1980" })
            {
                Assert.False(Contains(bytes, Encoding.UTF8.GetBytes(secret)), $"{secret} im Klartext in {Path.GetFileName(file)}");
                Assert.False(Contains(bytes, Encoding.Unicode.GetBytes(secret)), $"{secret} als UTF-16 in {Path.GetFileName(file)}");
            }
        }
    }

    [Fact]
    public async Task Auch_bei_Dokument_und_Text_schuetzen()
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("Befund: Kowalczyk, geb. 12.3.80, Blutdruck erhöht."));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "befund.txt");
        form.Add(new StringContent(Patient), "patient");
        var check = (await (await Client().PostAsync("/v1/documents/check", form)).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.DoesNotContain("owalczyk", check.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("12.3.80", check.ToJsonString());

        var protect = await Client().PostAsJsonAsync("/v1/protect", new { text = "Rückruf an Kowalczyk wegen Termin", patient = Patient });
        var body = (await protect.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("Rückruf an [NAME_1] wegen Termin", body["protected"]!.GetValue<string>());
    }

    [Fact]
    public async Task Abmelden_nach_Inaktivitaet_einstellbar()
    {
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-key");

        Assert.Equal(30, (await Client().GetFromJsonAsync<JsonObject>("/v1/me"))!["idleLogoutMinutes"]!.GetValue<int>());
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/admin/session", new { idleLogoutMinutes = 1000 })).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await Client().PutAsJsonAsync("/admin/session", new { idleLogoutMinutes = 0 })).StatusCode);

        (await admin.PutAsJsonAsync("/admin/session", new { idleLogoutMinutes = 15 })).EnsureSuccessStatusCode();
        Assert.Equal(15, (await Client().GetFromJsonAsync<JsonObject>("/v1/me"))!["idleLogoutMinutes"]!.GetValue<int>());
    }

    private static bool Contains(byte[] haystack, byte[] needle) =>
        haystack.AsSpan().IndexOf(needle) >= 0;
}
