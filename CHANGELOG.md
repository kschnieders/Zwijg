# Änderungen

Was sich in den einzelnen Versionen von Zwijg geändert hat. Die neueste Version steht oben.

Ein neuer Abschnitt entsteht mit dem Workflow **Changelog vorbereiten** aus den gemergten Pull Requests. Steht ein Abschnitt **Sicherheit** drin, zeigt Zwijg das Update als Sicherheitsupdate an.

## 0.2.0 (2026-10-01)

### Sicherheit

- Nur geschützte Felder an den Anbieter weitergeben (#6, @Titoki90)
- Platzhalter aus der Eingabe reservieren, in Adressen nicht zurücksetzen (#7, @Titoki90)
- Protokoll fälschungssicherer: HMAC-Hashkette mit Anker, Fehler beim Protokollieren sichtbar, CSV ohne Formeln (#9, @Titoki90)
- Admin-Härtung: Schlüssel nur für die eigene Adresse, saubere Vorlagen-Ids, Tageslimit nach Benutzer-Id (#10, @Titoki90)
- Im Zweifel lokal: Nur Cloud, zu langsame Schutzregeln und Dokument-Tags (#11, @Titoki90)
- Oberfläche: Abmelden räumt auf, Schlüssel nur für den Tab, Vorschau in der richtigen Reihenfolge (#16, @Titoki90)
- Betrieb absichern: Schlüssel mit Zertifikat, HTTPS Proxy, Docker ohne root, Herkunftsnachweise (#17, @Titoki90)
- Erkennung gegen bösartige und riesige Texte absichern (ReDoS, OCR-Speicher, Login-Bremse) (#18, @Titoki90)

### Neu

- Text schützen: geschützte Fassung für andere Programme (#4, @kschnieders)
- Texterkennung für eingescannte PDFs und Fotos (#5, @kschnieders)
- Diktieren mit Whisper (#12, @kschnieders)
- Changelog und Hinweise auf neue Versionen (#13, @kschnieders)

### Verbesserungen

- Erkennung gegen Schreibweisen, Formate und Überlappungen härten (#8, @Titoki90)
- Zwischenbilder der Texterkennung aufräumen, PDF-Prüfung als Heuristik beschreiben (#15, @Titoki90)
- Updates mit Sicherung, Probestart und Rückweg (#21, @kschnieders)

## 0.1.0 (2026-09-29)

### Neu

- Erste Version: Pseudonymisierung, Schutz vor Prompt Injection, Protokoll mit Hashkette, Weiterleitung lokal oder Cloud und Weboberfläche
