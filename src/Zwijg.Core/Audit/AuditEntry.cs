namespace Zwijg.Core.Audit;

// Ein Eintrag im Protokoll. Prompt ist immer schon pseudonymisiert.
public sealed record AuditEntry
{
    public long Id { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public required string User { get; init; }
    public required string Action { get; init; }
    public string? Route { get; init; }
    public string? Model { get; init; }
    public string? Sensitivity { get; init; }
    public string? Entities { get; init; }
    public int InjectionScore { get; init; }
    public bool Blocked { get; init; }
    public string? Reason { get; init; }
    public string? Prompt { get; init; }
    public int ResponseLength { get; init; }
    public long DurationMs { get; init; }
    public string PreviousHash { get; init; } = "";
    public string Hash { get; init; } = "";
}

public enum AuditStatus
{
    Ok,
    Blocked,
    Warning,
    Error
}

public sealed record AuditQuery(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? User = null,
    int Limit = 100,
    int Offset = 0,
    string? Search = null,
    string? Action = null,
    AuditStatus? Status = null,
    string? Route = null);

public sealed record AuditPage(IReadOnlyList<AuditEntry> Items, int Total);

// Ein Eintrag mit Prüfung, ob er selbst und seine Verkettung zum Vorgänger unverändert sind
public sealed record AuditDetail(AuditEntry Entry, bool HashValid, bool ChainValid);

public sealed record AuditVerifyResult(bool Ok, int Checked, long? BrokenAtId);

// Schlanke Zeile für Statistiken, ohne Prompt
public sealed record AuditStatRow(
    long Id, DateTimeOffset Timestamp, string User, string Action, string? Route, bool Blocked,
    string? Entities, string? Reason, int InjectionScore);

public interface IAuditLog
{
    Task<AuditEntry> WriteAsync(AuditEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken ct = default);

    // Wie QueryAsync, aber mit Gesamtzahl für seitenweise Anzeige
    Task<AuditPage> SearchAsync(AuditQuery query, CancellationToken ct = default);

    Task<AuditDetail?> GetAsync(long id, CancellationToken ct = default);
    Task<AuditVerifyResult> VerifyAsync(CancellationToken ct = default);

    // Anfragen (Chat und Dokument) eines Benutzers seit einem Zeitpunkt, z.B. für Tageslimits
    Task<int> CountRequestsAsync(string user, DateTimeOffset since, CancellationToken ct = default);

    Task<IReadOnlyList<AuditStatRow>> StatRowsAsync(DateTimeOffset since, CancellationToken ct = default);
}
