using System.Text.RegularExpressions;

namespace Zwijg.Gateway.Settings;

public sealed record TemplateVariable(string Name, string Type, bool Optional, IReadOnlyList<string> Options);

// Variablen in Vorlagen: {{Name}}, {{Name:typ}}, {{Name:auswahl=A|B|C}}, {{Name?}} für optionale Felder.
// Die Oberfläche setzt die Werte ein, hier wird nur beim Speichern geprüft, ob alles lesbar ist.
public static partial class TemplateVariables
{
    public static readonly string[] Types = ["text", "langtext", "datum", "uhrzeit", "termin", "zahl", "auswahl"];

    public const int MaxVariables = 15;

    [GeneratedRegex(@"\{\{\s*(?<name>[^{}:]+?)\s*(?::\s*(?<type>[^{}=]+?)\s*(?:=\s*(?<options>[^{}]*?))?)?\s*\}\}")]
    private static partial Regex Pattern();

    public static IReadOnlyList<TemplateVariable> Parse(string text)
    {
        var result = new List<TemplateVariable>();
        foreach (Match m in Pattern().Matches(text))
        {
            var name = m.Groups["name"].Value.Trim();
            var optional = name.EndsWith('?');
            name = name.TrimEnd('?').Trim();

            var type = m.Groups["type"].Success ? m.Groups["type"].Value.Trim().ToLowerInvariant() : "text";
            var options = m.Groups["options"].Success
                ? m.Groups["options"].Value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];

            // Gleiche Variable darf mehrfach vorkommen, wird aber nur einmal abgefragt
            if (result.All(v => !v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                result.Add(new TemplateVariable(name, type, optional, options));
        }

        return result;
    }

    // Gibt eine verständliche Fehlermeldung zurück oder null, wenn alles passt
    public static string? Validate(string text)
    {
        var vars = Parse(text);
        if (vars.Count > MaxVariables)
            return $"Höchstens {MaxVariables} Variablen pro Vorlage";

        foreach (var v in vars)
        {
            if (v.Name.Length is 0 or > 40)
                return "Variablennamen müssen 1 bis 40 Zeichen haben";
            if (!Types.Contains(v.Type))
                return $"Unbekannter Typ \"{v.Type}\" bei {{{{{v.Name}}}}}. Möglich sind: {string.Join(", ", Types)}";
            if (v.Type == "auswahl" && v.Options.Count < 2)
                return $"Die Auswahl bei {{{{{v.Name}}}}} braucht mindestens zwei Möglichkeiten, z.B. auswahl=Ja|Nein";
        }

        // Nicht geschlossene Klammern fallen sonst erst im Chat auf
        var open = Regex.Matches(text, @"\{\{").Count;
        var close = Regex.Matches(text, @"\}\}").Count;
        return open != close ? "Eine Variable ist nicht richtig geschlossen, bitte {{ und }} prüfen" : null;
    }
}
