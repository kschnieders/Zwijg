using Microsoft.Extensions.Logging.Abstractions;
using Zwijg.Gateway;

namespace Zwijg.Tests;

public class UpdateSnapshotTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"zwijg-snap-{Guid.NewGuid():N}");

    public UpdateSnapshotTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "keys"));
        Directory.CreateDirectory(Path.Combine(_dir, "models"));
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(_dir, "keys", "key-1.xml"), "<key/>");
        File.WriteAllText(Path.Combine(_dir, "models", "ggml-small.bin"), "groß");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string? Run(string version, bool skip = false, params string[] extra) =>
        UpdateSnapshot.CreateIfVersionChanged(_dir, version, extra, skip, NullLogger.Instance);

    [Fact]
    public void Neue_Version_sichert_vorher_die_Daten_ohne_Sprachmodelle()
    {
        File.WriteAllText(Path.Combine(_dir, ".version"), "1.0.0");

        var target = Run("1.1.0");

        Assert.NotNull(target);
        Assert.EndsWith("-vor-1.1.0-von-1.0.0", target);
        Assert.True(File.Exists(Path.Combine(target, "settings.json")));
        Assert.True(File.Exists(Path.Combine(target, "keys", "key-1.xml")));
        Assert.Equal("1.0.0", File.ReadAllText(Path.Combine(target, ".version")));
        Assert.False(Directory.Exists(Path.Combine(target, "models")));
        Assert.False(Directory.Exists(Path.Combine(target, "sicherungen")));
        Assert.Equal("1.1.0", File.ReadAllText(Path.Combine(_dir, ".version")));
    }

    [Fact]
    public void Gleiche_Version_und_neue_Installation_ohne_Sicherung()
    {
        File.WriteAllText(Path.Combine(_dir, ".version"), "1.0.0");
        Assert.Null(Run("1.0.0"));

        File.Delete(Path.Combine(_dir, "settings.json"));
        File.Delete(Path.Combine(_dir, ".version"));
        Assert.Null(Run("1.0.0"));
        Assert.Equal("1.0.0", File.ReadAllText(Path.Combine(_dir, ".version")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "sicherungen")));
    }

    [Fact]
    public void Abgeschaltet_wird_nur_die_Version_gemerkt()
    {
        File.WriteAllText(Path.Combine(_dir, ".version"), "1.0.0");

        Assert.Null(Run("1.1.0", skip: true));
        Assert.Equal("1.1.0", File.ReadAllText(Path.Combine(_dir, ".version")));
        Assert.Null(Run("1.1.0"));
    }

    [Fact]
    public void Protokoll_ausserhalb_kommt_mit_und_nur_die_neuesten_bleiben()
    {
        var audit = Path.Combine(Path.GetTempPath(), $"zwijg-audit-{Guid.NewGuid():N}.db");
        File.WriteAllText(audit, "protokoll");
        try
        {
            string? last = null;
            for (var i = 1; i <= UpdateSnapshot.Keep + 2; i++)
                last = Run($"1.{i}.0", false, audit, audit + ".fehlt");

            Assert.True(File.Exists(Path.Combine(last!, Path.GetFileName(audit))));
            Assert.Equal(UpdateSnapshot.Keep, Directory.GetDirectories(Path.Combine(_dir, UpdateSnapshot.FolderName)).Length);
        }
        finally
        {
            File.Delete(audit);
        }
    }
}
