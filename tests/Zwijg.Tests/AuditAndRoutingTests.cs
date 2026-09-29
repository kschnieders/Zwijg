using Microsoft.Data.Sqlite;
using Zwijg.Core.Audit;
using Zwijg.Core.Pseudonymization;
using Zwijg.Core.Routing;

namespace Zwijg.Tests;

public class AuditAndRoutingTests
{
    [Fact]
    public async Task Hashkette_ist_gueltig_und_erkennt_Manipulation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"zwijg-test-{Guid.NewGuid():N}.db");
        var log = new SqliteAuditLog(path);

        await log.WriteAsync(new AuditEntry { User = "a", Action = "chat", Prompt = "[NAME_1] hat Fieber" });
        await log.WriteAsync(new AuditEntry { User = "b", Action = "chat" });
        await log.WriteAsync(new AuditEntry { User = "a", Action = "document", Blocked = true });

        var ok = await log.VerifyAsync();
        Assert.True(ok.Ok);
        Assert.Equal(3, ok.Checked);

        await using (var con = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await con.OpenAsync();
            await new SqliteCommand("UPDATE Audit SET User = 'jemand' WHERE Id = 2", con).ExecuteNonQueryAsync();
        }

        var broken = await log.VerifyAsync();
        Assert.False(broken.Ok);
        Assert.Equal(2, broken.BrokenAtId);

        var onlyA = await log.QueryAsync(new AuditQuery(User: "a"));
        Assert.Equal(2, onlyA.Count);

        File.Delete(path);
    }

    private static async Task<(SqliteAuditLog Log, string Path)> FilledLog()
    {
        var path = Path.Combine(Path.GetTempPath(), $"zwijg-search-{Guid.NewGuid():N}.db");
        var log = new SqliteAuditLog(path);
        await log.WriteAsync(new AuditEntry { User = "empfang", Action = "chat", Route = "Local", Prompt = "[NAME_1] klagt über Übelkeit" });
        await log.WriteAsync(new AuditEntry { User = "empfang", Action = "chat", Blocked = true, Reason = "Prompt Injection: jailbreak" });
        await log.WriteAsync(new AuditEntry { User = "dr.weber", Action = "document", Route = "Local", Prompt = "Befund zusammenfassen" });
        await log.WriteAsync(new AuditEntry { User = "dr.weber", Action = "chat", Route = "Local", Reason = "Anbieterfehler: 502" });
        await log.WriteAsync(new AuditEntry { User = "admin", Action = "admin", Reason = "Regeln geändert" });
        return (log, path);
    }

    [Fact]
    public async Task Volltextsuche_ignoriert_Gross_und_Kleinschreibung_auch_bei_Umlauten()
    {
        var (log, path) = await FilledLog();

        var page = await log.SearchAsync(new AuditQuery(Search: "ÜBELKEIT"));

        Assert.Equal(1, page.Total);
        Assert.Contains("Übelkeit", page.Items[0].Prompt);
        File.Delete(path);
    }

    [Theory]
    [InlineData(AuditStatus.Ok, 2)]
    [InlineData(AuditStatus.Blocked, 1)]
    [InlineData(AuditStatus.Error, 1)]
    public async Task Filter_nach_Status(AuditStatus status, int expected)
    {
        var (log, path) = await FilledLog();

        Assert.Equal(expected, (await log.SearchAsync(new AuditQuery(Status: status))).Total);
        File.Delete(path);
    }

    [Fact]
    public async Task Seiten_und_Gesamtzahl()
    {
        var (log, path) = await FilledLog();

        var page = await log.SearchAsync(new AuditQuery(Limit: 2, Offset: 2));

        Assert.Equal(5, page.Total);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(3, page.Items[0].Id);
        File.Delete(path);
    }

    [Fact]
    public async Task Einzelpruefung_erkennt_Aenderung_und_Loeschung()
    {
        var (log, path) = await FilledLog();
        Assert.True((await log.GetAsync(3))!.HashValid);

        await using (var con = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await con.OpenAsync();
            await new SqliteCommand("UPDATE Audit SET Prompt = 'harmlos' WHERE Id = 3", con).ExecuteNonQueryAsync();
            await new SqliteCommand("DELETE FROM Audit WHERE Id = 4", con).ExecuteNonQueryAsync();
        }

        Assert.False((await log.GetAsync(3))!.HashValid);
        Assert.False((await log.GetAsync(5))!.ChainValid);
        Assert.True((await log.GetAsync(2))!.ChainValid);
        File.Delete(path);
    }

    [Theory]
    [InlineData(Sensitivity.None, false, RouteTarget.Cloud)]
    [InlineData(Sensitivity.Medium, false, RouteTarget.Cloud)]
    [InlineData(Sensitivity.High, false, RouteTarget.Local)]
    [InlineData(Sensitivity.None, true, RouteTarget.Local)]
    public void Auto_Routing(Sensitivity sensitivity, bool document, RouteTarget expected)
    {
        var policy = new RoutingPolicy(new RoutingOptions());

        Assert.Equal(expected, policy.Decide(sensitivity, document).Route);
    }

    [Fact]
    public void Client_kann_Cloud_nicht_erzwingen()
    {
        var policy = new RoutingPolicy(new RoutingOptions());

        Assert.Equal(RouteTarget.Local, policy.Decide(Sensitivity.High, false, RouteTarget.Cloud).Route);
        Assert.Equal(RouteTarget.Local, policy.Decide(Sensitivity.None, false, RouteTarget.Local).Route);
    }

    [Fact]
    public void LocalOnly_schickt_nie_in_die_Cloud()
    {
        var policy = new RoutingPolicy(new RoutingOptions { Mode = RoutingMode.LocalOnly });

        Assert.Equal(RouteTarget.Local, policy.Decide(Sensitivity.None, false, RouteTarget.Cloud).Route);
    }
}
