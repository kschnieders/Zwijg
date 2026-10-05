using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using ICSharpCode.SharpZipLib.Zip;

namespace Zwijg.Tests;

public class FilesEndpointTests(PatientFactory factory) : IClassFixture<PatientFactory>
{
    // Bei jedem Lauf neu, damit kein festes Kennwort im Code steht
    private static readonly string Kennwort = "test-" + Guid.NewGuid().ToString("N");

    private HttpClient Client(string key = "user-key")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private Task Enable(bool on = true, int maxMb = 500) =>
        Client("admin-key").PutAsJsonAsync("/admin/files", new { enabled = on, maxSizeMb = maxMb });

    private static MultipartFormDataContent Form(string kennwort, params (string Name, byte[] Bytes)[] files)
    {
        var form = new MultipartFormDataContent { { new StringContent(kennwort), "password" } };
        foreach (var (name, bytes) in files)
            form.Add(new ByteArrayContent(bytes), "file", name);
        return form;
    }

    [Fact]
    public async Task Verschluesseln_und_wieder_oeffnen()
    {
        await Enable();
        var me = await Client().GetFromJsonAsync<JsonObject>("/v1/me");
        Assert.Equal(500, me!["files"]!["maxSizeMb"]!.GetValue<int>());

        var res = await Client().PostAsync("/v1/files/encrypt", Form(Kennwort,
            ("Befund_Mustermann.pdf", Encoding.UTF8.GetBytes("Befund")), ("Roentgen.jpg", Encoding.UTF8.GetBytes("Bild"))));
        res.EnsureSuccessStatusCode();
        Assert.Equal("dokumente-verschluesselt.zip", res.Content.Headers.ContentDisposition!.FileName?.Trim('"'));
        var encrypted = await res.Content.ReadAsByteArrayAsync();
        Assert.DoesNotContain("Mustermann", Encoding.Latin1.GetString(encrypted));

        // Wieder öffnen ergibt das innere ZIP mit beiden Dateien
        var open = await Client().PostAsync("/v1/files/decrypt", Form(Kennwort, ("dokumente-verschluesselt.zip", encrypted)));
        open.EnsureSuccessStatusCode();
        using var zip = new ZipFile(new MemoryStream(await open.Content.ReadAsByteArrayAsync()));
        Assert.Equal(["Befund_Mustermann.pdf", "Roentgen.jpg"], zip.Cast<ZipEntry>().Select(e => e.Name));

        var wrong = await Client().PostAsync("/v1/files/decrypt", Form("falsches-kennwort-123", ("x.zip", encrypted)));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("Kennwort ist falsch", await wrong.Content.ReadAsStringAsync());

        // Im Protokoll keine Dateinamen
        var audit = await Client("admin-key").GetStringAsync("/admin/audit?action=files");
        Assert.Contains("2 Dateien verschl", audit);
        Assert.DoesNotContain("Mustermann", audit);
    }

    [Fact]
    public async Task Pruefungen()
    {
        await Enable();
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsync("/v1/files/encrypt", Form("kurz", ("a.txt", [1])))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsync("/v1/files/decrypt", Form(Kennwort, ("kein.zip", Encoding.UTF8.GetBytes("kein zip"))))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client().PutAsJsonAsync("/admin/files", new { enabled = true, maxSizeMb = 10 })).StatusCode);

        await Enable(maxMb: 1);
        try
        {
            var big = await Client().PostAsync("/v1/files/encrypt", Form(Kennwort, ("gross.bin", new byte[2 * 1024 * 1024])));
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);
        }
        finally
        {
            await Enable();
        }

        await Enable(on: false);
        try
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await Client().PostAsync("/v1/files/encrypt", Form(Kennwort, ("a.txt", [1])))).StatusCode);
            Assert.Null((await Client().GetFromJsonAsync<JsonObject>("/v1/me"))!["files"]);
        }
        finally
        {
            await Enable();
        }
    }
}
