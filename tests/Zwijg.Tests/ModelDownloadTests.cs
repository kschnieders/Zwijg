using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zwijg.Gateway;
using Zwijg.Gateway.Settings;

namespace Zwijg.Tests;

// Ein verändertes Sprachmodell darf nie benutzt werden
public class ModelDownloadTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("zwijg-modell-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private sealed class FakeHttp(byte[] body) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(body));
    }

    private sealed class Handler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    [Fact]
    public async Task Modell_mit_falscher_Pruefsumme_wird_verworfen()
    {
        var options = Options.Create(new GatewayOptions { SettingsPath = Path.Combine(_dir, "settings.json") });
        var store = new SettingsStore(options, new EphemeralDataProtectionProvider(), NullLogger<SettingsStore>.Instance);
        using var dictation = new DictationService(store, options, new FakeHttp("kein echtes Modell"u8.ToArray()), NullLogger<DictationService>.Instance);

        Assert.True(dictation.StartDownload("base"));
        for (var i = 0; i < 50 && dictation.Download.Running; i++)
            await Task.Delay(100);

        Assert.Contains("Prüfsumme", dictation.Download.Error);
        Assert.Empty(Directory.GetFiles(dictation.ModelDir));
    }
}
