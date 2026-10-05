using Zwijg.Core.Routing;
using Zwijg.Core.Security;

namespace Zwijg.Gateway.Settings;

// Alles, was in der Oberfläche geändert werden kann. Liegt als JSON im data Ordner.
public sealed class GatewaySettings
{
    // Format der Datei. Steigt, wenn sich etwas ändert, siehe SettingsStore.Migrations
    public int SchemaVersion { get; set; }

    // Beim Öffnen der Übersicht bei GitHub nachsehen, ob es eine neuere Version gibt, höchstens alle 12 Stunden
    public bool UpdateCheck { get; set; } = true;

    public List<Connection> Connections { get; set; } = [];

    // Welche Verbindung für sensible Anfragen (lokal) und welche für unkritische (Cloud) genutzt wird
    public string? LocalConnectionId { get; set; }
    public string? CloudConnectionId { get; set; }

    public RoutingOptions Routing { get; set; } = new();
    public InjectionOptions Injection { get; set; } = new();
    public bool StorePrompts { get; set; } = true;

    // Abmelden nach so vielen Minuten ohne Eingabe in der Oberfläche, 0 heißt nie.
    // Gegen offene Sitzungen am geteilten Rechner, etwa am Empfang.
    public int IdleLogoutMinutes { get; set; } = 30;
    public bool UseLocalLlmForNames { get; set; }

    // Eigene Listen der Praxis: immer ersetzen bzw. nie ersetzen
    public List<string> ExtraNames { get; set; } = [];
    public List<string> ExtraPlaces { get; set; } = [];
    public List<string> IgnoredWords { get; set; } = [];

    public List<UserRecord> Users { get; set; } = [];
    public List<Announcement> Announcements { get; set; } = [];

    public InstructionSettings Instructions { get; set; } = new();
    public List<ProtectionRule> ProtectionRules { get; set; } = [];
    public List<PromptTemplate> Templates { get; set; } = [];
    public HistoryOptions History { get; set; } = new();

    // Texterkennung für eingescannte PDFs und Fotos
    public OcrSettings Ocr { get; set; } = new();

    // Diktieren mit Whisper
    public DictationSettings Dictation { get; set; } = new();

    // Tägliche Sicherung des Datenordners
    public BackupSettings Backup { get; set; } = new();

    // Eigenes Aussehen: Praxisname, Logo, Farben
    public BrandingSettings Branding { get; set; } = new();

    // Dateien verschlüsseln, für den Versand an Empfänger ohne KIM
    public FilesSettings Files { get; set; } = new();
}

public sealed class Connection
{
    public string Id { get; set; } = NewId();
    public string Name { get; set; } = "";

    // Nur für die Anzeige, z.B. "claude", "openai", "ollama"
    public string Preset { get; set; } = "custom";

    // "OpenAI" (alles OpenAI kompatible), "Anthropic" oder "Echo"
    public string Type { get; set; } = ConnectionTypes.OpenAI;
    public string? BaseUrl { get; set; }

    // Verschlüsselt mit ASP.NET Data Protection
    public string? ApiKeyProtected { get; set; }
    public string? ApiKeyHint { get; set; }

    public string Model { get; set; } = "";
    public string? Effort { get; set; }
    public int TimeoutSeconds { get; set; } = 120;

    // Wie "kreativ" das Modell antwortet. Niedrig (0.2 bis 0.4) ist ruhiger und bleibt eher bei der Sprache.
    // Leer heißt: Standard des Anbieters. Wird bei Claude nicht verwendet.
    public double? Temperature { get; set; }

    // Läuft in der Praxis oder auf einem eigenen Server. Nur solche Verbindungen dürfen sensible Daten bekommen.
    public bool OnPremise { get; set; }

    public static string NewId() => Guid.NewGuid().ToString("N")[..10];
}

public static class ConnectionTypes
{
    public const string OpenAI = "OpenAI";
    public const string Anthropic = "Anthropic";
    public const string Echo = "Echo";

    public static bool IsValid(string type) => type is OpenAI or Anthropic or Echo;
}

