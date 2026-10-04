# Infoblatt Datenschutz

Für Datenschutzbeauftragte und Praxisleitung. Beschreibt, welche Daten Zwijg verarbeitet, wo sie liegen und wie sie geschützt sind. Die Angaben lassen sich in das Verzeichnis der Verarbeitungstätigkeiten übernehmen. Das ist keine Rechtsberatung.

## Zweck

Zwijg ist eine Zwischenstelle zwischen den Mitarbeitern der Praxis und KI Modellen. Es ersetzt Namen, Geburtsdaten, Versichertennummern, Adressen und ähnliche Angaben durch Platzhalter, bevor eine Anfrage an ein Modell geht, und setzt sie in der Antwort wieder ein. Anfragen mit Gesundheitsdaten zu einer Person bleiben bei einem lokalen Modell.

## Wer verantwortlich ist

- Zwijg läuft auf einem Rechner der Praxis. Die Praxis ist Verantwortliche. Der Entwickler von Zwijg sieht keine Daten und verarbeitet nichts im Auftrag.
- Wird ein Cloud Anbieter eingebunden (zum Beispiel Anthropic, OpenAI, Mistral), gehen dorthin nur pseudonymisierte Texte. Mit dem Anbieter ist trotzdem ein Vertrag zur Auftragsverarbeitung nötig.
- Ein lokales Modell, etwa über Ollama, läuft ebenfalls bei der Praxis.

## Welche Daten wo liegen

Alles liegt im Ordner `data` auf dem Rechner, auf dem Zwijg läuft.

| Daten | Inhalt | Schutz | Wie lange |
|---|---|---|---|
| `settings.json` | Einstellungen, Benutzer, Regeln, Verbindungen | Passwörter nur als PBKDF2 Hash, API Schlüssel verschlüsselt | bis zur Änderung |
| `history.db` | gespeicherte Unterhaltungen mit Klartext, Patientenfeld, Titel | verschlüsselt (AES, Schlüssel in `keys`) | 30 Tage ohne Änderung, höchstens 20 je Person, angepinnte bis zum Lösen. Einstellbar oder abschaltbar unter Regeln, Verlauf |
| `audit.db` | Protokoll: wer, wann, Route, Modell, erkannte Arten von Daten | Fragen nur pseudonymisiert (abschaltbar), Hashkette mit geheimem Schlüssel gegen Änderungen | wird nicht automatisch gelöscht |
| `keys` | Schlüssel für die Verschlüsselung | unter Windows an das Benutzerkonto gebunden (DPAPI), sonst mit Zertifikat | dauerhaft |
| Sicherungen | Kopie von allem oben | mit Passwort verschlüsselt (AES-256-GCM) | 7 Tage und 4 Wochen |

Nicht gespeichert werden: Aufnahmen beim Diktieren, hochgeladene Dokumente und Zwischenbilder der Texterkennung. Sie werden nur im Arbeitsspeicher oder kurz in einem temporären Ordner verarbeitet.

## Was an Dritte geht

- **KI Modell:** nur der pseudonymisierte Text. Was an welches Modell geht, steht im Protokoll.
- **GitHub:** die Update Prüfung fragt die Liste der Versionen ab, ohne Daten aus der Praxis. Abschaltbar.
- Keine Telemetrie, keine Werbung, keine Einbindung fremder Webseiten.

## Im Browser

Im Browser bleiben nur: der Zugangsschlüssel bis zum Schließen des Tabs, ein Sitzungscookie (12 Stunden, mit "angemeldet bleiben" 14 Tage) und Anzeigeeinstellungen wie hell oder dunkel. Patientendaten und das Patientenfeld liegen nur im Speicher der geöffneten Seite.

## Technische und organisatorische Maßnahmen

- **Pseudonymisierung** vor jeder Anfrage, mit Vorschau "Das sieht die KI". Erkennung über Regeln, Listen und optional ein lokales Modell. Sie ist gut, aber nicht lückenlos: Mitarbeiter sollen die Vorschau prüfen und können Stellen selbst verstecken.
- **Weiterleitung nach Sensibilität:** Person mit Gesundheitsdaten bleibt lokal. Admins legen fest, was in die Cloud darf, auch je Benutzer.
- **Schutz vor Prompt Injection** in Fragen und Dokumenten.
- **Zugriff:** eigene Konten, Rechte je Person, Passwörter mit Sperre nach Fehlversuchen, gesperrte Konten verlieren ihre Sitzung sofort.
- **Verschlüsselung** von Verlauf, API Schlüsseln und Sicherungen.
- **Protokoll** mit Hashkette, Änderungen und gelöschte Einträge fallen bei "Echtheit prüfen" auf.
- **Antwort-Check:** markiert Wirkstoffe, Dosierungen und Laborwerte, die nur in der Antwort stehen.
- **Updates** mit Sicherung und automatischem Rückweg, Sicherheitsupdates werden Admins angezeigt.

## Was die Praxis selbst tun sollte

- Zwijg im Praxisnetz nur über HTTPS erreichbar machen, siehe [Betrieb](betrieb.md).
- Die tägliche Sicherung einrichten, mit Ziel auf einem anderen Gerät, und das Passwort sicher aufbewahren.
- Unter Linux oder Docker die Schlüssel mit einem Zertifikat schützen.
- Mitarbeiter kurz einweisen: Vorschau prüfen, Patientenfeld nutzen, Antworten fachlich prüfen.
- Bei Cloud Anbietern den Vertrag zur Auftragsverarbeitung abschließen.
