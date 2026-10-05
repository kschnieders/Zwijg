using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Zwijg.Core.Storage;

namespace Zwijg.Core.Audit;

// Protokoll in einer SQLite Datei. Die Einträge sind über Hashes verkettet,
// damit man später prüfen kann, ob etwas geändert oder gelöscht wurde.
// Version 1: SHA-256 ohne Geheimnis, nur noch für alte Einträge.
// Version 2: HMAC-SHA256 mit eigenem Schlüssel, jedes Feld mit Länge, Id inklusive, UserId wenn gesetzt.
// Dazu ein Anker in einer eigenen Datei (Anzahl und letzter Hash), damit auch gelöschte Einträge am Ende auffallen.
public sealed class SqliteAuditLog : IAuditLog
{
    private const int CurrentVersion = 2;

    // Schritt in SqliteSchema.Migrate, mit dem HashVersion dazukam. Ältere Dateien bekommen beim Öffnen einen Anker.
    private const int AnchorSinceSchema = 2;

    private readonly string _connectionString;
    private readonly string _anchorPath;
    private readonly byte[] _key;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // Stand des Ankers, null wenn er fehlt oder ungültig ist. Dann wird er auch nicht neu geschrieben.
    private Anchor? _anchor;

    // Ohne Schlüssel liegt er in einer eigenen Datei neben der Datenbank, z.B. für Tests
    public SqliteAuditLog(string databasePath) : this(databasePath, AuditKey.LoadOrCreate(databasePath + ".key"))
    {
    }

    public SqliteAuditLog(string databasePath, byte[] key)
    {
        if (key.Length < AuditKey.Length)
            throw new ArgumentException("Schlüssel für das Protokoll ist zu kurz", nameof(key));

        var dir = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _key = key.ToArray();
        _anchorPath = Path.GetFullPath(databasePath) + ".kopf";
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        var before = EnsureTable();
        LoadAnchor(before);
    }

    public async Task<AuditEntry> WriteAsync(AuditEntry entry, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var con = await OpenAsync(ct);
            await using var tx = (SqliteTransaction)await con.BeginTransactionAsync(ct);

            var last = new SqliteCommand("SELECT Hash FROM Audit ORDER BY Id DESC LIMIT 1", con, tx);
            var previousHash = (string?)await last.ExecuteScalarAsync(ct) ?? "";

            var toSave = entry with { PreviousHash = previousHash, HashVersion = CurrentVersion, Hash = "" };

            // Die Id gehört mit in den Hash, deshalb erst einfügen und dann den Hash setzen
            var cmd = con.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO Audit (Timestamp, User, UserId, Action, Route, Model, Sensitivity, Entities, InjectionScore,
                                   Blocked, Reason, Prompt, ResponseLength, DurationMs, PreviousHash, Hash, HashVersion)
                VALUES ($ts, $user, $userId, $action, $route, $model, $sens, $ent, $inj, $blocked, $reason, $prompt,
                        $resp, $dur, $prev, $hash, $version);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$ts", FormatTime(toSave.Timestamp));
            cmd.Parameters.AddWithValue("$user", toSave.User);
            cmd.Parameters.AddWithValue("$userId", (object?)toSave.UserId ?? DBNull.Value);
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
            cmd.Parameters.AddWithValue("$version", toSave.HashVersion);

            var id = (long)(await cmd.ExecuteScalarAsync(ct))!;
            toSave = toSave with { Id = id };
            toSave = toSave with { Hash = ComputeHash(toSave) };

            var update = new SqliteCommand("UPDATE Audit SET Hash = $hash WHERE Id = $id", con, tx);
            update.Parameters.AddWithValue("$hash", toSave.Hash);
            update.Parameters.AddWithValue("$id", id);
            await update.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);

            // Die Anzahl kommt aus dem Anker, nicht aus der Datenbank. Sonst würden gelöschte Einträge nicht auffallen.
            if (_anchor != null)
            {
                _anchor = new Anchor(_anchor.Count + 1, id, toSave.Hash);
                SaveAnchor(_anchor);
            }

            return toSave;
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
                where.Add($"Blocked = 0 AND Action IN ('chat', 'document', 'protect', 'dictation', 'files') AND NOT {isError} AND NOT ({isWarning})");
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

