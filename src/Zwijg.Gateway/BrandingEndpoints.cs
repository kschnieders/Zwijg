using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Zwijg.Core.Audit;
using Zwijg.Gateway.Settings;

namespace Zwijg.Gateway;

public sealed record BrandingInput(string? PracticeName, string? Accent, string? GradientFrom, string? GradientTo, int? GradientAngle);

// Eigenes Aussehen: Praxisname, Logo, Akzentfarbe und Hintergrund der Anmeldung.
// Die Namensnennung von Zwijg bleibt dabei sichtbar ("mit Zwijg", Über Zwijg), so verlangt es die NOTICE.
public static partial class BrandingEndpoints
{
    public const int MaxLogoBytes = 512 * 1024;

    // Nur Rasterbilder. SVG kann Skripte enthalten und ist deshalb nicht erlaubt.
    private static readonly (string Ext, string Type, Func<byte[], bool> Is)[] Formats =
    [
        ("png", "image/png", b => b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A),
        ("jpg", "image/jpeg", b => b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF),
        ("webp", "image/webp", b => b.Length > 12 && b.AsSpan(0, 4).SequenceEqual("RIFF"u8) && b.AsSpan(8, 4).SequenceEqual("WEBP"u8)),
    ];

    public static void MapBranding(this WebApplication app)
    {
        // Ohne Anmeldung, die Anmeldeseite braucht es schon
        app.MapGet("/branding", (SettingsStore store) =>
        {
            var b = store.Current.Branding;
            return Results.Ok(new
            {
                name = b.PracticeName,
                accent = b.Accent,
                gradient = b.GradientFrom != null && b.GradientTo != null ? new { from = b.GradientFrom, to = b.GradientTo, angle = b.GradientAngle } : null,
                logo = b.LogoFile != null ? $"/branding/logo?v={b.LogoVersion}" : null,
            });
        });

        app.MapGet("/branding/logo", (SettingsStore store, IOptions<GatewayOptions> options) =>
        {
            var b = store.Current.Branding;
            // Nur die eigenen Dateinamen "logo.png" usw., nie einen Pfad aus den Einstellungen
            var format = Formats.FirstOrDefault(f => b.LogoFile == "logo." + f.Ext);
            var path = format.Ext != null ? Path.Combine(Folder(options.Value), b.LogoFile!) : null;
            if (path == null || !File.Exists(path))
                return Results.NotFound();
            return Results.File(File.ReadAllBytes(path), format.Type);
        });

        var admin = app.MapGroup("/admin/branding");

        admin.MapPut("", async (BrandingInput input, HttpContext ctx, SettingsStore store, IAuditLog audit) =>
        {
            var name = string.IsNullOrWhiteSpace(input.PracticeName) ? null : input.PracticeName.Trim();
            if (name?.Length > 60)
                return Results.BadRequest(new { error = "Der Praxisname darf höchstens 60 Zeichen haben" });
            foreach (var color in new[] { input.Accent, input.GradientFrom, input.GradientTo })
            {
                if (color != null && !Color().IsMatch(color))
                    return Results.BadRequest(new { error = "Farben bitte als #RRGGBB angeben, zum Beispiel #1f6f5c" });
            }
            if ((input.GradientFrom == null) != (input.GradientTo == null))
                return Results.BadRequest(new { error = "Für den Farbverlauf bitte beide Farben angeben" });
            if (input.GradientAngle is < 0 or > 360)
                return Results.BadRequest(new { error = "Der Winkel muss zwischen 0 und 360 Grad liegen" });

            return await AdminSettingsEndpoints.ChangeAsync(ctx, store, audit, "Darstellung geändert", s =>
            {
                s.Branding.PracticeName = name;
                s.Branding.Accent = input.Accent?.ToLowerInvariant();
                s.Branding.GradientFrom = input.GradientFrom?.ToLowerInvariant();
                s.Branding.GradientTo = input.GradientTo?.ToLowerInvariant();
                s.Branding.GradientAngle = input.GradientAngle ?? 135;
            });
        });

        admin.MapPost("/logo", async (HttpContext ctx, SettingsStore store, IOptions<GatewayOptions> options, IAuditLog audit, CancellationToken ct) =>
        {
            if (!ctx.Request.HasFormContentType || (await ctx.Request.ReadFormAsync(ct)).Files["file"] is not { } file)
                return Results.BadRequest(new { error = "Bitte ein Bild senden" });
            if (file.Length > MaxLogoBytes)
                return Results.BadRequest(new { error = "Das Logo darf höchstens 512 KB groß sein" });

            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            var bytes = buffer.ToArray();

            // Am Inhalt erkennen, nicht an Endung oder Angabe des Browsers
            var format = Formats.FirstOrDefault(f => f.Is(bytes));
            if (format.Ext == null)
                return Results.BadRequest(new { error = "Bitte ein Bild als PNG, JPG oder WebP hochladen. SVG geht aus Sicherheitsgründen nicht." });

            var folder = Folder(options.Value);
            Directory.CreateDirectory(folder);
            foreach (var old in Directory.GetFiles(folder, "logo.*"))
                File.Delete(old);
            await File.WriteAllBytesAsync(Path.Combine(folder, "logo." + format.Ext), bytes, ct);

            return await AdminSettingsEndpoints.ChangeAsync(ctx, store, audit, "Logo hochgeladen", s =>
            {
                s.Branding.LogoFile = "logo." + format.Ext;
                s.Branding.LogoVersion = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            });
        }).DisableAntiforgery();

        admin.MapDelete("/logo", async (HttpContext ctx, SettingsStore store, IOptions<GatewayOptions> options, IAuditLog audit) =>
        {
            var folder = Folder(options.Value);
            if (Directory.Exists(folder))
            {
                foreach (var old in Directory.GetFiles(folder, "logo.*"))
                    File.Delete(old);
            }
            return await AdminSettingsEndpoints.ChangeAsync(ctx, store, audit, "Logo entfernt", s => s.Branding.LogoFile = null);
        });
    }

    // Liegt im Datenordner, damit es Updates und Sicherungen übersteht
    public static string Folder(GatewayOptions options) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.SettingsPath))!, "branding");

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex Color();
}
