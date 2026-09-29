using System.Text;

namespace Zwijg.Core.Security;

// Entfernt unsichtbare Zeichen, mit denen man Anweisungen verstecken kann
// (Zero Width, Bidi Steuerzeichen, Unicode Tag Zeichen).
public static class TextSanitizer
{
    public static (string Text, int Removed) Clean(string input)
    {
        if (string.IsNullOrEmpty(input))
            return (input, 0);

        var sb = new StringBuilder(input.Length);
        var removed = 0;

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];

            // Unicode Tag Zeichen (U+E0000 bis U+E007F) liegen als Surrogatpaar vor
            if (char.IsHighSurrogate(c) && i + 1 < input.Length)
            {
                var cp = char.ConvertToUtf32(c, input[i + 1]);
                if (cp is >= 0xE0000 and <= 0xE007F)
                {
                    removed++;
                    i++;
                    continue;
                }
            }

            if (IsInvisible(c))
            {
                removed++;
                continue;
            }

            sb.Append(c);
        }

        return (sb.ToString(), removed);
    }

    private static bool IsInvisible(char c) => (int)c switch
    {
        0x00AD => true,
        >= 0x200B and <= 0x200F => true,
        >= 0x202A and <= 0x202E => true,
        >= 0x2060 and <= 0x2064 => true,
        >= 0x2066 and <= 0x2069 => true,
        0xFEFF => true,
        _ => false
    };
}