public sealed class UserRecord
{
    public string Id { get; set; } = Connection.NewId();
    public string Name { get; set; } = "";
    public string KeyHash { get; set; } = "";

    // Die ersten Zeichen des Schlüssels, damit man ihn in der Liste wiedererkennt
    public string KeyHint { get; set; } = "";
    public bool Admin { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;

    // Gesperrte Benutzer bleiben erhalten, kommen aber nicht mehr rein
    public bool Active { get; set; } = true;

    // Rechte und Anzeige, alles vom Server durchgesetzt
    public bool ShowPreview { get; set; } = true;
    public bool CanUseDocuments { get; set; } = true;
    public bool CloudAllowed { get; set; } = true;

    // Anfragen pro Tag, leer heißt unbegrenzt
    public int? DailyLimit { get; set; }

    public string? Note { get; set; }

    // Zusätzliche Anweisung nur für diese Person, z.B. "Antworte immer mit Beispielen für den Empfang"
    public string? Instructions { get; set; }

    public List<string> DismissedAnnouncements { get; set; } = [];

    // Kurze Einführung beim ersten Anmelden gesehen oder übersprungen
    public bool TourSeen { get; set; }

    // Schlüssel stammt aus der Ausgabe beim Start und wurde noch nie benutzt. Dann gibt es bei jedem Start einen neuen,
    // damit er nicht verloren ist, wenn der erste Start abbricht. Die erste Anmeldung damit beendet das.
    public bool StartKey { get; set; }

    // Anmeldung mit Benutzername und Passwort. Ohne Passwort geht nur der Zugangsschlüssel.
    public string Username { get; set; } = "";
    public string? PasswordHash { get; set; }
    public bool MustChangePassword { get; set; }

    // Ändert sich bei Passwortwechsel oder Sperre, dann sind alle alten Sitzungen sofort ungültig
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

    public void NewSecurityStamp() => SecurityStamp = Guid.NewGuid().ToString("N");
}

public enum AnnouncementLevel
{
    Info,
    Warning,
    Critical
}

public enum AnnouncementAudience
{
    All,
    Admins,
    Staff,
    Selected
}

// Hinweis vom Admin an alle oder bestimmte Benutzer, z.B. "Bitte keine Befunde von Kindern hochladen"
public sealed class Announcement
{
    public string Id { get; set; } = Connection.NewId();
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public AnnouncementLevel Level { get; set; } = AnnouncementLevel.Info;
    public AnnouncementAudience Audience { get; set; } = AnnouncementAudience.All;
    public List<string> UserIds { get; set; } = [];
    public DateTimeOffset? StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public bool Dismissible { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public string CreatedBy { get; set; } = "";

    public bool IsVisibleTo(UserRecord user, DateTimeOffset now)
    {
        if (!Enabled || (StartsAt != null && now < StartsAt) || (EndsAt != null && now > EndsAt))
            return false;

        if (Dismissible && user.DismissedAnnouncements.Contains(Id))
            return false;

        return Audience switch
        {
            AnnouncementAudience.Admins => user.Admin,
            AnnouncementAudience.Staff => !user.Admin,
            AnnouncementAudience.Selected => UserIds.Contains(user.Id),
            _ => true
        };
    }
}

public sealed class SettingsException(string message) : Exception(message);

// Baustein für den Chat, z.B. "Arztbrief entwerfen"
public enum TemplateMode
{
    // Text landet im Eingabefeld und kann noch bearbeitet werden
    Insert,

    // Wird direkt im Hintergrund an die KI geschickt, im Chat steht nur eine kurze Karte
    Run
}

public sealed class PromptTemplate
{
    public string Id { get; set; } = Connection.NewId();
    public string Title { get; set; } = "";

    // Darf Variablen enthalten, z.B. {{Patient}} oder {{Absagetermin:termin}}
    public string Text { get; set; } = "";
    public TemplateMode Mode { get; set; } = TemplateMode.Insert;
    public bool Enabled { get; set; } = true;

