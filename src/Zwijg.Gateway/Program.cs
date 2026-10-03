using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.Options;
using Zwijg.Core.Audit;
using Zwijg.Core.Ocr;
using Zwijg.Core.Pseudonymization;
using Zwijg.Core.Security;
using Zwijg.Gateway;
using Zwijg.Gateway.Providers;
using Zwijg.Gateway.Settings;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection("Zwijg"));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();

// Schlüssel zum Verschlüsseln der API Schlüssel liegen neben den Einstellungen.
// Mit Zertifikat (Zwijg:KeyProtection) werden sie damit verschlüsselt, sonst unter Windows mit DPAPI
// an das Benutzerkonto gebunden. Unter Linux und in Docker liegen sie ohne Zertifikat im Klartext.
builder.Services.AddDataProtection().SetApplicationName("Zwijg");
builder.Services.AddSingleton<KeyRingCertificate>();
builder.Services.AddOptions<KeyManagementOptions>()
    .Configure<IOptions<GatewayOptions>, ILoggerFactory, KeyRingCertificate>((o, gw, logs, cert) =>
    {
        o.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(KeyDirectory(gw.Value)), logs);
        if (cert.Value != null)
            o.XmlEncryptor = new KeyRingCertificateEncryptor(cert.Value, logs);
        else if (OperatingSystem.IsWindows())
            o.XmlEncryptor = new DpapiXmlEncryptor(protectToLocalMachine: false, logs);
    });

builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<UpdateChecker>();
builder.Services.AddSingleton<DictationService>();

// Anmeldung mit Benutzername und Passwort: verschlüsseltes Cookie, für Skripte unlesbar,
// nur von der eigenen Seite mitgeschickt. Die Schlüssel dafür liegen bei den anderen Data Protection Schlüsseln.
builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "zwijg.sitzung";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromHours(12);
        o.SlidingExpiration = true;

        // Eine API leitet nicht auf eine Login Seite um, sie antwortet mit 401 oder 403
        o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
    });
