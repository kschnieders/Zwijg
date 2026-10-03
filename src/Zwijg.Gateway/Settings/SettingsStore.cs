using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Zwijg.Core.Security;

namespace Zwijg.Gateway.Settings;

// Hält die aktuellen Einstellungen im Speicher und schreibt Änderungen in eine Datei.
// Beim ersten Start wird aus appsettings.json eine Grundkonfiguration erzeugt.
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly object _lock = new();
    private GatewaySettings _current;

    // Startschlüssel für die Ausgabe beim Start, null wenn keiner nötig ist. Steht nur im Speicher.
    public string? StartKeyToShow { get; private set; }

    public SettingsStore(IOptions<GatewayOptions> bootstrap, IDataProtectionProvider dataProtection, ILogger<SettingsStore> logger)
    {
        _path = Path.GetFullPath(bootstrap.Value.SettingsPath);
        _protector = dataProtection.CreateProtector("Zwijg.ConnectionKeys");

        if (File.Exists(_path))
        {
            _current = JsonSerializer.Deserialize<GatewaySettings>(File.ReadAllText(_path), Json) ?? new();

            // Ältere Einstellungen kennen noch keine Benutzernamen, dann einmal vergeben und speichern
            var changed = Migrate(_current, _path, logger);
            changed |= FillUsernames(_current);
            changed |= RemovePublicKeys(_current, logger);
            changed |= RenewStartKey(_current);
            if (changed)
                Save(_current);
        }
        else
        {
            _current = Seed(bootstrap.Value);
            _current.SchemaVersion = CurrentSchema;
            FillUsernames(_current);
            RemovePublicKeys(_current, logger);
            RenewStartKey(_current);
            Save(_current);
            logger.LogInformation("Einstellungen neu angelegt unter {Path}", _path);
        }
    }

    // Jede Änderung am Format der Datei bekommt hier einen Schritt, nie einen alten Schritt ändern.
    // Beim Start laufen alle Schritte ab der Version der Datei, vorher wird sie gesichert.
    // Neue Felder mit Standardwert brauchen keinen Schritt, die füllt der JSON Leser von selbst.
    private static readonly Action<GatewaySettings>[] Migrations =
    [
        s => FillUsernames(s), // 0 auf 1: Benutzernamen für die Anmeldung mit Passwort
        s => PromptTemplate.FixIds(s.Templates), // 1 auf 2: fremde Vorlagen Ids aus älteren Versionen ersetzen
        s => s.Users.ForEach(u => u.TourSeen = true), // 2 auf 3: wer schon da ist, kennt Zwijg und bekommt keine Einführung
    ];

    public static int CurrentSchema => Migrations.Length;

    private static bool Migrate(GatewaySettings s, string path, ILogger logger)
    {
        if (s.SchemaVersion > CurrentSchema)
        {
            // Nach einem Downgrade: lesen geht, unbekannte Felder gehen beim nächsten Speichern aber verloren
            logger.LogWarning("Die Einstellungen stammen von einer neueren Zwijg Version (Format {File}, erwartet {Current}). " +
                "Bitte Zwijg aktualisieren oder die Sicherung der alten Einstellungen zurückspielen.", s.SchemaVersion, CurrentSchema);
            return false;
        }

        if (s.SchemaVersion == CurrentSchema)
            return false;

        var backup = $"{path}.v{s.SchemaVersion}.bak";
        File.Copy(path, backup, overwrite: true);

        for (var v = s.SchemaVersion; v < CurrentSchema; v++)
            Migrations[v](s);

        logger.LogInformation("Einstellungen von Format {Old} auf {New} umgestellt, Sicherung unter {Backup}", s.SchemaVersion, CurrentSchema, backup);
        s.SchemaVersion = CurrentSchema;
        return true;
    }

    // Diese Schlüssel standen früher in appsettings.Development.json und damit öffentlich im Repo.
    // Wer das Repo kennt, käme damit rein. Deshalb werden sie beim Start immer gesperrt.
    private static readonly string[] PublicKeys = ["dev-praxis-admin", "dev-praxis-empfang"];

    private static bool RemovePublicKeys(GatewaySettings s, ILogger logger)
    {
        var hashes = PublicKeys.Select(HashKey).ToHashSet();
        var affected = s.Users.Where(u => hashes.Contains(u.KeyHash)).ToList();
        foreach (var u in affected)
        {
            u.KeyHash = "";
            u.KeyHint = "";
            logger.LogWarning("Der Zugangsschlüssel von {User} war öffentlich bekannt und wurde gesperrt", u.Name);
        }

        // Kommt danach kein Admin mehr rein, bekommt der erste einen neuen Startschlüssel
        if (affected.Count > 0 && !s.Users.Any(u => u.Admin && CanLogIn(u)))
            s.Users.First(u => u.Admin && u.Active).StartKey = true;

        return affected.Count > 0;
    }

    // Solange ein Startschlüssel nie benutzt wurde, bei jedem Start einen neuen vergeben und anzeigen.
    // Der alte gilt dann nicht mehr. So geht er nicht verloren, wenn der erste Start abbricht.
    private bool RenewStartKey(GatewaySettings s)
    {
        var admin = s.Users.FirstOrDefault(u => u.StartKey && u.Active);
        if (admin == null)
            return false;

        var key = NewUserKey();
        admin.KeyHash = HashKey(key);
        admin.KeyHint = Hint(key);
        StartKeyToShow = key;
        return true;
    }

    // Erste Anmeldung mit dem Startschlüssel: ab jetzt bleibt er, wie er ist
    public void StartKeyUsed(string userId) => Update(s =>
    {
        if (s.Users.FirstOrDefault(u => u.Id == userId) is { StartKey: true } u)
            u.StartKey = false;
    });

    // Anmelden geht mit Zugangsschlüssel oder Passwort
    public static bool CanLogIn(UserRecord u) => u.Active && (u.KeyHash != "" || u.PasswordHash != null);

    // Nie verändern, nur lesen. Änderungen laufen über Update.
    public GatewaySettings Current => _current;

    public int Version { get; private set; }

    public GatewaySettings Update(Action<GatewaySettings> change)
    {
        lock (_lock)
        {
            var copy = JsonSerializer.Deserialize<GatewaySettings>(JsonSerializer.Serialize(_current, Json), Json)!;
            change(copy);
            Validate(copy);
            Save(copy);
            _current = copy;
            Version++;
            return copy;
        }
    }

    public string? Protect(string? plain) => string.IsNullOrEmpty(plain) ? null : _protector.Protect(plain);

    public string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
            return null;

        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch (CryptographicException)
        {
            // Schlüsselring verloren, dann muss der API Schlüssel neu eingegeben werden
            return null;
        }
    }

    public static string HashKey(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static string NewUserKey() =>
        "zw_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static string Hint(string key) => key.Length <= 8 ? key[..Math.Min(3, key.Length)] + "..." : key[..6] + "...";

    private static void Validate(GatewaySettings s)
    {
        if (s.Users.Count(u => u.Admin && u.Active) == 0)
            throw new SettingsException("Es muss mindestens einen aktiven Admin geben, sonst kommt niemand mehr in die Verwaltung");

        if (!s.Users.Any(u => u.Admin && CanLogIn(u)))
            throw new SettingsException("Mindestens ein aktiver Admin braucht ein Passwort oder einen Zugangsschlüssel");

        if (s.Users.Any(u => string.IsNullOrWhiteSpace(u.Name)))
            throw new SettingsException("Jeder Benutzer braucht einen Namen");

        if (Zwijg.Core.Speech.WhisperTranscriber.Models.All(m => m.Id != s.Dictation.Model))
            throw new SettingsException("Unbekanntes Sprachmodell");
        if (s.Dictation.Threads is < 0 or > 64)
            throw new SettingsException("Die Zahl der Rechenkerne muss zwischen 0 und 64 liegen");
        if (!System.Text.RegularExpressions.Regex.IsMatch(s.Dictation.Language ?? "", "^[a-z]{2}$"))
            throw new SettingsException("Sprache bitte mit zwei Buchstaben angeben, zum Beispiel de");

        if (s.Ocr.MaxPages is < 1 or > 200)
            throw new SettingsException("Die Seitenzahl für die Texterkennung muss zwischen 1 und 200 liegen");
        if (!System.Text.RegularExpressions.Regex.IsMatch(s.Ocr.Languages ?? "", @"^[a-z_]{3,20}(\+[a-z_]{3,20}){0,5}$"))
            throw new SettingsException("Sprachen bitte wie deu oder deu+eng angeben");

        if (!Enum.IsDefined(s.Routing.Mode) || !Enum.IsDefined(s.Routing.CloudMaxSensitivity) || !Enum.IsDefined(s.Injection.Action))
            throw new SettingsException("Unbekannter Wert bei der Weiterleitung oder beim Manipulationsschutz");
        if (s.ProtectionRules.Any(r => !Enum.IsDefined(r.Action)))
            throw new SettingsException("Unbekannte Aktion bei einer Schutzregel");

        if (s.Users.Any(u => u.DailyLimit is < 1 or > 100_000))
            throw new SettingsException("Das Tageslimit muss zwischen 1 und 100000 liegen");

        foreach (var a in s.Announcements)
        {
            if (string.IsNullOrWhiteSpace(a.Title) && string.IsNullOrWhiteSpace(a.Message))
                throw new SettingsException("Eine Benachrichtigung braucht einen Titel oder Text");
            if (a.Message.Length > 2000 || a.Title.Length > 200)
                throw new SettingsException("Benachrichtigung ist zu lang");
            if (a.StartsAt != null && a.EndsAt != null && a.EndsAt < a.StartsAt)
                throw new SettingsException("Das Ende einer Benachrichtigung liegt vor dem Start");

            // Gelöschte Benutzer aus der Zielgruppe entfernen
            a.UserIds.RemoveAll(id => s.Users.All(u => u.Id != id));
        }

        foreach (var r in s.ProtectionRules)
        {
            if (string.IsNullOrWhiteSpace(r.Name))
                throw new SettingsException("Jede Schutzregel braucht einen Namen");
            if (r.Patterns.Length > 5000)
                throw new SettingsException($"Schutzregel {r.Name}: zu viele Begriffe");
            if (r.Action == RuleAction.Replace && !string.IsNullOrWhiteSpace(r.Label) && !RuleEngine.LabelPattern.IsMatch(r.Label))
                throw new SettingsException($"Schutzregel {r.Name}: Der Platzhalter darf nur aus 2 bis 20 Großbuchstaben bestehen, z.B. PATIENTENNR");

            try
            {
                RuleEngine.Build(r);
            }
            catch (ArgumentException ex)
            {
                throw new SettingsException($"Schutzregel {r.Name}: {ex.Message}");
            }
        }


        var h = s.History;
        if (h.MaxConversations is < 1 or > 500 || h.RetentionDays is < 1 or > 3650 || h.MaxPinned is < 0 or > 100)
            throw new SettingsException("Verlauf: bis 500 Unterhaltungen, 1 bis 3650 Tage und bis 100 angepinnte");
        var i = s.Instructions;
        if (i.CustomText.Length > InstructionComposer.MaxCustomLength || i.ResponseFooter.Length > 500)
            throw new SettingsException("Die Anweisungen sind zu lang");
        if (s.Users.Any(u => u.Instructions?.Length > 1000))
            throw new SettingsException("Persönliche Anweisungen dürfen höchstens 1000 Zeichen haben");

        if (s.Templates.Any(t => string.IsNullOrWhiteSpace(t.Title) || string.IsNullOrWhiteSpace(t.Text)))
            throw new SettingsException("Jede Vorlage braucht einen Titel und einen Text");
        if (s.Templates.Any(t => t.Title.Length > 60 || t.Text.Length > 4000))
            throw new SettingsException("Vorlage ist zu lang (Titel bis 60, Text bis 4000 Zeichen)");
        foreach (var t in s.Templates)
        {
            if (TemplateVariables.Validate(t.Text) is { } problem)
                throw new SettingsException($"Vorlage {t.Title}: {problem}");
        }

        // Weggeklickte Hinweise, die es nicht mehr gibt, vergessen
        var ids = s.Announcements.Select(a => a.Id).ToHashSet();
        foreach (var u in s.Users)
            u.DismissedAnnouncements.RemoveAll(id => !ids.Contains(id));

        if (s.Users.Select(u => u.Name.Trim().ToLowerInvariant()).Distinct().Count() != s.Users.Count)
            throw new SettingsException("Namen müssen eindeutig sein");

        FillUsernames(s);
        foreach (var u in s.Users)
        {
            if (!Passwords.UsernamePattern.IsMatch(u.Username))
                throw new SettingsException($"Benutzername \"{u.Username}\" ist ungültig. Erlaubt sind Kleinbuchstaben, Ziffern, Punkt, Minus und Unterstrich, 2 bis 40 Zeichen.");
        }
        if (s.Users.Select(u => u.Username).Distinct().Count() != s.Users.Count)
            throw new SettingsException("Benutzernamen für die Anmeldung müssen eindeutig sein");

        foreach (var c in s.Connections)
        {
            if (string.IsNullOrWhiteSpace(c.Name))
                throw new SettingsException("Jede Verbindung braucht einen Namen");
            if (!ConnectionTypes.IsValid(c.Type))
                throw new SettingsException($"Unbekannter Verbindungstyp {c.Type}");
            if (c.Type == ConnectionTypes.OpenAI && !Uri.TryCreate(c.BaseUrl, UriKind.Absolute, out _))
                throw new SettingsException($"Verbindung {c.Name}: Adresse fehlt oder ist ungültig");
            if (c.Type == ConnectionTypes.Anthropic && c.OnPremise)
                throw new SettingsException("Claude läuft immer in der Cloud und kann nicht als lokal markiert werden");
            if (c.Temperature is < 0 or > 2)
                throw new SettingsException($"Verbindung {c.Name}: Die Temperatur muss zwischen 0 und 2 liegen");
            if (c.TimeoutSeconds is < 5 or > 1800)
                throw new SettingsException("Timeout muss zwischen 5 und 1800 Sekunden liegen");
        }

        var local = s.Connections.FirstOrDefault(c => c.Id == s.LocalConnectionId);
        if (s.LocalConnectionId != null && local == null)
            s.LocalConnectionId = null;
        if (local is { OnPremise: false })
            throw new SettingsException($"{local.Name} läuft nicht lokal und darf keine sensiblen Daten bekommen");

        if (s.CloudConnectionId != null && s.Connections.All(c => c.Id != s.CloudConnectionId))
            s.CloudConnectionId = null;
    }

    // Leere Benutzernamen aus dem Namen ableiten, bei Dopplungen mit Zahl dahinter
    private static bool FillUsernames(GatewaySettings s)
    {
        var changed = false;
        foreach (var u in s.Users)
        {
            u.Username = u.Username.Trim().ToLowerInvariant();
            if (u.Username.Length > 0)
                continue;

            var name = Passwords.UsernameFrom(u.Name);
            var candidate = name;
            for (var i = 2; s.Users.Any(o => o != u && o.Username == candidate); i++)
                candidate = $"{name}{i}";
            u.Username = candidate;
            changed = true;
        }
        return changed;
    }

    private void Save(GatewaySettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        // Erst in eine temporäre Datei schreiben, damit bei einem Absturz nichts kaputt geht
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, _path, overwrite: true);
    }

    private GatewaySettings Seed(GatewayOptions o)
    {
        var s = new GatewaySettings
        {
            Routing = o.Routing,
            Injection = o.Injection,
            StorePrompts = o.Audit.StorePrompts,
            UseLocalLlmForNames = o.Pseudonymization.UseLocalLlmForNames,
        };

        if (FromBootstrap(o.Providers.Local, "Lokal", onPremise: true) is { } local)
        {
            s.Connections.Add(local);
            s.LocalConnectionId = local.Id;
        }

        if (FromBootstrap(o.Providers.Cloud, "Cloud", onPremise: false) is { } cloud)
        {
            s.Connections.Add(cloud);
            s.CloudConnectionId = cloud.Id;
        }

        foreach (var k in o.ApiKeys.Where(k => !string.IsNullOrEmpty(k.Key)))
        {
            s.Users.Add(new UserRecord { Name = k.User, KeyHash = HashKey(k.Key), KeyHint = Hint(k.Key), Admin = k.Admin });
        }

        // Ohne Benutzer käme niemand in den Adminbereich. Dann bekommt ein neuer Admin einen Startschlüssel,
        // den Zwijg beim Start anzeigt, siehe RenewStartKey.
        if (s.Users.Count(u => u.Admin) == 0)
            s.Users.Add(new UserRecord { Name = "admin", Admin = true, StartKey = true });

        return s;
    }

    private static bool IsOllama(ProviderOptions p) => p.BaseUrl?.Contains(":11434") == true;

    private Connection? FromBootstrap(ProviderOptions? p, string fallbackName, bool onPremise)
    {
        if (p == null || (string.IsNullOrWhiteSpace(p.BaseUrl) && !p.IsEcho && !p.IsAnthropic))
            return null;

        var type = p.IsEcho ? ConnectionTypes.Echo : p.IsAnthropic ? ConnectionTypes.Anthropic : ConnectionTypes.OpenAI;
        return new Connection
        {
            Name = p.IsEcho ? $"Echo ({fallbackName.ToLowerInvariant()}, nur Test)" : p.IsAnthropic ? "Claude" : IsOllama(p) ? "Ollama" : fallbackName,
            Preset = p.IsEcho ? "echo" : p.IsAnthropic ? "claude" : IsOllama(p) ? "ollama" : "custom",
            Type = type,
            BaseUrl = p.BaseUrl,
            ApiKeyProtected = Protect(p.ApiKey),
            ApiKeyHint = string.IsNullOrEmpty(p.ApiKey) ? null : Hint(p.ApiKey),
            Model = p.Model,
            Effort = p.Effort,
            TimeoutSeconds = p.TimeoutSeconds,
            OnPremise = onPremise && type != ConnectionTypes.Anthropic,
        };
    }
}
