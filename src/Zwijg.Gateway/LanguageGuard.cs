using System.Text.RegularExpressions;

namespace Zwijg.Gateway;

// Kleine lokale Modelle (z.B. Qwen) rutschen bei längeren Antworten gern ins Chinesische.
// Der Wächter erkennt Schriftzeichen, die in der Frage nicht vorkamen, und hilft beim Aufräumen.
public static partial class LanguageGuard
{
    public const string RetryInstruction =
        "Wichtig: Antworte ausschließlich in der Sprache der Frage, normalerweise Deutsch. " +
        "Verwende keine chinesischen, japanischen oder koreanischen Schriftzeichen.";

    public const string RemovedNote =
        "(Hinweis von Zwijg: Teile der Antwort waren in einer anderen Sprache und wurden entfernt.)";

    // Chinesisch, Japanisch, Koreanisch samt der zugehörigen Satzzeichen
    [GeneratedRegex(@"[\p{IsCJKUnifiedIdeographs}\p{IsCJKSymbolsandPunctuation}\p{IsHiragana}\p{IsKatakana}\p{IsHangulSyllables}\p{IsHalfwidthandFullwidthForms}]")]
    private static partial Regex ForeignChar();

    // Zusammenhängende Stücke mit fremder Schrift, inklusive Zahlen und Satzzeichen dazwischen
    [GeneratedRegex(@"[\p{IsCJKUnifiedIdeographs}\p{IsCJKSymbolsandPunctuation}\p{IsHiragana}\p{IsKatakana}\p{IsHangulSyllables}\p{IsHalfwidthandFullwidthForms}][\p{IsCJKUnifiedIdeographs}\p{IsCJKSymbolsandPunctuation}\p{IsHiragana}\p{IsKatakana}\p{IsHangulSyllables}\p{IsHalfwidthandFullwidthForms}\d:.,!?\s]*")]
    private static partial Regex ForeignRun();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyNewlines();

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex TrailingSpaces();

    // Nur ein Problem, wenn die Frage selbst keine solchen Zeichen enthielt
    public static bool HasUnexpectedScript(string question, string answer) =>
        ForeignChar().IsMatch(answer) && !ForeignChar().IsMatch(question);

    // Letzter Ausweg: fremde Stücke entfernen und einen Hinweis anhängen
    public static string Strip(string answer)
    {
        var cleaned = ForeignRun().Replace(answer, " ");
        cleaned = TrailingSpaces().Replace(cleaned, "\n");
        cleaned = ManyNewlines().Replace(cleaned, "\n\n").Trim();
        return cleaned + "\n\n" + RemovedNote;
    }
}
