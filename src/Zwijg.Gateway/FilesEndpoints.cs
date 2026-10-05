using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Zwijg.Core.Audit;
using Zwijg.Core.Files;
using Zwijg.Gateway.Settings;

namespace Zwijg.Gateway;

public sealed record FilesInput(bool Enabled, int MaxSizeMb);

// Dateien verschlüsseln und wieder öffnen, für den Versand an Empfänger ohne KIM.
// Der Upload wird Stück für Stück gelesen und gleich verschlüsselt. Eine unverschlüsselte Kopie liegt nie auf der Platte.
public static class FilesEndpoints
{
    public const int MinPasswordLength = 12;

    public static void MapFiles(this WebApplication app)
    {
        app.MapPost("/v1/files/encrypt", async (HttpContext ctx, SettingsStore store, IAuditLog audit, CancellationToken ct) =>
        {
            var o = store.Current.Files;
            if (!o.Enabled)
                return Results.Json(new { error = "Dateien verschlüsseln ist ausgeschaltet" }, statusCode: 403);
            if (Boundary(ctx) is not { } boundary)
                return Results.BadRequest(new { error = "Bitte als multipart/form-data senden" });
            AllowLargeBody(ctx, o);

            var reader = new MultipartReader(boundary, ctx.Request.Body);
            var password = "";
            var name = "dokumente";
            var temp = TempFile();
            var writer = default(EncryptedZipWriter);
            try
            {
                while (await reader.ReadNextSectionAsync(ct) is { } section)
                {
                    var disposition = ContentDispositionHeaderValue.Parse(section.ContentDisposition);
                    var field = disposition.Name.Value?.Trim('"');

                    if (!disposition.IsFileDisposition())
                    {
                        var value = await ReadFieldAsync(section.Body, ct);
                        if (value == null)
                        {
                            Discard(ref writer, temp);
                            return Results.BadRequest(new { error = "Ein Feld ist zu lang" });
                        }
                        if (field == "password") password = value;
                        else if (field == "name" && !string.IsNullOrWhiteSpace(value)) name = Path.GetFileNameWithoutExtension(EncryptedZipWriter.SafeName(value));
                        continue;
                    }

                    // Das Kennwort muss vor den Dateien kommen, sonst müsste der Upload zwischengespeichert werden
                    if (writer == null)
                    {
                        if (password.Length < MinPasswordLength)
                        {
                            Discard(ref writer, temp);
                            return Results.BadRequest(new { error = $"Das Kennwort braucht mindestens {MinPasswordLength} Zeichen" });
                        }
                        writer = new EncryptedZipWriter(temp, password);
                    }
                    writer.Add(disposition.FileName.Value?.Trim('"') ?? "datei", section.Body, o.MaxSizeMb * 1024L * 1024L);
                }

                if (writer == null || writer.Count == 0)
                {
                    Discard(ref writer, temp);
                    return Results.BadRequest(new { error = "Keine Datei erhalten" });
                }
                writer.Finish();
                await WriteAuditAsync(ctx, audit, $"{writer.Count} {(writer.Count == 1 ? "Datei" : "Dateien")} verschlüsselt ({writer.Bytes / 1048576.0:0.0} MB)", ct);

                temp.Position = 0;
                return Results.File(temp, "application/zip", name + "-verschluesselt.zip");
            }
            catch (FileTooLargeException)
            {
                Discard(ref writer, temp);
                return Results.Json(new { error = $"Die Dateien sind zusammen größer als {o.MaxSizeMb} MB" }, statusCode: 413);
            }
            catch
            {
                Discard(ref writer, temp);
                throw;
            }
            finally
            {
                writer?.Dispose();
            }
        }).DisableAntiforgery();

        app.MapPost("/v1/files/decrypt", async (HttpContext ctx, SettingsStore store, IAuditLog audit, CancellationToken ct) =>
        {
            var o = store.Current.Files;
            if (!o.Enabled)
                return Results.Json(new { error = "Dateien verschlüsseln ist ausgeschaltet" }, statusCode: 403);
            if (Boundary(ctx) is not { } boundary)
                return Results.BadRequest(new { error = "Bitte als multipart/form-data senden" });
            AllowLargeBody(ctx, o);

            // Die verschlüsselte Datei muss ganz vorliegen, um sie zu lesen. Auf der Platte liegt sie dabei verschlüsselt.
            var reader = new MultipartReader(boundary, ctx.Request.Body);
            var password = "";
            var temp = TempFile();
            var received = false;
            while (await reader.ReadNextSectionAsync(ct) is { } section)
            {
                var disposition = ContentDispositionHeaderValue.Parse(section.ContentDisposition);
                if (!disposition.IsFileDisposition())
                {
                    if (disposition.Name.Value?.Trim('"') == "password")
                        password = await ReadFieldAsync(section.Body, ct) ?? "";
                    continue;
                }
                await section.Body.CopyToAsync(temp, ct);
                received = true;
                if (temp.Length > o.MaxSizeMb * 1024L * 1024L)
                {
                    await temp.DisposeAsync();
                    return Results.Json(new { error = $"Die Datei ist größer als {o.MaxSizeMb} MB" }, statusCode: 413);
                }
            }

            if (!received)
            {
                await temp.DisposeAsync();
                return Results.BadRequest(new { error = "Keine Datei erhalten" });
            }

            temp.Position = 0;
            try
            {
                // Ausgepackt höchstens das Vierfache der erlaubten Größe, sonst ist es eine ZIP Bombe
                var result = EncryptedZipReader.Open(temp, password, o.MaxSizeMb * 4L * 1024L * 1024L);
                await WriteAuditAsync(ctx, audit, "Verschlüsselte Datei geöffnet", ct);
                return Results.Stream(async output =>
                {
                    try { await result.WriteTo(output, ct); }
                    finally { await temp.DisposeAsync(); }
                }, "application/octet-stream", result.FileName);
            }
            catch (WrongZipPasswordException)
            {
                await temp.DisposeAsync();
                return Results.BadRequest(new { error = "Das Kennwort ist falsch." });
            }
            catch (FileTooLargeException)
            {
                await temp.DisposeAsync();
                return Results.Json(new { error = "Ausgepackt wäre die Datei viel größer als erlaubt. Zwijg öffnet sie deshalb nicht." }, statusCode: 413);
            }
            catch (Exception ex) when (ex is ICSharpCode.SharpZipLib.SharpZipBaseException or InvalidDataException)
            {
                await temp.DisposeAsync();
                return Results.BadRequest(new { error = "Das ist keine lesbare ZIP Datei. Unterstützt werden ZIP Dateien mit Kennwort, zum Beispiel aus 7-Zip oder WinRAR." });
            }
        }).DisableAntiforgery();

        app.MapPut("/admin/files", async (FilesInput input, HttpContext ctx, SettingsStore store, IAuditLog audit) =>
        {
            if (input.MaxSizeMb is < 1 or > 4000)
                return Results.BadRequest(new { error = "Bitte eine Größe zwischen 1 und 4000 MB angeben" });
            return await AdminSettingsEndpoints.ChangeAsync(ctx, store, audit,
                input.Enabled ? $"Dateien verschlüsseln eingeschaltet, bis {input.MaxSizeMb} MB" : "Dateien verschlüsseln ausgeschaltet",
                s => s.Files = new FilesSettings { Enabled = input.Enabled, MaxSizeMb = input.MaxSizeMb });
        });
    }

