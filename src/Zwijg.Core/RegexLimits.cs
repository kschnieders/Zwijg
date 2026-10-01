namespace Zwijg.Core;

public static class RegexLimits
{
    // Notbremse für die eingebauten Muster der Erkennung. Die Muster sind so gebaut, dass sie auch bei langen
    // Texten linear bleiben, die Grenze fängt nur ab, was dabei übersehen wurde. Läuft sie ab, bricht die
    // Anfrage ab, statt ungeschützt weiterzugehen.
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);
}
