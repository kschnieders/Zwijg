using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;

namespace Zwijg.Gateway.Providers;

// Claude über das offizielle Anthropic SDK.
// Nimmt eine Anfrage im OpenAI Format entgegen und gibt die Antwort auch so zurück,
// damit der Rest vom Gateway nichts davon merkt.
public sealed class AnthropicProvider(string? apiKey, string model, string? effort, int timeoutSeconds) : IChatProvider
{
    private const int DefaultMaxTokens = 16000;

    private readonly AnthropicClient _client = new()
    {
        ApiKey = apiKey,
        Timeout = TimeSpan.FromSeconds(timeoutSeconds),
    };

    public string Model => string.IsNullOrWhiteSpace(model) ? "claude-opus-5-5" : model;

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        try
        {
            var page = await _client.Models.List(cancellationToken: ct);
            return page.Items.Select(m => m.ID).ToList();
        }
        catch (AnthropicApiException ex)
        {
            throw new ProviderException(502, "Claude API Fehler: " + ex.Message);
        }
    }

    public async Task<JsonObject> CompleteAsync(JsonObject request, CancellationToken ct)
    {
        var system = new List<string>();
        var messages = new List<BetaMessageParam>();

        foreach (var node in request["messages"]!.AsArray())
        {
            var role = node?["role"]?.GetValue<string>();
            var content = node?["content"]?.GetValue<string>() ?? "";

            if (role == "system")
                system.Add(content);
            else if (role == "assistant")
                messages.Add(new BetaMessageParam { Role = Role.Assistant, Content = content });
            else
                messages.Add(new BetaMessageParam { Role = Role.User, Content = content });
        }

        var model = request["model"]?.GetValue<string>() ?? Model;
        var maxTokens = request["max_tokens"]?.GetValue<int>() ?? DefaultMaxTokens;

        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = maxTokens,
            Messages = messages,
            OutputConfig = new BetaOutputConfig { Effort = ParseEffort(effort) },
            // Lehnt Claude aus Sicherheitsgründen ab, beantwortet ein anderes Modell im selben Aufruf
            Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
            Fallbacks = new Default(),
        };

        if (system.Count > 0)
            parameters = parameters with { System = string.Join("\n\n", system) };

        BetaMessage response;
        try
        {
            response = await _client.Beta.Messages.Create(parameters, ct);
        }
        catch (AnthropicApiException ex)
        {
            throw new ProviderException(502, "Claude API Fehler: " + ex.Message);
        }

        var text = string.Concat(response.Content
            .Select(b => b.TryPickText(out var t) ? t.Text : null)
            .Where(t => t != null));

        var stopReason = response.StopReason?.ToString();
        if (stopReason == "refusal" && string.IsNullOrEmpty(text))
            text = "Claude hat diese Anfrage abgelehnt.";

        return new JsonObject
        {
            ["id"] = response.ID,
            ["object"] = "chat.completion",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = model,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = text },
                    ["finish_reason"] = stopReason == "max_tokens" ? "length" : "stop"
                }
            },
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = response.Usage.InputTokens,
                ["completion_tokens"] = response.Usage.OutputTokens,
                ["total_tokens"] = response.Usage.InputTokens + response.Usage.OutputTokens
            }
        };
    }

    private static Effort ParseEffort(string? value) => value?.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "high" => Effort.High,
        "max" => Effort.Max,
        _ => Effort.Medium
    };
}
