using System.Diagnostics;
using System.Text;
using ICSharpCode.SharpZipLib.Zip;
using Zwijg.Core.Files;

namespace Zwijg.Tests;

public class EncryptedZipTests : IDisposable
{
    // Bei jedem Lauf neu, damit kein festes Kennwort im Code steht
    private static readonly string Kennwort = "test-" + Guid.NewGuid().ToString("N");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"zwijg-zip-{Guid.NewGuid():N}");

    public EncryptedZipTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Encrypt(params (string Name, string Content)[] files)
    {
        var path = Path.Combine(_dir, "dokumente-verschluesselt.zip");
        using var output = File.Create(path);
        using var writer = new EncryptedZipWriter(output, Kennwort);
        foreach (var (name, content) in files)
            writer.Add(name, new MemoryStream(Encoding.UTF8.GetBytes(content)), long.MaxValue);
        writer.Finish();
        return path;
    }

    [Fact]
    public void Aussen_nur_ein_verschluesseltes_Archiv_ohne_Dateinamen()
    {
        var path = Encrypt(("Befund_Mustermann_Erika.pdf", "Befund"), ("Roentgen_Thorax.jpg", "Bild"));

        using var zip = new ZipFile(path);
        var entry = Assert.Single(zip.Cast<ZipEntry>());
        Assert.Equal(EncryptedZipWriter.InnerName, entry.Name);
        Assert.Equal(256, entry.AESKeySize);
        Assert.True(entry.IsCrypted);

        // Weder Namen noch Inhalt stehen irgendwo lesbar in der Datei
        var bytes = File.ReadAllText(path, Encoding.Latin1);
        Assert.DoesNotContain("Mustermann", bytes);
        Assert.DoesNotContain("Roentgen", bytes);
        Assert.DoesNotContain("Befund", bytes);
    }

    [Fact]
    public async Task Wieder_oeffnen_mit_Kennwort()
    {
        var path = Encrypt(("Befund.pdf", "Inhalt 1"), ("Befund.pdf", "Inhalt 2"), ("../../boese.txt", "x"));

        await using var input = File.OpenRead(path);
        var result = EncryptedZipReader.Open(input, Kennwort, long.MaxValue);
        Assert.Equal(EncryptedZipWriter.InnerName, result.FileName);

        var inner = new MemoryStream();
        await result.WriteTo(inner, CancellationToken.None);
        inner.Position = 0;
        using var zip = new ZipFile(inner);
        Assert.Equal(["Befund.pdf", "Befund (2).pdf", "boese.txt"], zip.Cast<ZipEntry>().Select(e => e.Name));
    }

    [Fact]
    public void Falsches_Kennwort_wird_erkannt()
    {
        var path = Encrypt(("a.txt", "geheim"));
        using var input = File.OpenRead(path);
        Assert.Throws<WrongZipPasswordException>(() => EncryptedZipReader.Open(input, "falsches-kennwort", long.MaxValue));
    }

    [Fact]
    public async Task Zip_Bombe_wird_nicht_ausgepackt()
    {
        // 100 MB Nullen packen sich auf wenige Kilobyte
        var path = Path.Combine(_dir, "bombe.zip");
        using (var output = File.Create(path))
        using (var zip = new ZipOutputStream(output) { Password = Kennwort })
        {
            zip.PutNextEntry(new ZipEntry("nullen.bin") { AESKeySize = 256 });
            var block = new byte[1024 * 1024];
            for (var i = 0; i < 100; i++)
                zip.Write(block, 0, block.Length);
            zip.CloseEntry();
        }
        Assert.True(new FileInfo(path).Length < 1024 * 1024);

        await using var input = File.OpenRead(path);
        Assert.Throws<FileTooLargeException>(() => EncryptedZipReader.Open(input, Kennwort, 10L * 1024 * 1024));
    }

    [Fact]
    public void Zu_gross_bricht_ab()
    {
        using var output = new MemoryStream();
        using var writer = new EncryptedZipWriter(output, Kennwort);
        Assert.Throws<FileTooLargeException>(() => writer.Add("gross.bin", new MemoryStream(new byte[2000]), 1000));
    }

    // Mit dem echten 7-Zip prüfen, ob Empfänger die Datei öffnen können. Läuft, wo 7-Zip installiert ist.
    [Fact]
    public void Siebenzip_oeffnet_die_Datei()
    {
        var sevenZip = new[] { @"C:\Program Files\7-Zip\7z.exe", "/usr/bin/7z", "/usr/bin/7za" }.FirstOrDefault(File.Exists);
        if (sevenZip == null)
            return;

        var path = Encrypt(("Befund.txt", "Hallo aus Zwijg"));
        var target = Path.Combine(_dir, "ausgepackt");

        var (code, output) = Run(sevenZip, $"x \"{path}\" -p{Kennwort} -o\"{target}\" -y");
        Assert.True(code == 0, output);
        Assert.True(File.Exists(Path.Combine(target, EncryptedZipWriter.InnerName)));

        var (_, info) = Run(sevenZip, $"l -slt -p{Kennwort} \"{path}\"");
        Assert.Contains("AES-256", info);

        var (wrong, _) = Run(sevenZip, $"t \"{path}\" -pfalsch");
        Assert.NotEqual(0, wrong);
    }

    private static (int Code, string Output) Run(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }
}
