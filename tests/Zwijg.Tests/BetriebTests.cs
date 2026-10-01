using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Zwijg.Tests;

// Betrieb hinter einem HTTPS Proxy und Schutz der Data Protection Schlüssel
public class BetriebTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"zwijg-betrieb-{Guid.NewGuid():N}");
    private readonly List<IDisposable> _factories = [];

    public void Dispose()
    {
        foreach (var f in _factories)
            f.Dispose();
        if (Directory.Exists(_dir))
            foreach (var file in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // Eigene Instanz mit eigenem Datenordner, damit Schlüssel und Einstellungen nicht mit anderen Tests geteilt werden
    private WebApplicationFactory<Program> Gateway(Dictionary<string, string?>? extra = null, LogSammler? logs = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Zwijg:Audit:DatabasePath"] = Path.Combine(_dir, "audit.db"),
            ["Zwijg:SettingsPath"] = Path.Combine(_dir, "settings.json"),
        };
        foreach (var (k, v) in extra ?? [])
            settings[k] = v;

        var factory = new GatewayFactory().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration(c => c.AddInMemoryCollection(settings));
            if (logs != null)
                b.ConfigureLogging(l => l.AddProvider(logs));
        });
        _factories.Add(factory);
        return factory;
    }

    private static HttpClient Client(WebApplicationFactory<Program> f, string baseAddress = "http://localhost")
    {
        var client = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(baseAddress), HandleCookies = false });
        client.DefaultRequestHeaders.Add("X-Requested-With", "zwijg");
        return client;
    }

    // Legt einen Benutzer an und meldet ihn an. Liefert die Set-Cookie Zeile der Sitzung.
    private static async Task<string> SessionCookie(WebApplicationFactory<Program> f, string baseAddress = "http://localhost")
    {
        var admin = Client(f, baseAddress);
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-key");
        var create = await admin.PostAsJsonAsync("/admin/users",
            new { name = "Max Mustermann", username = "max.mustermann", admin = false, password = "Sommer-Regen-2026", mustChangePassword = false });
        create.EnsureSuccessStatusCode();

        var res = await Client(f, baseAddress).PostAsJsonAsync("/auth/login",
            new { username = "max.mustermann", password = "Sommer-Regen-2026", remember = false });
        res.EnsureSuccessStatusCode();
        return res.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("zwijg.sitzung", StringComparison.Ordinal));
    }

    private string CreateCertificate(string password)
    {
        Directory.CreateDirectory(_dir);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Zwijg Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var path = Path.Combine(_dir, "schluessel.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, password));
        return path;
    }

    [Fact]
    public async Task Cookie_folgt_ohne_Proxy_Option_der_Anfrage()
    {
        var f = Gateway();

        var cookie = await SessionCookie(f);

        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cookie_ist_hinter_TLS_Proxy_immer_Secure()
    {
        var f = Gateway(new() { ["Zwijg:BehindTlsProxy"] = "true" });

        // Auch wenn der Proxy kein X-Forwarded-Proto schickt
        var cookie = await SessionCookie(f);

        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Hsts_nur_bei_HTTPS()
    {
        var f = Gateway();

        var https = await Client(f, "https://zwijg.praxis.local").GetAsync("/health");
        var http = await Client(f, "http://zwijg.praxis.local").GetAsync("/health");

        Assert.True(https.Headers.Contains("Strict-Transport-Security"));
        Assert.False(http.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task X_Forwarded_Proto_zaehlt_nur_hinter_TLS_Proxy()
    {
        static HttpRequestMessage Forwarded()
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/health");
            req.Headers.Add("X-Forwarded-Proto", "https");
            return req;
        }

        var ohne = await Client(Gateway(), "http://zwijg.praxis.local").SendAsync(Forwarded());
        var mit = await Client(Gateway(new() { ["Zwijg:BehindTlsProxy"] = "true" }), "http://zwijg.praxis.local").SendAsync(Forwarded());

        Assert.False(ohne.Headers.Contains("Strict-Transport-Security"));
        Assert.True(mit.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Schluessel_werden_mit_Zertifikat_verschluesselt_und_nach_Neustart_gelesen()
    {
        var pfx = CreateCertificate("Test-Passwort-1");
        var extra = new Dictionary<string, string?>
        {
            ["Zwijg:KeyProtection:CertificatePath"] = pfx,
            ["Zwijg:KeyProtection:CertificatePassword"] = "Test-Passwort-1",
        };

        var first = Gateway(extra);
        var cookie = await SessionCookie(first);

        var files = Directory.GetFiles(Path.Combine(_dir, "keys"), "*.xml");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var xml = File.ReadAllText(file);
            Assert.Contains("KeyRingCertificateDecryptor", xml);
            Assert.DoesNotContain("<value>", xml);
        }

        // Neuer Start mit denselben Dateien: die Sitzung ist weiter gültig, der Schlüssel also lesbar
        first.Dispose();
        var second = Gateway(extra);
        var client = Client(second);
        client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        var me = await client.GetAsync("/v1/me");

        me.EnsureSuccessStatusCode();
    }

    [Fact]
    public void Falsches_Zertifikat_Passwort_verhindert_den_Start()
    {
        var pfx = CreateCertificate("Test-Passwort-1");
        var f = Gateway(new()
        {
            ["Zwijg:KeyProtection:CertificatePath"] = pfx,
            ["Zwijg:KeyProtection:CertificatePassword"] = "falsch",
        });

        // Nur das falsche Passwort, nicht irgendein anderer Startfehler
        var ex = Assert.Throws<CryptographicException>(() => f.CreateClient());
        Assert.Contains("password", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mit_Zertifikat_wird_ein_alter_Schluessel_ohne_Zertifikat_sofort_abgeloest()
    {
        // Bestehende Installation: Schlüssel im Klartext, wie bisher unter Linux und in Docker
        var keys = Path.Combine(_dir, "keys");
        Directory.CreateDirectory(keys);
        var alt = DataProtectionProvider.Create(new DirectoryInfo(keys), b => b.SetApplicationName("Zwijg"));
        var altDaten = alt.CreateProtector("Zwijg.Test").Protect("sk-geheim-1234567890");
        var altId = KeyIdOf(altDaten);

        var pfx = CreateCertificate("Test-Passwort-1");
        var f = Gateway(new()
        {
            ["Zwijg:KeyProtection:CertificatePath"] = pfx,
            ["Zwijg:KeyProtection:CertificatePassword"] = "Test-Passwort-1",
        });
        await SessionCookie(f);
        var protector = f.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("Zwijg.Test");
        var neuId = KeyIdOf(protector.Protect("neue Daten"));

        Assert.NotEqual(altId, neuId);
        var neuDatei = File.ReadAllText(Path.Combine(keys, $"key-{neuId}.xml"));
        Assert.Contains("KeyRingCertificateDecryptor", neuDatei);
        Assert.DoesNotContain("<value>", neuDatei);

        // Alte Daten bleiben lesbar
        Assert.Equal("sk-geheim-1234567890", protector.Unprotect(altDaten));
    }

    [Fact]
    public async Task Mit_Zertifikat_wird_beim_zweiten_Start_kein_weiterer_Schluessel_angelegt()
    {
        var pfx = CreateCertificate("Test-Passwort-1");
        var extra = new Dictionary<string, string?>
        {
            ["Zwijg:KeyProtection:CertificatePath"] = pfx,
            ["Zwijg:KeyProtection:CertificatePassword"] = "Test-Passwort-1",
        };
        var first = Gateway(extra);
        await SessionCookie(first);
        first.Dispose();
        var vorher = Directory.GetFiles(Path.Combine(_dir, "keys"), "key-*.xml").Length;

        Gateway(extra).CreateClient();

        Assert.Equal(vorher, Directory.GetFiles(Path.Combine(_dir, "keys"), "key-*.xml").Length);
    }

    [Fact]
    public void Ohne_Schreibrecht_im_Datenordner_bricht_der_Start_mit_Hinweis_ab()
    {
        // Erster Start legt die Dateien an, danach sind sie schreibgeschützt wie ein Volume, das root gehört
        Gateway().CreateClient();
        var settings = Path.Combine(_dir, "settings.json");
        File.SetAttributes(settings, FileAttributes.ReadOnly);

        var ex = Assert.Throws<InvalidOperationException>(() => Gateway().CreateClient());

        Assert.Contains("settings.json", ex.Message);
        Assert.Contains("Schreibrechte", ex.Message);
        Assert.Contains("chown", ex.Message);
    }

    // Data Protection Payload: 4 Byte Kennung, dann die Id des Schlüssels
    private static Guid KeyIdOf(string protectedText) =>
        new(Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(protectedText).AsSpan(4, 16));

    [Fact]
    public void Start_warnt_ohne_Schluesselschutz_ausser_unter_Windows()
    {
        var logs = new LogSammler();
        Gateway(logs: logs).CreateClient();

        var warned = logs.Messages.Any(m => m.Level == LogLevel.Warning && m.Text.Contains("Schlüssel", StringComparison.Ordinal)
            && m.Text.Contains("CertificatePath", StringComparison.Ordinal));
        Assert.Equal(!OperatingSystem.IsWindows(), warned);
    }

    [Fact]
    public void Start_warnt_nicht_mit_Zertifikat()
    {
        var pfx = CreateCertificate("Test-Passwort-1");
        var logs = new LogSammler();
        Gateway(new()
        {
            ["Zwijg:KeyProtection:CertificatePath"] = pfx,
            ["Zwijg:KeyProtection:CertificatePassword"] = "Test-Passwort-1",
        }, logs).CreateClient();

        Assert.DoesNotContain(logs.Messages, m => m.Level == LogLevel.Warning && m.Text.Contains("CertificatePath", StringComparison.Ordinal));
    }

    public sealed class LogSammler : ILoggerProvider
    {
        public ConcurrentBag<(LogLevel Level, string Text)> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(LogSammler owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Messages.Add((logLevel, formatter(state, exception)));
        }
    }
}