    // Die Id landet bei allen Benutzern in der Oberfläche, also nur eigene Ids zulassen.
    // Fremde oder doppelte Ids bekommen eine neue. Gibt zurück, ob sich etwas geändert hat.
    public static bool FixIds(List<PromptTemplate> templates)
    {
        var changed = false;
        var ids = new HashSet<string>();
        foreach (var t in templates)
        {
            if (t.Id is not { Length: 10 } || !t.Id.All(char.IsAsciiHexDigitLower) || !ids.Add(t.Id))
            {
                t.Id = Connection.NewId();
                ids.Add(t.Id);
                changed = true;
            }
        }
        return changed;
    }
}

// Gespeicherte Unterhaltungen im Chat. Liegen verschlüsselt und nur für die Person selbst lesbar.
public sealed class HistoryOptions
{
    public bool Enabled { get; set; } = true;

    // So viele nicht angepinnte Unterhaltungen behält jede Person, ältere werden gelöscht
    public int MaxConversations { get; set; } = 20;

    // Nicht angepinnte Unterhaltungen werden nach so vielen Tagen ohne Änderung gelöscht
    public int RetentionDays { get; set; } = 30;

    // Angepinnte bleiben, bis man sie löst. Deshalb eine eigene Obergrenze.
    public int MaxPinned { get; set; } = 10;
}

public sealed class FilesSettings
{
    // Aus, bis ein Admin den Bereich einschaltet. Nicht jede Praxis braucht ihn.
    public bool Enabled { get; set; }

    // Alle Dateien eines Vorgangs zusammen
    public int MaxSizeMb { get; set; } = 500;

    // Vorschlag für den Namen der verschlüsselten Datei, auf Wunsch mit Datum am Ende
    public string DefaultName { get; set; } = "dokumente";

    public bool AppendDate { get; set; } = true;
}

public sealed class BrandingSettings
{
    public string? PracticeName { get; set; }

    // Farben als #RRGGBB, leer heißt Standard
    public string? Accent { get; set; }
    public string? GradientFrom { get; set; }
    public string? GradientTo { get; set; }
    public int GradientAngle { get; set; } = 135;

    // Datei im Ordner data/branding, die Version hängt an der Adresse, damit Browser ein neues Logo laden
    public string? LogoFile { get; set; }
    public string LogoVersion { get; set; } = "1";
}

public sealed class BackupSettings
{
    // Aus, bis Zielordner und Passwort eingetragen sind
    public bool Enabled { get; set; }

    // Am besten ein anderes Laufwerk, ein NAS oder eine USB Platte
    public string? Directory { get; set; }

    // Uhrzeit der täglichen Sicherung, Ortszeit des Servers
    public string Time { get; set; } = "02:00";

    // Passwort für die Verschlüsselung, mit Data Protection geschützt wie die API Schlüssel
    public string? PasswordProtected { get; set; }

    public int KeepDays { get; set; } = 7;
    public int KeepWeeks { get; set; } = 4;

    // Die Praxis sichert den Server anders, zum Beispiel komplett. Dann keine Warnung.
    public bool External { get; set; }
}

public sealed class DictationSettings
{
    public bool Enabled { get; set; } = true;

    // base, small oder large-v3-turbo-q5_0, siehe WhisperTranscriber.Models
    public string Model { get; set; } = "small";

    // Leer heißt: im Ordner data/models
    public string? ModelDir { get; set; }

    // 0 heißt: alle Kerne bis auf einen
    public int Threads { get; set; }

    public string Language { get; set; } = "de";
}

public sealed class OcrSettings
{
    public bool Enabled { get; set; } = true;

    // Leer lassen, dann sucht Zwijg Tesseract selbst
    public string? TesseractPath { get; set; }

    // Ordner mit den Sprachdaten (*.traineddata), leer heißt: die von Tesseract selbst
    public string? TessdataDir { get; set; }

    public string Languages { get; set; } = "deu+eng";

    public int MaxPages { get; set; } = 20;

    public Zwijg.Core.Ocr.OcrOptions ToOptions() => new(TesseractPath, TessdataDir, Languages, MaxPages);
}
