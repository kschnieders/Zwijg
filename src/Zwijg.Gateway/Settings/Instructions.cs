using System.Text;

namespace Zwijg.Gateway.Settings;

// Wie die KI antworten soll. Wird jeder Anfrage als Systemnachricht mitgegeben.
public sealed class InstructionSettings
{
    public bool Enabled { get; set; }

    // "de", "en" oder "auto" (Sprache der Frage)
    public string Language { get; set; } = "de";

    // "sie", "du" oder "none"
    public string Addressing { get; set; } = "sie";

    // "neutral", "friendly", "concise"
    public string Tone { get; set; } = "neutral";

    // "short", "normal", "long"
    public string Length { get; set; } = "normal";

    // "auto", "bullets", "prose"
    public string Format { get; set; } = "auto";

    // "staff" (Fachsprache), "patients" (einfache Sprache), "none"
    public string Audience { get; set; } = "staff";

    // Eigene Regeln der Praxis, frei formuliert
    public string CustomText { get; set; } = "";

    // Wird unter jede Antwort gesetzt, z.B. "KI Antwort, bitte fachlich prüfen."
    public string ResponseFooter { get; set; } = "";
}

public static class InstructionComposer
{
    public const int MaxCustomLength = 4000;

    // Baut aus den Einstellungen und der persönlichen Anweisung den Text für die KI
    public static string? Compose(InstructionSettings s, string? personal = null)
    {
        var sb = new StringBuilder();

        if (s.Enabled)
        {
            sb.AppendLine("Du unterstützt das Team einer Arztpraxis.");

            sb.AppendLine(s.Language switch
            {
                "en" => "Antworte auf Englisch.",
                "auto" => "Antworte in der Sprache der Frage.",
                _ => "Antworte auf Deutsch."
            });

            var addressing = s.Addressing switch
            {
                "sie" => "Sprich die Person mit \"Sie\" an.",
                "du" => "Sprich die Person mit \"du\" an.",
                _ => null
            };
            if (addressing != null) sb.AppendLine(addressing);

            sb.AppendLine(s.Audience switch
            {
                "patients" => "Die Antworten sind für Patientinnen und Patienten gedacht. Nutze einfache Sprache, erkläre Fachbegriffe und vermeide Abkürzungen.",
                "staff" => "Die Antworten sind für medizinisches Fachpersonal. Fachbegriffe sind in Ordnung.",
                _ => ""
            });

            sb.AppendLine(s.Tone switch
            {
                "friendly" => "Formuliere freundlich und zugewandt.",
                "concise" => "Formuliere knapp und ohne Floskeln.",
                _ => "Formuliere sachlich und klar."
            });

            sb.AppendLine(s.Length switch
            {
                "short" => "Halte die Antwort kurz, wenige Sätze reichen meist.",
                "long" => "Antworte ausführlich und gründlich.",
                _ => ""
            });

            sb.AppendLine(s.Format switch
            {
                "bullets" => "Gliedere die Antwort in Stichpunkte.",
                "prose" => "Schreibe in ganzen Sätzen und Absätzen, ohne Aufzählungen.",
                _ => ""
            });

            if (!string.IsNullOrWhiteSpace(s.CustomText))
            {
                sb.AppendLine();
                sb.AppendLine("Regeln der Praxis:");
                sb.AppendLine(s.CustomText.Trim());
            }
        }

        if (!string.IsNullOrWhiteSpace(personal))
        {
            sb.AppendLine();
            sb.AppendLine("Zusätzlich für diese Person:");
            sb.AppendLine(personal.Trim());
        }

        // Leere Zeilen aus den leeren Optionen entfernen, doppelte Absätze zusammenfassen
        var text = string.Join("\n", sb.ToString().Replace("\r", "").Split('\n')
            .Aggregate(new List<string>(), (lines, l) =>
            {
                if (l.Length > 0 || (lines.Count > 0 && lines[^1].Length > 0))
                    lines.Add(l);
                return lines;
            })).Trim();

        return text.Length == 0 ? null : text;
    }
}
