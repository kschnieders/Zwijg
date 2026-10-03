using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Zwijg.Core.Pseudonymization;
using Zwijg.Core.Storage;
using Zwijg.Gateway.Settings;

namespace Zwijg.Gateway.History;

// Display ist die kurze Karte im Chat, wenn eine Vorlage direkt ausgeführt wurde. Content geht an die KI.
public sealed record ChatMessage(string Role, string Content, System.Text.Json.Nodes.JsonObject? Display = null);

public sealed record ConversationInfo(string Id, string Title, bool Pinned, DateTimeOffset Created, DateTimeOffset Updated, int MessageCount);

// Secrets: was jemand in dieser Unterhaltung selbst als geheim markiert hat
public sealed record Conversation(ConversationInfo Info, IReadOnlyList<ChatMessage> Messages, IReadOnlyList<SecretTerm> Secrets);

public sealed record ConversationPage(IReadOnlyList<ConversationInfo> Items, int Total);

// Unterhaltungen aus dem Chat. Titel und Nachrichten liegen verschlüsselt in einer eigenen Datenbank,
// jede Abfrage prüft die Benutzer Id. Admins können fremde Unterhaltungen nicht lesen.
public sealed class ConversationStore
{
    private readonly string _connectionString;
    private readonly IDataProtector _protector;
    private readonly SettingsStore _settings;

