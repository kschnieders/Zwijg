# Erste Schritte

## Starten

Das Paket für Windows oder Linux aus den [Releases](https://github.com/kschnieders/zwijg/releases/latest) entpacken und `Zwijg.Gateway.exe` (Windows) oder `./Zwijg.Gateway` (Linux) starten.

Beim ersten Start steht unten in der Konsole ein Kasten wie dieser:

```
============================================================
  Zwijg ist bereit:   http://localhost:5000
  Startschlüssel:     zw_...
============================================================
```

Den Schlüssel kopieren. Solange sich niemand damit angemeldet hat, gibt es bei jedem Start einen neuen, der alte gilt dann nicht mehr.

Steht stattdessen **Port 5000 ist schon belegt** da, läuft Zwijg schon oder ein anderes Programm nutzt den Port. Dann in der Datei `appsettings.json` eine Zeile `"Urls": "http://localhost:5050",` ergänzen und Zwijg unter dieser Adresse öffnen.

## Anmelden

1. http://localhost:5000 im Browser öffnen
2. Auf **Stattdessen mit Zugangsschlüssel anmelden** klicken
3. Den Startschlüssel einfügen und **Anmelden**

Beim ersten Anmelden zeigt eine kurze Einführung die wichtigsten Stellen. Mit **Überspringen** oder Esc geht es direkt los. Wieder ansehen geht unten links über das Konto, **Einführung ansehen**. Jeder neue Benutzer sieht sie einmal.

## Eigenes Passwort festlegen

1. Unten links auf den eigenen Namen klicken
2. **Passwort festlegen** wählen
3. Mindestens 10 Zeichen, ein kurzer Satz ist gut zu merken

Ab jetzt geht die Anmeldung mit Benutzername und Passwort. Wer keine anderen Programme über die Schnittstelle anbindet, kann unter **Benutzer** beim eigenen Konto den Schlüssel entfernen. Dann geht die Anmeldung nur noch mit Passwort.

## Kolleginnen und Kollegen anlegen

1. Links auf **Benutzer**, dann **Benutzer anlegen**
2. Name und Benutzername eintragen, bei Passwort auf **Vorschlagen** klicken
3. **Beim ersten Anmelden ändern** angehakt lassen
4. Rechte festlegen: Wer nicht in die Cloud darf, bekommt **Cloud erlaubt** aus
5. **Speichern** und das Startpasswort weitergeben

## Und dann

Als Nächstes eine KI verbinden, am besten eine lokale: [Lokale KI einbinden](lokale-ki.md).
