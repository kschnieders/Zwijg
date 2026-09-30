using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Zwijg.Core.Storage;

namespace Zwijg.Core.Audit;

// Protokoll in einer SQLite Datei. Die Einträge sind über Hashes verkettet,
// damit man später prüfen kann, ob etwas geändert oder gelöscht wurde.
public sealed class SqliteAuditLog : IAuditLog
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqliteAuditLog(string databasePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        EnsureTable();
    }

    public async Task<AuditEntry> WriteAsync(AuditEntry entry, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var con = await OpenAsync(ct);

            var last = new SqliteCommand("SELECT Hash FROM Audit ORDER BY Id DESC LIMIT 1", con);
            var previousHash = (string?)await last.ExecuteScalarAsync(ct) ?? "";

            var toSave = entry with { PreviousHash = previousHash };
            toSave = toSave with { Hash = ComputeHash(toSave) };

            var cmd = con.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Audit (Timestamp, User, Action, Route, Model, Sensitivity, Entities, InjectionScore,
                                   Blocked, Reason, Prompt, ResponseLength, DurationMs, PreviousHash, Hash)
                VALUES ($ts, $user, $action, $route, $model, $sens, $ent, $inj, $blocked, $reason, $prompt,
                        $resp, $dur, $prev, $hash);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$ts", FormatTime(toSave.Timestamp));
            cmd.Parameters.AddWithValue("$user", toSave.User);
            cmd.Parameters.AddWithValue("$action", toSave.Action);
            cmd.Parameters.AddWithValue("$route", (object?)toSave.Route ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$model", (object?)toSave.Model ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sens", (object?)toSave.Sensitivity ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ent", (object?)toSave.Entities ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$inj", toSave.InjectionScore);
            cmd.Parameters.AddWithValue("$blocked", toSave.Blocked ? 1 : 0);
            cmd.Parameters.AddWithValue("$reason", (object?)toSave.Reason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$prompt", (object?)toSave.Prompt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$resp", toSave.ResponseLength);
            cmd.Parameters.AddWithValue("$dur", toSave.DurationMs);
            cmd.Parameters.AddWithValue("$prev", toSave.PreviousHash);
            cmd.Parameters.AddWithValue("$hash", toSave.Hash);

            var id = (long)(await cmd.ExecuteScalarAsync(ct))!;
            return toSave with { Id = id };
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken ct = default) =>
        (await SearchAsync(query with { Offset = 0 }, ct, withTotal: false)).Items;

    public Task<AuditPage> SearchAsync(AuditQuery query, CancellationToken ct = default) =>
        SearchAsync(query, ct, withTotal: true);

    private async Task<AuditPage> SearchAsync(AuditQuery query, CancellationToken ct, bool withTotal)
    {
        await using var con = await OpenAsync(ct);
        var cmd = con.CreateCommand();
        var where = BuildFilter(query, cmd);

        var total = -1;
        if (withTotal)
        {
            cmd.CommandText = "SELECT COUNT(*) FROM Audit" + where;
            total = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }

        cmd.CommandText = "SELECT * FROM Audit" + where + " ORDER BY Id DESC LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 10_000));
        cmd.Parameters.AddWithValue("$offset", Math.Max(0, query.Offset));

        var items = await ReadAllAsync(cmd, ct);
        return new AuditPage(items, total < 0 ? items.Count : total);
    }

    // Alle Filter als SQL mit Parametern, nie mit eingesetztem Text
    private static string BuildFilter(AuditQuery q, SqliteCommand cmd)
    {
        var where = new List<string>();

        if (q.From != null)
        {
            where.Add("Timestamp >= $from");
            cmd.Parameters.AddWithValue("$from", FormatTime(q.From.Value));
        }
        if (q.To != null)
        {
            where.Add("Timestamp <= $to");
            cmd.Parameters.AddWithValue("$to", FormatTime(q.To.Value));
        }
        if (!string.IsNullOrWhiteSpace(q.User))
        {
            where.Add("User = $user");
            cmd.Parameters.AddWithValue("$user", q.User);
        }
        if (!string.IsNullOrWhiteSpace(q.Action))
        {
            where.Add("Action = $action");
            cmd.Parameters.AddWithValue("$action", q.Action);
        }
        if (!string.IsNullOrWhiteSpace(q.Route))
        {
            where.Add("Route = $route");
            cmd.Parameters.AddWithValue("$route", q.Route);
        }

        // COALESCE, weil NULL LIKE ... wieder NULL ergibt und dann auch NOT nicht greift
        const string isError = "(COALESCE(Reason, '') LIKE 'Anbieterfehler%' OR COALESCE(Reason, '') LIKE 'Kein Anbieter%')";
        const string isWarning = "COALESCE(Reason, '') LIKE 'Warnung%'";
        switch (q.Status)
        {
            case AuditStatus.Blocked:
                where.Add("Blocked = 1");
                break;
            case AuditStatus.Error:
                where.Add(isError);
                break;
            case AuditStatus.Warning:
                where.Add(isWarning);
                break;
            case AuditStatus.Ok:
                where.Add($"Blocked = 0 AND Action IN ('chat', 'document', 'protect') AND NOT {isError} AND NOT ({isWarning})");
                break;
        }

        // Volltext über alles, was lesbar ist. Jedes Wort muss irgendwo vorkommen.
        var words = (q.Search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(8).ToList();
        for (var i = 0; i < words.Count; i++)
        {
            var p = "$q" + i;
            where.Add($"(zlower(Prompt) LIKE {p} ESCAPE '\\' OR zlower(Reason) LIKE {p} ESCAPE '\\' OR zlower(User) LIKE {p} ESCAPE '\\' " +
                      $"OR zlower(Entities) LIKE {p} ESCAPE '\\' OR zlower(Model) LIKE {p} ESCAPE '\\' OR CAST(Id AS TEXT) = {p}id)");
            var escaped = words[i].ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            cmd.Parameters.AddWithValue(p, "%" + escaped + "%");
            cmd.Parameters.AddWithValue(p + "id", words[i].TrimStart('#'));
        }

        return where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "";
    }

    public async Task<AuditDetail?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var con = await OpenAsync(ct);
        var cmd = new SqliteCommand("SELECT * FROM Audit WHERE Id <= $id ORDER BY Id DESC LIMIT 2", con);
        cmd.Parameters.AddWithValue("$id", id);
        var rows = await ReadAllAsync(cmd, ct);

        if (rows.Count == 0 || rows[0].Id != id)
            return null;

        var entry = rows[0];
        var expectedPrevious = rows.Count > 1 ? rows[1].Hash : "";
        return new AuditDetail(entry, ComputeHash(entry) == entry.Hash, entry.PreviousHash == expectedPrevious);
    }

    public async Task<int> CountRequestsAsync(string user, DateTimeOffset since, CancellationToken ct = default)
    {
        await using var con = await OpenAsync(ct);
        var cmd = new SqliteCommand(
            "SELECT COUNT(*) FROM Audit WHERE User = $user AND Timestamp >= $since AND Action IN ('chat', 'document')", con);
        cmd.Parameters.AddWithValue("$user", user);
        cmd.Parameters.AddWithValue("$since", FormatTime(since));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<IReadOnlyList<AuditStatRow>> StatRowsAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        await using var con = await OpenAsync(ct);
        var cmd = new SqliteCommand(
            "SELECT Id, Timestamp, User, Action, Route, Blocked, Entities, Reason, InjectionScore " +
            "FROM Audit WHERE Timestamp >= $since ORDER BY Id", con);
        cmd.Parameters.AddWithValue("$since", FormatTime(since));

        var list = new List<AuditStatRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new AuditStatRow(
                r.GetInt64(0),
                DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture),
                r.GetString(2),
                r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.GetInt32(5) == 1,
                r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7),
                r.GetInt32(8)));
        }

        return list;
    }

    public async Task<AuditVerifyResult> VerifyAsync(CancellationToken ct = default)
    {
        await using var con = await OpenAsync(ct);
        var cmd = new SqliteCommand("SELECT * FROM Audit ORDER BY Id", con);

        var previous = "";
        var count = 0;
        foreach (var e in await ReadAllAsync(cmd, ct))
        {
            count++;
            if (e.PreviousHash != previous || ComputeHash(e) != e.Hash)
                return new AuditVerifyResult(false, count, e.Id);

            previous = e.Hash;
        }

        return new AuditVerifyResult(true, count, null);
    }

    private static string ComputeHash(AuditEntry e)
    {
        var raw = string.Join("|",
            FormatTime(e.Timestamp), e.User, e.Action, e.Route, e.Model, e.Sensitivity, e.Entities,
            e.InjectionScore, e.Blocked, e.Reason, e.Prompt, e.ResponseLength, e.DurationMs, e.PreviousHash);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    // Festes Format, damit der Hash nach dem Lesen aus der DB wieder passt.
    private static string FormatTime(DateTimeOffset t) =>
        t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static async Task<List<AuditEntry>> ReadAllAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<AuditEntry>();
        await using var r = await cmd.ExecuteReaderAsync(ct);

        while (await r.ReadAsync(ct))
        {
            list.Add(new AuditEntry
            {
                Id = r.GetInt64(r.GetOrdinal("Id")),
                Timestamp = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("Timestamp")), CultureInfo.InvariantCulture),
                User = r.GetString(r.GetOrdinal("User")),
                Action = r.GetString(r.GetOrdinal("Action")),
                Route = GetNullable(r, "Route"),
                Model = GetNullable(r, "Model"),
                Sensitivity = GetNullable(r, "Sensitivity"),
                Entities = GetNullable(r, "Entities"),
                InjectionScore = r.GetInt32(r.GetOrdinal("InjectionScore")),
                Blocked = r.GetInt32(r.GetOrdinal("Blocked")) == 1,
                Reason = GetNullable(r, "Reason"),
                Prompt = GetNullable(r, "Prompt"),
                ResponseLength = r.GetInt32(r.GetOrdinal("ResponseLength")),
                DurationMs = r.GetInt64(r.GetOrdinal("DurationMs")),
                PreviousHash = r.GetString(r.GetOrdinal("PreviousHash")),
                Hash = r.GetString(r.GetOrdinal("Hash")),
            });
        }

        return list;
    }

    private static string? GetNullable(SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var con = new SqliteConnection(_connectionString);
        await con.OpenAsync(ct);

        // SQLite kann Groß und Kleinschreibung nur bei ASCII ignorieren, Umlaute brauchen eine eigene Funktion
        con.CreateFunction("zlower", (string? s) => s?.ToLowerInvariant(), isDeterministic: true);
        return con;
    }

    private void EnsureTable()
    {
        using var con = new SqliteConnection(_connectionString);
        con.Open();

        // Schritt 1 ist der ursprüngliche Aufbau. Neue Spalten usw. als weitere Schritte anhängen.
        SqliteSchema.Migrate(con, """
            CREATE TABLE IF NOT EXISTS Audit (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Timestamp TEXT NOT NULL,
                User TEXT NOT NULL,
                Action TEXT NOT NULL,
                Route TEXT,
                Model TEXT,
                Sensitivity TEXT,
                Entities TEXT,
                InjectionScore INTEGER NOT NULL,
                Blocked INTEGER NOT NULL,
                Reason TEXT,
                Prompt TEXT,
                ResponseLength INTEGER NOT NULL,
                DurationMs INTEGER NOT NULL,
                PreviousHash TEXT NOT NULL,
                Hash TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Audit_Timestamp ON Audit (Timestamp);
            CREATE INDEX IF NOT EXISTS IX_Audit_User ON Audit (User);
            CREATE INDEX IF NOT EXISTS IX_Audit_User_Time ON Audit (User, Timestamp);
            """);
    }
}
