using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Zwijg.Gateway.Settings;

namespace Zwijg.Gateway;

public sealed record UpdateInfo(
    string Current,
    string? Latest,
    bool UpdateAvailable,
    string? ReleaseUrl,
    string? Notes,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? CheckedAt,
    string? Error,
    bool Enabled,
    string InstallType,
    string Repository);

public sealed record UpdateCheckInput(bool Enabled);

// Fragt bei GitHub nach der neuesten Version. Dabei geht nur diese Frage raus, keine Daten aus der Praxis.
public sealed partial class UpdateChecker(IHttpClientFactory http, SettingsStore settings, IConfiguration config, ILogger<UpdateChecker> logger)
{
    public const string HttpClientName = "github";
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(12);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private (string? Latest, string? Url, string? Notes, DateTimeOffset? Published, string? Error, DateTimeOffset At)? _cached;

    // Wer Zwijg abspaltet, kann hier sein eigenes Repository eintragen
    public string Repository => config["Zwijg:UpdateRepository"] is { Length: > 0 } repo ? repo : "kschnieders/zwijg";

    public async Task<UpdateInfo> GetAsync(bool refresh, CancellationToken ct)
    {
        var enabled = settings.Current.UpdateCheck;

        // Ist die Prüfung aus, fragt Zwijg nur, wenn ein Admin ausdrücklich auf "Jetzt prüfen" klickt
        if (enabled || refresh)
        {
            await _lock.WaitAsync(ct);
            try
            {
                if (refresh || _cached == null || DateTimeOffset.UtcNow - _cached.Value.At > CacheFor)
                    _cached = await FetchAsync(ct);
            }
            finally
            {
                _lock.Release();
            }
        }

        var current = Endpoints.Version;
        var c = _cached;
        var newer = c?.Latest != null && IsNewer(c.Value.Latest, current);
        return new UpdateInfo(current, c?.Latest, newer, c?.Url, c?.Notes, c?.Published, c?.At, c?.Error, enabled, InstallType(), Repository);
    }

    private async Task<(string?, string?, string?, DateTimeOffset?, string?, DateTimeOffset)> FetchAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));

            var client = http.CreateClient(HttpClientName);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
            req.Headers.UserAgent.ParseAdd($"Zwijg/{Endpoints.Version}");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var res = await client.SendAsync(req, timeout.Token);
            if (res.StatusCode == HttpStatusCode.NotFound)
                return (null, null, null, null, null, now); // Noch keine Veröffentlichung
            if (!res.IsSuccessStatusCode)
                return (null, null, null, null, $"GitHub antwortet mit {(int)res.StatusCode}", now);

            var json = JsonNode.Parse(await res.Content.ReadAsStringAsync(timeout.Token));
            var tag = json?["tag_name"]?.GetValue<string>();
            var notes = json?["body"]?.GetValue<string>();
            if (notes?.Length > 4000)
                notes = notes[..4000] + " ...";
            var published = json?["published_at"]?.GetValue<string>() is { } p && DateTimeOffset.TryParse(p, out var d) ? d : (DateTimeOffset?)null;

            return (tag?.TrimStart('v', 'V'), json?["html_url"]?.GetValue<string>(), notes, published, null, now);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            logger.LogInformation("Update Prüfung fehlgeschlagen: {Message}", ex.Message);
            return (null, null, null, null, "GitHub ist gerade nicht erreichbar", now);
        }
    }

    // Vergleicht nur die Zahlen vorne, "1.2.0-beta" zählt wie "1.2.0"
    public static bool IsNewer(string latest, string current) =>
        Parse(latest) is { } l && Parse(current) is { } c && l > c;

    private static Version? Parse(string text) =>
        VersionPattern().Match(text) is { Success: true } m
            ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))
            : null;

    [GeneratedRegex(@"^v?(\d+)\.(\d+)\.(\d+)")]
    private static partial Regex VersionPattern();

    // Wie wurde Zwijg installiert? Danach richten sich die Update Befehle in der Oberfläche.
    private static string InstallType()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
            return "docker";
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "update.ps1")))
            return "windows";
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "update.sh")))
            return "linux";
        return "source";
    }
}

public static class UpdateEndpoints
{
    public static void MapUpdates(this WebApplication app)
    {
        var admin = app.MapGroup("/admin");

        admin.MapGet("/version", (bool? refresh, UpdateChecker updates, CancellationToken ct) =>
            updates.GetAsync(refresh == true, ct));

        admin.MapPut("/version/check", (UpdateCheckInput input, HttpContext ctx, SettingsStore store, Zwijg.Core.Audit.IAuditLog audit) =>
            AdminSettingsEndpoints.Change(ctx, store, audit,
                input.Enabled ? "Update Prüfung eingeschaltet" : "Update Prüfung ausgeschaltet",
                s => s.UpdateCheck = input.Enabled));
    }
}
