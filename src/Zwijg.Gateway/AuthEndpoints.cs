using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Zwijg.Core.Audit;
using Zwijg.Gateway.Settings;

namespace Zwijg.Gateway;

public sealed record LoginInput(string Username, string Password, bool Remember);

public sealed record PasswordChangeInput(string? Current, string New);

// Bremst das Durchprobieren von Passwörtern: nach 5 Fehlversuchen kurze Sperre, die mit jedem weiteren wächst.
// Wer nach Ende der Sperre 15 Minuten lang nichts falsch macht, fängt wieder bei null an.
public sealed class LoginThrottle(TimeProvider? time = null)
{
    private const int FreeAttempts = 5;
    private static readonly TimeSpan MaxLock = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ForgetAfter = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CleanupEvery = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (int Fails, DateTimeOffset Until, DateTimeOffset Last)> _state = new();
    private long _lastCleanupTicks;

    public TimeSpan? LockedFor(string key)
    {
        var now = _time.GetUtcNow();
        if (_state.TryGetValue(key, out var s) && s.Until > now)
            return s.Until - now;
        return null;
    }

    public void Failed(string key)
    {
        var now = _time.GetUtcNow();
        Cleanup(now);

        _state.AddOrUpdate(key, _ => (1, DateTimeOffset.MinValue, now), (_, s) =>
        {
            var fails = Expired(s, now) ? 1 : s.Fails + 1;
            if (fails < FreeAttempts)
                return (fails, DateTimeOffset.MinValue, now);
            var wait = TimeSpan.FromSeconds(Math.Min(MaxLock.TotalSeconds, 30 * Math.Pow(2, fails - FreeAttempts)));
            return (fails, now + wait, now);
        });
    }

    public void Succeeded(string key) => _state.TryRemove(key, out _);

    private static bool Expired((int Fails, DateTimeOffset Until, DateTimeOffset Last) s, DateTimeOffset now) =>
        s.Until <= now && now - (s.Until > s.Last ? s.Until : s.Last) > ForgetAfter;

    // Sonst wächst die Liste mit jedem ausgedachten Benutzernamen und wird nie kleiner
    private void Cleanup(DateTimeOffset now)
    {
        // Bei parallelen Anfragen räumt nur eine auf
        var last = Interlocked.Read(ref _lastCleanupTicks);
        if (now.UtcTicks - last < CleanupEvery.Ticks
            || Interlocked.CompareExchange(ref _lastCleanupTicks, now.UtcTicks, last) != last)
            return;

        foreach (var (key, s) in _state)
        {
            if (Expired(s, now))
                _state.TryRemove(new KeyValuePair<string, (int, DateTimeOffset, DateTimeOffset)>(key, s));
        }
    }
}

public static class AuthEndpoints
{
    public const string UserIdClaim = "zwijg:uid";
    public const string StampClaim = "zwijg:stamp";

    private static readonly TimeSpan RememberFor = TimeSpan.FromDays(14);

    // Wie bei der Prüfung der Benutzernamen in den Einstellungen
    private const int MaxUsernameLength = 40;

    public static void MapAuth(this WebApplication app)
    {
        app.MapPost("/auth/login", async (LoginInput input, HttpContext ctx, SettingsStore store, LoginThrottle throttle, IAuditLog audit) =>
        {
            // Benutzernamen haben höchstens 40 Zeichen. Längere gar nicht erst merken oder protokollieren.
            var username = (input.Username ?? "").Trim();
            if (username.Length > MaxUsernameLength)
                return Results.Json(new { error = "Benutzername oder Passwort ist falsch" }, statusCode: 401);

            username = username.ToLowerInvariant();
            var shownName = username;
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
