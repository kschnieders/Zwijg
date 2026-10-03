using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Zwijg.Core.Backup;

// Verschlüsselte Sicherungsdatei. Innen liegt eine ZIP mit den Dateien aus dem Datenordner.
//
// Aufbau: Kopf (Kennung, Version, Salz, Runden, Nonce Anfang), dann Blöcke zu je höchstens 1 MiB.
// Jeder Block ist mit AES-256-GCM verschlüsselt. Blocknummer und "letzter Block" gehören zu den geprüften Daten,
// so fällt auf, wenn Blöcke fehlen, vertauscht oder am Ende abgeschnitten sind.
// Der Schlüssel kommt per PBKDF2 aus dem Passwort.
public static class BackupArchive
{
    public const string Extension = ".zwijg";
    private static readonly byte[] Magic = "ZWIJGSIC"u8.ToArray();
    private const byte FormatVersion = 1;
    private const int Iterations = 600_000;
    private const int ChunkSize = 1 << 20;
    private const int TagSize = 16;

    public sealed class WrongPasswordException() : Exception("Das Passwort ist falsch oder die Datei ist beschädigt.");

    // Dateien: Name in der Sicherung, zum Beispiel "keys/key-1.xml", und Pfad auf der Platte
    public static void Create(string target, IReadOnlyDictionary<string, string> files, string password)
    {
        var zip = Path.GetTempFileName();
        try
        {
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
            {
                foreach (var (name, path) in files)
                    archive.CreateEntryFromFile(path, name, CompressionLevel.Optimal);
            }

            // Erst unter anderem Namen schreiben, dann umbenennen. So liegt nie eine halbe Sicherung im Ordner.
            var partial = target + ".teil";
            using (var input = File.OpenRead(zip))
            using (var output = File.Create(partial))
                Encrypt(input, output, password);
            File.Move(partial, target, overwrite: true);
        }
        finally
        {
            File.Delete(zip);
        }
    }

    // Packt die Sicherung in einen Ordner aus und gibt die Namen der Dateien zurück
    public static IReadOnlyList<string> Extract(string source, string targetDir, string password)
    {
        var zip = Path.GetTempFileName();
        try
        {
            using (var input = File.OpenRead(source))
            using (var output = File.Create(zip))
                Decrypt(input, output, password);

            var names = new List<string>();
            using var archive = ZipFile.OpenRead(zip);
            var root = Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar;
            foreach (var entry in archive.Entries.Where(e => e.Name != ""))
            {
                // Nur Pfade innerhalb des Zielordners, nie "../" oder absolute Pfade
                var path = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
                if (!path.StartsWith(root, StringComparison.Ordinal))
                    throw new InvalidDataException($"Unerlaubter Pfad in der Sicherung: {entry.FullName}");

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path, overwrite: true);
                names.Add(entry.FullName);
            }
            return names;
        }
        finally
        {
            File.Delete(zip);
        }
    }

    public static void Encrypt(Stream input, Stream output, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var noncePrefix = RandomNumberGenerator.GetBytes(4);
        var header = Header(salt, Iterations, noncePrefix);
        output.Write(header);

        using var aes = new AesGcm(Key(password, salt, Iterations), TagSize);
        var buffer = new byte[ChunkSize];
        var next = new byte[ChunkSize];
        var length = ReadFull(input, buffer);
        long index = 0;
        while (true)
        {
            // Einen Block vorauslesen, damit der letzte als letzter markiert werden kann
            var nextLength = length == ChunkSize ? ReadFull(input, next) : 0;
            var last = nextLength == 0;

            var cipher = new byte[length];
            var tag = new byte[TagSize];
            aes.Encrypt(Nonce(noncePrefix, index), buffer.AsSpan(0, length), cipher, tag, Associated(header, index, last));

            Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(size, length);
            output.Write(size);
            output.Write(cipher);
            output.Write(tag);

            if (last)
                break;
            (buffer, next) = (next, buffer);
            length = nextLength;
            index++;
        }
    }

    public static void Decrypt(Stream input, Stream output, string password)
    {
        var header = new byte[Magic.Length + 1 + 16 + 4 + 4];
        if (ReadFull(input, header) != header.Length || !header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("Das ist keine Sicherung von Zwijg.");
        if (header[Magic.Length] != FormatVersion)
            throw new InvalidDataException("Diese Sicherung stammt von einer neueren Zwijg Version.");

        var salt = header.AsSpan(Magic.Length + 1, 16).ToArray();
        var iterations = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(Magic.Length + 17, 4));
        var noncePrefix = header.AsSpan(Magic.Length + 21, 4).ToArray();
        if (iterations is < 100_000 or > 10_000_000)
            throw new InvalidDataException("Die Sicherung ist beschädigt.");

        using var aes = new AesGcm(Key(password, salt, iterations), TagSize);
        var size = new byte[4];
        long index = 0;
        while (true)
        {
            if (ReadFull(input, size) != 4)
                throw index == 0 ? new WrongPasswordException() : new InvalidDataException("Die Sicherung ist unvollständig.");
            var length = BinaryPrimitives.ReadInt32LittleEndian(size);
            if (length is < 0 or > ChunkSize)
                throw new InvalidDataException("Die Sicherung ist beschädigt.");

            var cipher = new byte[length];
            var tag = new byte[TagSize];
            if (ReadFull(input, cipher) != length || ReadFull(input, tag) != TagSize)
                throw new InvalidDataException("Die Sicherung ist unvollständig.");

            // Ob es der letzte Block ist, verrät erst der Versuch: passt die Prüfsumme nur mit "letzter Block",
            // dann darf danach nichts mehr kommen
            var plain = new byte[length];
            var last = false;
            try
            {
                aes.Decrypt(Nonce(noncePrefix, index), cipher, tag, plain, Associated(header, index, false));
            }
            catch (AuthenticationTagMismatchException)
            {
                try
                {
                    aes.Decrypt(Nonce(noncePrefix, index), cipher, tag, plain, Associated(header, index, true));
                    last = true;
                }
                catch (AuthenticationTagMismatchException)
                {
                    throw index == 0 ? new WrongPasswordException() : new InvalidDataException("Die Sicherung ist beschädigt.");
                }
            }

            output.Write(plain);
            if (last)
            {
                if (input.ReadByte() != -1)
                    throw new InvalidDataException("Die Sicherung ist beschädigt.");
                return;
            }
            index++;
        }
    }

    private static byte[] Header(byte[] salt, int iterations, byte[] noncePrefix)
    {
        var header = new byte[Magic.Length + 1 + 16 + 4 + 4];
        Magic.CopyTo(header, 0);
        header[Magic.Length] = FormatVersion;
        salt.CopyTo(header, Magic.Length + 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(Magic.Length + 17), iterations);
        noncePrefix.CopyTo(header, Magic.Length + 21);
        return header;
    }

    private static byte[] Key(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    // 4 Byte Zufall aus dem Kopf und 8 Byte Blocknummer, so wiederholt sich keine Nonce
    private static byte[] Nonce(byte[] prefix, long index)
    {
        var nonce = new byte[12];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteInt64LittleEndian(nonce.AsSpan(4), index);
        return nonce;
    }

    private static byte[] Associated(byte[] header, long index, bool last)
    {
        var data = new byte[header.Length + 9];
        header.CopyTo(data, 0);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(header.Length), index);
        data[^1] = last ? (byte)1 : (byte)0;
        return data;
    }

    private static int ReadFull(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }
}