    private static string? Boundary(HttpContext ctx) =>
        MediaTypeHeaderValue.TryParse(ctx.Request.ContentType, out var type) && type.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            ? HeaderUtilities.RemoveQuotes(type.Boundary).Value
            : null;

    // Röntgenbilder sind groß. Die ZIP Bibliothek arbeitet synchron, das ist hier erlaubt.
    private static void AllowLargeBody(HttpContext ctx, FilesSettings o)
    {
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            size.MaxRequestBodySize = o.MaxSizeMb * 1024L * 1024L + 1024 * 1024;
        if (ctx.Features.Get<IHttpBodyControlFeature>() is { } body)
            body.AllowSynchronousIO = true;
    }

    // Textfelder wie das Kennwort, höchstens 1000 Zeichen. Sonst ließe sich der Speicher mit einem riesigen Feld füllen.
    private static async Task<string?> ReadFieldAsync(Stream body, CancellationToken ct)
    {
        var buffer = new char[1001];
        using var reader = new StreamReader(body);
        var length = await reader.ReadBlockAsync(buffer, ct);
        return length > 1000 ? null : new string(buffer, 0, length);
    }

    // Abbruch: erst das Archiv schließen, dann die Datei, sonst schreibt das Archiv in eine geschlossene Datei
    private static void Discard(ref EncryptedZipWriter? writer, FileStream temp)
    {
        try { writer?.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException or ICSharpCode.SharpZipLib.SharpZipBaseException) { }
        writer = null;
        temp.Dispose();
    }

    // Wird beim Schließen gelöscht und enthält nur Verschlüsseltes
    private static FileStream TempFile() => new(
        Path.Combine(Path.GetTempPath(), $"zwijg-datei-{Guid.NewGuid():N}.tmp"),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);

    // Im Protokoll nur Anzahl und Größe, nie Dateinamen
    private static async Task WriteAuditAsync(HttpContext ctx, IAuditLog audit, string text, CancellationToken ct)
    {
        var user = ApiKeyMiddleware.GetUser(ctx);
        await audit.WriteAsync(new AuditEntry { User = user.Name, UserId = user.Id, Action = "files", Route = "Local", Reason = text }, ct);
    }
}
