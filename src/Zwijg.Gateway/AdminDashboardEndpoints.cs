using Zwijg.Core.Audit;
using Zwijg.Core.Routing;
using Zwijg.Gateway.Providers;
using Zwijg.Gateway.Settings;

namespace Zwijg.Gateway;

public sealed record AnnouncementInput(
    string Title,
    string Message,
    AnnouncementLevel Level,
    AnnouncementAudience Audience,
    List<string>? UserIds,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    bool Dismissible,
    bool Enabled);

// Übersicht für Admins, Nutzung pro Benutzer und Benachrichtigungen
public static class AdminDashboardEndpoints
{
    private const int ChartDays = 14;

    public static void MapAdminDashboard(this WebApplication app)
    {
        var admin = app.MapGroup("/admin");

        admin.MapGet("/stats", async (IAuditLog audit, SettingsStore store, ProviderRegistry providers, CancellationToken ct) =>
        {
            var today = ChatPipeline.StartOfToday();
            var since = today.AddDays(-(ChartDays - 1));
            var rows = await audit.StatRowsAsync(since, ct);
            var requests = rows.Where(r => r.Action is "chat" or "document").ToList();
            var week = requests.Where(r => r.Timestamp >= today.AddDays(-6)).ToList();

            var days = Enumerable.Range(0, ChartDays).Select(i =>
            {
                var start = since.AddDays(i);
                var inDay = requests.Where(r => r.Timestamp >= start && r.Timestamp < start.AddDays(1)).ToList();
                return new
                {
                    date = start.ToString("yyyy-MM-dd"),
                    local = inDay.Count(r => !r.Blocked && r.Route == "Local"),
                    cloud = inDay.Count(r => !r.Blocked && r.Route == "Cloud"),
                    blocked = inDay.Count(r => r.Blocked),
                };
            });

            var s = store.Current;
            return Results.Ok(new
            {
                today = Summary(requests.Where(r => r.Timestamp >= today).ToList()),
                week = Summary(week),
                days,
                // Auffällige Ereignisse: blockiert oder Fehler beim Anbieter
                recent = rows
                    .Where(r => r.Blocked || IsError(r) || r.Action == "admin")
                    .OrderByDescending(r => r.Id)
                    .Take(12)
                    .Select(r => new
                    {
                        r.Id, r.Timestamp, r.User, r.Action, r.Reason, r.InjectionScore,
                        kind = r.Blocked ? "blocked" : IsError(r) ? "error" : "admin"
                    }),
                topUsers = week
                    .GroupBy(r => r.User)
                    .Select(g => new { user = g.Key, requests = g.Count(), blocked = g.Count(r => r.Blocked) })
                    .OrderByDescending(x => x.requests)
                    .Take(5),
                routes = new
                {
                    local = providers.GetConnection(RouteTarget.Local)?.Name,
                    localReady = providers.IsConfigured(RouteTarget.Local),
                    cloud = providers.GetConnection(RouteTarget.Cloud)?.Name,
                    cloudReady = providers.IsConfigured(RouteTarget.Cloud),
                    mode = s.Routing.Mode,
                },
                users = new { total = s.Users.Count, active = s.Users.Count(u => u.Active), admins = s.Users.Count(u => u.Admin) },
                announcements = s.Announcements.Count(a => a.Enabled && (a.EndsAt == null || a.EndsAt > DateTimeOffset.UtcNow)),
            });
        });

        // Nutzung pro Benutzer für die Benutzerliste
        admin.MapGet("/users/stats", async (IAuditLog audit, CancellationToken ct) =>
        {
            var today = ChatPipeline.StartOfToday();
            var rows = await audit.StatRowsAsync(today.AddDays(-29), ct);

            return Results.Ok(rows
                .Where(r => r.Action is "chat" or "document")
                .GroupBy(r => r.User)
                .ToDictionary(g => g.Key, g => new
                {
                    lastSeen = g.Max(r => r.Timestamp),
                    today = g.Count(r => r.Timestamp >= today),
                    month = g.Count(),
                    blocked = g.Count(r => r.Blocked),
                }));
        });

        // Benachrichtigungen

        admin.MapPost("/announcements", (AnnouncementInput input, HttpContext ctx, SettingsStore store, IAuditLog audit) =>
            AdminSettingsEndpoints.Change(ctx, store, audit, $"Benachrichtigung \"{Short(input.Title)}\" angelegt", s =>
            {
                var a = new Announcement { CreatedBy = ApiKeyMiddleware.GetUser(ctx).Name };
                Apply(a, input);
                s.Announcements.Add(a);
            }));

        admin.MapPut("/announcements/{id}", (string id, AnnouncementInput input, HttpContext ctx, SettingsStore store, IAuditLog audit) =>
            AdminSettingsEndpoints.Change(ctx, store, audit, $"Benachrichtigung \"{Short(input.Title)}\" geändert", s =>
            {
                var a = s.Announcements.FirstOrDefault(x => x.Id == id) ?? throw new SettingsException("Benachrichtigung nicht gefunden");
                Apply(a, input);
            }));

        // Wieder allen zeigen, auch denen, die sie schon weggeklickt haben
        admin.MapPost("/announcements/{id}/reset", (string id, HttpContext ctx, SettingsStore store, IAuditLog audit) =>
            AdminSettingsEndpoints.Change(ctx, store, audit, "Benachrichtigung erneut an alle", s =>
            {
                foreach (var u in s.Users)
                    u.DismissedAnnouncements.Remove(id);
            }));

        admin.MapDelete("/announcements/{id}", (string id, HttpContext ctx, SettingsStore store, IAuditLog audit) =>
        {
            var title = store.Current.Announcements.FirstOrDefault(a => a.Id == id)?.Title ?? id;
            return AdminSettingsEndpoints.Change(ctx, store, audit, $"Benachrichtigung \"{Short(title)}\" gelöscht", s =>
            {
                s.Announcements.RemoveAll(a => a.Id == id);
                foreach (var u in s.Users)
                    u.DismissedAnnouncements.Remove(id);
            });
        });
    }

