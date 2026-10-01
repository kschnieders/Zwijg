using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Zwijg.Core.Audit;

namespace Zwijg.Tests;

// Das Protokoll soll Änderungen, Löschungen und neu berechnete Ketten erkennen
public class AuditIntegrityTests
{
    private static string NewPath() => Path.Combine(Path.GetTempPath(), $"zwijg-integ-{Guid.NewGuid():N}.db");

    private static async Task Sql(string path, string sql)
    {
        await using var con = new SqliteConnection($"Data Source={path};Pooling=False");
        await con.OpenAsync();
        await new SqliteCommand(sql, con).ExecuteNonQueryAsync();
    }

    private static async Task<SqliteAuditLog> Filled(string path, int count)
    {
        var log = new SqliteAuditLog(path);
        for (var i = 1; i <= count; i++)
            await log.WriteAsync(new AuditEntry { User = "empfang", Action = "chat", Prompt = $"[NAME_1] Anfrage {i}" });
        return log;
    }

    [Fact]
    public async Task Loeschen_der_letzten_Eintraege_faellt_auf()
    {
        var path = NewPath();
        var log = await Filled(path, 4);
        Assert.True((await log.VerifyAsync()).Ok);

        await Sql(path, "DELETE FROM Audit WHERE Id IN (3, 4)");

        Assert.False((await log.VerifyAsync()).Ok);
        Assert.False((await new SqliteAuditLog(path).VerifyAsync()).Ok);
    }

