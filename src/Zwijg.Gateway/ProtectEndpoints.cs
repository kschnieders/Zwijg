using System.Text.Json.Nodes;
using Zwijg.Core.Audit;
using Zwijg.Core.Pseudonymization;
using Zwijg.Core.Routing;
using Zwijg.Core.Security;
using Zwijg.Gateway.History;
using Zwijg.Gateway.Settings;

namespace Zwijg.Gateway;

// Known: Zuordnung aus früheren Runden, damit derselbe Name denselben Platzhalter behält
public sealed record ProtectRequest(string? Text, JsonArray? Secrets = null, List<ProtectEntry>? Known = null);

public sealed record ProtectEntry(string Placeholder, string Value);

// "Text schützen": Text geschützt zum Kopieren in ein anderes Programm ausgeben.
// Das Zurücksetzen der Antwort passiert im Browser, die Zuordnung wird auf dem Server nicht gespeichert.
public static class ProtectEndpoints
{
    private const int MaxLength = 100_000;

    public static void MapProtect(this WebApplication app)
    {
        app.MapPost("/v1/protect", async (ProtectRequest req, HttpContext ctx, Pseudonymizer pseudonymizer,
            SettingsStore settings, IAuditLog audit, CancellationToken ct) =>
        {
            var user = ApiKeyMiddleware.GetUser(ctx);
            var o = settings.Current;
            var text = TextSanitizer.Clean(req.Text ?? "").Text;

            if (string.IsNullOrWhiteSpace(text))
                return Results.BadRequest(new { error = "Bitte einen Text einfügen" });
            if (text.Length > MaxLength)
                return Results.BadRequest(new { error = "Der Text ist zu lang, höchstens 100.000 Zeichen" });

            var map = new PseudonymMap();
            foreach (var e in req.Known ?? [])
                map.Seed(e.Placeholder, e.Value);

            var pseudo = await pseudonymizer.PseudonymizeAsync(text, map, ct, HistoryEndpoints.ReadSecrets(req.Secrets));

            // Ein kopierter Text verlässt die Praxis wie eine Anfrage an die Cloud, deshalb gelten dieselben Regeln
            if (Refusal(user, o, text, map) is { } reason)
            {
                await audit.WriteAsync(new AuditEntry
                {
                    User = user.Name,
                    UserId = user.Id,
                    Action = "protect",
                    Sensitivity = map.MaxSensitivity.ToString(),
                    Entities = map.Summary(),
                    Blocked = true,
                    Reason = reason,
                }, ct);
                return Results.Json(new { error = reason }, statusCode: 403);
            }

            await audit.WriteAsync(new AuditEntry
            {
                User = user.Name,
                UserId = user.Id,
                Action = "protect",
                Route = "External",
                Sensitivity = map.MaxSensitivity.ToString(),
                Entities = map.Summary(),
                Prompt = o.StorePrompts ? pseudo : null,
            }, ct);

            return Results.Ok(new
            {
                @protected = pseudo,
                mapping = map.Entries.Select(e => new { placeholder = e.Key, value = e.Value }),
                entities = map.TypeCounts.ToDictionary(x => EntityTypeInfo.Label(x.Key), x => x.Value),
                sensitivity = map.MaxSensitivity.ToString(),
                healthTerms = map.HealthTerms,
            });
        });
    }

    private static string? Refusal(GatewayUser user, GatewaySettings o, string text, PseudonymMap map)
    {
        if (!user.CloudAllowed)
            return "Du darfst keine Texte aus der Praxis geben. Bitte wende dich an die Verwaltung.";

        if (o.Routing.Mode == RoutingMode.LocalOnly)
            return "Laut Regeln verlässt nichts die Praxis (Weiterleitung steht auf Nur lokal).";

        var hits = RuleEngine.Find(text, o.ProtectionRules, a => a is RuleAction.Block or RuleAction.LocalOnly or RuleAction.Replace);

        // Zu langsame Ersetzen-Regel: Der Wert stünde womöglich im Klartext im geschützten Text
        if (hits.FirstOrDefault(h => h.TimedOut && h.Rule.Action == RuleAction.Replace) is { } slow)
            return $"Die Schutzregel \"{slow.Rule.Name}\" konnte nicht rechtzeitig geprüft werden.";

        var rules = hits.Where(h => h.Rule.Action != RuleAction.Replace)
            .Select(h => h.Rule).DistinctBy(r => r.Id).ToList();
        if (rules.FirstOrDefault(r => r.Action == RuleAction.Block) is { } block)
            return $"Die Schutzregel \"{block.Name}\" blockiert diesen Text.";
        if (rules.FirstOrDefault(r => r.Action == RuleAction.LocalOnly) is { } local)
            return $"Die Schutzregel \"{local.Name}\" hält diesen Text in der Praxis.";

        if (new RoutingPolicy(o.Routing).Decide(map.MaxSensitivity, false).Route == RouteTarget.Local)
            return "Dieser Text ist laut Regeln zu sensibel, um die Praxis zu verlassen, auch geschützt. " +
                   "Ein Admin kann das unter Regeln, Weiterleitung bei \"Was darf höchstens in die Cloud?\" ändern.";

        return null;
    }
}
