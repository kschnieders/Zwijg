using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;

namespace Zwijg.Tests;

// Hochgeladene Dokumente enthalten Patientendaten und dürfen nicht im allgemeinen Temp Ordner landen
public class UploadTempTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    [Fact]
    public async Task Grosses_Dokument_landet_nicht_im_Temp_Ordner()
    {
        var created = new ConcurrentBag<string>();
        using var watcher = new FileSystemWatcher(Path.GetTempPath(), "ASPNETCORE_*") { EnableRaisingEvents = true };
        watcher.Created += (_, e) => created.Add(e.Name!);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-key");
        var text = string.Concat(Enumerable.Repeat("Befund von Max Mustermann, Blutdruck in Ordnung. ", 6000));
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(text)), "file", "befund.txt" } };
        form.Last().Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        var res = await client.PostAsync("/v1/documents/check", form);
        await Task.Delay(500);

        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        Assert.Empty(created);
    }
}
