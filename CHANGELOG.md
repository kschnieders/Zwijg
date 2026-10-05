# Änderungen

Was sich in den einzelnen Versionen von Zwijg geändert hat. Die neueste Version steht oben.

Ein neuer Abschnitt entsteht mit dem Workflow **Changelog vorbereiten** aus den gemergten Pull Requests. Steht ein Abschnitt **Sicherheit** drin, zeigt Zwijg das Update als Sicherheitsupdate an.

## 0.4.0 (2026-10-05)

### Wichtig beim Update

- **Automatisch abmelden:** Nach 30 Minuten ohne Eingabe meldet Zwijg ab, auch bei "Angemeldet bleiben" und wenn der Browser Tab geschlossen war. Admins stellen die Zeit unter **Benutzer** ein, 0 heißt nie.
- **Dateien verschlüsseln** ist neu und zuerst ausgeschaltet. Admins schalten es unter **Verwaltung**, **Dateien** ein.

### Sicherheit

- Die tägliche Sicherung legt ihre unverschlüsselten Zwischenstände nur noch in einem Ordner ab, den andere Benutzer auf dem Rechner nicht lesen können. Reste nach einem Absturz werden beim nächsten Start gelöscht. (#35, @kschnieders)

### Neu

- **Patientenfeld:** Name und Geburtsdatum einmal eintragen, Zwijg versteckt sie in der ganzen Unterhaltung, auch wenn nur der Nachname oder ein anderes Datumsformat vorkommt. Das Feld wird verschlüsselt gespeichert. (#29, @kschnieders)
- **Dateien verschlüsseln:** Röntgenbilder, Befunde und andere Dateien mit Kennwort als AES-256 ZIP verschlüsseln, für Empfänger ohne KIM. Der Empfänger öffnet sie mit 7-Zip, WinRAR oder Keka. (#34, #37, @kschnieders)
- **Darstellung:** Praxisname, Logo und eigene Farben, auch auf der Anmeldeseite. (#33, @kschnieders)
- **Automatisch abmelden** nach einer einstellbaren Zeit ohne Eingabe. (#32, #35, @kschnieders)

### Verbesserungen

- Das Patientenfeld gibt es auch bei **Dokument** und **Text schützen**. (#32, @kschnieders)
- Die Fenster **Über Zwijg** und **Version und Updates** sind breiter und besser lesbar. (#37, @kschnieders)

## 0.3.0 (2026-10-03)

### Wichtig beim Update

- **Einmal Strg+F5 im Browser:** Ältere Versionen haben dem Browser erlaubt, die Oberfläche zu speichern. Damit sicher die neue erscheint, nach dem Update einmal Strg+F5 drücken. Ab 0.3.0 holt der Browser neue Fassungen von selbst.
- **Sicherung einrichten:** Nach dem Update sehen Admins den Hinweis **Keine Sicherung eingerichtet**. Unter **Sicherung** einen Zielordner und ein Passwort festlegen, am besten auf einem anderen Laufwerk oder NAS.
- Die kurze Einführung sehen nur neue Benutzer. Wer schon da ist, startet sie bei Bedarf über das Konto unten links.

### Neu

- **Antwort-Check:** Zwijg markiert Wirkstoffe, Dosierungen, Einnahmeschemata und Laborwerte, die nur in der Antwort stehen und nicht in der Frage. So fallen erfundene Angaben auf. In anderen Programmen steht der Hinweis als Text unter der Antwort. (#23, @kschnieders)
- **Tägliche Sicherung:** Einstellungen, Verlauf, Protokoll und Schlüssel werden einmal am Tag verschlüsselt in einen Ordner nach Wahl gesichert, verpasste Sicherungen werden nachgeholt. Zurückspielen mit `Zwijg.Gateway --zurueckspielen <Datei>`. (#27, @kschnieders)
- **Einführung:** Beim ersten Anmelden zeigt eine kurze Tour die wichtigsten Stellen, überspringen geht immer. (#25, @kschnieders)

### Verbesserungen

- **Text schützen:** Eigene Stellen wie eine Firma oder ein Projekt lassen sich wie im Chat markieren und verstecken. (#24, @kschnieders)

### Fehler behoben

- Ist der Port schon belegt, kommt eine kurze, verständliche Meldung statt einer langen Fehlerausgabe. Der Startschlüssel geht nicht mehr verloren, wenn der erste Start abbricht, und steht gut sichtbar unter den Logzeilen. (#26, @kschnieders)

## 0.2.0 (2026-10-02)

### Wichtig beim Update

- **Docker:** Zwijg läuft im Container nicht mehr als root. Bestehende Installationen brauchen nach `docker compose pull` einmalig `docker compose run --rm --no-deps --user root --entrypoint chown zwijg -R 1654 /app/data` und danach `docker compose up -d`. Ohne diesen Schritt startet Zwijg nicht und nennt genau diesen Befehl.
- **Anmeldung mit Zugangsschlüssel** gilt nur noch, bis der Browser Tab geschlossen wird. Die Anmeldung mit Passwort bleibt, wie sie ist.
- **Weiterleitung Nur Cloud:** Anfragen, die lokal bleiben müssten, etwa ein Name zusammen mit einer Diagnose, werden jetzt blockiert statt in die Cloud geschickt.
- Das Update von 0.1.0 läuft noch mit dem alten Skript. Es sichert die Daten, startet die neue Version aber nicht zur Probe. Ab 0.2.0 gibt es den Probestart und den Rückweg mit `-Zurueck`.

### Sicherheit

- An den Anbieter gehen nur noch Rolle und geschützter Text. Vorher konnten Patientendaten in Zusatzfeldern wie Werkzeugaufrufen ungeschützt mitgehen. (#6, @Titoki90)
- Stehen schon Platzhalter wie [NAME_1] im Text, vergibt Zwijg keine Nummer doppelt. Das verhindert, dass Patienten verwechselt werden. In Links setzt Zwijg keine echten Daten mehr ein. (#7, @Titoki90)
- Das Protokoll ist mit einem geheimen Schlüssel gegen nachträgliche Änderungen geschützt, auch gelöschte Einträge fallen auf. Der CSV Export führt keine Formeln mehr aus. (#9, @Titoki90)
- Der gespeicherte API Schlüssel einer Verbindung geht nur noch an die eingetragene Adresse. Das Tageslimit zählt pro Benutzer, auch nach einer Umbenennung. (#10, @Titoki90)
- Im Zweifel bleibt eine Anfrage lokal oder wird blockiert, auch bei zu langsamen Schutzregeln und manipulierten Dokumenten. (#11, @Titoki90)
- Abmelden räumt die Oberfläche komplett auf. Am geteilten Rechner sieht die nächste Person nichts mehr von der vorigen. (#16, @Titoki90)
- Schlüssel lassen sich mit einem Zertifikat schützen, Zwijg läuft sauber hinter einem HTTPS Proxy und in Docker ohne root. Downloads haben einen Herkunftsnachweis. (#17, @Titoki90)
- Riesige oder absichtlich verschachtelte Texte und Scans können Zwijg nicht mehr lahmlegen. Wiederholte Anmeldeversuche werden gebremst. (#18, @Titoki90)

### Neu

- **Text schützen:** geschützte Fassung zum Kopieren in andere Programme, zum Beispiel ChatGPT im Browser, und die Antwort mit den echten Daten zurück (#4, @kschnieders)
- **Texterkennung:** eingescannte oder gefaxte PDFs und Fotos von Befunden werden auf dem eigenen Rechner gelesen und genauso geschützt (#5, @kschnieders)
- **Diktieren:** im Chat sprechen statt tippen, die Spracherkennung läuft auf dem eigenen Rechner (#12, @kschnieders)
- **Was ist neu:** Admins sehen neue Versionen und Sicherheitsupdates gleich nach der Anmeldung, mit allen Änderungen seit der installierten Version (#13, @kschnieders)

### Verbesserungen

- Erkannt werden jetzt auch Namen in Großbuchstaben, Telefonnummern mit Ländervorwahl, mehr Schreibweisen von Geburtsdaten und klein geschriebene IBAN (#8, @Titoki90)
- Zwischenbilder der Texterkennung werden zuverlässig gelöscht (#15, @Titoki90)
- Updates sichern Programm und Daten, starten die neue Version zur Probe und gehen bei einem Fehler automatisch zurück (#21, @kschnieders)

## 0.1.0 (2026-09-29)

### Neu

- Erste Version: Pseudonymisierung, Schutz vor Prompt Injection, Protokoll mit Hashkette, Weiterleitung lokal oder Cloud und Weboberfläche