    public async Task<int> CountRequestsAsync(string userId, string userName, DateTimeOffset since, CancellationToken ct = default)
    {
        await using var con = await OpenAsync(ct);
        var cmd = new SqliteCommand(
            "SELECT COUNT(*) FROM Audit WHERE (UserId = $userId OR (UserId IS NULL AND User = $user)) " +
            "AND Timestamp >= $since AND Action IN ('chat', 'document')", con);
        cmd.Parameters.AddWithValue("$userId", userId);
        cmd.Parameters.AddWithValue("$user", userName);
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
        // Erst den Anker lesen, dann die Einträge. So sind alle Einträge, die der Anker kennt, auch dabei.
        // Der Anker im Speicher geht vor. So fällt auch eine zurückgelegte alte Ankerdatei auf, solange Zwijg läuft.
        Anchor? anchor;
        await _writeLock.WaitAsync(ct);
        try
        {
            var fromFile = ReadAnchor();
            anchor = fromFile == null ? null : _anchor ?? fromFile;
        }
        finally
        {
            _writeLock.Release();
        }

        await using var con = await OpenAsync(ct);
        var cmd = new SqliteCommand("SELECT * FROM Audit ORDER BY Id", con);

        var previous = "";
        var count = 0;
        var anchored = 0;
        var anchorFound = anchor is { LastId: 0, Hash: "" };
        var newSeen = false;
        foreach (var e in await ReadAllAsync(cmd, ct))
        {
            count++;

            // Alte Einträge (ohne Schlüssel) kann jeder nachrechnen. Nach dem ersten neuen und nach dem Anker darf keiner mehr kommen.
            var old = e.HashVersion < CurrentVersion;
            if (e.PreviousHash != previous || ComputeHash(e) != e.Hash || (old && newSeen) || (old && anchor != null && e.Id > anchor.LastId))
                return new AuditVerifyResult(false, count, e.Id);

            newSeen |= !old;
            if (anchor != null && e.Id <= anchor.LastId)
            {
                anchored++;
                if (e.Id == anchor.LastId)
                    anchorFound = e.Hash == anchor.Hash;
            }

            previous = e.Hash;
        }

        if (anchor == null)
            return new AuditVerifyResult(false, count, null, "Die Ankerdatei des Protokolls fehlt oder wurde verändert");
        if (!anchorFound)
            return new AuditVerifyResult(false, count, anchor.LastId);
        if (anchored != anchor.Count)
            return new AuditVerifyResult(false, count, null, $"Es fehlen Einträge: erwartet {anchor.Count}, gefunden {anchored}");

        return new AuditVerifyResult(true, count, null);
    }

    private string ComputeHash(AuditEntry e)
    {
        if (e.HashVersion < CurrentVersion)
        {
            var raw = string.Join("|",
                FormatTime(e.Timestamp), e.User, e.Action, e.Route, e.Model, e.Sensitivity, e.Entities,
                e.InjectionScore, e.Blocked, e.Reason, e.Prompt, e.ResponseLength, e.DurationMs, e.PreviousHash);

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        }

        string?[] fields =
        [
            "zwijg-audit-v2",
            e.Id.ToString(CultureInfo.InvariantCulture), FormatTime(e.Timestamp), e.User, e.Action, e.Route, e.Model,
            e.Sensitivity, e.Entities, e.InjectionScore.ToString(CultureInfo.InvariantCulture), e.Blocked ? "1" : "0",
            e.Reason, e.Prompt, e.ResponseLength.ToString(CultureInfo.InvariantCulture),
            e.DurationMs.ToString(CultureInfo.InvariantCulture), e.PreviousHash
        ];

        // UserId kam nach Version 2 dazu. Nur wenn sie gesetzt ist, als weiteres Feld anhängen,
        // so bleiben Einträge ohne Id gültig. Wegen der Längen lässt sich das nicht mit einem anderen Feld verwechseln.
        return Mac(e.UserId == null ? fields : [.. fields, e.UserId]);
    }

