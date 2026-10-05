using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Zwijg.Gateway.Settings;

namespace Zwijg.Gateway;

// Abmelden nach Inaktivität auch auf dem Server. Der Browser meldet zwar selbst ab, aber nur, solange Zwijg offen ist.
// Schließt jemand den Tab und lässt den Browser offen, bliebe das Cookie sonst bis zu 12 Stunden gültig.
public static class SessionIdle
{
    // Die Abfrage alle 60 Sekunden schickt diesen Kopf mit, sie zählt nicht als Eingabe
    public const string BackgroundHeader = "X-Zwijg-Hintergrund";
    private const string SeenKey = "zwijg.aktiv";

    public static async Task ValidateAsync(CookieValidatePrincipalContext ctx)
    {
        var services = ctx.HttpContext.RequestServices;
        var minutes = services.GetRequiredService<SettingsStore>().Current.IdleLogoutMinutes;
        if (minutes <= 0)
            return;

        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var seen = ctx.Properties.Items.TryGetValue(SeenKey, out var value)
                   && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : ctx.Properties.IssuedUtc ?? now;

        if (now - seen > TimeSpan.FromMinutes(minutes))
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        // Höchstens einmal pro Minute das Cookie erneuern, nur bei echter Nutzung
        if (!ctx.HttpContext.Request.Headers.ContainsKey(BackgroundHeader) && now - seen >= TimeSpan.FromMinutes(1))
        {
            ctx.Properties.Items[SeenKey] = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            ctx.ShouldRenew = true;
        }
    }
}
