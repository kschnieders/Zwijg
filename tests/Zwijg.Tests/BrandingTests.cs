using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace Zwijg.Tests;

public class BrandingTests(PatientFactory factory) : IClassFixture<PatientFactory>
{
    // Kleinstes gültiges PNG, 1x1 Pixel
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private HttpClient Client(string key = "admin-key")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private Task<HttpResponseMessage> Upload(byte[] bytes, string name, string type, string key = "admin-key")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(file, "file", name);
        return Client(key).PostAsync("/admin/branding/logo", form);
    }

    [Fact]
    public async Task Name_Farben_und_Logo_fuer_alle_sichtbar()
    {
        (await Client().PutAsJsonAsync("/admin/branding", new { practiceName = "Praxis Dr. Sommer", accent = "#AA3355", gradientFrom = "#ffffff", gradientTo = "#aa3355", gradientAngle = 90 })).EnsureSuccessStatusCode();
        (await Upload(Png, "logo.png", "image/png")).EnsureSuccessStatusCode();

        // Ohne Anmeldung, die Anmeldeseite braucht es
        var b = (await factory.CreateClient().GetFromJsonAsync<JsonObject>("/branding"))!;
        Assert.Equal("Praxis Dr. Sommer", b["name"]!.GetValue<string>());
        Assert.Equal("#aa3355", b["accent"]!.GetValue<string>());
        Assert.Equal(90, b["gradient"]!["angle"]!.GetValue<int>());

        var logo = await factory.CreateClient().GetAsync(b["logo"]!.GetValue<string>());
        Assert.Equal("image/png", logo.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", logo.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(Png, await logo.Content.ReadAsByteArrayAsync());

        (await Client().DeleteAsync("/admin/branding/logo")).EnsureSuccessStatusCode();
        Assert.Null((await factory.CreateClient().GetFromJsonAsync<JsonObject>("/branding"))!["logo"]);
    }

    [Theory]
    // SVG mit Skript, als PNG ausgegeben
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>", "logo.png", "image/png")]
    [InlineData("<html><script>alert(1)</script></html>", "logo.jpg", "image/jpeg")]
    [InlineData("GIF89a", "logo.gif", "image/gif")]
    public async Task Nur_echte_Bilder_als_Logo(string content, string name, string type)
    {
        var res = await Upload(Encoding.UTF8.GetBytes(content), name, type);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Pruefungen_und_nur_fuer_Admins()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PutAsJsonAsync("/admin/branding", new { accent = "red" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PutAsJsonAsync("/admin/branding", new { accent = "#1f6f5c;background:url(x)" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PutAsJsonAsync("/admin/branding", new { gradientFrom = "#ffffff" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PutAsJsonAsync("/admin/branding", new { practiceName = new string('x', 61) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(new byte[600 * 1024], "gross.png", "image/png")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("user-key").PutAsJsonAsync("/admin/branding", new { accent = "#000000" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload(Png, "logo.png", "image/png", "user-key")).StatusCode);
    }
}