    // Jedes Feld mit seiner Länge in Bytes, null mit eigenem Zeichen. So lässt sich nichts zwischen Feldern verschieben.
    private string Mac(params string?[] fields)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, _key);
        foreach (var f in fields)
        {
            if (f == null)
            {
                hmac.AppendData("-"u8);
                continue;
            }

            var bytes = Encoding.UTF8.GetBytes(f);
            hmac.AppendData(Encoding.ASCII.GetBytes($"+{bytes.Length}:"));
            hmac.AppendData(bytes);
        }

        return Convert.ToHexString(hmac.GetHashAndReset());
    }

    // Anker: Anzahl der Einträge, Id und Hash des letzten. Liegt neben der Datenbank und ist mit dem Schlüssel signiert.
    private sealed record Anchor(long Count, long LastId, string Hash);

    private string AnchorMac(Anchor a) =>
        Mac("zwijg-audit-kopf", a.Count.ToString(CultureInfo.InvariantCulture), a.LastId.ToString(CultureInfo.InvariantCulture), a.Hash);

    private Anchor? ReadAnchor()
    {
        if (!File.Exists(_anchorPath))
            return null;

        var parts = File.ReadAllText(_anchorPath).Trim().Split(';');
        if (parts.Length != 4
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var lastId))
            return null;

        var anchor = new Anchor(count, lastId, parts[2]);
        var expected = Encoding.ASCII.GetBytes(AnchorMac(anchor));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(parts[3])) ? anchor : null;
    }

    // Erst in eine neue Datei schreiben und dann umbenennen, damit nie ein halber Anker auf der Platte liegt.
    // Klappt das nicht, ist der Eintrag trotzdem gespeichert. Der Anker im Speicher stimmt, der nächste Eintrag schreibt ihn nach.
    private void SaveAnchor(Anchor a)
    {
        var temp = _anchorPath + ".neu";
        try
        {
            File.WriteAllText(temp, string.Join(";", a.Count, a.LastId, a.Hash, AnchorMac(a)));
            File.Move(temp, _anchorPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void LoadAnchor(int schemaBefore)
    {
        using var con = new SqliteConnection(_connectionString);
        con.Open();

        var anchor = ReadAnchor();
        if (anchor == null)
        {
            // Nur neue oder alte Dateien ohne Anker bekommen einen. Fehlt er später, wurde er gelöscht.
            if (File.Exists(_anchorPath) || schemaBefore >= AnchorSinceSchema)
                return;

            using var head = new SqliteCommand("SELECT COUNT(*), COALESCE(MAX(Id), 0) FROM Audit", con);
            using var r = head.ExecuteReader();
            r.Read();
            anchor = new Anchor(r.GetInt64(0), 0, "");
            var lastId = r.GetInt64(1);
            r.Close();
            _anchor = lastId == 0 ? anchor : anchor with { LastId = lastId, Hash = HashOf(con, lastId) };
            SaveAnchor(_anchor);
            return;
        }

        // Nach einem Absturz zwischen Speichern und Anker können Einträge hinter dem Anker liegen.
        // Nur übernehmen, was mit dem Schlüssel signiert ist und lückenlos an den Anker anschließt.
        using var after = new SqliteCommand("SELECT * FROM Audit WHERE Id > $id ORDER BY Id", con);
        after.Parameters.AddWithValue("$id", anchor.LastId);
        var rows = ReadAllAsync(after, CancellationToken.None).GetAwaiter().GetResult();
        var previous = anchor.Hash;
        var genuine = rows.All(e =>
        {
            var ok = e.HashVersion == CurrentVersion && e.PreviousHash == previous && ComputeHash(e) == e.Hash;
            previous = e.Hash;
            return ok;
        });

        if (rows.Count > 0 && genuine)
        {
            anchor = new Anchor(anchor.Count + rows.Count, rows[^1].Id, rows[^1].Hash);
            SaveAnchor(anchor);
        }

        _anchor = anchor;
    }

    private static string HashOf(SqliteConnection con, long id)
    {
        using var cmd = new SqliteCommand("SELECT Hash FROM Audit WHERE Id = $id", con);
        cmd.Parameters.AddWithValue("$id", id);
        return (string?)cmd.ExecuteScalar() ?? "";
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
                UserId = GetNullable(r, "UserId"),
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
                HashVersion = r.GetInt32(r.GetOrdinal("HashVersion")),
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

    private int EnsureTable()
    {
        using var con = new SqliteConnection(_connectionString);
        con.Open();

        // Schritt 1 ist der ursprüngliche Aufbau. Neue Spalten usw. als weitere Schritte anhängen.
        // Schritt 2: Version des Hashes je Eintrag, alte Einträge bleiben bei Version 1.
        // Schritt 3: Benutzer Id für das Tageslimit.
        return SqliteSchema.Migrate(con, """
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
            """,
            "ALTER TABLE Audit ADD COLUMN HashVersion INTEGER NOT NULL DEFAULT 1",
            """
            ALTER TABLE Audit ADD COLUMN UserId TEXT;
            CREATE INDEX IF NOT EXISTS IX_Audit_UserId_Time ON Audit (UserId, Timestamp);
            """);
    }
}
