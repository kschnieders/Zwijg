using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Zwijg.Core.Pseudonymization;

namespace Zwijg.Core.Security;

public enum RuleAction
{
    // Fundstelle durch Platzhalter ersetzen, z.B. interne Patientennummern
    Replace,

    // Anfrage darf nicht in die Cloud, z.B. bei HIV oder Psychiatrie
    LocalOnly,

    // Anfrage wird gar nicht erst verschickt, z.B. bei Passwörtern
    Block,

    // Nur im Protokoll vermerken
    Warn
}

// Eigene Schutzregel der Praxis
public sealed class ProtectionRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "";

    // Ein Begriff pro Zeile, oder bei IsRegex ein regulärer Ausdruck pro Zeile
    public string Patterns { get; set; } = "";
    public bool IsRegex { get; set; }
    public bool CaseSensitive { get; set; }
    public RuleAction Action { get; set; } = RuleAction.Replace;

    // Name des Platzhalters bei Replace, z.B. PATIENTENNR ergibt [PATIENTENNR_1]
    public string? Label { get; set; }

    // Meldung an den Benutzer bei Block
    public string? Message { get; set; }
    public bool Enabled { get; set; } = true;
}

// TimedOut: Das Muster war zu langsam. Dann zählt die Regel als getroffen, ohne Fundstelle.
public sealed record RuleHit(ProtectionRule Rule, int Start, int Length, string Value, bool TimedOut = false);

public static class RuleEngine
{
    // Schützt vor Mustern, die sich festfressen (z.B. (a+)+b)
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

    private static readonly ConcurrentDictionary<string, Regex> Cache = new();

    // Pseudonymisierung und Weiterleitung prüfen denselben Text. Ein Muster, das sich daran schon
    // festgefressen hat, nicht noch einmal laufen lassen. Der Eintrag verschwindet mit dem Text.
    private static readonly ConditionalWeakTable<string, ConcurrentDictionary<Regex, byte>> SlowOn = new();

    public static readonly Regex LabelPattern = new("^[A-Z]{2,20}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Wirft eine ArgumentException mit verständlicher Meldung, wenn ein Muster kaputt ist
    public static Regex Build(ProtectionRule rule)
    {
        var key = $"{rule.IsRegex}|{rule.CaseSensitive}|{rule.Patterns}";
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        var parts = rule.Patterns.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            throw new ArgumentException("Die Regel braucht mindestens einen Begriff oder ein Muster");

        var alternatives = parts.Select(p =>
        {
            if (rule.IsRegex)
            {
                try
                {
                    _ = new Regex(p, RegexOptions.None, Timeout);
                }
                catch (ArgumentException ex)
                {
                    throw new ArgumentException($"Muster \"{p}\" ist ungültig: {ex.Message}");
                }
                return "(?:" + p + ")";
            }

            // Wörter nur als ganze Wörter finden, "HIV" soll nicht in "Archiv" treffen
            var escaped = Regex.Escape(p);
            var start = char.IsLetterOrDigit(p[0]) ? @"\b" : "";
            var end = char.IsLetterOrDigit(p[^1]) ? @"\b" : "";
            return start + escaped + end;
        });

        var options = RegexOptions.CultureInvariant | (rule.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        var regex = new Regex(string.Join("|", alternatives), options, Timeout);

        if (Cache.Count > 500)
            Cache.Clear();
        Cache[key] = regex;
        return regex;
    }

    public static IReadOnlyList<RuleHit> Find(string text, IEnumerable<ProtectionRule> rules, Func<RuleAction, bool>? which = null)
    {
        var hits = new List<RuleHit>();
        if (string.IsNullOrEmpty(text))
            return hits;

        foreach (var rule in rules)
        {
            if (!rule.Enabled || (which != null && !which(rule.Action)))
                continue;

            Regex? regex = null;
            try
            {
                regex = Build(rule);
                if (SlowOn.TryGetValue(text, out var known) && known.ContainsKey(regex))
                {
                    hits.Add(new RuleHit(rule, 0, 0, "", TimedOut: true));
                    continue;
                }

                foreach (Match m in regex.Matches(text))
                {
                    if (m.Length > 0)
                        hits.Add(new RuleHit(rule, m.Index, m.Length, m.Value));
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Zu langsames Muster: Server nicht blockieren, aber die Regel gilt als getroffen
                hits.Add(new RuleHit(rule, 0, 0, "", TimedOut: true));
                SlowOn.GetOrCreateValue(text)[regex!] = 0;
            }
            catch (ArgumentException)
            {
                // Kaputte Regel, wird beim Speichern eigentlich schon abgefangen
            }
        }

        return hits;
    }
}

// Liefert die Fundstellen der Ersetzen-Regeln an die Pseudonymisierung
public sealed class CustomRuleDetector(Func<IReadOnlyList<ProtectionRule>> rules) : IPiiDetector
{
    public Task<IReadOnlyList<PiiMatch>> DetectAsync(string text, CancellationToken ct = default)
    {
        // Zu langsame Regeln haben keine Fundstelle, die Weiterleitung hält die Anfrage dann lokal
        var matches = RuleEngine.Find(text, rules(), a => a == RuleAction.Replace)
            .Where(h => !h.TimedOut)
            .Select(h => new PiiMatch(EntityType.Custom, h.Start, h.Length, h.Value,
                string.IsNullOrWhiteSpace(h.Rule.Label) ? "EIGENE" : h.Rule.Label))
            .ToList();

        return Task.FromResult<IReadOnlyList<PiiMatch>>(matches);
    }
}
