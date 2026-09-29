using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Zwijg.Core.Pseudonymization;
using Zwijg.Core.Routing;
using Zwijg.Gateway.Providers;
using Zwijg.Gateway.Settings;

namespace Zwijg.Gateway;

// Zweiter Ansatz: das lokale Modell sucht Namen und Orte heraus, die die Regeln nicht kennen.
// Läuft nur gegen den lokalen Anbieter, der Klartext verlässt also nie die Praxis.
public sealed class LocalLlmNameDetector(
    ProviderRegistry providers,
    SettingsStore settings,
    ILogger<LocalLlmNameDetector> logger) : IPiiDetector
{
    private const int MaxTextLength = 8000;

    // Ist das lokale Modell weg, nicht ewig warten. Die Regeln laufen ja trotzdem.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private const string Instruction =
        "Du findest personenbezogene Angaben in deutschen Texten aus einer Arztpraxis. " +
        "Liste alle Namen von Personen (Vor- und Nachnamen), alle Wohnorte, Städte, Dörfer und Straßen " +
        "und alle Kennnummern (Versicherungsnummer, Patientennummer, Fallnummer und ähnliche) auf. " +
        "Keine Krankheiten, Medikamente, Firmen oder Kliniken. " +
        "Antworte nur mit JSON in dieser Form: {\"namen\": [\"...\"], \"orte\": [\"...\"], \"nummern\": [\"...\"]}";

    // Im Chat kommt der Verlauf bei jeder Nachricht erneut, daher Ergebnisse kurz merken.
    // Liegt nur im Arbeitsspeicher.
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 500 });

    public async Task<IReadOnlyList<PiiMatch>> DetectAsync(string text, CancellationToken ct = default)
    {
        if (!settings.Current.UseLocalLlmForNames || !providers.IsLocalRealModel() || text.Trim().Length < 3)
            return [];

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        if (!_cache.TryGetValue(key, out Found? found) || found == null)
        {
            found = await AskModelAsync(text, ct);
            if (found == null)
                return [];

            _cache.Set(key, found, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromMinutes(30) });
        }

        return FindInText(text, found.Names, EntityType.Name, splitWords: true)
            .Concat(FindInText(text, found.Places, EntityType.City, splitWords: false))
            .Concat(FindInText(text, found.Numbers, EntityType.Identifier, splitWords: false))
            .ToList();
    }

    private async Task<Found?> AskModelAsync(string text, CancellationToken ct)
    {
        var provider = providers.Get(RouteTarget.Local)!;
        var request = new JsonObject
        {
            ["model"] = provider.Model,
            ["temperature"] = 0,
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = Instruction },
                new JsonObject { ["role"] = "user", ["content"] = text.Length > MaxTextLength ? text[..MaxTextLength] : text }
            }
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        try
        {
            var response = await provider.CompleteAsync(request, timeout.Token);
            var answer = response["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
            return Parse(answer);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Die Regeln laufen trotzdem, daher nur loggen
            logger.LogWarning(ex, "Suche mit lokalem Modell fehlgeschlagen");
            return null;
        }
    }

    private static Found Parse(string answer)
    {
        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        if (start < 0 || end <= start)
            return new Found([], [], []);

        try
        {
            var json = JsonNode.Parse(answer[start..(end + 1)]);
            return new Found(ReadList(json?["namen"]), ReadList(json?["orte"]),
                ReadList(json?["nummern"]).Where(n => n.Count(char.IsDigit) >= 4).ToList());
        }
        catch (JsonException)
        {
            return new Found([], [], []);
        }
    }

    private static List<string> ReadList(JsonNode? node) =>
        (node as JsonArray ?? [])
            .Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null)
            .OfType<string>()
            .Where(s => s.Length >= 2)
            .ToList();

    // Nur übernehmen, was wirklich im Text steht. Namen wortweise, damit "Herr Mustermann" auch passt.
    private static IEnumerable<PiiMatch> FindInText(string text, List<string> values, EntityType type, bool splitWords)
    {
        var terms = splitWords
            ? values.SelectMany(v => v.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            : values;

        // Das Modell liefert gern "Frau Anna Schmidt" oder "Dr. Weber". Anrede und Titel sind aber kein Name.
        foreach (var term in terms.Where(t => t.Length >= 2 && (char.IsUpper(t[0]) || char.IsDigit(t[0])) && !NotAName.Contains(t)).Distinct())
        {
            foreach (Match m in Regex.Matches(text, $@"\b{Regex.Escape(term)}\b"))
                yield return new PiiMatch(type, m.Index, m.Length, m.Value);
        }
    }

    private static readonly HashSet<string> NotAName = new(StringComparer.OrdinalIgnoreCase)
    {
        "Herr", "Herrn", "Frau", "Fräulein", "Hr.", "Fr.", "Frl.", "Dr.", "Dr", "Prof.", "Prof", "med.", "Dipl.",
        "Patient", "Patientin", "Schwester", "Pfleger", "Doktor", "Familie", "Fam.", "von", "van", "de", "zu"
    };

    private sealed record Found(List<string> Names, List<string> Places, List<string> Numbers);
}
