using System.Diagnostics;
using System.Text.Json.Nodes;
using Zwijg.Core.Audit;
using Zwijg.Core.Routing;
using Zwijg.Gateway.Providers;
using Zwijg.Gateway.Settings;

namespace Zwijg.Gateway;

public sealed record ConnectionInput(
    string Name,
    string? Preset,
    string Type,
    string? BaseUrl,
    string? ApiKey,
    bool ClearApiKey,
    string? Model,
    string? Effort,
    int? TimeoutSeconds,
    double? Temperature,
    bool OnPremise);

public sealed record RoutesInput(string? LocalConnectionId, string? CloudConnectionId);

public sealed record PolicyInput(
    RoutingOptions Routing,
    InjectionOptions Injection,
    bool StorePrompts,
    bool UseLocalLlmForNames,
    List<string>? ExtraNames = null,
    List<string>? ExtraPlaces = null,
    List<string>? IgnoredWords = null);

public sealed record UserInput(
    string Name,
    bool Admin,
    bool Active = true,
    bool ShowPreview = true,
    bool CanUseDocuments = true,
    bool CloudAllowed = true,
    int? DailyLimit = null,
    string? Note = null,
    string? Instructions = null,
    string? Username = null,
    string? Password = null,
    bool? MustChangePassword = null);

public sealed record PreviewInput(ConnectionInput Connection, string? Id);

// Konfiguration über die Oberfläche. Jede Änderung landet auch im Protokoll.
public static class AdminSettingsEndpoints
{
    public static void MapAdminSettings(this WebApplication app)
    {
        var admin = app.MapGroup("/admin");

        admin.MapGet("/settings", (SettingsStore store, ProviderRegistry providers) => Results.Ok(View(store.Current, providers)));

        // Verbindungen

        admin.MapPost("/connections", (ConnectionInput input, HttpContext ctx, SettingsStore store, IAuditLog audit) =>
            ChangeAsync(ctx, store, audit, $"Verbindung {input.Name} angelegt", s =>
            {
                var c = new Connection();
                Apply(c, input, store);
                s.Connections.Add(c);

                // Die erste passende Verbindung gleich zuordnen, das spart einen Schritt
                if (s.LocalConnectionId == null && c.OnPremise)
                    s.LocalConnectionId = c.Id;
                else if (s.CloudConnectionId == null && !c.OnPremise)
                    s.CloudConnectionId = c.Id;
            }));

        admin.MapPut("/connections/{id}", (string id, ConnectionInput input, HttpContext ctx, SettingsStore store, IAuditLog audit) =>
            ChangeAsync(ctx, store, audit, $"Verbindung {input.Name} geändert", s =>
            {
                var c = s.Connections.FirstOrDefault(x => x.Id == id) ?? throw new SettingsException("Verbindung nicht gefunden");
                Apply(c, input, store);
            }));

        admin.MapDelete("/connections/{id}", async (string id, HttpContext ctx, SettingsStore store, IAuditLog audit) =>
        {
            var name = store.Current.Connections.FirstOrDefault(c => c.Id == id)?.Name ?? id;
            return await ChangeAsync(ctx, store, audit, $"Verbindung {name} gelöscht", s =>
            {
                s.Connections.RemoveAll(c => c.Id == id);
                if (s.LocalConnectionId == id) s.LocalConnectionId = null;
                if (s.CloudConnectionId == id) s.CloudConnectionId = null;
            });
        });

        admin.MapPut("/routes", (RoutesInput input, HttpContext ctx, SettingsStore store, IAuditLog audit) =>
            ChangeAsync(ctx, store, audit, "Zuordnung lokal/Cloud geändert", s =>
            {
                s.LocalConnectionId = string.IsNullOrEmpty(input.LocalConnectionId) ? null : input.LocalConnectionId;
                s.CloudConnectionId = string.IsNullOrEmpty(input.CloudConnectionId) ? null : input.CloudConnectionId;
            }));

        admin.MapPost("/connections/{id}/test", async (string id, SettingsStore store, ProviderRegistry providers, CancellationToken ct) =>
        {
            var c = store.Current.Connections.FirstOrDefault(x => x.Id == id);
            return c == null
                ? Results.NotFound(new { error = "Verbindung nicht gefunden" })
                : await TestAsync(providers.IsUsable(c) ? providers.Create(c) : null, ct);
        });

        admin.MapGet("/connections/{id}/models", async (string id, SettingsStore store, ProviderRegistry providers, CancellationToken ct) =>
        {
            var c = store.Current.Connections.FirstOrDefault(x => x.Id == id);
            return c == null
                ? Results.NotFound(new { error = "Verbindung nicht gefunden" })
                : await ModelsAsync(providers.IsUsable(c) ? providers.Create(c) : null, ct);
        });

        // Dasselbe mit den Eingaben aus dem Formular, bevor gespeichert wurde
        admin.MapPost("/connections/preview/test", async (PreviewInput input, SettingsStore store, ProviderRegistry providers, CancellationToken ct) =>
        {
            var c = FromPreview(input, store);
            return await TestAsync(providers.IsUsable(c) ? providers.CreateTemporary(c) : null, ct);
        });

        admin.MapPost("/connections/preview/models", async (PreviewInput input, SettingsStore store, ProviderRegistry providers, CancellationToken ct) =>
        {
            var c = FromPreview(input, store);
            return await ModelsAsync(providers.IsUsable(c) ? providers.CreateTemporary(c) : null, ct);
        });

        // Regeln

        admin.MapPut("/policy", (PolicyInput input, HttpContext ctx, SettingsStore store, IAuditLog audit) =>
            ChangeAsync(ctx, store, audit, "Regeln geändert", s =>
            {
                if (input.Injection.PromptThreshold is < 1 or > 1000 || input.Injection.DocumentThreshold is < 1 or > 1000)
                    throw new SettingsException("Schwellen müssen zwischen 1 und 1000 liegen");

                s.Routing = input.Routing;
                s.Injection = input.Injection;
                s.StorePrompts = input.StorePrompts;
                s.UseLocalLlmForNames = input.UseLocalLlmForNames;
                if (input.ExtraNames != null) s.ExtraNames = Clean(input.ExtraNames);
                if (input.ExtraPlaces != null) s.ExtraPlaces = Clean(input.ExtraPlaces);
                if (input.IgnoredWords != null) s.IgnoredWords = Clean(input.IgnoredWords);
            }));

        // Benutzer

        admin.MapPost("/users", async (UserInput input, HttpContext ctx, SettingsStore store, IAuditLog audit) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name))
                return Results.BadRequest(new { error = "Name fehlt" });

