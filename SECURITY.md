# Sicherheit

Zwijg verarbeitet Patientendaten. Sicherheitslücken nehmen wir deshalb besonders ernst.

## Unterstützte Versionen

Sicherheitsupdates gibt es für die jeweils neueste Version. Ältere Versionen bekommen keine Korrekturen mehr, bitte vorher aktualisieren.

| Version | Unterstützt |
| --- | --- |
| neueste Version unter [Releases](https://github.com/kschnieders/zwijg/releases/latest) | ja |
| ältere Versionen | nein |

## Lücke melden

Bitte **kein öffentliches Issue** und keinen Pull Request für eine Sicherheitslücke. Meldungen gehen vertraulich über GitHub:

**[Lücke vertraulich melden](https://github.com/kschnieders/zwijg/security/advisories/new)**

Hilfreich sind:

- betroffene Version und Betriebsart (Windows, Linux oder Docker)
- welcher Teil betroffen ist, zum Beispiel Anmeldung, Schutz der Patientendaten, Protokoll oder Sicherung
- Schritte zum Nachstellen, wenn möglich mit einem kleinen Beispiel
- was ein Angreifer damit erreichen kann

Bitte nur ausgedachte Daten wie "Max Mustermann" verwenden, nie echte Patientendaten.

## Ablauf

Zwijg wird aktuell von einer Person gepflegt. Die Fristen gelten deshalb in der Regel, im Urlaub oder bei Krankheit kann es ausnahmsweise etwas länger dauern.

- **Eingang bestätigt** in der Regel innerhalb von 7 Tagen
- **Erste Einschätzung** in der Regel innerhalb von 14 Tagen
- **Korrektur** so schnell wie möglich, je nach Schwere. Bis dahin bleibt die Meldung vertraulich.
- **Veröffentlichung** gemeinsam abgestimmt, sobald ein Update bereitsteht, spätestens nach 90 Tagen. Das Update steht im [Changelog](CHANGELOG.md) unter **Sicherheit**, dazu gibt es ein GitHub Security Advisory.

Wer möchte, wird im Advisory und im Changelog genannt.

## Was zählt als Sicherheitslücke

Zum Beispiel:

- Patientendaten gehen ungeschützt an einen Cloud Anbieter, obwohl die Einstellungen das verhindern sollten
- Zugriff ohne Anmeldung oder mit zu wenig Rechten
- Patientendaten lassen sich aus Verlauf, Protokoll oder Sicherung im Klartext lesen
- das Protokoll lässt sich unbemerkt ändern
- Ausführen von fremdem Code, Cross Site Scripting oder Ähnliches

Kein Sicherheitsproblem in diesem Sinne:

- ein einzelner Name oder Begriff, den die Erkennung übersieht. Das ist eine bekannte Grenze (siehe README unter **Grenzen**) und gehört als normales Issue mit ausgedachtem Beispiel gemeldet. Lässt sich die Erkennung dagegen gezielt und zuverlässig umgehen, bitte vertraulich melden.
- Angriffe, die schon vollen Zugriff auf den Rechner oder Admin Rechte in Zwijg voraussetzen
- fehlende Härtung ohne konkreten Angriff, zum Beispiel einzelne Header

## Testen

Bitte nur auf der eigenen Installation testen, nie auf fremden Systemen oder mit echten Patientendaten. Wer sich daran hält und eine Lücke vertraulich meldet, muss von uns nichts befürchten.

## English

Please do not open a public issue for security vulnerabilities. Report them privately via [GitHub Security Advisories](https://github.com/kschnieders/zwijg/security/advisories/new). Only the latest release receives security fixes. Zwijg is maintained by one person. We usually confirm receipt within 7 days, give a first assessment within 14 days and coordinate disclosure with you once a fix is available, at the latest after 90 days. Please test only on your own installation and never use real patient data.
