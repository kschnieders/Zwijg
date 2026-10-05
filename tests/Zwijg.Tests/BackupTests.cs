using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Zwijg.Core.Backup;
using Zwijg.Gateway.Backup;

namespace Zwijg.Tests;

public sealed class BackupFactory : WebApplicationFactory<Program>
{
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), $"zwijg-bk-{Guid.NewGuid():N}", "data");
    public string Target { get; } = Path.Combine(Path.GetTempPath(), $"zwijg-bk-ziel-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Zwijg:ApiKeys:0:Key"] = "admin-key",
            ["Zwijg:ApiKeys:0:User"] = "admin",
            ["Zwijg:ApiKeys:0:Admin"] = "true",
            ["Zwijg:ApiKeys:1:Key"] = "user-key",
            ["Zwijg:ApiKeys:1:User"] = "empfang",
            ["Zwijg:Providers:Local:Type"] = "Echo",
            ["Zwijg:Audit:DatabasePath"] = Path.Combine(DataDir, "audit.db"),
            ["Zwijg:SettingsPath"] = Path.Combine(DataDir, "settings.json"),
        }));
    }
}

public class BackupTests(BackupFactory factory) : IClassFixture<BackupFactory>
{
    private const string Password = "langes-sicheres-passwort";

    private HttpClient Client(string key = "admin-key")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private Task<HttpResponseMessage> Configure(object input) => Client().PutAsJsonAsync("/admin/backup", input);

