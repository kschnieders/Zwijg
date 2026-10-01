using Microsoft.Extensions.Options;
using Zwijg.Core.Audit;
using Zwijg.Core.Speech;
using Zwijg.Gateway.Settings;

namespace Zwijg.Gateway;

public sealed record DictationInput(bool Enabled, string? Model, string? ModelDir, int? Threads, string? Language);

public sealed record DownloadInput(string Model);

public sealed record DownloadState(string? Model, long Received, long Total, bool Running, string? Error);

// Diktieren: Modell verwalten und Sprache in Text umwandeln, alles auf diesem Rechner
public sealed class DictationService(
    SettingsStore settings,
    IOptions<GatewayOptions> options,
    IHttpClientFactory http,
    ILogger<DictationService> logger) : IDisposable
{
    private const long MaxAudioBytes = 25 * 1024 * 1024;

    private readonly WhisperTranscriber _whisper = new();
    private readonly object _lock = new();
    private DownloadState _download = new(null, 0, 0, false, null);

    public DownloadState Download => _download;

    public string ModelDir
    {
        get
        {
            var configured = settings.Current.Dictation.ModelDir;
            return !string.IsNullOrWhiteSpace(configured)
                ? configured
                : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.Value.SettingsPath))!, "models");
        }
    }

    public SpeechModel Model => WhisperTranscriber.Models.FirstOrDefault(m => m.Id == settings.Current.Dictation.Model)
        ?? WhisperTranscriber.Models[1];

    public string ModelPath => Path.Combine(ModelDir, Model.FileName);

    public bool Ready => settings.Current.Dictation.Enabled && File.Exists(ModelPath);

    public async Task<(string? Text, string? Error, int Status)> TranscribeAsync(IFormFile file, string? language, CancellationToken ct)
    {
        var o = settings.Current.Dictation;
        if (!o.Enabled)
            return (null, "Diktieren ist ausgeschaltet", 503);
        if (!File.Exists(ModelPath))
            return (null, "Für das Diktieren fehlt noch das Sprachmodell. Ein Admin kann es unter Regeln, Erkennung laden.", 503);
        if (file.Length == 0 || file.Length > MaxAudioBytes)
            return (null, "Die Aufnahme ist leer oder zu groß (höchstens 25 MB)", 400);

        float[] samples;
        try
        {
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            samples = Wav.ReadMono16k(buffer.ToArray());
        }
        catch (FormatException ex)
        {
            return (null, $"Bitte als WAV schicken. {ex.Message}", 415);
        }

        if (samples.Length < Wav.SampleRate / 4)
            return ("", null, 200);

        var lang = string.IsNullOrWhiteSpace(language) ? o.Language : language.Trim().ToLowerInvariant();
        var text = await _whisper.TranscribeAsync(samples, ModelPath, lang, o.Threads, ct);
        return (text, null, 200);
    }

    // Lädt ein Modell im Hintergrund. Erst in eine .part Datei, fertig umbenennen, damit nie eine halbe Datei benutzt wird.
    public bool StartDownload(string modelId)
    {
        var model = WhisperTranscriber.Models.FirstOrDefault(m => m.Id == modelId);
        if (model == null)
            return false;

        lock (_lock)
        {
            if (_download.Running)
                return false;
            _download = new(model.Id, 0, model.Bytes, true, null);
        }

        _ = Task.Run(async () =>
        {
            var target = Path.Combine(ModelDir, model.FileName);
            var part = target + ".part";
            try
            {
                Directory.CreateDirectory(ModelDir);
                using var res = await http.CreateClient("models").GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead);
                res.EnsureSuccessStatusCode();
                var total = res.Content.Headers.ContentLength ?? model.Bytes;

                await using (var source = await res.Content.ReadAsStreamAsync())
                await using (var dest = File.Create(part))
                {
                    var buffer = new byte[1 << 20];
                    long received = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer)) > 0)
                    {
                        await dest.WriteAsync(buffer.AsMemory(0, read));
                        received += read;
                        _download = new(model.Id, received, total, true, null);
                    }
                }

                File.Move(part, target, overwrite: true);
                _download = new(model.Id, total, total, false, null);
                logger.LogInformation("Sprachmodell {Model} geladen", model.Id);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Laden des Sprachmodells fehlgeschlagen");
                try { File.Delete(part); } catch (IOException) { }
                _download = _download with { Running = false, Error = "Laden fehlgeschlagen. Ist der Rechner mit dem Internet verbunden?" };
            }
        });

        return true;
    }

    public void Dispose() => _whisper.Dispose();
}

public static class DictationEndpoints
{
    public static void MapDictation(this WebApplication app)
    {
        // OpenAI kompatibel: multipart mit "file", optional "language"
        app.MapPost("/v1/audio/transcriptions", async (HttpContext ctx, DictationService dictation, IAuditLog audit, CancellationToken ct) =>
        {
            if (!ctx.Request.HasFormContentType)
                return Results.Json(new { error = "Bitte als multipart/form-data mit file senden" }, statusCode: 400);

            var form = await ctx.Request.ReadFormAsync(ct);
            if (form.Files["file"] is not { } file)
                return Results.Json(new { error = "Keine Aufnahme erhalten" }, statusCode: 400);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var (text, error, status) = await dictation.TranscribeAsync(file, form["language"], ct);
            if (error != null)
                return Results.Json(new { error }, statusCode: status);

            // Ins Protokoll kommt, wer wann diktiert hat, aber nie der Inhalt
            await audit.WriteAsync(new AuditEntry
            {
                User = ApiKeyMiddleware.GetUser(ctx).Name,
                Action = "dictation",
                Route = "Local",
                Model = "whisper " + dictation.Model.Id,
                ResponseLength = text!.Length,
                DurationMs = watch.ElapsedMilliseconds,
            }, ct);

            return Results.Ok(new { text });
        }).DisableAntiforgery();

        var admin = app.MapGroup("/admin");

        admin.MapGet("/dictation", (DictationService dictation, SettingsStore store) =>
        {
            var path = dictation.ModelPath;
            return Results.Ok(new
            {
                settings = store.Current.Dictation,
                models = WhisperTranscriber.Models.Select(m => new
                {
                    m.Id, m.Label, m.Note, m.Bytes,
                    present = File.Exists(Path.Combine(dictation.ModelDir, m.FileName)),
                }),
                modelDir = dictation.ModelDir,
                ready = dictation.Ready,
                modelPresent = File.Exists(path),
                download = dictation.Download,
            });
        });

        admin.MapPut("/dictation", (DictationInput input, HttpContext ctx, SettingsStore store, IAuditLog audit) =>
            AdminSettingsEndpoints.Change(ctx, store, audit,
                input.Enabled ? "Diktieren eingestellt" : "Diktieren ausgeschaltet",
                s => s.Dictation = new DictationSettings
                {
                    Enabled = input.Enabled,
                    Model = input.Model ?? s.Dictation.Model,
                    ModelDir = string.IsNullOrWhiteSpace(input.ModelDir) ? null : input.ModelDir.Trim().Trim('"'),
                    Threads = input.Threads ?? 0,
                    Language = (input.Language ?? "de").Trim().ToLowerInvariant(),
                }));

        admin.MapPost("/dictation/download", (DownloadInput input, DictationService dictation) =>
            dictation.StartDownload(input.Model)
                ? Results.Ok(new { started = true })
                : Results.Json(new { error = "Läuft schon oder unbekanntes Modell" }, statusCode: 409));
    }
}
