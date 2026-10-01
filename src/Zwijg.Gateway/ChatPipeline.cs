using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zwijg.Gateway.Settings;
using Zwijg.Core.Audit;
using Zwijg.Core.Pseudonymization;
using Zwijg.Core.Routing;
using Zwijg.Core.Security;
using Zwijg.Gateway.Providers;

namespace Zwijg.Gateway;

public sealed record ChatOutcome(int StatusCode, JsonObject Body, RouteDecision? Route = null, int Pseudonyms = 0, string? Connection = null)
{
    public static ChatOutcome Error(int status, string message, object? details = null)
    {
        var body = new JsonObject { ["error"] = message };
        if (details != null)
            body["details"] = System.Text.Json.JsonSerializer.SerializeToNode(details, System.Text.Json.JsonSerializerOptions.Web);
        return new ChatOutcome(status, body);
    }
}

// Ablauf einer Anfrage:
// bereinigen, auf Injection prüfen, pseudonymisieren, Ziel wählen, Modell fragen,
// echte Werte wieder einsetzen, protokollieren.
public sealed class ChatPipeline(
    Pseudonymizer pseudonymizer,
    InjectionDetector injectionDetector,
    ProviderRegistry providers,
    IAuditLog audit,
    SettingsStore settings,
    ILogger<ChatPipeline> logger)
{
    private const int MaxLoggedPromptLength = 4000;

    private const string PlaceholderHint =
        "Hinweis: Platzhalter in eckigen Klammern wie [NAME_1] oder [GEBURTSDATUM_1] stehen für echte Daten. " +
        "Übernimm sie unverändert in deine Antwort und versuche nicht, sie aufzulösen.";

    // Einstellungen, die an den Anbieter gehen. stop ist freier Text und wird deshalb extra geschützt.
    private static readonly string[] IntegerFields = ["max_tokens", "max_completion_tokens", "seed", "n"];
    private static readonly string[] NumberFields = [.. IntegerFields, "temperature", "top_p", "frequency_penalty", "presence_penalty"];

    // Andere Rollen wie "tool" gehören zu Tool Aufrufen oder würden Injection Prüfung und Schutzregeln umgehen
    private static readonly string[] Roles = ["system", "developer", "user", "assistant"];

    // Diese Felder tragen freien Text und ändern, was das Modell tut. Still entfernen würde die Antwort verfälschen.
    private static readonly string[] RejectedFields = ["tools", "tool_choice", "functions", "function_call", "prediction"];

    public async Task<ChatOutcome> RunAsync(
        GatewayUser user,
        JsonObject request,
        string action,
        bool hasDocument = false,
        InjectionResult? extraFindings = null,
        RouteTarget? requestedRoute = null,
        IReadOnlyList<SecretTerm>? secrets = null,
        CancellationToken ct = default)
    {
        var o = settings.Current;
        var routing = new RoutingPolicy(o.Routing);
        var watch = Stopwatch.StartNew();

        if (request["messages"] is not JsonArray messages || messages.Count == 0)
            return ChatOutcome.Error(400, "messages fehlt oder ist leer");

        if (CheckFields(request) is { } invalid)
            return ChatOutcome.Error(400, invalid);

        if (user.DailyLimit is { } limit && await audit.CountRequestsAsync(user.Name, StartOfToday(), ct) >= limit)
            return ChatOutcome.Error(429, $"Dein Tageslimit von {limit} Anfragen ist erreicht. Morgen geht es weiter.");

        // 1. Texte aus den Nachrichten holen
        var roles = new List<string>();
        var texts = new List<string>();
        foreach (var node in messages)
        {
            if (node is not JsonObject message)
                return ChatOutcome.Error(400, "Jede Nachricht muss ein Objekt mit role und content sein");

            if (message["role"] is not JsonValue r || !r.TryGetValue<string>(out var role) || !Roles.Contains(role))
                return ChatOutcome.Error(400, $"role muss {string.Join(", ", Roles)} sein");

            // Open WebUI schickt bei neueren OpenAI Modellen "developer" statt "system"
            if (role == "developer")
                role = "system";

            // Tool Aufrufe tragen freien Text, den Zwijg nicht schützen kann
            if (message.FirstOrDefault(p => p.Key is "tool_calls" or "function_call" && p.Value != null).Key is { } toolField)
                return ChatOutcome.Error(400, Unsupported(toolField));

            var text = ReadContent(message["content"]);
            if (text == null)
                return ChatOutcome.Error(400, "Nur Textinhalte werden unterstützt");

            roles.Add(role);
            texts.Add(text);
        }

        // 2. Auf Injection prüfen, nur was vom Nutzer kommt
        var injection = extraFindings ?? InjectionResult.Empty;
        for (var i = 0; i < texts.Count; i++)
        {
            if (roles[i] == "user")
                injection = injection.Combine(injectionDetector.Scan(texts[i]));
        }

        // 3. Unsichtbare Zeichen raus und pseudonymisieren
        var map = new PseudonymMap();
        var cleaned = texts.Select(t => TextSanitizer.Clean(t).Text).ToList();

        // stop ist freier Text und läuft deshalb mit durch Pseudonymisierung und Schutzregeln
        var stops = ReadStops(request["stop"]).Select(s => TextSanitizer.Clean(s).Text).ToList();
        var all = await pseudonymizer.PseudonymizeAsync([.. cleaned, .. stops], map, ct, secrets);
        var pseudo = all.Take(cleaned.Count).ToList();
        var pseudoStops = all.Skip(cleaned.Count).ToList();
        var loggedPrompt = o.StorePrompts ? LastUserText(roles, pseudo) : null;

        var threshold = hasDocument ? o.Injection.DocumentThreshold : o.Injection.PromptThreshold;
        var suspicious = injection.Score >= threshold;

        if (suspicious && o.Injection.Action == InjectionAction.Block)
        {
            await audit.WriteAsync(new AuditEntry
            {
                User = user.Name,
                Action = action,
                Sensitivity = map.MaxSensitivity.ToString(),
                Entities = map.Summary(),
                InjectionScore = injection.Score,
                Blocked = true,
                Reason = "Prompt Injection: " + injection.Reasons,
                Prompt = loggedPrompt,
                DurationMs = watch.ElapsedMilliseconds
            }, ct);

            return ChatOutcome.Error(400, "Anfrage blockiert: möglicher Manipulationsversuch (Prompt Injection)",
                new { score = injection.Score, findings = injection.Findings.Select(f => new { f.Rule, f.Snippet }) });
        }

        // Eigene Schutzregeln der Praxis. "Ersetzen" lief schon über die Pseudonymisierung.
        var ruleHits = new List<RuleHit>();
        // Antworten des Modells nur für "nur lokal" prüfen. Mit Blockieren hinge das Gespräch sonst fest,
        // sobald das Modell einmal ein gesperrtes Wort schreibt.
        for (var i = 0; i < cleaned.Count; i++)
        {
            var assistant = roles[i] == "assistant";
            ruleHits.AddRange(RuleEngine.Find(cleaned[i], o.ProtectionRules,
                a => assistant ? a == RuleAction.LocalOnly : a != RuleAction.Replace));
        }
        foreach (var text in stops)
            ruleHits.AddRange(RuleEngine.Find(text, o.ProtectionRules, a => a != RuleAction.Replace));

        var blockedBy = ruleHits.FirstOrDefault(h => h.Rule.Action == RuleAction.Block)?.Rule;
        if (blockedBy != null)
        {
            await audit.WriteAsync(new AuditEntry
            {
                User = user.Name,
                Action = action,
                Sensitivity = map.MaxSensitivity.ToString(),
                Entities = map.Summary(),
                InjectionScore = injection.Score,
                Blocked = true,
                Reason = "Schutzregel: " + blockedBy.Name,
                Prompt = loggedPrompt,
                DurationMs = watch.ElapsedMilliseconds
            }, ct);

            // Den gefundenen Text nicht zurückgeben, er könnte genau das Geheimnis sein
            return ChatOutcome.Error(400,
                string.IsNullOrWhiteSpace(blockedBy.Message)
                    ? $"Anfrage blockiert: verstößt gegen die Praxisregel \"{blockedBy.Name}\""
                    : blockedBy.Message,
                new { findings = new[] { new { rule = blockedBy.Name, snippet = "Schutzregel der Praxis" } } });
        }

        string RuleNames(RuleAction a) =>
            string.Join(", ", ruleHits.Where(h => h.Rule.Action == a).Select(h => h.Rule.Name).Distinct());
        var localOnlyRules = RuleNames(RuleAction.LocalOnly);
        var warnRules = RuleNames(RuleAction.Warn);

        // 4. Ziel wählen. Wenn lokal nötig aber nicht da ist, gibt es keinen Ausweg in die Cloud.
        requestedRoute ??= ParseModelPrefix(request["model"]?.GetValue<string>(), out _);
        var decision = routing.Decide(map.MaxSensitivity, hasDocument, requestedRoute);

        if (decision.Route == RouteTarget.Cloud && !user.CloudAllowed)
            decision = new RouteDecision(RouteTarget.Local, "Benutzer darf nur lokal");

        if (decision.Route == RouteTarget.Cloud && localOnlyRules.Length > 0)
            decision = new RouteDecision(RouteTarget.Local, "Schutzregel: " + localOnlyRules);

        if (decision.Route == RouteTarget.Cloud && !providers.IsConfigured(RouteTarget.Cloud) && providers.IsConfigured(RouteTarget.Local))
            decision = new RouteDecision(RouteTarget.Local, "keine Cloud konfiguriert");

        var provider = providers.Get(decision.Route);
        if (provider == null)
        {
            await WriteErrorAsync(user, action, decision, map, injection, loggedPrompt, "Kein Anbieter für " + decision.Route, watch, ct);
            return ChatOutcome.Error(503, $"Kein Anbieter für Route {decision.Route} konfiguriert");
        }

        // 5. Anfrage für das Modell bauen
        ParseModelPrefix(request["model"]?.GetValue<string>(), out var requestedModel);
        var model = string.IsNullOrWhiteSpace(requestedModel) ? provider.Model : requestedModel;
        var connectionName = providers.GetConnection(decision.Route)?.Name;

        // Nur bekannte Felder weitergeben. Alles andere könnte Klartext enthalten, den die Pseudonymisierung nicht sieht.
        var forward = new JsonObject { ["model"] = model };
        foreach (var key in NumberFields)
        {
            if (request[key] is { } value)
                forward[key] = value.DeepClone();
        }
        if (request["stop"] is JsonArray)
            forward["stop"] = new JsonArray([.. pseudoStops.Select(s => (JsonNode?)s)]);
        else if (pseudoStops.Count > 0)
            forward["stop"] = pseudoStops[0];
        if (request["response_format"] is JsonObject format)
            forward["response_format"] = new JsonObject { ["type"] = format["type"]!.GetValue<string>() };

        var outMessages = new JsonArray();

        // Anweisungen der Praxis und persönliche Anweisung kommen ganz nach vorn
        var personal = o.Users.FirstOrDefault(u => u.Id == user.Id)?.Instructions;
        var instructions = InstructionComposer.Compose(o.Instructions, personal);
        if (instructions != null)
            outMessages.Add(new JsonObject { ["role"] = "system", ["content"] = instructions });

        if (map.Count > 0)
            outMessages.Add(new JsonObject { ["role"] = "system", ["content"] = PlaceholderHint });

        // Nur Rolle und geschützter Text. Felder wie name oder zwijg_display bleiben hier.
        for (var i = 0; i < messages.Count; i++)
            outMessages.Add(new JsonObject { ["role"] = roles[i], ["content"] = pseudo[i] });
        forward["messages"] = outMessages;

        // Temperatur der Verbindung, wenn der Client keine eigene schickt
        if (forward["temperature"] == null && providers.GetConnection(decision.Route)?.Temperature is { } temperature)
            forward["temperature"] = temperature;

        // 6. Modell fragen
        JsonObject response;
        try
        {
            response = await provider.CompleteAsync(forward, ct);
        }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Anbieter {Route} nicht erreichbar", decision.Route);
            await WriteErrorAsync(user, action, decision, map, injection, loggedPrompt, "Anbieterfehler: " + ex.Message, watch, ct);
            return ChatOutcome.Error(502, $"{connectionName} ist gerade nicht erreichbar oder meldet einen Fehler");
        }

        // Sprachwächter: kleine Modelle wechseln manchmal mitten in der Antwort ins Chinesische
        var question = roles.LastIndexOf("user") is var last and >= 0 ? cleaned[last] : "";
        var languageNote = await GuardLanguageAsync(provider, forward, response, question, ct);

        // 7. Echte Werte wieder einsetzen
        var responseLength = 0;
        if (response["choices"] is JsonArray choices)
        {
            foreach (var choice in choices)
            {
                if (choice?["message"] is JsonObject msg && msg["content"] is JsonValue content
                    && content.TryGetValue<string>(out var text))
                {
                    responseLength += text.Length;
                    var restored = map.Restore(text);

                    // Hinweis der Praxis unter jede Antwort, z.B. "KI Antwort, bitte fachlich prüfen."
                    var footer = o.Instructions.ResponseFooter.Trim();
                    if (footer.Length > 0)
                        restored = restored.TrimEnd() + "\n\n" + footer;

                    msg["content"] = restored;
                }
            }
        }

        var warnings = new List<string>();
        if (suspicious) warnings.Add("Warnung Prompt Injection: " + injection.Reasons);
        if (warnRules.Length > 0) warnings.Add("Warnung Schutzregel: " + warnRules);
        if (languageNote != null) warnings.Add("Warnung " + languageNote);

        await audit.WriteAsync(new AuditEntry
        {
            User = user.Name,
            Action = action,
            Route = decision.Route.ToString(),
            Model = $"{model} ({connectionName})",
            Sensitivity = map.MaxSensitivity.ToString(),
            Entities = map.Summary(),
            InjectionScore = injection.Score,
            Reason = warnings.Count > 0 ? string.Join("; ", warnings) : decision.Reason,
            Prompt = loggedPrompt,
            ResponseLength = responseLength,
            DurationMs = watch.ElapsedMilliseconds
        }, ct);

        return new ChatOutcome(200, response, decision, map.Count, connectionName);
    }

    // Enthält die Antwort fremde Schriftzeichen, die in der Frage nicht vorkamen, einmal strenger nachfragen.
    // Hilft auch das nicht, die fremden Stücke entfernen. Gibt einen Vermerk fürs Protokoll zurück.
    private async Task<string?> GuardLanguageAsync(IChatProvider provider, JsonObject forward, JsonObject response,
        string question, CancellationToken ct)
    {
        var first = AnswerOf(response);
        if (first == null || !LanguageGuard.HasUnexpectedScript(question, first))
            return null;

        var retry = (JsonObject)forward.DeepClone();
        retry["messages"]!.AsArray().Add(new JsonObject { ["role"] = "system", ["content"] = LanguageGuard.RetryInstruction });
        retry["temperature"] = 0.2;

        try
        {
            var second = AnswerOf(await provider.CompleteAsync(retry, ct));
            if (second != null && !LanguageGuard.HasUnexpectedScript(question, second))
            {
                SetAnswer(response, second);
                return "Sprachkorrektur: Antwort war teils in fremder Schrift, zweiter Versuch sauber";
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Zweiter Versuch nach Sprachwechsel fehlgeschlagen");
        }

        SetAnswer(response, LanguageGuard.Strip(first));
        return "Sprachkorrektur: fremde Schrift aus der Antwort entfernt";
    }

    private static string? AnswerOf(JsonObject response) =>
        response["choices"]?[0]?["message"]?["content"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static void SetAnswer(JsonObject response, string text)
    {
        if (response["choices"]?[0]?["message"] is JsonObject msg)
            msg["content"] = text;
    }

    // Mitternacht in der Zeitzone des Servers, also der Praxis
    public static DateTimeOffset StartOfToday() => new(DateTime.Today, TimeZoneInfo.Local.GetUtcOffset(DateTime.Today));

    // "local/llama3.1" oder "cloud/gpt-4o-mini" legt Ziel und Modell fest.
    // Alles andere nimmt das Standardmodell des gewählten Ziels.
    public static RouteTarget? ParseModelPrefix(string? model, out string? modelName)
    {
        modelName = null;
        if (string.IsNullOrWhiteSpace(model))
            return null;

        if (model.StartsWith("local/", StringComparison.OrdinalIgnoreCase))
        {
            modelName = model["local/".Length..];
            return RouteTarget.Local;
        }

        if (model.StartsWith("cloud/", StringComparison.OrdinalIgnoreCase))
        {
            modelName = model["cloud/".Length..];
            return RouteTarget.Cloud;
        }

        return null;
    }

    // Prüft die Felder außerhalb der Nachrichten. Gibt eine Fehlermeldung zurück oder null.
    private static string? CheckFields(JsonObject request)
    {
        // Leere Listen wie "tools": [] tragen keinen Text und sind erlaubt
        if (RejectedFields.FirstOrDefault(f => request[f] is { } v && v is not JsonArray { Count: 0 }) is { } rejected)
            return Unsupported(rejected);

        if (request["model"] is { } model && !IsString(model))
            return "model muss Text sein";

        if (NumberFields.FirstOrDefault(f => request[f] is { } v && v.GetValueKind() != JsonValueKind.Number) is { } number)
            return $"{number} muss eine Zahl sein";

        // seed darf wie bei OpenAI 64 Bit groß sein
        if (IntegerFields.FirstOrDefault(f => request[f] is JsonValue v && !(f == "seed" ? v.TryGetValue<long>(out _) : v.TryGetValue<int>(out _))) is { } integer)
            return $"{integer} muss eine ganze Zahl im gültigen Bereich sein";

        if (request["stop"] is { } stop && !IsString(stop) && !(stop is JsonArray list && list.All(s => s != null && IsString(s))))
            return "stop muss Text oder eine Liste von Texten sein";

        // Nur "text" und "json_object". Ein json_schema enthält freien Text.
        if (request["response_format"] is { } format
            && !(format is JsonObject f && f["type"] is { } type && IsString(type) && type.GetValue<string>() is "text" or "json_object"))
            return Unsupported("response_format");

        return null;
    }

    private static List<string> ReadStops(JsonNode? stop) => stop switch
    {
        JsonArray list => list.Select(s => s!.GetValue<string>()).ToList(),
        JsonValue v => [v.GetValue<string>()],
        _ => []
    };

    private static string Unsupported(string field) =>
        $"Das Feld \"{field}\" wird nicht unterstützt. Zwijg gibt nur Text weiter, den es vorher schützen kann.";

    private static bool IsString(JsonNode node) => node.GetValueKind() == JsonValueKind.String;

    private static string? ReadContent(JsonNode? content)
    {
        if (content == null)
            return "";

        if (content is JsonValue v && v.TryGetValue<string>(out var s))
            return s;

        // Format mit Teilen: [{ "type": "text", "text": "..." }]
        if (content is JsonArray parts)
        {
            var texts = new List<string>();
            foreach (var part in parts)
            {
                if (part is not JsonObject p || p["type"] is not { } type || !IsString(type) || type.GetValue<string>() != "text")
                    return null;
                if (p["text"] is { } text && !IsString(text))
                    return null;
                texts.Add(p["text"]?.GetValue<string>() ?? "");
            }
            return string.Join("\n", texts);
        }

        return null;
    }

    private static string? LastUserText(List<string> roles, IReadOnlyList<string> texts)
    {
        var i = roles.LastIndexOf("user");
        if (i < 0)
            return null;

        var t = texts[i];
        return t.Length <= MaxLoggedPromptLength ? t : t[..MaxLoggedPromptLength] + " [gekürzt]";
    }

    private Task WriteErrorAsync(GatewayUser user, string action, RouteDecision decision, PseudonymMap map,
        InjectionResult injection, string? prompt, string reason, Stopwatch watch, CancellationToken ct) =>
        audit.WriteAsync(new AuditEntry
        {
            User = user.Name,
            Action = action,
            Route = decision.Route.ToString(),
            Sensitivity = map.MaxSensitivity.ToString(),
            Entities = map.Summary(),
            InjectionScore = injection.Score,
            Reason = reason,
            Prompt = prompt,
            DurationMs = watch.ElapsedMilliseconds
        }, ct);
}
