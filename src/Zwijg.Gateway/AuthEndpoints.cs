using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Zwijg.Core.Audit;
using Zwijg.Gateway.Settings;

namespace Zwijg.Gateway;

public sealed record LoginInput(string Username, string Password, bool Remember);

public sealed record PasswordChangeInput(string? Current, string New);

// Bremst das Durchprobieren von Passwörtern: nach 5 Fehlversuchen kurze Sperre, die mit jedem weiteren wächst
public sealed class LoginThrottle
{
    private const int FreeAttempts = 5;
    private static readonly TimeSpan MaxLock = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, (int Fails, DateTimeOffset Until)> _state = new();

    public TimeSpan? LockedFor(string key)
    {
        if (_state.TryGetValue(key, out var s) && s.Until > DateTimeOffset.UtcNow)
            return s.Until - DateTimeOffset.UtcNow;
        return null;
    }

    public void Failed(string key)
    {
        _state.AddOrUpdate(key, _ => (1, DateTimeOffset.MinValue), (_, s) =>
        {
            var fails = s.Fails + 1;
            if (fails < FreeAttempts)
                return (fails, DateTimeOffset.MinValue);
            var wait = TimeSpan.FromSeconds(Math.Min(MaxLock.TotalSeconds, 30 * Math.Pow(2, fails - FreeAttempts)));
            return (fails, DateTimeOffset.UtcNow + wait);
        });
    }

    public void Succeeded(string key) => _state.TryRemove(key, out _);
}

public static class AuthEndpoints
{
    public const string UserIdClaim = "zwijg:uid";
    public const string StampClaim = "zwijg:stamp";

    private static readonly TimeSpan RememberFor = TimeSpan.FromDays(14);

    public static void MapAuth(this WebApplication app)
    {
        app.MapPost("/auth/login", async (LoginInput input, HttpContext ctx, SettingsStore store, LoginThrottle throttle, IAuditLog audit) =>
        {
            var username = (input.Username ?? "").Trim().ToLowerInvariant();
            var shownName = username.Length > 40 ? username[..40] : username;
            var key = $"{username}|{ctx.Connection.RemoteIpAddress}";

            if (throttle.LockedFor(key) is { } wait)
                return Results.Json(new { error = $"Zu viele Fehlversuche. Bitte in {Math.Ceiling(wait.TotalSeconds)} Sekunden erneut versuchen." }, statusCode: 429);

            var user = store.Current.Users.FirstOrDefault(u => u.Username == username);

            // Auch bei unbekanntem Benutzer einmal prüfen, damit die Antwortzeit nichts verrät
            var ok = Passwords.Verify(input.Password ?? "", user?.PasswordHash) && user != null;

            if (!ok)
            {
                throttle.Failed(key);
                await audit.WriteAsync(new AuditEntry { User = shownName.Length > 0 ? shownName : "(leer)", Action = "login", Blocked = true, Reason = "Anmeldung fehlgeschlagen" });
                return Results.Json(new { error = "Benutzername oder Passwort ist falsch" }, statusCode: 401);
            }

            if (!user!.Active)
            {
                await audit.WriteAsync(new AuditEntry { User = user.Name, Action = "login", Blocked = true, Reason = "Anmeldung abgelehnt: Zugang gesperrt" });
                return Results.Json(new { error = "Dein Zugang ist gesperrt. Bitte wende dich an die Verwaltung.", locked = true }, statusCode: 403);
            }

            throttle.Succeeded(key);
            await SignInAsync(ctx, user, input.Remember);
            await audit.WriteAsync(new AuditEntry { User = user.Name, Action = "login", Reason = input.Remember ? "Anmeldung, angemeldet bleiben" : "Anmeldung" });

            return Results.Ok(new { ok = true, mustChangePassword = user.MustChangePassword });
        });

        app.MapPost("/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new { ok = true });
        });

        // Eigenes Passwort ändern. Läuft unter /v1, also mit normaler Anmeldung.
        app.MapPost("/v1/account/password", async (PasswordChangeInput input, HttpContext ctx, SettingsStore store, IAuditLog audit) =>
        {
            var me = ApiKeyMiddleware.GetUser(ctx);
            var record = store.Current.Users.FirstOrDefault(u => u.Id == me.Id);
            if (record == null)
                return Results.Unauthorized();

            // Beim ersten Anmelden mit Startpasswort braucht es das alte nicht noch einmal
            if (record.PasswordHash != null && !record.MustChangePassword && !Passwords.Verify(input.Current ?? "", record.PasswordHash))
                return Results.BadRequest(new { error = "Das aktuelle Passwort stimmt nicht" });

            if (Passwords.Problem(input.New, record.Username) is { } problem)
                return Results.BadRequest(new { error = problem });
            if (record.PasswordHash != null && Passwords.Verify(input.New, record.PasswordHash))
                return Results.BadRequest(new { error = "Das neue Passwort muss sich vom alten unterscheiden" });

            var updated = store.Update(s =>
            {
                var u = s.Users.First(x => x.Id == me.Id);
                u.PasswordHash = Passwords.Hash(input.New);
                u.MustChangePassword = false;
                u.NewSecurityStamp();
            }).Users.First(u => u.Id == me.Id);

            // Andere Sitzungen sind damit abgemeldet, die eigene bekommt den neuen Stempel
            if (me.ViaSession)
                await SignInAsync(ctx, updated, remember: ctx.User.FindFirst("zwijg:remember")?.Value == "1");

            await audit.WriteAsync(new AuditEntry { User = me.Name, Action = "login", Reason = "Eigenes Passwort geändert" });
            return Results.Ok(new { ok = true });
        });
    }

    private static Task SignInAsync(HttpContext ctx, UserRecord user, bool remember)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(UserIdClaim, user.Id),
            new Claim(StampClaim, user.SecurityStamp),
            new Claim("zwijg:remember", remember ? "1" : "0"),
        ], CookieAuthenticationDefaults.AuthenticationScheme);

        var props = new AuthenticationProperties
        {
            IsPersistent = remember,
            ExpiresUtc = remember ? DateTimeOffset.UtcNow + RememberFor : null,
            AllowRefresh = true,
        };

        return ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), props);
    }
}
