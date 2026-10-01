# Betrieb in der Praxis

## Zwijg für andere Rechner erreichbar machen

Das Paket hört anfangs nur auf den eigenen Rechner. Damit die anderen Arbeitsplätze es erreichen, beim Start die Adresse mitgeben:

```
Zwijg.Gateway.exe --urls http://0.0.0.0:5000
```

Dann ist Zwijg im Netz unter `http://NAME-DES-RECHNERS:5000` erreichbar. In der Firewall den Port 5000 für das Praxisnetz freigeben.

Damit Zwijg nach einem Neustart von selbst läuft:

- Windows: in der **Aufgabenplanung** eine Aufgabe anlegen, Auslöser **Beim Start**, Aktion das Programm mit `--urls http://0.0.0.0:5000`
- Linux: eine systemd Einheit, zum Beispiel `/etc/systemd/system/zwijg.service`:

```
[Unit]
Description=Zwijg
After=network.target

[Service]
WorkingDirectory=/opt/zwijg
ExecStart=/opt/zwijg/Zwijg.Gateway --urls http://0.0.0.0:5000
Restart=always
User=zwijg

[Install]
WantedBy=multi-user.target
```

Danach `sudo systemctl enable --now zwijg`.

## HTTPS

Ohne HTTPS gehen Passwörter lesbar durchs Netz. Am einfachsten ist [Caddy](https://caddyserver.com) als Vorschaltung. Eine Datei `Caddyfile`:

```
zwijg.praxis.local {
    tls internal
    reverse_proxy localhost:5000
}
```

`tls internal` erzeugt ein eigenes Zertifikat. Die Arbeitsplätze müssen der Zertifizierungsstelle von Caddy einmal vertrauen, sonst zeigt der Browser eine Warnung. Zwijg selbst dann wieder nur auf `localhost:5000` laufen lassen, also ohne `--urls`.

## Sicherung

Alles Wichtige liegt im Ordner `data`:

- `settings.json`: Einstellungen, Benutzer, Verbindungen
- `audit.db`: Protokoll, dazu `audit.db.key` (Schlüssel für die Echtheitsprüfung) und `audit.db.kopf` (Anzahl und letzter Eintrag). Die drei Dateien gehören zusammen.
- `history.db`: gespeicherte Unterhaltungen
- `keys`: die Schlüssel zum Entschlüsseln

Den ganzen Ordner regelmäßig sichern, am besten, während Zwijg kurz beendet ist.

Wichtig unter Windows: Die Schlüssel in `keys` sind zusätzlich an das Windows Konto gebunden, unter dem Zwijg läuft. Auf einem anderen Rechner oder unter einem anderen Konto lassen sie sich nicht öffnen. Dann müssen API Schlüssel der Anbieter neu eingetragen werden, und gespeicherte Unterhaltungen sind nicht mehr lesbar. Einstellungen, Benutzer und Protokoll bleiben erhalten, die Echtheit älterer Protokolleinträge lässt sich dann aber nicht mehr prüfen.

## Updates

Gibt es eine neue Version, sehen Admins nach der Anmeldung oben einen Hinweis. Schließt sie Sicherheitslücken, ist er rot und heißt **Sicherheitsupdate**. Ein Klick zeigt, was sich seit der installierten Version geändert hat, und die passenden Befehle. Was die installierte Version Neues hat, steht unter **Über Zwijg** und in der Datei `CHANGELOG.md`. Kurz gefasst:

- Windows Paket: im Zwijg Ordner `powershell -ExecutionPolicy Bypass -File update.ps1`
- Linux Paket: Zwijg beenden, dann `./update.sh`
- Docker: `docker compose pull && docker compose up -d`

Die Skripte sichern vorher den Ordner `data` und lassen ihn unangetastet.

## Protokoll prüfen

Unter **Protokoll** auf **Echtheit prüfen** klicken. Zwijg prüft dann die Hashkette und meldet, ob Einträge nachträglich verändert oder gelöscht wurden. Die Hashes sind mit einem eigenen Schlüssel gebildet, der nicht in der Datenbank liegt. Wer nur die Datenbank ändern kann, kann sie also nicht passend neu berechnen. Grenze: Wer eine ältere Kopie von `audit.db.kopf` hat, kann nach einem Neustart die neueren Einträge am Ende löschen, ohne dass es auffällt. Dagegen hilft nur eine Sicherung außerhalb des Servers.

Meldet die Prüfung einen Fehler, bleibt das so, bis das Protokoll neu beginnt: Zwijg beenden, `audit.db`, `audit.db.key` und `audit.db.kopf` zusammen in einen Archivordner verschieben und Zwijg wieder starten. Mit **CSV Export** lässt sich das Protokoll zum Beispiel für den Datenschutzbeauftragten ausgeben.