            var key = SettingsStore.NewUserKey();
            var result = await ChangeAsync(ctx, store, audit, $"Benutzer {input.Name.Trim()} angelegt", s =>
            {
                var u = new UserRecord { KeyHash = SettingsStore.HashKey(key), KeyHint = SettingsStore.Hint(key) };
                Apply(u, input);
                s.Users.Add(u);
            });

            // Der Schlüssel wird nur dieses eine Mal gezeigt
            return result is IStatusCodeHttpResult { StatusCode: 200 } ? Results.Ok(new { key }) : result;
        });

        admin.MapPut("/users/{id}", async (string id, UserInput input, HttpContext ctx, SettingsStore store, IAuditLog audit) =>
        {
            var me = ApiKeyMiddleware.GetUser(ctx);
            if (me.Id == id && !input.Active)
                return Results.BadRequest(new { error = "Du kannst dich nicht selbst sperren" });

            var before = store.Current.Users.FirstOrDefault(u => u.Id == id);
            return await ChangeAsync(ctx, store, audit, $"Benutzer {input.Name.Trim()} geändert{Describe(before, input)}", s =>
            {
                var u = s.Users.FirstOrDefault(x => x.Id == id) ?? throw new SettingsException("Benutzer nicht gefunden");
                Apply(u, input);
            });
        });

        // Neues Startpasswort, wird genau einmal angezeigt und muss beim Anmelden geändert werden
        admin.MapPost("/users/{id}/password-reset", async (string id, HttpContext ctx, SettingsStore store, IAuditLog audit) =>
        {
            var password = Passwords.Generate();
            var name = store.Current.Users.FirstOrDefault(u => u.Id == id)?.Name ?? id;
            var result = await ChangeAsync(ctx, store, audit, $"Passwort für {name} zurückgesetzt", s =>
            {
                var u = s.Users.FirstOrDefault(x => x.Id == id) ?? throw new SettingsException("Benutzer nicht gefunden");
                u.PasswordHash = Passwords.Hash(password);
                u.MustChangePassword = true;
                u.NewSecurityStamp();
            });

            return result is IStatusCodeHttpResult { StatusCode: 200 } ? Results.Ok(new { password }) : result;
        });

        admin.MapPost("/users/{id}/rotate", async (string id, HttpContext ctx, SettingsStore store, IAuditLog audit) =>
        {
            var key = SettingsStore.NewUserKey();
            var name = store.Current.Users.FirstOrDefault(u => u.Id == id)?.Name ?? id;
            var result = await ChangeAsync(ctx, store, audit, $"Neuer Schlüssel für {name}", s =>
            {
                var u = s.Users.FirstOrDefault(x => x.Id == id) ?? throw new SettingsException("Benutzer nicht gefunden");
                u.KeyHash = SettingsStore.HashKey(key);
                u.KeyHint = SettingsStore.Hint(key);
            });

            return result is IStatusCodeHttpResult { StatusCode: 200 } ? Results.Ok(new { key }) : result;
        });

        // Schlüssel ganz entfernen, dann geht die Anmeldung nur noch mit Passwort
        admin.MapDelete("/users/{id}/key", async (string id, HttpContext ctx, SettingsStore store, IAuditLog audit) =>
        {
            var name = store.Current.Users.FirstOrDefault(u => u.Id == id)?.Name ?? id;
            return await ChangeAsync(ctx, store, audit, $"Zugangsschlüssel von {name} entfernt", s =>
            {
                var u = s.Users.FirstOrDefault(x => x.Id == id) ?? throw new SettingsException("Benutzer nicht gefunden");
                if (u.PasswordHash == null)
                    throw new SettingsException($"{u.Name} hat noch kein Passwort. Ohne Schlüssel käme {u.Name} nicht mehr rein, bitte zuerst ein Passwort vergeben.");
                u.KeyHash = "";
                u.KeyHint = "";
            });
        });

        admin.MapDelete("/users/{id}", async (string id, HttpContext ctx, SettingsStore store, IAuditLog audit,
            History.ConversationStore history, CancellationToken ct) =>
        {
            if (ApiKeyMiddleware.GetUser(ctx).Id == id)
                return Results.BadRequest(new { error = "Du kannst dich nicht selbst löschen" });

            var name = store.Current.Users.FirstOrDefault(u => u.Id == id)?.Name ?? id;
            var result = await ChangeAsync(ctx, store, audit, $"Benutzer {name} gelöscht", s => s.Users.RemoveAll(u => u.Id == id));

            // Gespeicherte Unterhaltungen der Person gleich mit löschen
            if (result is IStatusCodeHttpResult { StatusCode: 200 })
                await history.DeleteAllAsync(id, ct);
            return result;
        });
    }

    internal static async Task<IResult> ChangeAsync(HttpContext ctx, SettingsStore store, IAuditLog audit, string description, Action<GatewaySettings> change)
    {
        try
        {
            store.Update(change);
        }
        catch (SettingsException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        // Die Änderung ist schon gespeichert, also trotzdem Ok. Klappt das Protokoll nicht, muss es wenigstens im Log stehen.
        try
        {
            await audit.WriteAsync(new AuditEntry
            {
                User = ApiKeyMiddleware.GetUser(ctx).Name,
                Action = "admin",
                Reason = description
            });
        }
        catch (Exception ex)
        {
            ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Zwijg.Audit")
                .LogError(ex, "Admin Änderung nicht im Protokoll: {Description}", OneLine(description));
        }

        return Results.Ok(new { ok = true });
    }

    private static void Apply(Connection c, ConnectionInput input, SettingsStore store)
    {
        c.Name = input.Name.Trim();
        c.Preset = string.IsNullOrWhiteSpace(input.Preset) ? "custom" : input.Preset;
        c.Type = input.Type;
        c.BaseUrl = string.IsNullOrWhiteSpace(input.BaseUrl) ? null : input.BaseUrl.Trim();
        c.Model = input.Model?.Trim() ?? "";
        c.Effort = string.IsNullOrWhiteSpace(input.Effort) ? null : input.Effort;
        c.TimeoutSeconds = input.TimeoutSeconds ?? 120;
        c.Temperature = c.Type == ConnectionTypes.Anthropic ? null : input.Temperature;
        c.OnPremise = input.OnPremise;

        // Leeres Feld heißt: alten Schlüssel behalten
        if (input.ClearApiKey)
        {
            c.ApiKeyProtected = null;
            c.ApiKeyHint = null;
        }
        else if (!string.IsNullOrWhiteSpace(input.ApiKey))
        {
            c.ApiKeyProtected = store.Protect(input.ApiKey.Trim());
            c.ApiKeyHint = "..." + input.ApiKey.Trim()[^Math.Min(4, input.ApiKey.Trim().Length)..];
        }
    }

    // API Schlüssel verlassen den Server nie, die Oberfläche sieht nur ob einer gesetzt ist
    private static object View(GatewaySettings s, ProviderRegistry providers) => new
    {
        connections = s.Connections.Select(c => new
        {
            c.Id,
            c.Name,
            c.Preset,
            c.Type,
            c.BaseUrl,
            hasApiKey = c.ApiKeyProtected != null,
            usesEnvironmentKey = c.ApiKeyProtected == null && c.Type == ConnectionTypes.Anthropic && providers.IsUsable(c),
            c.ApiKeyHint,
            c.Model,
            c.Effort,
            c.TimeoutSeconds,
            c.Temperature,
            c.OnPremise,
            usable = providers.IsUsable(c),
        }),
        s.LocalConnectionId,
        s.CloudConnectionId,
        policy = new { s.Routing, s.Injection, s.StorePrompts, s.UseLocalLlmForNames, s.ExtraNames, s.ExtraPlaces, s.IgnoredWords },
        users = s.Users.Select(u => new
        {
            u.Id, u.Name, u.KeyHint, u.Admin, u.Created, u.Active,
            u.ShowPreview, u.CanUseDocuments, u.CloudAllowed, u.DailyLimit, u.Note, u.Instructions,
            u.Username, hasPassword = u.PasswordHash != null, u.MustChangePassword
        }),
        announcements = s.Announcements.OrderByDescending(a => a.Created),
        s.Instructions,
        s.ProtectionRules,
        s.Templates,
    };

    private static void Apply(UserRecord u, UserInput input)
    {
        u.Name = input.Name.Trim();
        u.Admin = input.Admin;
        u.Active = input.Active;
        u.ShowPreview = input.ShowPreview;
        u.CanUseDocuments = input.CanUseDocuments;
        u.CloudAllowed = input.CloudAllowed;
        u.DailyLimit = input.DailyLimit is > 0 ? input.DailyLimit : null;
        u.Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim()[..Math.Min(500, input.Note.Trim().Length)];
        u.Instructions = string.IsNullOrWhiteSpace(input.Instructions) ? null : input.Instructions.Trim();

        // Leer heißt: Benutzername bleibt, der Speicher leitet ihn sonst aus dem Namen ab
        if (!string.IsNullOrWhiteSpace(input.Username))
            u.Username = input.Username.Trim().ToLowerInvariant();

        if (!string.IsNullOrEmpty(input.Password))
        {
            if (Passwords.Problem(input.Password, u.Username.Length > 0 ? u.Username : input.Name) is { } problem)
                throw new SettingsException(problem);
            u.PasswordHash = Passwords.Hash(input.Password);
            u.MustChangePassword = input.MustChangePassword ?? true;
            u.NewSecurityStamp();
        }
        else if (input.MustChangePassword is { } must && u.PasswordHash != null)
        {
            u.MustChangePassword = must;
        }

        // Gesperrt heißt auch: laufende Sitzungen sofort beenden
        if (!u.Active)
            u.NewSecurityStamp();
    }

    // Für das Protokoll: was genau hat sich geändert
    private static string Describe(UserRecord? before, UserInput after)
    {
        if (before == null)
            return "";

        var changes = new List<string>();
        if (before.Active != after.Active) changes.Add(after.Active ? "entsperrt" : "gesperrt");
        if (before.Admin != after.Admin) changes.Add(after.Admin ? "ist jetzt Admin" : "kein Admin mehr");
        if (before.CloudAllowed != after.CloudAllowed) changes.Add(after.CloudAllowed ? "Cloud erlaubt" : "nur lokal");
        if (before.CanUseDocuments != after.CanUseDocuments) changes.Add(after.CanUseDocuments ? "Dokumente erlaubt" : "keine Dokumente");
        if (before.ShowPreview != after.ShowPreview) changes.Add(after.ShowPreview ? "Vorschau an" : "Vorschau aus");
        if (before.DailyLimit != (after.DailyLimit is > 0 ? after.DailyLimit : null))
            changes.Add(after.DailyLimit is > 0 ? $"Limit {after.DailyLimit}/Tag" : "kein Limit");
        if (before.Name != after.Name.Trim()) changes.Add($"vorher {before.Name}");

        return changes.Count > 0 ? ": " + string.Join(", ", changes) : "";
    }

    private static List<string> Clean(List<string> list) =>
        list.Select(x => x.Trim()).Where(x => x.Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase).Take(5000).ToList();

    private const string Incomplete = "Verbindung ist unvollständig (Adresse oder API Schlüssel fehlt)";

    private static async Task<IResult> TestAsync(IChatProvider? provider, CancellationToken ct)
    {
        if (provider == null)
            return Results.Ok(new { ok = false, error = Incomplete });

        var request = new JsonObject
        {
            ["model"] = provider.Model,
            ["max_tokens"] = 1000,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = "Antworte nur mit dem Wort OK." }
            }
        };

        var watch = Stopwatch.StartNew();
        try
        {
            var response = await provider.CompleteAsync(request, ct);
            var reply = response["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
            return Results.Ok(new { ok = true, ms = watch.ElapsedMilliseconds, reply = reply.Length > 200 ? reply[..200] : reply });
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return Results.Ok(new { ok = false, ms = watch.ElapsedMilliseconds, error = Friendly(ex) });
        }
    }

    private static async Task<IResult> ModelsAsync(IChatProvider? provider, CancellationToken ct)
    {
        if (provider == null)
            return Results.BadRequest(new { error = Incomplete });

        try
        {
            return Results.Ok(await provider.ListModelsAsync(ct));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return Results.BadRequest(new { error = Friendly(ex) });
        }
    }

    // Beim Bearbeiten bleibt das Schlüsselfeld leer, dann den gespeicherten Schlüssel nehmen
    private static Connection FromPreview(PreviewInput input, SettingsStore store)
    {
        var existing = store.Current.Connections.FirstOrDefault(c => c.Id == input.Id);
        var c = new Connection { ApiKeyProtected = existing?.ApiKeyProtected };
        Apply(c, input.Connection, store);
        return c;
    }

    private static string Friendly(Exception ex) => ex switch
    {
        ProviderException p => p.Message,
        HttpRequestException h => "Server nicht erreichbar: " + h.Message,
        TaskCanceledException => "Zeitüberschreitung, der Server hat nicht rechtzeitig geantwortet",
        _ => ex.Message
    };

    // Namen kommen aus Eingaben. Ohne Zeilenumbruch kann niemand eine falsche Zeile ins Log schreiben.
    private static string OneLine(string text) => text.Replace("\r", " ").Replace("\n", " ");
}
