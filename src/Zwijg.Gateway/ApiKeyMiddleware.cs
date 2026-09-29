using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Zwijg.Gateway.Settings;

namespace Zwijg.Gateway;

public sealed record GatewayUser(
    string Id, string Name, bool IsAdmin, bool ShowPreview, bool CanUseDocuments, bool CloudAllowed, int? DailyLimit,
    bool MustChangePassword = false, bool ViaSession = false);

// Zwei Wege hinein: Zugangsschlüssel (für Programme und als Notzugang) oder die Sitzung nach
// Anmeldung mit Benutzername und Passwort. Beides führt zum selben Benutzer mit denselben Rechten.
public sealed class ApiKeyMiddleware(RequestDelegate next, SettingsStore store, ILogger<ApiKeyMiddleware> logger)
{
    private const string UserKey = "zwijg.user";

    // Nur diese Pfade gehen noch, solange das Startpasswort nicht geändert ist
    private static readonly string[] AllowedBeforePasswordChange = ["/v1/me", "/v1/account/password"];

    // Platzhalter für gesperrte Benutzer, damit sie eine klare Meldung bekommen
    private static readonly GatewayUser Locked = new("", "", false, false, false, false, 0);

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        var isAdminPath = path.StartsWithSegments("/admin");

        if (!path.StartsWithSegments("/v1") && !isAdminPath)
        {
            await next(context);
            return;
        }

        var key = ReadKey(context.Request);
        var user = key != null ? FindUserByKey(key) : await FindUserBySessionAsync(context);

        if (user == null)
        {
            // Nur die Länge loggen, nie den Schlüssel selbst. Steuerzeichen raus, sonst ließen sich
            // mit einem Zeilenumbruch im Pfad falsche Logzeilen einschleusen.
            if (key != null)
                logger.LogWarning("Anmeldung abgelehnt für {Path}, Schlüssel mit {Length} Zeichen", ForLog(path), key.Length);
            await Deny(context, 401, new { error = key != null ? "Ungültiger Zugangsschlüssel" : "Bitte anmelden" });
            return;
        }

        if (ReferenceEquals(user, Locked))
        {
            await Deny(context, 403, new { error = "Dein Zugang ist gesperrt. Bitte wende dich an die Verwaltung.", locked = true });
            return;
        }

        if (user.ViaSession)
        {
            // Schutz vor fremden Webseiten: ändernde Anfragen mit Sitzung brauchen unsere Kennung.
            // Fremde Seiten können diesen Header nicht setzen.
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
                && context.Request.Headers["X-Requested-With"] != "zwijg")
            {
                await Deny(context, 403, new { error = "Anfrage ohne Kennung abgelehnt" });
                return;
            }

            if (user.MustChangePassword && !AllowedBeforePasswordChange.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase)))
            {
                await Deny(context, 403, new { error = "Bitte zuerst ein eigenes Passwort festlegen", mustChangePassword = true });
                return;
            }
        }

        if (isAdminPath && !user.IsAdmin)
        {
            await Deny(context, 403, new { error = "Nur für Admins" });
            return;
        }

        context.Items[UserKey] = user;
        await next(context);
    }

    public static GatewayUser GetUser(HttpContext context) => (GatewayUser)context.Items[UserKey]!;

    private static async Task Deny(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(body);
    }

    private static string ForLog(PathString path)
    {
        var text = path.Value ?? "";
        text = new string(text.Select(c => char.IsControl(c) ? '_' : c).ToArray());
        return text.Length <= 200 ? text : text[..200];
    }

    private static string? ReadKey(HttpRequest request)
    {
        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth["Bearer ".Length..].Trim();

        var header = request.Headers["X-Api-Key"].ToString();
        return string.IsNullOrWhiteSpace(header) ? null : header.Trim();
    }

    private GatewayUser? FindUserByKey(string key)
    {
        var given = Encoding.ASCII.GetBytes(SettingsStore.HashKey(key));
        foreach (var u in store.Current.Users)
        {
            if (CryptographicOperations.FixedTimeEquals(given, Encoding.ASCII.GetBytes(u.KeyHash)))
                return u.Active ? ToGatewayUser(u, viaSession: false) : Locked;
        }

        return null;
    }

    // Die Sitzung gilt nur, solange Benutzer und Stempel noch passen. Nach Sperre oder
    // Passwortwechsel ist sie sofort ungültig, auch wenn das Cookie noch Tage liefe.
    private async Task<GatewayUser?> FindUserBySessionAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return null;

        var id = context.User.FindFirstValue(AuthEndpoints.UserIdClaim);
        var stamp = context.User.FindFirstValue(AuthEndpoints.StampClaim);
        var u = store.Current.Users.FirstOrDefault(x => x.Id == id);

        if (u == null || u.SecurityStamp != stamp)
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return null;
        }

        return u.Active ? ToGatewayUser(u, viaSession: true) : Locked;
    }

    private static GatewayUser ToGatewayUser(UserRecord u, bool viaSession) =>
        new(u.Id, u.Name, u.Admin, u.ShowPreview, u.CanUseDocuments, u.CloudAllowed, u.DailyLimit, u.MustChangePassword, viaSession);
}
