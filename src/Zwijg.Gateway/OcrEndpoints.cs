using Zwijg.Core.Audit;
using Zwijg.Core.Ocr;
using Zwijg.Gateway.Settings;

namespace Zwijg.Gateway;

public sealed record OcrInput(bool Enabled, string? TesseractPath, string? TessdataDir, string? Languages, int? MaxPages);

// Einstellungen und Status der Texterkennung für Admins
public static class OcrEndpoints
{
    public static void MapOcr(this WebApplication app)
    {
        var admin = app.MapGroup("/admin");

        admin.MapGet("/ocr", async (SettingsStore store, CancellationToken ct) =>
        {
            var o = store.Current.Ocr;
            var status = await TesseractOcr.CheckAsync(o.ToOptions(), ct);
            return Results.Ok(new { settings = o, status });
        });

        admin.MapPut("/ocr", (OcrInput input, HttpContext ctx, SettingsStore store, IAuditLog audit) =>
            AdminSettingsEndpoints.ChangeAsync(ctx, store, audit,
                input.Enabled ? "Texterkennung eingestellt" : "Texterkennung ausgeschaltet",
                s => s.Ocr = new OcrSettings
                {
                    Enabled = input.Enabled,
                    TesseractPath = Clean(input.TesseractPath),
                    TessdataDir = Clean(input.TessdataDir),
                    Languages = Clean(input.Languages)?.ToLowerInvariant() ?? "deu+eng",
                    MaxPages = input.MaxPages ?? 20,
                }));
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"');
}
