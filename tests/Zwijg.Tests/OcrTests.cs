using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Writer;
using Zwijg.Core.Ocr;

namespace Zwijg.Tests;

// Läuft nur, wenn Tesseract mit deutschen Sprachdaten da ist. Eigener Ordner für Sprachdaten über ZWIJG_TEST_TESSDATA.
public sealed class OcrFactAttribute : FactAttribute
{
    public static readonly OcrOptions Options = new(null, Environment.GetEnvironmentVariable("ZWIJG_TEST_TESSDATA"), "deu", 5);

    private static readonly Lazy<bool> Available = new(() => TesseractOcr.CheckAsync(Options).GetAwaiter().GetResult().Available);

    public OcrFactAttribute()
    {
        if (!Available.Value)
            Skip = "Tesseract mit deutschen Sprachdaten ist nicht installiert";
    }
}

[Collection("Texterkennung")]
public class OcrTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    private static readonly byte[] ScanPng = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "scan.png"));

    private HttpClient Client(string key = "admin-key")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    // PDF ohne Textebene, nur mit dem Bild, wie ein Scan vom Kopierer
    private static byte[] ScannedPdf(int pages = 1)
    {
        var builder = new PdfDocumentBuilder();
        for (var i = 0; i < pages; i++)
        {
            var page = builder.AddPage(PageSize.A4);
            page.AddPng(ScanPng, new PdfRectangle(40, 500, 555, 765));
        }
        return builder.Build();
    }

    private async Task<(HttpStatusCode Status, JsonObject Body)> Check(byte[] bytes, string fileName, string type)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(file, "file", fileName);
        var res = await Client().PostAsync("/v1/documents/check", form);
        return (res.StatusCode, (await res.Content.ReadFromJsonAsync<JsonObject>())!);
    }

    private Task<HttpResponseMessage> SetOcr(bool enabled) =>
        Client().PutAsJsonAsync("/admin/ocr", new
        {
            enabled,
            tessdataDir = OcrFactAttribute.Options.TessdataDir,
            languages = "deu",
            maxPages = 5,
        });

    [Fact]
    public void Durchsichtiger_Hintergrund_wird_weiss()
    {
        var file = Path.GetTempFileName();
        try
        {
            // Ein schwarzer deckender und ein ganz durchsichtiger Punkt (BGRA)
            TesseractOcr.WriteGrayscale(file, [0, 0, 0, 255, 0, 0, 0, 0], 2, 1);
            var bytes = File.ReadAllBytes(file);
            Assert.Equal([0, 255], bytes[^2..]);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Ohne_Texterkennung_gibt_es_einen_klaren_Hinweis()
    {
        (await SetOcr(false)).EnsureSuccessStatusCode();
        try
        {
            var (status, body) = await Check(ScannedPdf(), "scan.pdf", "application/pdf");
            Assert.Equal((HttpStatusCode)422, status);
            Assert.Contains("ausgeschaltet", body["error"]!.GetValue<string>());
        }
        finally
        {
            (await SetOcr(true)).EnsureSuccessStatusCode();
        }
    }

    [OcrFact]
    public async Task Foto_eines_Befunds_wird_gelesen()
    {
        var result = await TesseractOcr.ReadImageAsync(ScanPng, ".png", OcrFactAttribute.Options);

        Assert.Null(result.Error);
        Assert.Contains("Mustermann", result.Text);
        Assert.Contains("Diabetes", result.Text);
    }

    [OcrFact]
    public async Task Gescanntes_PDF_wird_gelesen_und_geschuetzt()
    {
        (await SetOcr(true)).EnsureSuccessStatusCode();

        var (status, body) = await Check(ScannedPdf(pages: 2), "scan.pdf", "application/pdf");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body["ocr"]!.GetValue<bool>());
        Assert.Equal(2, body["pages"]!.GetValue<int>());
        var text = body["pseudonymized"]!.GetValue<string>();
        Assert.DoesNotContain("Mustermann", text);
        Assert.Contains("[NAME_", text);
        Assert.Contains(body["healthTerms"]!.AsArray(), t => t!.GetValue<string>() == "Diabetes");
    }

    [OcrFact]
    public async Task Status_zeigt_Tesseract_und_Sprachen()
    {
        (await SetOcr(true)).EnsureSuccessStatusCode();

        var ocr = await Client().GetFromJsonAsync<JsonObject>("/admin/ocr");

        Assert.True(ocr!["status"]!["available"]!.GetValue<bool>());
        Assert.Contains(ocr["status"]!["languages"]!.AsArray(), l => l!.GetValue<string>() == "deu");
    }

    [Fact]
    public async Task Nur_Admins_sehen_die_Einstellungen()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("user-key").GetAsync("/admin/ocr")).StatusCode);
    }
}
