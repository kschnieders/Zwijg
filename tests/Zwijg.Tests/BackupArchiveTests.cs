using System.IO.Compression;
using System.Security.Cryptography;
using Zwijg.Core.Backup;

namespace Zwijg.Tests;

public class BackupArchiveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"zwijg-arch-{Guid.NewGuid():N}");

    public BackupArchiveTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] RoundTrip(byte[] data, string password, string? other = null)
    {
        using var encrypted = new MemoryStream();
        BackupArchive.Encrypt(new MemoryStream(data), encrypted, password);
        encrypted.Position = 0;
        using var plain = new MemoryStream();
        BackupArchive.Decrypt(encrypted, plain, other ?? password);
        return plain.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(1 << 20)]
    [InlineData((1 << 20) + 1)]
    [InlineData(3 * (1 << 20) + 17)]
    public void Verschluesseln_und_zurueck_bei_jeder_Groesse(int size)
    {
        var data = RandomNumberGenerator.GetBytes(size);
        Assert.Equal(data, RoundTrip(data, "richtiges Passwort"));
    }

    [Fact]
    public void Falsches_Passwort_wird_erkannt()
    {
        Assert.Throws<BackupArchive.WrongPasswordException>(() => RoundTrip("geheim"u8.ToArray(), "richtig", "falsch"));
    }

    [Fact]
    public void Veraenderte_abgeschnittene_oder_verlaengerte_Dateien_werden_erkannt()
    {
        using var encrypted = new MemoryStream();
        BackupArchive.Encrypt(new MemoryStream(RandomNumberGenerator.GetBytes(3 * (1 << 20))), encrypted, "pw");
        var bytes = encrypted.ToArray();

        // Ein Byte mitten in einem Block, das Ende ab dem letzten Block weg, oder etwas angehängt
        var changed = (byte[])bytes.Clone();
        changed[2 * (1 << 20)] ^= 1;
        var cut = bytes[..(3 * (1 << 20) - 100)];
        // Genau nach dem zweiten Block abgeschnitten: Jeder übrige Block ist für sich gültig, nur der letzte fehlt
        var cutAtBlock = bytes[..(33 + 2 * (4 + (1 << 20) + 16))];
        var longer = bytes.Concat(new byte[] { 1, 2, 3 }).ToArray();

        foreach (var bad in new[] { changed, cut, cutAtBlock, longer })
            Assert.ThrowsAny<InvalidDataException>(() => BackupArchive.Decrypt(new MemoryStream(bad), new MemoryStream(), "pw"));
    }

    [Fact]
    public void Arbeitsordner_nur_fuer_den_eigenen_Benutzer()
    {
        var dir = SecureTemp.CreateDirectory("zwijg-test");
        try
        {
            Assert.True(Directory.Exists(dir));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
        }
        finally
        {
            SecureTemp.Delete(dir);
        }
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Fremde_Datei_wird_abgelehnt()
    {
        Assert.Throws<InvalidDataException>(() => BackupArchive.Decrypt(new MemoryStream("PK zip"u8.ToArray()), new MemoryStream(), "pw"));
    }

    [Fact]
    public void Dateien_mit_Unterordnern_kommen_genau_so_zurueck()
    {
        var src = Path.Combine(_dir, "quelle");
        Directory.CreateDirectory(Path.Combine(src, "keys"));
        File.WriteAllText(Path.Combine(src, "settings.json"), "{ \"a\": 1 }");
        File.WriteAllText(Path.Combine(src, "keys", "key-1.xml"), "<key/>");
        var file = Path.Combine(_dir, "test" + BackupArchive.Extension);

        BackupArchive.Create(file, new Dictionary<string, string>
        {
            ["settings.json"] = Path.Combine(src, "settings.json"),
            ["keys/key-1.xml"] = Path.Combine(src, "keys", "key-1.xml"),
        }, "pw", _dir);

        Assert.False(File.Exists(file + ".teil"));
        var target = Path.Combine(_dir, "ziel");
        var names = BackupArchive.Extract(file, target, "pw", _dir);

        Assert.Equal(["settings.json", "keys/key-1.xml"], names);
        Assert.Equal("{ \"a\": 1 }", File.ReadAllText(Path.Combine(target, "settings.json")));
        Assert.Equal("<key/>", File.ReadAllText(Path.Combine(target, "keys", "key-1.xml")));
    }

    [Fact]
    public void Pfade_ausserhalb_des_Ziels_werden_abgelehnt()
    {
        // ZIP mit "../boese.txt", wie sie ein Angreifer bauen könnte
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("../boese.txt").Open()))
            writer.Write("x");
        zip.Position = 0;

        var file = Path.Combine(_dir, "boese" + BackupArchive.Extension);
        using (var output = File.Create(file))
            BackupArchive.Encrypt(zip, output, "pw");

        Assert.Throws<InvalidDataException>(() => BackupArchive.Extract(file, Path.Combine(_dir, "ziel"), "pw", _dir));
        Assert.False(File.Exists(Path.Combine(_dir, "boese.txt")));
    }
}
