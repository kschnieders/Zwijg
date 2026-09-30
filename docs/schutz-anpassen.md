# Schutz anpassen

Zwijg erkennt von sich aus Namen, Orte, Geburtsdaten, Versichertennummern und vieles mehr. Für die eigene Praxis lässt sich das unter **Regeln** verfeinern.

## Namen und Orte aus der Umgebung

Unter **Regeln**, **Erkennung**, **Eigene Listen**:

- **Namen, die immer ersetzt werden:** Ärztinnen, Mitarbeiter, Pflegedienste, seltene Nachnamen aus der Gegend
- **Orte, die immer ersetzt werden:** Ortsteile, Bauerschaften, Straßen
- **Wörter, die nie ersetzt werden:** falls Zwijg ein normales Wort für einen Namen hält

Ein Eintrag pro Zeile, Groß und Kleinschreibung ist egal.

## Zweite Stufe mit dem lokalen Modell

Unter **Regeln**, **Erkennung** den Haken bei **Lokales Modell zusätzlich nach Namen und Orten suchen lassen** setzen. Das lokale Modell liest dann jeden neuen Text einmal mit und findet auch Namen, die ganz ohne Zusammenhang dastehen. Das kostet pro Text etwa eine drittel Sekunde. Voraussetzung ist eine [lokale KI](lokale-ki.md).

## Eigene Schutzregeln

Unter **Regeln**, **Schutzregeln**, **Neue Regel**. Eine Regel sucht nach Wörtern oder nach einem Muster und macht dann eins von vier Dingen:

- **Ersetzen:** durch einen Platzhalter, in der Antwort wieder eingesetzt
- **Nur lokal:** die Anfrage geht nie in die Cloud
- **Blockieren:** die Anfrage wird gar nicht verschickt
- **Nur protokollieren:** geht durch, im Protokoll steht eine Warnung

Beispiele:

- Interne Aktenzeichen wie `AZ-2026-0815` ersetzen: bei **Muster** `AZ-\d{4}-\d{4}` eintragen
- Alles mit "Betäubungsmittel" nur lokal verarbeiten
- Anfragen mit "Passwort" blockieren

Mit **Ausprobieren** sieht man direkt, was die Regel in einem Beispieltext findet. Unter **Beispiele übernehmen** gibt es fertige Regeln zum Anpassen.

## Anweisungen an die KI

Unter **Regeln**, **Anweisungen** steht, wie die KI antworten soll: Sprache, Anrede, Ton, Länge. Das gilt für jede Anfrage. Hier keine Patientendaten eintragen, der Text geht an jedes Modell.

Einzelne Personen bekommen unter **Benutzer** eine **Persönliche Anweisung an die KI**, etwa "Antworte kurz und in einfacher Sprache" für den Empfang.

## Kontrollieren

Unter **Protokoll** steht zu jeder Anfrage, was wirklich an die KI ging. Ein Blick dort hinein nach den ersten Tagen zeigt, ob die Erkennung für die eigene Praxis passt.
