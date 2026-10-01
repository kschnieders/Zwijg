using Zwijg.Core.Pseudonymization;

namespace Zwijg.Core.Routing;

public enum RouteTarget
{
    Local,
    Cloud
}

public enum RoutingMode
{
    // Je nach Sensibilität der Anfrage
    Auto,
    LocalOnly,
    CloudOnly
}

public sealed class RoutingOptions
{
    public RoutingMode Mode { get; set; } = RoutingMode.Auto;

    // Bis zu dieser Stufe darf eine Anfrage in die Cloud (nach der Pseudonymisierung).
    public Sensitivity CloudMaxSensitivity { get; set; } = Sensitivity.Medium;

    // Hochgeladene Dokumente immer lokal verarbeiten
    public bool DocumentsLocalOnly { get; set; } = true;
}

// Blocked: Die Anfrage müsste lokal bleiben, darf laut Einstellung aber nur in die Cloud
public sealed record RouteDecision(RouteTarget Route, string Reason, bool Blocked = false);

public sealed class RoutingPolicy(RoutingOptions options)
{
    public RouteDecision Decide(Sensitivity sensitivity, bool hasDocument, RouteTarget? requested = null)
    {
        // Unbekannte Werte aus einer alten Einstellungsdatei: im Zweifel lokal
        if (options.Mode == RoutingMode.LocalOnly || !Enum.IsDefined(options.Mode))
            return new(RouteTarget.Local, "nur lokal erlaubt");

        var cloudMax = Enum.IsDefined(options.CloudMaxSensitivity) ? options.CloudMaxSensitivity : Sensitivity.None;
        var mustStayLocal = hasDocument && options.DocumentsLocalOnly ? "Dokumente bleiben lokal"
            : sensitivity > cloudMax ? $"Sensibilität {sensitivity}"
            : null;

        if (mustStayLocal != null)
        {
            // "Nur Cloud" heißt: kein lokales Modell. Dann lieber blockieren als zu viel in die Cloud schicken.
            if (options.Mode == RoutingMode.CloudOnly && requested != RouteTarget.Local)
                return new(RouteTarget.Local, mustStayLocal, Blocked: true);
            return new(RouteTarget.Local, mustStayLocal);
        }

        // Wunsch des Clients: lokal geht immer, Cloud nur wenn die Regeln es erlauben.
        if (requested == RouteTarget.Local)
            return new(RouteTarget.Local, "vom Client gewünscht");

        if (options.Mode == RoutingMode.CloudOnly)
            return new(RouteTarget.Cloud, "nur Cloud konfiguriert");

        return new(RouteTarget.Cloud, $"Sensibilität {sensitivity}");
    }
}
