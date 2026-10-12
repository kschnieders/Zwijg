using Whisper.net;

namespace Zwijg.Core.Speech;

public sealed record SpeechModel(string Id, string Label, string Note, long Bytes, string Sha256)
{
    // Fester Stand, damit die Prüfsumme immer passt
    public const string Revision = "5359861c739e955e79d9a303bcbc70fb988958b1";

    public string FileName => $"ggml-{Id}.bin";

    public string Url => $"https://huggingface.co/ggerganov/whisper.cpp/resolve/{Revision}/{FileName}";
}

// Spracherkennung mit Whisper (whisper.cpp über Whisper.net), komplett auf diesem Rechner.
// Das Modell wird einmal geladen und bleibt im Speicher, bis ein anderes gewählt wird.
public sealed class WhisperTranscriber : IDisposable
{
    public static readonly IReadOnlyList<SpeechModel> Models =
    [
        new("base", "Schnell", "Für kurze, deutliche Diktate. Läuft auch auf schwachen Rechnern.", 147_951_465,
            "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe"),
        new("small", "Gut", "Guter Kompromiss für den Praxisalltag.", 487_601_967,
            "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b"),
        new("large-v3-turbo-q5_0", "Sehr gut", "Versteht am meisten, braucht aber einen kräftigen Rechner.", 574_041_195,
            "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2"),
    ];

    // Fachwörter als Hinweis, dann schreibt Whisper eher "Metformin" als "Med Formin"
    private const string MedicalPrompt =
        "Arztbrief, Befund, Diagnose, Anamnese, Therapie, Überweisung, Arbeitsunfähigkeit, Hypertonie, Diabetes mellitus, " +
        "Pneumonie, Metformin, Ramipril, Ibuprofen, Pantoprazol, L-Thyroxin, HbA1c, Kreatinin, EKG, Sonographie.";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperFactory? _factory;
    private string? _loadedPath;

    public async Task<string> TranscribeAsync(float[] samples, string modelPath, string language, int threads, CancellationToken ct)
    {
        // Ein Modell rechnet mit allen Kernen, mehrere gleichzeitig würden sich nur ausbremsen
        await _gate.WaitAsync(ct);
        try
        {
            if (_factory == null || _loadedPath != modelPath)
            {
                _factory?.Dispose();
                _factory = WhisperFactory.FromPath(modelPath);
                _loadedPath = modelPath;
            }

            using var processor = _factory.CreateBuilder()
                .WithLanguage(language)
                .WithThreads(threads > 0 ? threads : Math.Max(1, Environment.ProcessorCount - 1))
                .WithPrompt(MedicalPrompt)
                .Build();

            var parts = new List<string>();
            await foreach (var segment in processor.ProcessAsync(samples, ct))
                parts.Add(segment.Text.Trim());

            return string.Join(" ", parts.Where(p => p.Length > 0 && !IsNoise(p))).Trim();
        }
        finally
        {
            _gate.Release();
        }
    }

    // Whisper schreibt bei Stille gern Dinge wie "[Musik]" oder "Untertitel im Auftrag des ZDF"
    private static bool IsNoise(string part) =>
        (part.StartsWith('[') && part.EndsWith(']')) || (part.StartsWith('(') && part.EndsWith(')'))
        || part.Contains("Untertitel", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        _factory?.Dispose();
        _gate.Dispose();
    }
}
