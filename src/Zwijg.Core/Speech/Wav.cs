namespace Zwijg.Core.Speech;

// Liest WAV Dateien (PCM 16 Bit oder Float 32 Bit, beliebige Abtastrate und Kanäle)
// und macht daraus Mono mit 16 kHz, so wie Whisper es braucht.
public static class Wav
{
    public const int SampleRate = 16000;

    public static float[] ReadMono16k(byte[] data)
    {
        if (data.Length < 12 || Text(data, 0) != "RIFF" || Text(data, 8) != "WAVE")
            throw new FormatException("Keine WAV Datei");

        int channels = 0, rate = 0, bits = 0, format = 0;
        var pos = 12;
        while (pos + 8 <= data.Length)
        {
            var id = Text(data, pos);
            var size = BitConverter.ToInt32(data, pos + 4);
            var body = pos + 8;
            if (size < 0 || body + size > data.Length)
                size = data.Length - body; // manche Aufnahmen tragen die Länge nicht ein

            if (id == "fmt " && size >= 16)
            {
                format = BitConverter.ToUInt16(data, body);
                channels = BitConverter.ToUInt16(data, body + 2);
                rate = BitConverter.ToInt32(data, body + 4);
                bits = BitConverter.ToUInt16(data, body + 14);
                if (format == 0xFFFE && size >= 26)
                    format = BitConverter.ToUInt16(data, body + 24); // WAVE_FORMAT_EXTENSIBLE
            }
            else if (id == "data")
            {
                if (channels == 0)
                    throw new FormatException("Das Format steht in der WAV Datei erst nach den Daten");
                return Resample(ToMono(data.AsSpan(body, size), format, bits, channels), rate);
            }

            pos = body + size + (size & 1);
        }

        throw new FormatException("Keine Tondaten in der WAV Datei");
    }

    private static float[] ToMono(ReadOnlySpan<byte> raw, int format, int bits, int channels)
    {
        var bytesPerSample = bits / 8;
        if (!((format == 1 && bits == 16) || (format == 3 && bits == 32)) || channels < 1)
            throw new FormatException("Nur WAV mit PCM 16 Bit oder Float 32 Bit");

        var frames = raw.Length / (bytesPerSample * channels);
        var mono = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var o = (f * channels + c) * bytesPerSample;
                sum += format == 1
                    ? BitConverter.ToInt16(raw.Slice(o, 2)) / 32768f
                    : BitConverter.ToSingle(raw.Slice(o, 4));
            }
            mono[f] = sum / channels;
        }
        return mono;
    }

    // Einfach linear umrechnen, für Sprache reicht das
    private static float[] Resample(float[] input, int rate)
    {
        if (rate == SampleRate || input.Length == 0)
            return input;
        if (rate < 8000 || rate > 384000)
            throw new FormatException("Ungewöhnliche Abtastrate");

        var length = (int)((long)input.Length * SampleRate / rate);
        var output = new float[length];
        var step = (double)rate / SampleRate;
        for (var i = 0; i < length; i++)
        {
            var x = i * step;
            var a = (int)x;
            var b = Math.Min(a + 1, input.Length - 1);
            output[i] = (float)(input[a] + (input[b] - input[a]) * (x - a));
        }
        return output;
    }

    private static string Text(byte[] data, int pos) => System.Text.Encoding.ASCII.GetString(data, pos, 4);
}
