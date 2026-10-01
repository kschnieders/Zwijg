# Cloud Anbieter einbinden

Cloud Modelle wie Claude oder ChatGPT sind oft besser als lokale, laufen aber beim Anbieter. Zwijg schickt ihnen nur, was laut Regeln raus darf, und immer mit Platzhaltern statt Patientendaten.

## Bevor es losgeht

- **API Schlüssel:** Ein normales Abo von Claude oder ChatGPT reicht nicht. Die API wird getrennt abgerechnet. Den Schlüssel gibt es bei platform.claude.com oder platform.openai.com.
- **Vertrag:** Für Patientendaten braucht die Praxis mit dem Anbieter einen Vertrag zur Auftragsverarbeitung.
- **Kostenlose Tarife:** Dort dürfen Anbieter Eingaben oft zum Training nutzen. Nur mit Testdaten verwenden.

## In Zwijg verbinden

1. Als Admin links auf **Verbindungen**, dann **Neue Verbindung**
2. Anbieter auswählen, zum Beispiel **Claude** oder **ChatGPT**
3. **API Schlüssel** einfügen
4. Bei Modell **Modelle laden** und eins auswählen
5. **Verbindung testen**, dann **Speichern**
6. Unter **Zuordnung** bei **Für unkritische Daten (Cloud)** auswählen und **Zuordnung speichern**

Der API Schlüssel liegt danach verschlüsselt auf dem Server und wird nie wieder angezeigt.

## Was darf in die Cloud?

Unter **Regeln**, **Weiterleitung**:

- **Automatisch:** Sensible Anfragen gehen an das lokale Modell, der Rest in die Cloud. Das ist die empfohlene Einstellung.
- **Nur Cloud:** Für Praxen ohne lokales Modell. Die Grenze bei **Was darf höchstens in die Cloud?** und **Hochgeladene Dokumente immer lokal verarbeiten** gelten trotzdem. Was danach lokal bleiben müsste, wird blockiert, mit einem Hinweis an den Benutzer.
- **Was darf höchstens in die Cloud?** Am vorsichtigsten ist **Nur Anfragen ganz ohne erkannte Personendaten**.
- **Hochgeladene Dokumente immer lokal verarbeiten** angehakt lassen.

Eine Person zusammen mit Gesundheitsdaten gilt immer als hoch sensibel und bleibt lokal, solange die Einstellung nicht ausdrücklich auf **Alles** steht.

Einzelne Benutzer lassen sich ganz von der Cloud ausschließen: unter **Benutzer** bei der Person **Cloud erlaubt** ausschalten.

## Kosten im Blick

Unter **Benutzer** lässt sich pro Person ein **Tageslimit** setzen. Die **Übersicht** zeigt, wie viele Anfragen lokal und wie viele in die Cloud gingen.