    [Fact]
    public async Task Einstellungen_werden_geprueft()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Configure(new { enabled = true, directory = "relativ/ordner", time = "02:00", password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Configure(new { enabled = true, directory = factory.Target, time = "02:00", password = "kurz" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Configure(new { enabled = true, directory = factory.Target, time = "25:00", password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("user-key").GetAsync("/admin/backup")).StatusCode);
    }

    [Fact]
    public async Task Sichern_und_zurueckspielen_ergibt_denselben_Stand()
    {
        // Etwas Protokoll erzeugen, damit die Datenbank nicht leer ist
        for (var i = 0; i < 3; i++)
            (await Client("user-key").PostAsJsonAsync("/v1/chat/completions",
                new { model = "auto", messages = new[] { new { role = "user", content = $"Frage {i} zu Herrn Max Mustermann" } } })).EnsureSuccessStatusCode();

        (await Configure(new { enabled = true, directory = factory.Target, time = "02:00", password = Password })).EnsureSuccessStatusCode();
        var before = await Client().GetFromJsonAsync<JsonObject>("/admin/backup");
        Assert.True(before!["settings"]!["hasPassword"]!.GetValue<bool>());
        Assert.Equal("pending", before["state"]!.GetValue<string>());

        // Den Eintrag "Sicherung erstellt" schreibt Zwijg erst nach dem Kopieren, er ist also nicht drin
        var entries = Count(Path.Combine(factory.DataDir, "audit.db"));
        var run = await Client().PostAsync("/admin/backup/run", null);
        run.EnsureSuccessStatusCode();
        var file = Directory.GetFiles(factory.Target, "*" + BackupArchive.Extension).Single();

        var after = await Client().GetFromJsonAsync<JsonObject>("/admin/backup");
        Assert.Equal("ok", after!["state"]!.GetValue<string>());
        Assert.Single(after["files"]!.AsArray());
        var me = await Client().GetFromJsonAsync<JsonObject>("/v1/me");
        Assert.Equal("ok", me!["backup"]!["state"]!.GetValue<string>());
        Assert.Null((await Client("user-key").GetFromJsonAsync<JsonObject>("/v1/me"))!["backup"]);

        // In einen leeren zweiten Ordner zurückspielen, wie auf einem neuen Rechner
        var root = Path.Combine(Path.GetTempPath(), $"zwijg-bk-neu-{Guid.NewGuid():N}");
        var dataDir = Path.Combine(root, "data");
        var auditPath = Path.Combine(dataDir, "audit.db");
        BackupRestore.Restore(file, Password, dataDir, auditPath);

        Assert.Equal(File.ReadAllText(Path.Combine(factory.DataDir, "settings.json")), File.ReadAllText(Path.Combine(dataDir, "settings.json")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(factory.DataDir, "audit.db.key")), File.ReadAllBytes(auditPath + ".key"));
        Assert.True(Directory.GetFiles(Path.Combine(dataDir, "keys")).Length > 0);
        Assert.Equal(entries, Count(auditPath));
        Assert.Equal(entries + 1, Count(Path.Combine(factory.DataDir, "audit.db")));
        Assert.True(entries >= 4);
    }

    [Fact]
    public void Zurueckspielen_legt_den_alten_Stand_beiseite_und_falsches_Passwort_aendert_nichts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"zwijg-bk-alt-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "quelle");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "settings.json"), "neu");
        File.WriteAllText(Path.Combine(source, "info.json"), "{}");
        var file = Path.Combine(root, "s" + BackupArchive.Extension);
        BackupArchive.Create(file, new Dictionary<string, string>
        {
            ["data/settings.json"] = Path.Combine(source, "settings.json"),
            ["zwijg-sicherung.json"] = Path.Combine(source, "info.json"),
        }, Password, root);

        var dataDir = Path.Combine(root, "data");
        Directory.CreateDirectory(Path.Combine(dataDir, "models"));
        File.WriteAllText(Path.Combine(dataDir, "settings.json"), "alt");
        File.WriteAllText(Path.Combine(dataDir, "models", "ggml-small.bin"), "modell");

        Assert.Throws<BackupArchive.WrongPasswordException>(() => BackupRestore.Restore(file, "falsches-passwort", dataDir, Path.Combine(dataDir, "audit.db")));
        Assert.Equal("alt", File.ReadAllText(Path.Combine(dataDir, "settings.json")));

        var aside = BackupRestore.Restore(file, Password, dataDir, Path.Combine(dataDir, "audit.db"));

        Assert.Equal("neu", File.ReadAllText(Path.Combine(dataDir, "settings.json")));
        Assert.Equal("alt", File.ReadAllText(Path.Combine(aside, "settings.json")));
        Assert.True(File.Exists(Path.Combine(dataDir, "models", "ggml-small.bin")));
    }

    [Theory]
    // Geplant 02:00
    [InlineData("2026-10-04 03:00", null, true)]                    // noch nie gesichert
    [InlineData("2026-10-04 03:00", "2026-10-04 02:00", false)]     // heute schon
    [InlineData("2026-10-04 03:00", "2026-10-03 02:00", true)]      // heute noch nicht
    [InlineData("2026-10-04 01:00", "2026-10-03 02:00", false)]     // heute ist es noch nicht so weit
    [InlineData("2026-10-04 01:00", "2026-10-02 02:00", true)]      // gestern verpasst, gleich nachholen
    public void Faellig_auch_zum_Nachholen(string now, string? last, bool due)
    {
        static DateTimeOffset At(string s) => new(DateTime.Parse(s), TimeSpan.FromHours(2));
        Assert.Equal(due, BackupService.IsDue(At(now), "02:00", last == null ? null : At(last), null));
    }

    [Fact]
    public void Nach_einem_Fehler_erst_eine_Stunde_spaeter_wieder()
    {
        var now = new DateTimeOffset(2026, 10, 4, 3, 0, 0, TimeSpan.Zero);
        Assert.False(BackupService.IsDue(now, "02:00", null, now.AddMinutes(-10)));
        Assert.True(BackupService.IsDue(now, "02:00", null, now.AddMinutes(-61)));
    }

    [Fact]
    public void Aufbewahrung_sieben_Tage_und_vier_Wochen()
    {
        var now = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        // Jeden Tag eine um 02:00 der letzten 60 Tage, heute zusätzlich eine von Hand
        var files = Enumerable.Range(0, 60).Select(d => ($"tag-{d}", now.Date.AddDays(-d).AddHours(2)))
            .Select(f => (f.Item1, new DateTimeOffset(f.Item2, TimeSpan.Zero)))
            .Append(("hand", now.AddHours(-1)))
            .ToList();

        var delete = BackupService.ToDelete(files, now, 7, 4).ToHashSet();
        var kept = files.Select(f => f.Item1).Where(n => !delete.Contains(n)).ToList();

        Assert.Contains("hand", kept);
        Assert.DoesNotContain("tag-0", kept); // gleicher Tag, die neuere bleibt
        Assert.All(Enumerable.Range(1, 6), d => Assert.Contains($"tag-{d}", kept));
        Assert.InRange(kept.Count, 7 + 2, 7 + 4);
        Assert.DoesNotContain("tag-59", kept);
    }

    private static long Count(string db)
    {
        using var con = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Pooling=False");
        con.Open();
        using var cmd = new SqliteCommand("SELECT COUNT(*) FROM Audit", con);
        return (long)cmd.ExecuteScalar()!;
    }
}
