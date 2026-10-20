using System.Collections.Concurrent;
using System.Net;
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
        // Knapp über 64 KB, ab da hat ASP.NET früher auf die Platte geschrieben. Größer macht die Erkennung nur langsam.
        var text = string.Concat(Enumerable.Repeat("Befund von Max Mustermann, Blutdruck in Ordnung. ", 1500));
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(text)), "file", "befund.txt" } };
        form.Last().Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        var res = await client.PostAsync("/v1/documents/check", form);
        await Task.Delay(500);

        // Hauptsache, das Formular wurde gelesen. Ob die Erkennung danach fertig wird, ist hier egal.
        Assert.DoesNotContain(res.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        Assert.Empty(created);
    }
}