    public ConversationStore(IOptions<GatewayOptions> options, IDataProtectionProvider dataProtection, SettingsStore settings)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(options.Value.SettingsPath))!;
        Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "history.db"), Pooling = false }.ToString();
        _protector = dataProtection.CreateProtector("Zwijg.History");
        _settings = settings;

        using var con = new SqliteConnection(_connectionString);
        con.Open();

        // Schritt 1 ist der ursprüngliche Aufbau. Neue Spalten usw. als weitere Schritte anhängen.
        SqliteSchema.Migrate(con, """
            CREATE TABLE IF NOT EXISTS Conversations (
                Id TEXT PRIMARY KEY,
                UserId TEXT NOT NULL,
                Title TEXT NOT NULL,
                Pinned INTEGER NOT NULL,
                Created TEXT NOT NULL,
                Updated TEXT NOT NULL,
                MessageCount INTEGER NOT NULL,
                Messages TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Conv_User ON Conversations (UserId, Pinned, Updated);
            """,
            // Schritt 2: selbst markierte Geheimnisse, verschlüsselt wie die Nachrichten
            "ALTER TABLE Conversations ADD COLUMN Secrets TEXT");
    }

    public async Task<ConversationPage> ListAsync(string userId, int limit, int offset, CancellationToken ct = default)
    {
        await using var con = await OpenAsync(ct);

        var count = new SqliteCommand("SELECT COUNT(*) FROM Conversations WHERE UserId = $u", con);
        count.Parameters.AddWithValue("$u", userId);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));

        var cmd = new SqliteCommand(
            "SELECT Id, Title, Pinned, Created, Updated, MessageCount FROM Conversations WHERE UserId = $u " +
            "ORDER BY Pinned DESC, Updated DESC LIMIT $limit OFFSET $offset", con);
        cmd.Parameters.AddWithValue("$u", userId);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        cmd.Parameters.AddWithValue("$offset", Math.Max(0, offset));

        var items = new List<ConversationInfo>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            items.Add(ReadInfo(r));

        return new ConversationPage(items, total);
    }

    public async Task<Conversation?> GetAsync(string userId, string id, CancellationToken ct = default)
    {
        await using var con = await OpenAsync(ct);
        var cmd = new SqliteCommand(
            "SELECT Id, Title, Pinned, Created, Updated, MessageCount, Messages, Secrets FROM Conversations WHERE Id = $id AND UserId = $u", con);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$u", userId);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return null;

        var messages = JsonSerializer.Deserialize<List<ChatMessage>>(Unprotect(r.GetString(6)) ?? "[]") ?? [];
        return new Conversation(ReadInfo(r), messages, ReadSecrets(r, 7));
    }

    // Legt eine neue Unterhaltung an oder ersetzt die Nachrichten einer bestehenden. Gibt die Id zurück.
    public async Task<string> SaveAsync(string userId, string? id, IReadOnlyList<ChatMessage> messages, string title,
        IReadOnlyList<SecretTerm>? secrets = null, CancellationToken ct = default)
    {
        var now = Format(DateTimeOffset.UtcNow);
        var json = _protector.Protect(JsonSerializer.Serialize(messages));
        object secretJson = secrets is { Count: > 0 } ? _protector.Protect(JsonSerializer.Serialize(secrets)) : DBNull.Value;
        await using var con = await OpenAsync(ct);

        if (id != null)
        {
            var update = new SqliteCommand(
                "UPDATE Conversations SET Messages = $m, Secrets = $s, MessageCount = $n, Updated = $now WHERE Id = $id AND UserId = $u", con);
            update.Parameters.AddWithValue("$m", json);
            update.Parameters.AddWithValue("$s", secretJson);
            update.Parameters.AddWithValue("$n", messages.Count);
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$u", userId);
            if (await update.ExecuteNonQueryAsync(ct) > 0)
                return id;
        }

        // Neue Id immer vom Server, nie vom Client übernehmen
        var newId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var insert = new SqliteCommand(
            "INSERT INTO Conversations (Id, UserId, Title, Pinned, Created, Updated, MessageCount, Messages, Secrets) " +
            "VALUES ($id, $u, $t, 0, $now, $now, $n, $m, $s)", con);
        insert.Parameters.AddWithValue("$s", secretJson);
        insert.Parameters.AddWithValue("$id", newId);
        insert.Parameters.AddWithValue("$u", userId);
        insert.Parameters.AddWithValue("$t", _protector.Protect(title));
        insert.Parameters.AddWithValue("$now", now);
        insert.Parameters.AddWithValue("$n", messages.Count);
        insert.Parameters.AddWithValue("$m", json);
        await insert.ExecuteNonQueryAsync(ct);

        await CleanupAsync(ct);
        return newId;
    }

    // Umbenennen oder anpinnen. Wirft eine SettingsException, wenn zu viele angepinnt sind.
    public async Task<bool> UpdateAsync(string userId, string id, string? title, bool? pinned, CancellationToken ct = default)
    {
        await using var con = await OpenAsync(ct);

        if (pinned == true)
        {
            var count = new SqliteCommand("SELECT COUNT(*) FROM Conversations WHERE UserId = $u AND Pinned = 1 AND Id <> $id", con);
            count.Parameters.AddWithValue("$u", userId);
            count.Parameters.AddWithValue("$id", id);
            var max = _settings.Current.History.MaxPinned;
            if (Convert.ToInt32(await count.ExecuteScalarAsync(ct)) >= max)
                throw new SettingsException($"Es können höchstens {max} Unterhaltungen angepinnt werden");
        }

        var cmd = new SqliteCommand(
            "UPDATE Conversations SET Title = COALESCE($t, Title), Pinned = COALESCE($p, Pinned) WHERE Id = $id AND UserId = $u", con);
        cmd.Parameters.AddWithValue("$t", string.IsNullOrWhiteSpace(title) ? DBNull.Value : _protector.Protect(title.Trim()));
        cmd.Parameters.AddWithValue("$p", pinned == null ? DBNull.Value : pinned.Value ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$u", userId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> DeleteAsync(string userId, string id, CancellationToken ct = default)
    {
        await using var con = await OpenAsync(ct);
        var cmd = new SqliteCommand("DELETE FROM Conversations WHERE Id = $id AND UserId = $u", con);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$u", userId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // userId null heißt: alle Unterhaltungen aller Benutzer
    public async Task<int> DeleteAllAsync(string? userId, CancellationToken ct = default)
    {
        await using var con = await OpenAsync(ct);
        var cmd = new SqliteCommand(userId == null ? "DELETE FROM Conversations" : "DELETE FROM Conversations WHERE UserId = $u", con);
        if (userId != null)
            cmd.Parameters.AddWithValue("$u", userId);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(int Conversations, int Users)> CountAsync(CancellationToken ct = default)
    {
        await using var con = await OpenAsync(ct);
        await using var r = await new SqliteCommand("SELECT COUNT(*), COUNT(DISTINCT UserId) FROM Conversations", con).ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (r.GetInt32(0), r.GetInt32(1));
    }

    // Titel nach neuen Regeln neu berechnen, aber nur, wo noch Auslassungen "…" drin stehen.
    // Selbst umbenannte Titel enthalten die normalerweise nicht und bleiben so erhalten.
    public async Task<int> RetitleAsync(Func<Conversation, Task<string>> makeTitle, CancellationToken ct = default)
    {
        var candidates = new List<Conversation>();
        await using (var con = await OpenAsync(ct))
        {
            var cmd = new SqliteCommand("SELECT Id, Title, Pinned, Created, Updated, MessageCount, Messages, Secrets FROM Conversations", con);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var info = ReadInfo(r);
                if (!info.Title.Contains('…'))
                    continue;
                var messages = JsonSerializer.Deserialize<List<ChatMessage>>(Unprotect(r.GetString(6)) ?? "[]") ?? [];
                candidates.Add(new Conversation(info, messages, ReadSecrets(r, 7)));
            }
        }

        var changed = 0;
        foreach (var c in candidates)
        {
            var title = await makeTitle(c);
            if (string.IsNullOrWhiteSpace(title) || title == c.Info.Title)
                continue;

            await using var con = await OpenAsync(ct);
            var update = new SqliteCommand("UPDATE Conversations SET Title = $t WHERE Id = $id", con);
            update.Parameters.AddWithValue("$t", _protector.Protect(title));
            update.Parameters.AddWithValue("$id", c.Info.Id);
            changed += await update.ExecuteNonQueryAsync(ct);
        }

        return changed;
    }

    private List<SecretTerm> ReadSecrets(SqliteDataReader r, int column) =>
        r.IsDBNull(column) ? [] : JsonSerializer.Deserialize<List<SecretTerm>>(Unprotect(r.GetString(column)) ?? "[]") ?? [];

    // Löscht, was zu alt ist oder über der Anzahl liegt. Ist der Verlauf aus, wird alles gelöscht.
    public async Task<int> CleanupAsync(CancellationToken ct = default)
    {
        var h = _settings.Current.History;
        if (!h.Enabled)
            return await DeleteAllAsync(null, ct);

        await using var con = await OpenAsync(ct);
        var cmd = con.CreateCommand();
        cmd.CommandText = """
            DELETE FROM Conversations WHERE Pinned = 0 AND Updated < $cutoff;
            DELETE FROM Conversations WHERE Pinned = 0 AND Id NOT IN (
                SELECT c2.Id FROM Conversations c2
                WHERE c2.UserId = Conversations.UserId AND c2.Pinned = 0
                ORDER BY c2.Updated DESC LIMIT $max);
            """;
        cmd.Parameters.AddWithValue("$cutoff", Format(DateTimeOffset.UtcNow.AddDays(-h.RetentionDays)));
        cmd.Parameters.AddWithValue("$max", h.MaxConversations);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private ConversationInfo ReadInfo(SqliteDataReader r) => new(
        r.GetString(0),
        Unprotect(r.GetString(1)) ?? "Unterhaltung",
        r.GetInt32(2) == 1,
        DateTimeOffset.Parse(r.GetString(3), CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture),
        r.GetInt32(5));

    private string? Unprotect(string value)
    {
        try
        {
            return _protector.Unprotect(value);
        }
        catch (CryptographicException)
        {
            // Schlüssel verloren, dann ist der Inhalt eben nicht mehr lesbar
            return null;
        }
    }

    private static string Format(DateTimeOffset t) =>
        t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var con = new SqliteConnection(_connectionString);
        await con.OpenAsync(ct);
        return con;
    }
}

// Räumt einmal pro Stunde auf, damit alte Unterhaltungen auch ohne neue Nachrichten verschwinden
public sealed class ConversationCleanup(
    ConversationStore store,
    Zwijg.Core.Pseudonymization.Pseudonymizer pseudonymizer,
    ILogger<ConversationCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Einmal beim Start: alte Titel wie "Patientenabsage: …, …" mit den besseren Regeln neu setzen
        try
        {
            var retitled = await store.RetitleAsync(async c =>
            {
                var first = c.Messages.FirstOrDefault(m => m.Role == "user");
                if (first == null)
                    return c.Info.Title;
                return first.Display is { } display
                    ? await HistoryEndpoints.SafeDisplayTitleAsync(display, pseudonymizer, stoppingToken, c.Secrets)
                    : await HistoryEndpoints.SafeTitleAsync(first.Content, pseudonymizer, stoppingToken, c.Secrets);
            }, stoppingToken);

            if (retitled > 0)
                logger.LogInformation("{Count} Titel im Verlauf aufgefrischt", retitled);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Auffrischen der Titel fehlgeschlagen");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            do
            {
                try
                {
                    var deleted = await store.CleanupAsync(stoppingToken);
                    if (deleted > 0)
                        logger.LogInformation("{Count} alte Unterhaltungen gelöscht", deleted);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Aufräumen des Verlaufs fehlgeschlagen");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Zwijg wird beendet, das ist kein Fehler
        }
    }
}
