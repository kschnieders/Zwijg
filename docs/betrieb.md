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

Ohne HTTPS gehen Passwörter lesbar durchs Netz, ebenso Prompts, Dokumente und Antworten, also auch Patientendaten vor dem Pseudonymisieren. Am einfachsten ist [Caddy](https://caddyserver.com) als Vorschaltung. Eine Datei `Caddyfile`:

```
zwijg.praxis.local {
    tls internal
    reverse_proxy localhost:5000
}
```

`tls internal` erzeugt ein eigenes Zertifikat. Die Arbeitsplätze müssen der Zertifizierungsstelle von Caddy einmal vertrauen, sonst zeigt der Browser eine Warnung. Zwijg selbst dann wieder nur auf `localhost:5000` laufen lassen, also ohne `--urls`.

Zwijg muss dann wissen, dass es hinter einem HTTPS Proxy läuft. In `appsettings.json` neben dem Programm:

```json
{
  "AllowedHosts": "zwijg.praxis.local;localhost",
  "Zwijg": {
    "BehindTlsProxy": true
  }
}
```

In Docker dasselbe als Umgebungsvariablen: `Zwijg__BehindTlsProxy: "true"` und `AllowedHosts: "zwijg.praxis.local;localhost"`.

- `BehindTlsProxy`: Das Anmeldecookie wird nur noch über HTTPS geschickt, und Zwijg wertet den Header `X-Forwarded-Proto` vom Proxy aus. Der Port von Zwijg darf dann nur für den Proxy erreichbar sein, nicht für das ganze Netz.
- `AllowedHosts`: Zwijg antwortet nur unter diesen Namen. `*` erlaubt jeden Namen.

Über HTTPS schickt Zwijg den Header `Strict-Transport-Security` (HSTS), außer für `localhost`. Browser rufen Zwijg unter diesem Namen dann 30 Tage lang nur noch per HTTPS auf und lassen Zertifikatswarnungen nicht mehr wegklicken. Deshalb zuerst dafür sorgen, dass alle Arbeitsplätze dem Zertifikat vertrauen.

## Sicherung

Alles Wichtige liegt im Ordner `data`:

- `settings.json`: Einstellungen, Benutzer, Verbindungen
- `audit.db`: Protokoll, dazu `audit.db.key` (Schlüssel für die Echtheitsprüfung) und `audit.db.kopf` (Anzahl und letzter Eintrag). Die drei Dateien gehören zusammen.
- `history.db`: gespeicherte Unterhaltungen
- `keys`: die Schlüssel zum Entschlüsseln

Den ganzen Ordner regelmäßig sichern. Am einfachsten macht das Zwijg selbst, siehe unten.

### Tägliche Sicherung einrichten

Als Admin links auf **Sicherung**:

1. **Täglich sichern** einschalten
2. **Zielordner** eintragen, am besten ein anderes Laufwerk, ein NAS oder eine USB Platte, zum Beispiel `E:\Zwijg-Sicherung` oder `/mnt/nas/zwijg`. Liegt er auf derselben Festplatte wie Zwijg, zeigt Zwijg eine Warnung.
3. **Uhrzeit** wählen, Standard ist 02:00. War der Rechner dann aus, holt Zwijg die Sicherung beim nächsten Start nach.
4. **Passwort** festlegen, mindestens 10 Zeichen, und **aufschreiben**. Ohne dieses Passwort lässt sich keine Sicherung zurückspielen.
5. **Speichern**. Die erste Sicherung startet wenige Minuten später, oder gleich mit **Jetzt sichern**.

Jede Sicherung ist eine einzelne verschlüsselte Datei, zum Beispiel `zwijg-sicherung-2026-10-04-020000.zwijg`. Darin sind Einstellungen, Verlauf, Protokoll und Schlüssel, aber nicht die Sprachmodelle fürs Diktieren. Es bleiben die letzten 7 Tage und eine pro Woche aus den letzten 4 Wochen.

Ist keine Sicherung eingerichtet, schlägt eine fehl oder ist die letzte älter als zwei Tage, sehen Admins nach der Anmeldung einen Hinweis. Wer den Server anders sichert, etwa komplett, schaltet **Wir sichern den Server anders** ein. Dann muss der Ordner `data` dabei sein.

### Sicherung zurückspielen

Zwijg beenden und im Ordner von Zwijg ausführen:

```
Zwijg.Gateway.exe --zurueckspielen E:\Zwijg-Sicherung\zwijg-sicherung-2026-10-04-020000.zwijg
```

Unter Linux `./Zwijg.Gateway`. In Docker erst `docker compose stop zwijg`, dann `docker compose run --rm -v /mnt/nas/zwijg:/sicherung zwijg --zurueckspielen /sicherung/zwijg-sicherung-2026-10-04-020000.zwijg`. Das Beenden ist dort besonders wichtig, weil Zwijg im Container nicht erkennen kann, ob ein anderer Container noch läuft. Zwijg fragt nach dem Passwort und prüft die Datei, bevor es etwas ändert. Der bisherige Stand kommt in einen Ordner `data-vor-wiederherstellung-<Datum>` daneben, es geht also nichts verloren. Danach Zwijg wieder starten.

Wichtig unter Windows: Die Schlüssel in `keys` sind zusätzlich an das Windows Konto gebunden, unter dem Zwijg läuft. Auf einem anderen Rechner oder unter einem anderen Konto lassen sie sich nicht öffnen. Dann müssen API Schlüssel der Anbieter neu eingetragen werden, und gespeicherte Unterhaltungen sind nicht mehr lesbar. Einstellungen, Benutzer und Protokoll bleiben erhalten, die Echtheit älterer Protokolleinträge lässt sich dann aber nicht mehr prüfen.

### Sicherungen enthalten die Schlüssel

Wer eine Sicherung des Ordners `data` hat, hat auch die Schlüssel. Unter Linux und in Docker liegen sie ohne Zertifikat (siehe unten) im Klartext. Damit lassen sich API Schlüssel und gespeicherte Unterhaltungen entschlüsseln und Anmeldungen fälschen. Das betrifft auch `audit.db.key`: Dieser Schlüssel für die Echtheitsprüfung des Protokolls ist mit den Schlüsseln in `keys` verschlüsselt. Wer beides hat, kann Protokolleinträge ändern und die Hashes passend neu berechnen.

- Die Sicherungen von Zwijg selbst sind mit dem Passwort verschlüsselt. Eigene Sicherungen des Ordners `data` verschlüsselt ablegen, zum Beispiel in einem verschlüsselten Archiv oder auf einem verschlüsselten Laufwerk, und nicht frei im Praxisnetz.
- Das gilt auch für die Sicherungen der Update Skripte, `data-sicherung-*.tar.gz` (Linux) und `data-sicherung-*.zip` (Windows) im Zwijg Ordner. Nicht mehr gebrauchte Sicherungen löschen.
- Ein Zertifikat für die Schlüssel getrennt von den Daten aufbewahren. Wer nur die Datensicherung hat, kann dann nichts entschlüsseln.

### Schlüssel mit einem Zertifikat schützen

Unter Linux und in Docker gibt es kein DPAPI. Zwijg kann die Schlüssel dort mit einem eigenen Zertifikat verschlüsseln. Ohne Zertifikat schreibt Zwijg beim Start eine Warnung ins Log. Unter Windows geht es auch, dann statt DPAPI; Vorteil: Die Schlüssel lassen sich mit dem Zertifikat auf einen anderen Rechner mitnehmen.

Zertifikat einmal anlegen (Passwort selbst wählen):

```
openssl req -x509 -newkey rsa:3072 -nodes -days 3650 -subj "/CN=Zwijg Schluessel" -keyout schluessel.pem -out zertifikat.pem
openssl pkcs12 -export -inkey schluessel.pem -in zertifikat.pem -out zwijg-schluessel.pfx
rm schluessel.pem zertifikat.pem
```

Dann Zwijg mitgeben, zum Beispiel als Umgebungsvariablen:

```
Zwijg__KeyProtection__CertificatePath=/etc/zwijg/zwijg-schluessel.pfx
Zwijg__KeyProtection__CertificatePassword=...
```

In Docker die Datei nur lesend einbinden, zum Beispiel `- ./zwijg-schluessel.pfx:/run/zwijg/zwijg-schluessel.pfx:ro` unter `volumes`. Der Container läuft als Benutzer 1654, die Datei muss für ihn lesbar sein.

Wichtig:

- Die `.pfx` Datei und das Passwort **nicht** im Ordner `data` ablegen und getrennt sichern. Ohne sie lassen sich die neuen Schlüssel nicht mehr öffnen, mit den Folgen wie oben unter Windows beschrieben. Auch alle Anmeldungen enden.
- Ist der Pfad falsch oder das Passwort falsch, startet Zwijg nicht.
- Das Zertifikat später nicht gegen ein anderes tauschen. Zwijg startet dann zwar und legt einen neuen Schlüssel an, aber ein Wechsel wirkt wie ein Verlust: Alle Anmeldungen enden, API Schlüssel müssen neu eingetragen werden, gespeicherte Unterhaltungen sind nicht mehr lesbar, und die Echtheit älterer Protokolleinträge lässt sich nicht mehr prüfen. Deshalb eine lange Laufzeit wählen (`-days 3650` oben) und die `.pfx` Datei dauerhaft aufbewahren.
- Beim ersten Start mit Zertifikat legt Zwijg sofort einen neuen, verschlüsselten Schlüssel an. Alles, was ab dann verschlüsselt wird, ist geschützt. Die alten Schlüssel bleiben, wie sie sind, damit ältere Daten lesbar bleiben. Gespeicherte API Schlüssel und ältere Unterhaltungen sind deshalb weiter mit einem ungeschützten Schlüssel verschlüsselt. API Schlüssel der Anbieter einmal neu eintragen, dann sind auch sie geschützt. Wer von Anfang an alles geschützt haben will, richtet das Zertifikat vor dem ersten Start ein.

## Darstellung

Unter **Darstellung** legen Admins Praxisname, Logo (PNG, JPG oder WebP), eine eigene Akzentfarbe und einen Farbverlauf für die Anmeldeseite fest. Das Logo liegt in `data/branding` und ist in der Sicherung enthalten. Der Hinweis "mit Zwijg" und **Über Zwijg** bleiben, so verlangt es die Namensnennung in der Lizenz.

## Updates

Gibt es eine neue Version, sehen Admins nach der Anmeldung oben einen Hinweis. Schließt sie Sicherheitslücken, ist er rot und heißt **Sicherheitsupdate**. Ein Klick zeigt, was sich seit der installierten Version geändert hat, und die passenden Befehle. Was die installierte Version Neues hat, steht unter **Über Zwijg** und in der Datei `CHANGELOG.md`. Kurz gefasst:

- Windows Paket: im Zwijg Ordner `powershell -ExecutionPolicy Bypass -File update.ps1`
- Linux Paket: Zwijg beenden, dann `./update.sh`
- Docker: `docker compose pull && docker compose up -d`

### Was beim Update passiert

Die Skripte für Windows und Linux gehen so vor:

1. Neue Version herunterladen und mit der Prüfsumme von GitHub vergleichen. Stimmt sie nicht, bleibt alles, wie es ist.
2. Programm und Daten zusammen nach `update-sicherungen` kopieren. Die Sprachmodelle für das Diktieren kommen nicht mit, die ändert ein Update nicht.
3. Neue Version einspielen und einmal zur Probe starten.
4. Startet sie nicht, kommt automatisch der alte Stand zurück, Programm und Daten.

Die letzten drei Sicherungen bleiben liegen. Ohne Internet auf dem Server geht das Update auch mit einer vorher heruntergeladenen Datei: `update.ps1 -Paket C:\Downloads\zwijg-1.2.0-win-x64.zip` oder `./update.sh --paket zwijg-1.2.0-linux-x64.zip`.

Zwijg selbst sichert zusätzlich beim ersten Start einer neuen Version seine Daten nach `data/sicherungen`, bevor es Einstellungen und Datenbanken umstellt. Das gilt für jede Installationsart, auch für Docker.

### Zurück zur alten Version

Macht die neue Version später Probleme, Zwijg beenden und:

- Windows: `powershell -ExecutionPolicy Bypass -File update.ps1 -Zurueck`
- Linux: `./update.sh --zurueck`

Das holt Programm und Daten vom Stand vor dem letzten Update zurück. Eine bestimmte Sicherung geht mit `-Sicherung <Name>` beziehungsweise `--sicherung <Name>`, die Namen stehen im Ordner `update-sicherungen`. Der Stand vor dem Zurückgehen wird ebenfalls gesichert, das lässt sich also auch wieder rückgängig machen.

Wichtig: Daten aus der Zeit nach dem Update, etwa neue Protokolleinträge oder Unterhaltungen, sind danach weg. Die ältere Version kann Daten der neueren nicht lesen.

Docker:

1. In `docker-compose.yml` die alte Version eintragen, zum Beispiel `image: ghcr.io/kschnieders/zwijg:1.1.0` statt `latest`
2. Die passende Sicherung heraussuchen: `docker compose run --rm --no-deps --entrypoint ls zwijg /app/data/sicherungen`. Sie heißt zum Beispiel `20261005-091500-vor-1.2.0-von-1.1.0`, also Datum, neue und alte Version
3. Daten zurückholen, `NAME` durch den Ordner aus Schritt 2 ersetzen:

   ```
   docker compose run --rm --no-deps --entrypoint sh zwijg -c 'cd /app/data && find . -mindepth 1 -maxdepth 1 ! -name sicherungen ! -name models -exec rm -rf {} + && cp -a sicherungen/NAME/. .'
   ```
4. `docker compose up -d`

### Downloads prüfen

Zu jedem Release Paket gibt es einen signierten Herkunftsnachweis von GitHub. Er belegt, dass die ZIP Datei im Release Workflow dieses Repositorys gebaut wurde und unverändert ist. Prüfen mit der [GitHub CLI](https://cli.github.com):

```
gh attestation verify zwijg-1.0.0-linux-x64.zip --repo kschnieders/zwijg
```

Dasselbe für das Docker Image:

```
gh attestation verify oci://ghcr.io/kschnieders/zwijg:1.0.0 --repo kschnieders/zwijg
```

Das geht für Releases, die nach dieser Änderung erschienen sind.

### Umstieg auf eine Version mit Docker ohne root, HSTS und Schlüsselschutz

Diese Version ändert das Verhalten bestehender Installationen:

- **Docker läuft nicht mehr als root**, sondern als Benutzer 1654. Ein bestehendes Volume gehört noch root, dann kann Zwijg dort nichts speichern und bricht den Start mit einem Hinweis auf diesen Befehl ab. Einmalig nach dem `docker compose pull` und vor dem `docker compose up -d`:

  ```
  docker compose run --rm --no-deps --user root --entrypoint chown zwijg -R 1654 /app/data
  ```

  Mit `docker run` statt Compose: `docker run --rm --user root --entrypoint chown -v zwijg-data:/app/data ghcr.io/kschnieders/zwijg:latest -R 1654 /app/data`
- **Ollama** hat in `docker-compose.yml` jetzt eine feste Version statt `latest`. Die geladenen Modelle bleiben im Volume erhalten.
- **HSTS:** Wird Zwijg schon per HTTPS aufgerufen, schicken die Antworten jetzt `Strict-Transport-Security`, siehe [HTTPS](#https). Hinter einem Proxy erst, wenn `BehindTlsProxy` gesetzt ist.
- **Linux und Docker:** Beim Start steht eine Warnung im Log, solange die Schlüssel ohne Zertifikat liegen, siehe [Schlüssel mit einem Zertifikat schützen](#schlüssel-mit-einem-zertifikat-schützen). Es läuft aber alles wie bisher.
- **Neue Einstellungen** `BehindTlsProxy` und `KeyProtection` sind aus, bis man sie setzt.

## Protokoll prüfen

Unter **Protokoll** auf **Echtheit prüfen** klicken. Zwijg prüft dann die Hashkette und meldet, ob Einträge nachträglich verändert oder gelöscht wurden. Die Hashes sind mit einem eigenen Schlüssel gebildet, der nicht in der Datenbank liegt. Wer nur die Datenbank ändern kann, kann sie also nicht passend neu berechnen. Grenze: Wer eine ältere Kopie von `audit.db.kopf` hat, kann nach einem Neustart die neueren Einträge am Ende löschen, ohne dass es auffällt. Dagegen hilft nur eine Sicherung außerhalb des Servers.

Meldet die Prüfung einen Fehler, bleibt das so, bis das Protokoll neu beginnt: Zwijg beenden, `audit.db`, `audit.db.key` und `audit.db.kopf` zusammen in einen Archivordner verschieben und Zwijg wieder starten. Mit **CSV Export** lässt sich das Protokoll zum Beispiel für den Datenschutzbeauftragten ausgeben.