// Hinter einem HTTPS Proxy kommt die Anfrage per HTTP an. Das Cookie soll trotzdem nur über HTTPS gehen.
builder.Services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
    .Configure<IOptions<GatewayOptions>>((o, gw) =>
    {
        if (gw.Value.BehindTlsProxy)
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    });
builder.Services.AddSingleton<ProviderRegistry>();
builder.Services.AddSingleton<IPiiDetector, RegexPiiDetector>();
builder.Services.AddSingleton<IPiiDetector, NameDetector>();
builder.Services.AddSingleton<IPiiDetector, PlaceDetector>();
builder.Services.AddSingleton<IPiiDetector>(sp =>
{
    var store = sp.GetRequiredService<SettingsStore>();
    return new CustomTermsDetector(() => (store.Current.ExtraNames, store.Current.ExtraPlaces));
});
builder.Services.AddSingleton<IPiiDetector>(sp =>
{
    var store = sp.GetRequiredService<SettingsStore>();
    return new CustomRuleDetector(() => store.Current.ProtectionRules);
});
builder.Services.AddSingleton<IPiiDetector, LocalLlmNameDetector>();
builder.Services.AddSingleton(sp =>
{
    var store = sp.GetRequiredService<SettingsStore>();
    return new Pseudonymizer(sp.GetServices<IPiiDetector>(), () => store.Current.IgnoredWords);
});
builder.Services.AddSingleton<InjectionDetector>();
builder.Services.AddSingleton<IAuditLog>(sp =>
{
    // Schlüssel für die Hashkette liegt verschlüsselt in einer eigenen Datei neben dem Protokoll, nie in der Datenbank
    var path = sp.GetRequiredService<IOptions<GatewayOptions>>().Value.Audit.DatabasePath;
    var protector = sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("Zwijg.Audit.Key");
    var keyFile = path + ".key";
    byte[] key;
    try
    {
        key = AuditKey.LoadOrCreate(keyFile, protector.Protect, protector.Unprotect);
    }
    catch (InvalidOperationException ex)
    {
        // Schlüsselring verloren, z.B. nach einem Umzug. Zwijg startet trotzdem, aber ältere Einträge
        // lassen sich nicht mehr prüfen. Das meldet dann "Echtheit prüfen".
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("Zwijg.Audit")
            .LogError(ex, "Schlüssel für das Protokoll nicht lesbar, es wird ein neuer angelegt");
        File.Move(keyFile, keyFile + ".unlesbar-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), overwrite: true);
        key = AuditKey.LoadOrCreate(keyFile, protector.Protect, protector.Unprotect);
    }

    return new SqliteAuditLog(path, key);
});
builder.Services.AddSingleton<DailyLimiter>();
builder.Services.AddSingleton<ChatPipeline>();
builder.Services.AddSingleton<Zwijg.Gateway.History.ConversationStore>();
builder.Services.AddHostedService<Zwijg.Gateway.History.ConversationCleanup>();

var app = builder.Build();

// Ohne Schreibrechte im Datenordner gar nicht erst starten, sonst scheitert später jede Anfrage ohne klaren Grund
var gateway = app.Services.GetRequiredService<IOptions<GatewayOptions>>().Value;
var dataDir = Path.GetDirectoryName(Path.GetFullPath(gateway.SettingsPath))!;
DataDirectoryCheck.EnsureWritable(
    [dataDir, KeyDirectory(gateway), Path.GetDirectoryName(Path.GetFullPath(gateway.Audit.DatabasePath))!],
    [gateway.SettingsPath, Path.Combine(dataDir, "history.db"), gateway.Audit.DatabasePath, gateway.Audit.DatabasePath + ".key", gateway.Audit.DatabasePath + ".kopf"]);

// Neue Version: Daten sichern, bevor Einstellungen und Datenbanken umgestellt werden. Das Update Skript sichert
// beim Probestart schon selbst und schaltet das hier mit Zwijg:SkipUpdateSnapshot ab.
UpdateSnapshot.CreateIfVersionChanged(dataDir, Endpoints.Version,
    [gateway.Audit.DatabasePath, gateway.Audit.DatabasePath + ".key", gateway.Audit.DatabasePath + ".kopf"],
    app.Configuration.GetValue<bool>("Zwijg:SkipUpdateSnapshot"),
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Zwijg.Update"));

// Zertifikat für die Schlüssel gleich laden, ein falscher Pfad oder ein falsches Passwort verhindert den Start.
// Vor allem anderen, damit schon die ersten neuen Daten mit einem geschützten Schlüssel verschlüsselt werden.
KeyRingCertificate.EnsureProtectedDefaultKey(app.Services);

// Einstellungen gleich beim Start laden, damit Fehler sofort auffallen
app.Services.GetRequiredService<SettingsStore>();
app.Services.GetRequiredService<IAuditLog>();

// Texterkennung meldet nicht löschbare Zwischenbilder ins Log. Liegengebliebene vom letzten Lauf jetzt entfernen.
TesseractOcr.Log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Zwijg.Texterkennung");
await TesseractOcr.DeleteLeftoversAsync(TesseractOcr.Log);

if (app.Services.GetRequiredService<KeyRingCertificate>().Value == null && !OperatingSystem.IsWindows())
    app.Logger.LogWarning("Die Schlüssel in {Dir} liegen unverschlüsselt. Wer sie hat, kann API Schlüssel und Verlauf entschlüsseln " +
        "und Sitzungen fälschen. Zwijg:KeyProtection:CertificatePath setzen, siehe docs/betrieb.md", KeyDirectory(gateway));

// Hinter dem Proxy zählt nur, ob er per HTTPS angesprochen wurde. X-Forwarded-For wird bewusst nicht übernommen,
// sonst könnte jeder seine Adresse für die Anmeldesperre selbst wählen. Proxy Adressen werden nicht geprüft,
// weil der Proxy in Docker nicht auf localhost läuft. Ein gefälschtes Proto bewirkt nur einen HSTS Header über HTTP,
// den Browser ignorieren. Der Port von Zwijg darf dann nur für den Proxy erreichbar sein.
if (gateway.BehindTlsProxy)
{
    var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto };
    forwarded.KnownNetworks.Clear();
    forwarded.KnownProxies.Clear();
    app.UseForwardedHeaders(forwarded);
}

// Fehler nie mit Details nach außen geben, die Anfrage könnte Patientendaten enthalten
app.UseExceptionHandler(error => error.Run(async ctx =>
{
    var ex = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    var badInput = ex is BadHttpRequestException or System.Text.Json.JsonException
        || ex?.InnerException is System.Text.DecoderFallbackException;

    // Zu langer oder zu verschachtelter Text: die Erkennung bricht ab, und nichts verlässt die Praxis
    var (status, message) = ex switch
    {
        _ when badInput => (400, "Ungültige Anfrage (JSON oder UTF-8 fehlerhaft)"),
        TextTooLongException => (413, Pseudonymizer.TooLongMessage),
        System.Text.RegularExpressions.RegexMatchTimeoutException => (422, ChatPipeline.TextTooComplexMessage),
        _ => (500, "Interner Fehler"),
    };

    ctx.Response.StatusCode = status;
    await ctx.Response.WriteAsJsonAsync(new { error = message });
}));

// Browser merken sich, Zwijg nur noch per HTTPS aufzurufen. Gilt nur für Antworten über HTTPS und nicht für localhost.
if (!app.Environment.IsDevelopment())
    app.UseHsts();

// Schutz für die Weboberfläche: nicht in fremde Seiten einbetten, nur eigene Skripte ausführen
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
    await next();
});

// Weboberfläche unter http://localhost:5247
app.UseDefaultFiles();
// Zeichensatz immer mitschicken, sonst raten Browser bei Umlauten in JS und CSS
var contentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
contentTypes.Mappings[".js"] = "text/javascript; charset=utf-8";
contentTypes.Mappings[".css"] = "text/css; charset=utf-8";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypes,
    // Browser fragen jedes Mal kurz nach, ob sich die Datei geändert hat. Sonst sieht man nach einem Update
    // noch tagelang die alte Oberfläche. Ist sie gleich geblieben, kommt nur "unverändert" zurück.
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});

app.UseAuthentication();
app.UseMiddleware<ApiKeyMiddleware>();
app.MapGatewayEndpoints();

app.Run();

static string KeyDirectory(GatewayOptions gw) =>
    Path.Combine(Path.GetDirectoryName(Path.GetFullPath(gw.SettingsPath))!, "keys");

public partial class Program;
