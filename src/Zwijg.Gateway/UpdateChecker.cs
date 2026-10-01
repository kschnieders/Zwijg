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
    string Repository,
    bool Security,
    IReadOnlyList<ReleaseNote> Releases);

// Eine Version auf GitHub. Security ist true, wenn die Beschreibung einen Abschnitt "Sicherheit" hat.
public sealed record ReleaseNote(string Version, string? Notes, string? Url, DateTimeOffset? PublishedAt, bool Security);

public sealed record UpdateCheckInput(bool Enabled);

// Fragt bei GitHub nach der neuesten Version. Dabei geht nur diese Frage raus, keine Daten aus der Praxis.
public sealed partial class UpdateChecker(IHttpClientFactory http, SettingsStore settings, IConfiguration config, ILogger<UpdateChecker> logger)
{
    public const string HttpClientName = "github";
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(12);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private Fetched? _cached;

    private sealed record Fetched(IReadOnlyList<ReleaseNote> Releases, string? Error, DateTimeOffset At);

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
                if (refresh || _cached == null || DateTimeOffset.UtcNow - _cached.At > CacheFor)
                    _cached = await FetchAsync(ct);
            }
            finally
            {
                _lock.Release();
            }
        }

        var current = Endpoints.Version;
        var c = _cached;
        var latest = c?.Releases.FirstOrDefault();

        // Alles zwischen der installierten und der neuesten Version, damit man nichts verpasst
        var missed = c?.Releases.Where(r => IsNewer(r.Version, current)).ToList() ?? [];
        return new UpdateInfo(current, latest?.Version, missed.Count > 0, latest?.Url, latest?.Notes, latest?.PublishedAt,
            c?.At, c?.Error, enabled, InstallType(), Repository, missed.Any(r => r.Security), missed);
    }

    private async Task<Fetched> FetchAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));

            var client = http.CreateClient(HttpClientName);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases?per_page=30");
            req.Headers.UserAgent.ParseAdd($"Zwijg/{Endpoints.Version}");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var res = await client.SendAsync(req, timeout.Token);
            if (res.StatusCode == HttpStatusCode.NotFound)
                return new([], null, now); // Noch keine Veröffentlichung
            if (!res.IsSuccessStatusCode)
                return new([], $"GitHub antwortet mit {(int)res.StatusCode}", now);

            var releases = new List<ReleaseNote>();
            if (JsonNode.Parse(await res.Content.ReadAsStringAsync(timeout.Token)) is JsonArray list)
            {
                foreach (var json in list.OfType<JsonObject>())
                {
                    // Entwürfe und Vorabversionen zählen nicht
                    if (json["draft"]?.GetValue<bool>() == true || json["prerelease"]?.GetValue<bool>() == true)
                        continue;
                    if (json["tag_name"]?.GetValue<string>()?.TrimStart('v', 'V') is not { } version || Parse(version) == null)
                        continue;

                    var notes = json["body"]?.GetValue<string>();
                    if (notes?.Length > 4000)
                        notes = notes[..4000] + " ...";
                    var published = json["published_at"]?.GetValue<string>() is { } p && DateTimeOffset.TryParse(p, out var d) ? d : (DateTimeOffset?)null;
                    releases.Add(new ReleaseNote(version, notes, json["html_url"]?.GetValue<string>(), published, Changelog.IsSecurity(notes)));
                }
            }

            // Neueste zuerst, egal in welcher Reihenfolge GitHub sie liefert
            releases.Sort((a, b) => Parse(b.Version)!.CompareTo(Parse(a.Version)));
            return new(releases, null, now);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            logger.LogInformation("Update Prüfung fehlgeschlagen: {Message}", ex.Message);
            return new([], "GitHub ist gerade nicht erreichbar", now);
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
            AdminSettingsEndpoints.ChangeAsync(ctx, store, audit,
                input.Enabled ? "Update Prüfung eingeschaltet" : "Update Prüfung ausgeschaltet",
                s => s.UpdateCheck = input.Enabled));
    }
}
