using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.Options;
using Zwijg.Core.Audit;
using Zwijg.Core.Pseudonymization;
using Zwijg.Core.Security;
using Zwijg.Gateway;
using Zwijg.Gateway.Providers;
using Zwijg.Gateway.Settings;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection("Zwijg"));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddHttpClient();

// Schlüssel zum Verschlüsseln der API Schlüssel liegen neben den Einstellungen.
// Unter Windows werden sie zusätzlich mit DPAPI an das Benutzerkonto gebunden.
builder.Services.AddDataProtection().SetApplicationName("Zwijg");
builder.Services.AddOptions<KeyManagementOptions>()
    .Configure<IOptions<GatewayOptions>, ILoggerFactory>((o, gw, logs) =>
    {
        var dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(gw.Value.SettingsPath))!, "keys");
        o.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(dir), logs);
        if (OperatingSystem.IsWindows())
            o.XmlEncryptor = new DpapiXmlEncryptor(protectToLocalMachine: false, logs);
    });

builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<UpdateChecker>();

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
    new SqliteAuditLog(sp.GetRequiredService<IOptions<GatewayOptions>>().Value.Audit.DatabasePath));
builder.Services.AddSingleton<ChatPipeline>();
builder.Services.AddSingleton<Zwijg.Gateway.History.ConversationStore>();
builder.Services.AddHostedService<Zwijg.Gateway.History.ConversationCleanup>();

var app = builder.Build();

// Einstellungen gleich beim Start laden, damit Fehler sofort auffallen
app.Services.GetRequiredService<SettingsStore>();

// Fehler nie mit Details nach außen geben, die Anfrage könnte Patientendaten enthalten
app.UseExceptionHandler(error => error.Run(async ctx =>
{
    var ex = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    var badInput = ex is BadHttpRequestException or System.Text.Json.JsonException
        || ex?.InnerException is System.Text.DecoderFallbackException;

    ctx.Response.StatusCode = badInput ? 400 : 500;
    await ctx.Response.WriteAsJsonAsync(new { error = badInput ? "Ungültige Anfrage (JSON oder UTF-8 fehlerhaft)" : "Interner Fehler" });
}));

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
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypes });

app.UseAuthentication();
app.UseMiddleware<ApiKeyMiddleware>();
app.MapGatewayEndpoints();

app.Run();

public partial class Program;
