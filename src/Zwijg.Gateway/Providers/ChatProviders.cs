using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Zwijg.Core.Routing;
using Zwijg.Gateway.Settings;

namespace Zwijg.Gateway.Providers;

public interface IChatProvider
{
    string Model { get; }
    Task<JsonObject> CompleteAsync(JsonObject request, CancellationToken ct);
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct);
}

public sealed class ProviderException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

// Spricht jede API an, die /chat/completions wie OpenAI anbietet:
// OpenAI, Mistral, Gemini, Groq, Ollama, LM Studio, vLLM und eigene Server.
public sealed class OpenAiCompatibleProvider(HttpClient http, string baseUrl, string? apiKey, string model) : IChatProvider
{
    public string Model => model;

    public async Task<JsonObject> CompleteAsync(JsonObject request, CancellationToken ct)
    {
        using var msg = CreateRequest(HttpMethod.Post, "/chat/completions");
        msg.Content = JsonContent.Create(request);

        using var res = await http.SendAsync(msg, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
            throw new ProviderException((int)res.StatusCode, $"Anbieter antwortet mit {(int)res.StatusCode}: {Shorten(body)}");

        return JsonNode.Parse(body)?.AsObject()
               ?? throw new ProviderException(502, "Leere Antwort vom Anbieter");
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        using var msg = CreateRequest(HttpMethod.Get, "/models");
        using var res = await http.SendAsync(msg, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
            throw new ProviderException((int)res.StatusCode, $"Modellliste nicht abrufbar ({(int)res.StatusCode}): {Shorten(body)}");

        return (JsonNode.Parse(body)?["data"] as JsonArray ?? [])
            .Select(m => m?["id"]?.GetValue<string>())
            .OfType<string>()
            .Select(id => id.StartsWith("models/") ? id["models/".Length..] : id)
            .Order()
            .ToList();
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var msg = new HttpRequestMessage(method, baseUrl.TrimEnd('/') + path);
        if (!string.IsNullOrWhiteSpace(apiKey))
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return msg;
    }

    // Fehlertexte kürzen, sie können lang sein
    private static string Shorten(string s) => s.Length <= 200 ? s : s[..200] + "...";
}

// Gibt einfach die letzte Nutzernachricht zurück. Praktisch zum Testen ohne Modell.
public sealed class EchoProvider(string model) : IChatProvider
{
    public string Model => model;

    public Task<JsonObject> CompleteAsync(JsonObject request, CancellationToken ct)
    {
        var messages = request["messages"]!.AsArray();

        // Wie strenge Anbieter: unbekannte Felder in Nachrichten sind ein Fehler
        if (messages.Any(m => m is JsonObject o && o.Any(p => p.Key is not ("role" or "content" or "name"))))
            throw new ProviderException(400, "Unbekanntes Feld in einer Nachricht");

        var last = messages
            .LastOrDefault(m => m?["role"]?.GetValue<string>() == "user")?["content"]?.GetValue<string>() ?? "";

        // "/system" zeigt die Anweisungen, die ein echtes Modell bekommen hätte. Praktisch zum Prüfen.
        var reply = last.Trim() == "/system"
            ? string.Join("\n\n", messages.Where(m => m?["role"]?.GetValue<string>() == "system")
                .Select(m => m!["content"]!.GetValue<string>())).Trim() is { Length: > 0 } s ? s : "(keine Systemanweisungen)"
            : last.Trim() == "/drift"
                // Simuliert ein Modell, das ins Chinesische rutscht, und sich beim strengeren zweiten Versuch fängt
                ? messages.Any(m => m?["content"]?.GetValue<string>() == Zwijg.Gateway.LanguageGuard.RetryInstruction)
                    ? "Die Antwort ist jetzt vollständig auf Deutsch."
                    : "Die Antwort beginnt auf Deutsch. 请按照药说明服用，并在必要时保持充足水分。 Danach wieder Deutsch."
                : "Echo: " + last;

        var response = new JsonObject
        {
            ["id"] = "echo-" + Guid.NewGuid().ToString("N")[..12],
            ["object"] = "chat.completion",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = request["model"]?.GetValue<string>() ?? model,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = reply },
                    ["finish_reason"] = "stop"
                }
            }
        };

        return Task.FromResult(response);
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([model]);
}

public sealed class ProviderRegistry(SettingsStore store, IHttpClientFactory httpFactory)
{
    private readonly ConcurrentDictionary<string, (int Version, IChatProvider Provider)> _cache = new();

    public Connection? GetConnection(RouteTarget route)
    {
        var s = store.Current;
        var id = route == RouteTarget.Local ? s.LocalConnectionId : s.CloudConnectionId;
        var c = s.Connections.FirstOrDefault(x => x.Id == id);

        // Sicherheitsnetz: sensible Daten nur an Verbindungen, die als lokal markiert sind
        if (route == RouteTarget.Local && c is not { OnPremise: true })
            return null;

        return c;
    }

    public bool IsConfigured(RouteTarget route) => GetConnection(route) is { } c && IsUsable(c);

    public IChatProvider? Get(RouteTarget route) =>
        GetConnection(route) is { } c && IsUsable(c) ? Create(c) : null;

    public bool IsLocalRealModel() => GetConnection(RouteTarget.Local) is { Type: ConnectionTypes.OpenAI };

    public bool IsUsable(Connection c) => c.Type switch
    {
        ConnectionTypes.Echo => true,
        ConnectionTypes.Anthropic => !string.IsNullOrWhiteSpace(ApiKeyOf(c)),
        _ => !string.IsNullOrWhiteSpace(c.BaseUrl)
    };

    // Für ungespeicherte Verbindungen aus dem Formular, landet nicht im Cache
    public IChatProvider CreateTemporary(Connection c) => c.Type switch
    {
        ConnectionTypes.Echo => new EchoProvider(string.IsNullOrEmpty(c.Model) ? "echo" : c.Model),
        ConnectionTypes.Anthropic => new AnthropicProvider(ApiKeyOf(c), c.Model, c.Effort, c.TimeoutSeconds),
        _ => CreateOpenAi(c)
    };

    public IChatProvider Create(Connection c)
    {
        var version = store.Version;
        if (_cache.TryGetValue(c.Id, out var cached) && cached.Version == version)
            return cached.Provider;

        IChatProvider provider = c.Type switch
        {
            ConnectionTypes.Echo => new EchoProvider(string.IsNullOrEmpty(c.Model) ? "echo" : c.Model),
            ConnectionTypes.Anthropic => new AnthropicProvider(ApiKeyOf(c), c.Model, c.Effort, c.TimeoutSeconds),
            _ => CreateOpenAi(c)
        };

        _cache[c.Id] = (version, provider);
        return provider;
    }

    private OpenAiCompatibleProvider CreateOpenAi(Connection c)
    {
        var http = httpFactory.CreateClient("provider");
        http.Timeout = TimeSpan.FromSeconds(c.TimeoutSeconds);
        return new OpenAiCompatibleProvider(http, c.BaseUrl!, ApiKeyOf(c), c.Model);
    }

    // Für Claude darf der Schlüssel auch aus der Umgebungsvariable kommen
    private string? ApiKeyOf(Connection c) =>
        store.Unprotect(c.ApiKeyProtected)
        ?? (c.Type == ConnectionTypes.Anthropic ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") : null);
}