    // So hat Zwijg vor der HMAC Version gerechnet, ohne Geheimnis. Das kann jeder nachbauen.
    private static string OldHash(AuditEntry e)
    {
        var raw = string.Join("|",
            e.Timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            e.User, e.Action, e.Route, e.Model, e.Sensitivity, e.Entities,
            e.InjectionScore, e.Blocked, e.Reason, e.Prompt, e.ResponseLength, e.DurationMs, e.PreviousHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private static async Task<bool> HasColumn(string path, string column)
    {
        await using var con = new SqliteConnection($"Data Source={path};Pooling=False");
        await con.OpenAsync();
        await using var r = await new SqliteCommand("PRAGMA table_info(Audit)", con).ExecuteReaderAsync();
        while (await r.ReadAsync())
            if (r.GetString(1) == column)
                return true;
        return false;
    }

    [Fact]
    public async Task Mit_altem_Verfahren_neu_berechnete_Kette_faellt_auf()
    {
        var path = NewPath();
        var log = await Filled(path, 3);

        await Sql(path, "UPDATE Audit SET Prompt = 'harmlos' WHERE Id = 2");

        // Angreifer rechnet die ganze Kette neu und gibt sie als alte Version aus
        var versioned = await HasColumn(path, "HashVersion");
        var previous = "";
        foreach (var e in (await log.QueryAsync(new AuditQuery())).OrderBy(e => e.Id))
        {
            var hash = OldHash(e with { PreviousHash = previous });
            await Sql(path, $"UPDATE Audit SET PreviousHash = '{previous}', Hash = '{hash}'" +
                            (versioned ? ", HashVersion = 1" : "") + $" WHERE Id = {e.Id}");
            previous = hash;
        }

        Assert.False((await log.VerifyAsync()).Ok);
        Assert.False((await new SqliteAuditLog(path).VerifyAsync()).Ok);
    }

    [Theory]
    [InlineData("UPDATE Audit SET Reason = 'a', Prompt = 'b|c' WHERE Id = 1")]
    [InlineData("UPDATE Audit SET Reason = '' WHERE Id = 2")]
    public async Task Verschieben_zwischen_Feldern_und_leer_statt_null_fallen_auf(string sql)
    {
        var path = NewPath();
        var log = new SqliteAuditLog(path);
        await log.WriteAsync(new AuditEntry { User = "empfang", Action = "chat", Reason = "a|b", Prompt = "c" });
        await log.WriteAsync(new AuditEntry { User = "empfang", Action = "chat", Reason = null, Prompt = "d" });

        // Ohne letzten Eintrag würde schon die Kette den Fehler zeigen, hier geht es um den Eintrag selbst
        await log.WriteAsync(new AuditEntry { User = "empfang", Action = "chat" });
        await Sql(path, sql);

        Assert.False((await log.VerifyAsync()).Ok);
    }

    [Fact]
    public async Task Fehlender_Anker_faellt_auf()
    {
        var path = NewPath();
        var log = await Filled(path, 2);

        File.Delete(path + ".kopf");

        Assert.False((await log.VerifyAsync()).Ok);
        Assert.False((await new SqliteAuditLog(path).VerifyAsync()).Ok);
    }

    [Fact]
    public async Task Gesperrter_Anker_verhindert_das_Schreiben_nicht()
    {
        var path = NewPath();
        var log = await Filled(path, 1);

        // Ein Verzeichnis an der Stelle der Zwischendatei lässt das Schreiben des Ankers scheitern
        Directory.CreateDirectory(path + ".kopf.neu");
        await log.WriteAsync(new AuditEntry { User = "empfang", Action = "chat" });
        Assert.NotNull(new SqliteAuditLog(path));
        Directory.Delete(path + ".kopf.neu");

        await log.WriteAsync(new AuditEntry { User = "empfang", Action = "chat" });
        Assert.True((await log.VerifyAsync()).Ok);
        Assert.True((await new SqliteAuditLog(path).VerifyAsync()).Ok);
        Assert.StartsWith("3;3;", File.ReadAllText(path + ".kopf"));
    }

    [Fact]
    public async Task Zurueckgelegter_alter_Anker_faellt_im_laufenden_Betrieb_auf()
    {
        var path = NewPath();
        var log = await Filled(path, 2);
        var oldAnchor = File.ReadAllText(path + ".kopf");
        await log.WriteAsync(new AuditEntry { User = "empfang", Action = "chat" });
        await log.WriteAsync(new AuditEntry { User = "empfang", Action = "chat" });

        await Sql(path, "DELETE FROM Audit WHERE Id IN (3, 4)");
        File.WriteAllText(path + ".kopf", oldAnchor);

        Assert.False((await log.VerifyAsync()).Ok);
    }

    [Fact]
    public void Schluesseldatei_mit_falscher_Laenge_wird_abgelehnt()
    {
        // So sieht ein verschlüsselter Schlüssel aus, der zufällig auch als Base64 durchgeht
        var path = NewPath() + ".key";
        File.WriteAllText(path, Convert.ToBase64String(RandomNumberGenerator.GetBytes(132)));

        Assert.Throws<InvalidOperationException>(() => AuditKey.LoadOrCreate(path));
    }

    [Fact]
    public void Fehler_beim_Verschluesseln_hinterlaesst_keine_leere_Schluesseldatei()
    {
        var path = NewPath() + ".key";

        Assert.Throws<IOException>(() => AuditKey.LoadOrCreate(path, _ => throw new IOException("kaputt"), s => s));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Anderer_Schluessel_passt_nicht()
    {
        var path = NewPath();
        await new SqliteAuditLog(path, RandomNumberGenerator.GetBytes(32))
            .WriteAsync(new AuditEntry { User = "empfang", Action = "chat" });

        Assert.False((await new SqliteAuditLog(path, RandomNumberGenerator.GetBytes(32)).VerifyAsync()).Ok);
    }

    [Fact]
    public async Task Alte_Eintraege_bleiben_gueltig_und_werden_weiter_verkettet()
    {
        var path = NewPath();

        // Datei im Aufbau vor dieser Änderung, mit Hashes nach altem Verfahren
        await using (var con = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await con.OpenAsync();
            await new SqliteCommand("""
                CREATE TABLE Audit (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT, Timestamp TEXT NOT NULL, User TEXT NOT NULL, Action TEXT NOT NULL,
                    Route TEXT, Model TEXT, Sensitivity TEXT, Entities TEXT, InjectionScore INTEGER NOT NULL,
                    Blocked INTEGER NOT NULL, Reason TEXT, Prompt TEXT, ResponseLength INTEGER NOT NULL,
                    DurationMs INTEGER NOT NULL, PreviousHash TEXT NOT NULL, Hash TEXT NOT NULL);
                PRAGMA user_version = 1;
                """, con).ExecuteNonQueryAsync();

            var previous = "";
            for (var i = 1; i <= 2; i++)
            {
                var e = new AuditEntry
                {
                    Timestamp = new DateTimeOffset(2026, 9, i, 8, 0, 0, TimeSpan.Zero), User = "empfang", Action = "chat",
                    Prompt = $"Anfrage {i}", PreviousHash = previous
                };
                var hash = OldHash(e);
                var cmd = con.CreateCommand();
                cmd.CommandText = "INSERT INTO Audit (Timestamp, User, Action, Prompt, InjectionScore, Blocked, ResponseLength, DurationMs, PreviousHash, Hash) " +
                                  "VALUES ($ts, 'empfang', 'chat', $p, 0, 0, 0, 0, $prev, $hash)";
                cmd.Parameters.AddWithValue("$ts", e.Timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$p", e.Prompt);
                cmd.Parameters.AddWithValue("$prev", previous);
                cmd.Parameters.AddWithValue("$hash", hash);
                await cmd.ExecuteNonQueryAsync();
                previous = hash;
            }
        }

        var log = new SqliteAuditLog(path);
        Assert.True((await log.VerifyAsync()).Ok);

        await log.WriteAsync(new AuditEntry { User = "empfang", Action = "chat", Prompt = "Anfrage 3" });
        var result = await log.VerifyAsync();
        Assert.True(result.Ok);
        Assert.Equal(3, result.Checked);
        Assert.True((await log.GetAsync(1))!.HashValid);
        Assert.True((await log.GetAsync(3))!.HashValid);
        Assert.True((await log.GetAsync(3))!.ChainValid);

        // Ein alter Eintrag lässt sich jetzt auch nicht mehr unbemerkt neu berechnen
        await Sql(path, "UPDATE Audit SET Prompt = 'harmlos' WHERE Id = 2");
        var e2 = (await log.GetAsync(2))!.Entry;
        await Sql(path, $"UPDATE Audit SET Hash = '{OldHash(e2)}' WHERE Id = 2");
        Assert.False((await log.VerifyAsync()).Ok);
    }
}

// Schreibt das Protokoll fehlerhaft, die Admin Änderung darf daran nicht still vorbeigehen
public sealed class FailingAuditFactory : GatewayFactory
{
    public List<string> Errors { get; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ILoggerProvider>(new ListLoggerProvider(Errors));
            services.AddSingleton<IAuditLog>(sp => new FailingAudit());
        });
    }

    private sealed class FailingAudit : IAuditLog
    {
        public async Task<AuditEntry> WriteAsync(AuditEntry entry, CancellationToken ct = default)
        {
            await Task.Yield();
            throw new IOException("Platte voll");
        }

        public Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditEntry>>([]);
        public Task<AuditPage> SearchAsync(AuditQuery query, CancellationToken ct = default) =>
            Task.FromResult(new AuditPage([], 0));
        public Task<AuditDetail?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult<AuditDetail?>(null);
        public Task<AuditVerifyResult> VerifyAsync(CancellationToken ct = default) => Task.FromResult(new AuditVerifyResult(true, 0, null));
        public Task<int> CountRequestsAsync(string userId, string userName, DateTimeOffset since, CancellationToken ct = default) => Task.FromResult(0);
        public Task<IReadOnlyList<AuditStatRow>> StatRowsAsync(DateTimeOffset since, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditStatRow>>([]);
    }

    private sealed class ListLoggerProvider(List<string> errors) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(errors);
        public void Dispose() { }
    }

    private sealed class ListLogger(List<string> errors) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
                lock (errors)
                    errors.Add(formatter(state, exception) + " " + exception?.Message);
        }
    }
}