    private static object Summary(List<AuditStatRow> rows) => new
    {
        requests = rows.Count,
        blocked = rows.Count(r => r.Blocked),
        errors = rows.Count(IsError),
        local = rows.Count(r => !r.Blocked && r.Route == "Local"),
        cloud = rows.Count(r => !r.Blocked && r.Route == "Cloud"),
        protectedValues = rows.Sum(r => CountProtected(r.Entities)),
        withHealthData = rows.Count(r => r.Entities?.Contains("GESUNDHEIT") == true),
        users = rows.Select(r => r.User).Distinct().Count(),
    };

    private static bool IsError(AuditStatRow r) =>
        r.Reason != null && (r.Reason.StartsWith("Anbieterfehler") || r.Reason.StartsWith("Kein Anbieter"));

    // "NAME:2, ORT:1, GESUNDHEIT:2" ergibt 3 ersetzte Werte
    private static int CountProtected(string? entities)
    {
        if (string.IsNullOrEmpty(entities))
            return 0;

        return entities.Split(',', StringSplitOptions.TrimEntries)
            .Where(p => !p.StartsWith("GESUNDHEIT"))
            .Sum(p => int.TryParse(p[(p.IndexOf(':') + 1)..], out var n) ? n : 0);
    }

    private static void Apply(Announcement a, AnnouncementInput input)
    {
        a.Title = input.Title.Trim();
        a.Message = input.Message.Trim();
        a.Level = input.Level;
        a.Audience = input.Audience;
        a.UserIds = input.Audience == AnnouncementAudience.Selected ? input.UserIds ?? [] : [];
        a.StartsAt = input.StartsAt;
        a.EndsAt = input.EndsAt;
        a.Dismissible = input.Dismissible;
        a.Enabled = input.Enabled;
    }

    private static string Short(string s) => s.Length <= 40 ? s : s[..40] + "...";
}
