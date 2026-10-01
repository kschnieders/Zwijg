using System.Globalization;
using System.Text;

namespace Zwijg.Core.Security;

// Entfernt unsichtbare Zeichen, mit denen man Anweisungen verstecken kann
// (Zero Width, Bidi Steuerzeichen, Unicode Tag Zeichen, Variantenwähler, leere Hangul Zeichen).
// Buchstaben und Ziffern in Sonderschreibweise wie "Ａ１２" oder "𝐈𝐠𝐧𝐨𝐫𝐞" werden zu normalen Zeichen.
public static class TextSanitizer
{
    public static (string Text, int Removed) Clean(string input)
    {
        if (string.IsNullOrEmpty(input))
            return (input, 0);

        // Normalize wirft bei ungepaarten Surrogaten, deshalb vorher durch U+FFFD ersetzen
        input = ReplaceLoneSurrogates(input).Normalize(NormalizationForm.FormC);
        var sb = new StringBuilder(input.Length);
        var removed = 0;

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];

            if (char.IsHighSurrogate(c) && i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]))
            {
                var cp = char.ConvertToUtf32(c, input[i + 1]);

                // Unicode Tag Zeichen und Variantenwähler Ergänzung
                if (cp is >= 0xE0000 and <= 0xE007F or >= 0xE0100 and <= 0xE01EF)
                {
                    removed++;
                    i++;
                    continue;
                }

                AppendSimple(sb, input.Substring(i, 2));
                i++;
                continue;
            }

            if (IsInvisible(c))
            {
                removed++;
                continue;
            }

            if (c < 0x80)
                sb.Append(c);
            else
                AppendSimple(sb, c.ToString());
        }

        return (sb.ToString(), removed);
    }

    // Nur Buchstaben und Dezimalziffern vereinfachen (NFKC), und nur wenn daraus ASCII wird.
    // Hochzahlen, Brüche und Zeichen wie µ bleiben, sonst würde aus "10³/µl" der Text "103/μl".
    private static void AppendSimple(StringBuilder sb, string ch)
    {
        var category = CharUnicodeInfo.GetUnicodeCategory(ch, 0);
        if (category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter or UnicodeCategory.DecimalDigitNumber)
        {
            var simple = ch.Normalize(NormalizationForm.FormKC);
            if (simple != ch && simple.All(char.IsAscii))
            {
                sb.Append(simple);
                return;
            }
        }
        sb.Append(ch);
    }

    private static string ReplaceLoneSurrogates(string input)
    {
        StringBuilder? sb = null;
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            var paired = char.IsHighSurrogate(c) && i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]);
            if (paired)
            {
                sb?.Append(c).Append(input[i + 1]);
                i++;
                continue;
            }

            if (char.IsSurrogate(c))
            {
                sb ??= new StringBuilder(input, 0, i, input.Length);
                sb.Append('\uFFFD');
                continue;
            }

            sb?.Append(c);
        }

        return sb?.ToString() ?? input;
    }

    private static bool IsInvisible(char c) => (int)c switch
    {
        0x00AD => true,
        0x034F => true,
        0x061C => true,
        0x115F or 0x1160 => true,
        0x180E => true,
        >= 0x200B and <= 0x200F => true,
        >= 0x202A and <= 0x202E => true,
        >= 0x2060 and <= 0x2064 => true,
        >= 0x2066 and <= 0x2069 => true,
        0x3164 => true,
        >= 0xFE00 and <= 0xFE0F => true,
        0xFEFF => true,
        0xFFA0 => true,
        >= 0xFFF9 and <= 0xFFFB => true,
        _ => false
    };
}
