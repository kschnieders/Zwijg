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

public sealed record RouteDecision(RouteTarget Route, string Reason);

public sealed class RoutingPolicy(RoutingOptions options)
{
    public RouteDecision Decide(Sensitivity sensitivity, bool hasDocument, RouteTarget? requested = null)
    {
        if (options.Mode == RoutingMode.LocalOnly)
            return new(RouteTarget.Local, "nur lokal erlaubt");

        if (options.Mode == RoutingMode.CloudOnly)
            return new(RouteTarget.Cloud, "nur Cloud konfiguriert");

        if (hasDocument && options.DocumentsLocalOnly)
            return new(RouteTarget.Local, "Dokumente bleiben lokal");

        if (sensitivity > options.CloudMaxSensitivity)
            return new(RouteTarget.Local, $"Sensibilität {sensitivity}");

        // Wunsch des Clients: lokal geht immer, Cloud nur wenn die Regeln es erlauben.
        if (requested == RouteTarget.Local)
            return new(RouteTarget.Local, "vom Client gewünscht");

        return new(RouteTarget.Cloud, $"Sensibilität {sensitivity}");
    }
}
