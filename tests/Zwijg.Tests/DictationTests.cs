using System.Diagnostics;
using Xunit.Abstractions;
using Zwijg.Core.Speech;

namespace Zwijg.Tests;

// Läuft nur, wenn ein Whisper Modell da ist. Pfad über ZWIJG_TEST_WHISPER_MODEL.
public sealed class WhisperFactAttribute : FactAttribute
{
    public static readonly string? ModelPath = Environment.GetEnvironmentVariable("ZWIJG_TEST_WHISPER_MODEL");

    public WhisperFactAttribute()
    {
        if (string.IsNullOrEmpty(ModelPath) || !File.Exists(ModelPath))
            Skip = "Kein Whisper Modell für Tests (ZWIJG_TEST_WHISPER_MODEL)";
    }
}

public class DictationTests(ITestOutputHelper output)
{
    private static readonly byte[] Speech = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "diktat.wav"));

    // WAV mit beliebigem Format bauen, zum Prüfen des Lesers
    private static byte[] MakeWav(short[] samples, int rate, int channels)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + samples.Length * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)channels);
        w.Write(rate); w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write("data"u8); w.Write(samples.Length * 2);
        foreach (var s in samples) w.Write(s);
        return ms.ToArray();
    }

    [Fact]
    public void Aufnahme_von_Windows_mit_langem_Formatblock_wird_gelesen()
    {
        var samples = Wav.ReadMono16k(Speech);
        Assert.InRange(samples.Length / (double)Wav.SampleRate, 10, 16);
    }

    [Fact]
    public void Stereo_mit_48_kHz_wird_zu_Mono_mit_16_kHz()
    {
        // Eine Sekunde, links laut, rechts leise
        var frames = 48000;
        var data = new short[frames * 2];
        for (var i = 0; i < frames; i++) { data[i * 2] = 16000; data[i * 2 + 1] = 0; }

        var samples = Wav.ReadMono16k(MakeWav(data, 48000, 2));

        Assert.Equal(16000, samples.Length);
        Assert.InRange(samples[100], 0.24f, 0.25f);
    }

    [Fact]
    public void Kein_WAV_wird_abgelehnt()
    {
        Assert.Throws<FormatException>(() => Wav.ReadMono16k("OggS das ist kein WAV"u8.ToArray()));
    }

    [WhisperFact]
    public async Task Diktat_wird_erkannt()
    {
        using var whisper = new WhisperTranscriber();
        var watch = Stopwatch.StartNew();

        var text = await whisper.TranscribeAsync(Wav.ReadMono16k(Speech), WhisperFactAttribute.ModelPath!, "de", 0, default);

        output.WriteLine($"{watch.ElapsedMilliseconds} ms: {text}");
        // Das kleine Testmodell verhört sich bei Fachwörtern leicht, deshalb nur den Anfang des Wirkstoffs prüfen
        Assert.Contains("Mustermann", text);
        Assert.Contains("Lungenentzündung", text);
        Assert.Contains("Amoxi", text, StringComparison.OrdinalIgnoreCase);
    }
}
