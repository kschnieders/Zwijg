using System.Net;
using System.Net.Sockets;

namespace Zwijg.Gateway;

// Prüfungen vor dem Start, damit typische Fehler eine klare Meldung bekommen statt einer langen Fehlerausgabe
public static class StartupChecks
{
    // So startet Kestrel, wenn nichts eingestellt ist
    public const string DefaultUrl = "http://localhost:5000";

    // Gibt die erste Adresse zurück, deren Port schon belegt ist, sonst null.
    // Adressen mit einem Rechnernamen werden nicht geprüft, die löst erst Kestrel auf.
    public static string? FindBusyUrl(string? urls)
    {
        foreach (var url in (string.IsNullOrWhiteSpace(urls) ? DefaultUrl : urls).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Endpoint(url) is not { } endpoint)
                continue;

            try
            {
                var probe = new TcpListener(endpoint);
                probe.Start();
                probe.Stop();
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                return url;
            }
            catch (SocketException)
            {
                // Andere Fehler, etwa fehlende Rechte, meldet Kestrel selbst
            }
        }
        return null;
    }

    public static string BusyMessage(string url)
    {
        var port = Endpoint(url)?.Port.ToString() ?? url;
        var shown = Shown(url);
        var other = Endpoint(url)?.Port == 5050 ? 5051 : 5050;
        return $"""

            Port {port} ist schon belegt, Zwijg kann nicht starten.

            Läuft Zwijg vielleicht schon? Dann einfach {shown} im Browser öffnen.
            Sonst einen anderen Port wählen, in appsettings.json zum Beispiel:
                "Urls": "http://localhost:{other}",

            """;
    }

    // Adresse so, wie man sie im Browser öffnet: localhost statt 0.0.0.0, [::] oder +
    public static string Shown(string url) =>
        System.Text.RegularExpressions.Regex.Replace(url, @"//(?:\+|\*|0\.0\.0\.0|\[::\]|127\.0\.0\.1|\[::1\])(?=[:/]|$)", "//localhost");

    // Für den Kasten beim Start, gut sichtbar unter den Logzeilen
    public static string ReadyBanner(string url, string? startKey)
    {
        var line = new string('=', 60);
        var lines = new List<string> { "", line, $"  Zwijg ist bereit:   {Shown(url)}" };
        if (startKey != null)
        {
            lines.Add($"  Startschlüssel:     {startKey}");
            lines.Add("  Damit als Admin anmelden und gleich ein eigenes Passwort festlegen.");
            lines.Add("  Bis zur ersten Anmeldung gibt es bei jedem Start einen neuen.");
        }
        lines.Add(line);
        lines.Add("");
        return string.Join(Environment.NewLine, lines);
    }

    private static IPEndPoint? Endpoint(string url)
    {
        // "+" und "*" sind keine gültigen Rechnernamen für Uri
        if (!Uri.TryCreate(url.Replace("//+", "//0.0.0.0").Replace("//*", "//0.0.0.0"), UriKind.Absolute, out var uri))
            return null;

        var port = uri.IsDefaultPort ? (uri.Scheme == "https" ? 443 : 80) : uri.Port;
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return new IPEndPoint(IPAddress.Loopback, port);
        return IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) ? new IPEndPoint(ip, port) : null;
    }
}