public class AuditWriteFailureTests(FailingAuditFactory factory) : IClassFixture<FailingAuditFactory>
{
    [Fact]
    public async Task Fehler_beim_Protokollieren_einer_Admin_Aenderung_wird_geloggt()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-key");

        var res = await client.PutAsJsonAsync("/admin/version/check", new { enabled = false });

        // Die Änderung ist schon gespeichert, also 200, aber der Fehler muss im Log stehen
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        lock (factory.Errors)
            Assert.Contains(factory.Errors, e => e.Contains("Platte voll"));
    }
}

// Schlüsseldatei, die sich nicht mehr entschlüsseln lässt, z.B. nach einem Umzug
public sealed class BrokenAuditKeyFactory : GatewayFactory
{
    public BrokenAuditKeyFactory()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(DbPath + ".key", "kaputt");
    }
}

public class BrokenAuditKeyTests(BrokenAuditKeyFactory factory) : IClassFixture<BrokenAuditKeyFactory>
{
    [Fact]
    public async Task Unlesbarer_Schluessel_verhindert_den_Start_nicht()
    {
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-key");

        var res = await admin.GetAsync("/admin/audit/verify");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Single(Directory.GetFiles(factory.DataDir, "audit.db.key.unlesbar-*"));
    }
}

public class AuditExportTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    [Fact]
    public async Task Csv_Export_entschaerft_Formeln()
    {
        var browser = factory.CreateClient();
        browser.DefaultRequestHeaders.Add("X-Requested-With", "zwijg");
        await browser.PostAsJsonAsync("/auth/login", new { username = "=hyperlink(\"http://evil.example\",\"x\")", password = "falsch" });

        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-key");
        var csv = await admin.GetStringAsync("/admin/audit/export.csv");

        Assert.Contains(";\"'=hyperlink(", csv);
        Assert.DoesNotContain(";\"=hyperlink(", csv);
    }
}
