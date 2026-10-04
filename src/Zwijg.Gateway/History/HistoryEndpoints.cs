using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Zwijg.Core.Audit;
using Zwijg.Core.Pseudonymization;
using Zwijg.Core.Security;
using Zwijg.Gateway.Settings;

namespace Zwijg.Gateway.History;

public sealed record ConversationUpdate(string? Title, bool? Pinned);

public static partial class HistoryEndpoints
{
    public static void MapHistory(this WebApplication app)
    {
        // Eigene Unterhaltungen, nie die von anderen
        var mine = app.MapGroup("/v1/conversations");

        mine.MapGet("", async (HttpContext ctx, ConversationStore store, SettingsStore settings, int? limit, int? offset, CancellationToken ct) =>
        {
            if (!settings.Current.History.Enabled)
                return Results.Ok(new { items = Array.Empty<object>(), total = 0 });

            var page = await store.ListAsync(ApiKeyMiddleware.GetUser(ctx).Id, limit ?? 20, offset ?? 0, ct);
            return Results.Ok(new { items = page.Items, total = page.Total });
        });

        mine.MapGet("/{id}", async (string id, HttpContext ctx, ConversationStore store, CancellationToken ct) =>
            await store.GetAsync(ApiKeyMiddleware.GetUser(ctx).Id, id, ct) is { } c
                ? Results.Ok(new { c.Info.Id, c.Info.Title, c.Info.Pinned, c.Info.Updated, c.Messages, c.Secrets, c.Patient })
                : Results.NotFound(new { error = "Unterhaltung nicht gefunden" }));

        mine.MapPut("/{id}", async (string id, ConversationUpdate input, HttpContext ctx, ConversationStore store, CancellationToken ct) =>
        {
            if (input.Title?.Length > 100)
                return Results.BadRequest(new { error = "Der Titel darf höchstens 100 Zeichen haben" });

            try
            {
                return await store.UpdateAsync(ApiKeyMiddleware.GetUser(ctx).Id, id, input.Title, input.Pinned, ct)
                    ? Results.Ok(new { ok = true })
                    : Results.NotFound(new { error = "Unterhaltung nicht gefunden" });
            }
            catch (SettingsException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        mine.MapDelete("/{id}", async (string id, HttpContext ctx, ConversationStore store, CancellationToken ct) =>
            await store.DeleteAsync(ApiKeyMiddleware.GetUser(ctx).Id, id, ct)
                ? Results.Ok(new { ok = true })
                : Results.NotFound(new { error = "Unterhaltung nicht gefunden" }));

        mine.MapDelete("", async (HttpContext ctx, ConversationStore store, CancellationToken ct) =>
            Results.Ok(new { deleted = await store.DeleteAllAsync(ApiKeyMiddleware.GetUser(ctx).Id, ct) }));

        // Verwaltung: nur Einstellungen und Zahlen, keine Inhalte
        app.MapGet("/admin/history", async (SettingsStore settings, ConversationStore store, CancellationToken ct) =>
        {
            var (conversations, users) = await store.CountAsync(ct);
            return Results.Ok(new { options = settings.Current.History, conversations, users });
        });

        app.MapPut("/admin/history", async (HistoryOptions input, HttpContext ctx, SettingsStore settings, ConversationStore store,
            IAuditLog audit, CancellationToken ct) =>
        {
            var o = settings.Current.History;
            var detail = input.Enabled
                ? $"{input.MaxConversations} Unterhaltungen, {input.RetentionDays} Tage, {input.MaxPinned} angepinnt"
                : "ausgeschaltet, alle Verläufe gelöscht";
            var result = await AdminSettingsEndpoints.ChangeAsync(ctx, settings, audit, $"Verlauf geändert: {detail}", s => s.History = input);

            if (result is IStatusCodeHttpResult { StatusCode: 200 })
                await store.CleanupAsync(ct);
            return result;
        });

        app.MapDelete("/admin/conversations", async (HttpContext ctx, ConversationStore store, IAuditLog audit, CancellationToken ct) =>
        {
            var deleted = await store.DeleteAllAsync(null, ct);
            await audit.WriteAsync(new AuditEntry
            {
                User = ApiKeyMiddleware.GetUser(ctx).Name,
                Action = "admin",
                Reason = $"Alle Verläufe gelöscht ({deleted} Unterhaltungen)"
            }, ct);
            return Results.Ok(new { deleted });
        });
    }

    // Holt die Unterhaltungs Id aus der Anfrage und entfernt das Feld, damit es nicht an das Modell geht.
    // "new" legt eine neue an, fehlt das Feld, wird nichts gespeichert (z.B. bei API Clients).
    public static (bool Save, string? Id) TakeConversation(JsonObject request)
    {
        var z = TakeZwijg(request);
        return (z.Save, z.Id);
    }

    // Alles unter "zwijg" gehört nur zum Gateway und darf nie mit an das Modell.
    // Secrets: selbst markiert. Patient: Inhalt des Patientenfelds, daraus werden weitere versteckte Begriffe.
    public static (bool Save, string? Id, List<SecretTerm> Secrets, string? Patient) TakeZwijg(JsonObject request)
    {
        var secrets = ReadSecrets(request["zwijg"]?["secrets"]);
        var patient = ReadPatient(request["zwijg"]?["patient"]);
        var z = TakeConversationOnly(request);
        return (z.Save, z.Id, secrets, patient);
    }

    public static string? ReadPatient(JsonNode? node)
    {
        var text = (node as JsonValue)?.TryGetValue<string>(out var s) == true ? s.Trim() : null;
        if (string.IsNullOrEmpty(text))
            return null;
        return text.Length > PatientTerms.MaxLength ? text[..PatientTerms.MaxLength] : text;
    }

    // Selbst markierte Geheimnisse, z.B. [{ "value": "Projekt Nordlicht", "label": "GEHEIM" }]
    public static List<SecretTerm> ReadSecrets(JsonNode? node)
    {
        if (node is not JsonArray list)
            return [];

        return list.OfType<JsonObject>()
            .Select(o => (Value: (o["value"] as JsonValue)?.GetValue<string>()?.Trim() ?? "",
                          Label: (o["label"] as JsonValue)?.GetValue<string>()?.Trim().ToUpperInvariant() ?? ""))
            .Where(s => s.Value.Length is >= 2 and <= 500)
            .Select(s => new SecretTerm(s.Value, SecretLabel().IsMatch(s.Label) ? s.Label : "GEHEIM"))
            .DistinctBy(s => s.Value, StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToList();
    }

    private static (bool Save, string? Id) TakeConversationOnly(JsonObject request)
    {
        var node = request["zwijg"]?["conversation"];
        request.Remove("zwijg");

        if (node is not JsonValue v || !v.TryGetValue<string>(out var value) || string.IsNullOrWhiteSpace(value))
            return (false, null);

        return (true, value == "new" ? null : value);
    }

    // Titel aus der ersten Frage, aber ohne Patientendaten. Aus "Herr Max Mustermann hat Fieber"
    // wird "Herr … hat Fieber", damit niemand über die Schulter Namen in der Seitenleiste liest.
    // Einfache Daten wie "02.10.2026" dürfen bleiben, sie verraten allein niemanden. Geburtsdaten nicht.
    public static async Task<string> SafeTitleAsync(string firstQuestion, Pseudonymizer pseudonymizer, CancellationToken ct,
        IReadOnlyList<SecretTerm>? secrets = null)
    {
        var map = new PseudonymMap();
        var pseudo = await pseudonymizer.PseudonymizeAsync(TextSanitizer.Clean(firstQuestion).Text, map, ct, secrets);
        pseudo = PlainDates().Replace(pseudo, m => map.Restore(m.Value));
        var title = Placeholders().Replace(pseudo, "…");
        title = Ellipses().Replace(title, "…");
        title = Whitespace().Replace(title, " ").Trim();
        return title.Length <= 70 ? title : title[..67].TrimEnd() + "...";
    }

    // Für Vorlagen: "Patientenabsage · Freitag, 02.10.2026 um 09:30 Uhr".
    // Nimmt den ersten Wert, der ganz ohne Patientendaten auskommt, Namen landen also nie im Titel.
    public static async Task<string> SafeDisplayTitleAsync(JsonObject display, Pseudonymizer pseudonymizer, CancellationToken ct,
        IReadOnlyList<SecretTerm>? secrets = null)
    {
        var title = display["title"]?.GetValue<string>() ?? "Vorlage";
        foreach (var field in display["fields"] as JsonArray ?? [])
        {
            var value = field?["value"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var safe = await SafeTitleAsync(value, pseudonymizer, ct, secrets);
            if (!safe.Contains('…'))
                return $"{title} · {safe}";
        }

        return title;
    }

    // Bezeichnung für eigene Platzhalter, nur A bis Z wie GEHEIM oder FIRMA, sonst findet Restore sie nicht wieder
    [GeneratedRegex(@"^[A-Z]{2,20}$")]
    private static partial Regex SecretLabel();

    [GeneratedRegex(@"\[DATUM_\d+\]")]
    private static partial Regex PlainDates();

    [GeneratedRegex(@"\[[A-Z]+_\d+\]")]
    private static partial Regex Placeholders();

    [GeneratedRegex(@"…(?:[\s,]*…)+")]
    private static partial Regex Ellipses();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
