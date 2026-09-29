using System.Text.RegularExpressions;

namespace Zwijg.Core.Security;

public sealed record InjectionFinding(string Rule, int Weight, string Snippet);

public sealed record InjectionResult(int Score, IReadOnlyList<InjectionFinding> Findings)
{
    public static readonly InjectionResult Empty = new(0, []);

    public string Reasons => string.Join(", ", Findings.Select(f => f.Rule).Distinct());

    public InjectionResult Combine(InjectionResult other) =>
        new(Score + other.Score, [.. Findings, .. other.Findings]);
}

// Einfache Heuristik mit Punkten pro Regel. Deutsch und Englisch.
// Fängt nicht alles ab, aber die typischen Muster aus Dokumenten und Copy&Paste.
public sealed class InjectionDetector
{
    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.IgnoreCase |
                                      RegexOptions.CultureInvariant | RegexOptions.Singleline;

    private static readonly (string Rule, int Weight, Regex Pattern)[] Rules =
    [
        ("anweisungen-ignorieren", 60, new Regex(
            @"\b(ignore|disregard|forget|override|bypass)\b.{0,40}\b(previous|prior|above|earlier|all|any|system|your)\b.{0,40}\b(instructions?|prompts?|rules?|guidelines?|messages?)\b", Opts)),
        ("anweisungen-ignorieren", 60, new Regex(
            @"\b(ignorier\w*|vergiss|vergesst|missachte\w*|überschreib\w*|umgeh\w*)\b.{0,50}\b(anweisung\w*|instruktion\w*|regeln|vorgaben|richtlinien|prompts?|befehle?)\b", Opts)),

        ("rollenwechsel", 30, new Regex(
            @"\b(you are now|from now on,? you|act as|pretend (to be|you are)|roleplay as)\b", Opts)),
        ("rollenwechsel", 30, new Regex(
            @"\b(du bist (jetzt|ab sofort|nun|ab jetzt)|ab (jetzt|sofort) bist du|spiele die rolle|tu so,? als (ob|wärst))\b", Opts)),

        ("systemprompt-abfragen", 40, new Regex(
            @"\b(reveal|show|print|output|repeat|leak|gib|zeig\w*|nenne?|verrate?)\b.{0,40}\b(system ?prompt|systemnachricht|system message|initial instructions|deine anweisungen|your instructions|api.?key|passw(or)?t\w*|zugangsdaten)\b", Opts)),

        ("chat-template-token", 50, new Regex(
            @"<\|im_start\|>|<\|im_end\|>|<\|system\|>|<\|endoftext\|>|\[/?INST\]|<<SYS>>|</?system>|^\s*#{2,}\s*(system|instruction|anweisung)", Opts | RegexOptions.Multiline)),

        ("jailbreak", 40, new Regex(
            @"\b(jailbreak|DAN mode|do anything now|developer mode|entwicklermodus|ohne einschränkungen|without (any )?restrictions)\b", Opts)),

        ("daten-abfluss", 30, new Regex(
            @"\b(send|post|upload|sende|schick\w*|übermittle|übertrage|lade)\b.{0,60}(https?://|\bwebhook\b|\be-?mail an\b)", Opts)),
        ("markdown-bild-mit-parametern", 40, new Regex(
            @"!\[[^\]]*\]\(\s*https?://[^)\s]*\?[^)]*\)", Opts)),

        ("an-die-ki-gerichtet", 25, new Regex(
            @"\b(an die ki|an das (sprach)?modell|note to (the )?(ai|assistant|llm)|hinweis für (die )?ki|if you are an? (ai|llm|language model)|wenn du eine ki bist)\b", Opts)),

        ("base64-block", 15, new Regex(@"[A-Za-z0-9+/]{200,}={0,2}", Opts)),
    ];

    public InjectionResult Scan(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return InjectionResult.Empty;

        var findings = new List<InjectionFinding>();

        var (clean, hidden) = TextSanitizer.Clean(text);
        if (hidden > 0)
            findings.Add(new InjectionFinding("unsichtbare-zeichen", Math.Min(10 + hidden, 40), $"{hidden} Zeichen"));

        foreach (var (rule, weight, pattern) in Rules)
        {
            var m = pattern.Match(clean);
            if (m.Success)
                findings.Add(new InjectionFinding(rule, weight, Shorten(m.Value)));
        }

        return new InjectionResult(findings.Sum(f => f.Weight), findings);
    }

    private static string Shorten(string s) => s.Length <= 80 ? s : s[..77] + "...";
}
