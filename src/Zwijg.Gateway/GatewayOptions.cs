using Zwijg.Core.Routing;

namespace Zwijg.Gateway;

// Werte aus appsettings.json. Sie dienen nur beim allerersten Start als Vorlage,
// danach wird alles in der Oberfläche verwaltet (siehe Settings/SettingsStore).
public sealed class GatewayOptions
{
    public string SettingsPath { get; set; } = "data/settings.json";
    public List<ApiKeyOptions> ApiKeys { get; set; } = [];
    public ProvidersOptions Providers { get; set; } = new();
    public RoutingOptions Routing { get; set; } = new();
    public InjectionOptions Injection { get; set; } = new();
    public AuditOptions Audit { get; set; } = new();
    public PseudonymizationOptions Pseudonymization { get; set; } = new();
}

public sealed class ApiKeyOptions
{
    public string Key { get; set; } = "";
    public string User { get; set; } = "";
    public bool Admin { get; set; }
}

public sealed class ProvidersOptions
{
    public ProviderOptions? Local { get; set; }
    public ProviderOptions? Cloud { get; set; }
}

public sealed class ProviderOptions
{
    // "OpenAI" für alles mit OpenAI kompatibler API (Ollama, LM Studio, vLLM, OpenAI, Mistral, Azure ...)
    // "Anthropic" für Claude, Schlüssel aus ApiKey oder der Umgebungsvariable ANTHROPIC_API_KEY
    // "Echo" zum Ausprobieren ohne echtes Modell
    public string Type { get; set; } = "OpenAI";
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 120;

    // Nur für Claude: low, medium, high, max
    public string? Effort { get; set; }

    public bool IsEcho => Type.Equals("Echo", StringComparison.OrdinalIgnoreCase);
    public bool IsAnthropic => Type.Equals("Anthropic", StringComparison.OrdinalIgnoreCase);
}

public enum InjectionAction
{
    Block,
    Warn
}

public sealed class InjectionOptions
{
    public InjectionAction Action { get; set; } = InjectionAction.Block;
    public int PromptThreshold { get; set; } = 60;

    // Dokumente sollten gar keine Anweisungen enthalten, daher strenger
    public int DocumentThreshold { get; set; } = 30;
}

public sealed class AuditOptions
{
    public string DatabasePath { get; set; } = "data/audit.db";

    // Pseudonymisierten Prompt mit speichern. Ohne ihn steht nur drin, wer wann was für eine Anfrage gestellt hat.
    public bool StorePrompts { get; set; } = true;
}

public sealed class PseudonymizationOptions
{
    // Zusätzlich das lokale Modell nach Namen suchen lassen. Langsamer, findet aber mehr.
    public bool UseLocalLlmForNames { get; set; }
}
