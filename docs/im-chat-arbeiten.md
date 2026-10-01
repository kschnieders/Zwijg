# Im Chat arbeiten

Im Chat schreibt man ganz normal, auch mit Namen, Geburtsdaten und Versichertennummern. Zwijg ersetzt sie vor dem Versand und setzt sie in der Antwort wieder ein.

## Die Vorschau rechts lesen

Unter **Das sieht die KI** steht beim Tippen genau der Text, der rausgeht. Ersetzte Stellen sind gelb markiert, zum Beispiel `[NAME_1]`.

Kurz draufschauen lohnt sich immer. Steht dort noch ein Name im Klartext, einfach verstecken, siehe unten.

## Etwas selbst verstecken

Für alles, was Zwijg nicht von allein erkennt, etwa einen Projektnamen, eine Firma oder eine Kontonummer:

1. Den Text im Eingabefeld markieren
2. Auf **Verstecken** klicken oder **Strg+Umschalt+H** drücken
3. Die Art wählen, zum Beispiel Firma oder Nummer, dann **Verstecken**

Der Text ist dann in der ganzen Unterhaltung versteckt, auch in früheren und späteren Nachrichten. Rechts unter **Versteckt in dieser Unterhaltung** steht die Liste, mit × lässt sich ein Eintrag wieder freigeben.

## Diktieren

Mit **Diktieren** neben Senden oder **Strg+M** spricht man statt zu tippen. Der Text landet im Eingabefeld, man prüft ihn und schickt ihn wie gewohnt ab. Einrichten und HTTPS, siehe [Diktieren](diktieren.md).

## Vorlagen nutzen

Über dem Eingabefeld stehen Vorlagen als Knöpfe, zum Beispiel für Absagen oder Arztbriefe. Manche fragen vorher ein paar Angaben ab, etwa Name und Termin, und schicken die Anweisung dann im Hintergrund. Im Chat steht nur eine kurze Karte.

Neue Vorlagen legen Admins unter **Regeln**, **Vorlagen** an. Felder wie `{{Patient}}` oder `{{Termin:termin}}` werden dann im Chat abgefragt.

## Text für andere Programme schützen

Manche Programme lassen sich nicht an Zwijg anbinden, zum Beispiel ChatGPT im Browser oder ein Schreibprogramm mit KI. Dafür gibt es links **Text schützen**:

1. Text einfügen und **Schützen** klicken
2. Die geschützte Fassung **Kopieren** und im anderen Programm einfügen
3. Die Antwort von dort kopieren und rechts einfügen
4. Zwijg setzt die echten Daten wieder ein, zum **Kopieren**

Schützt man danach einen weiteren Text zum selben Patienten, behält derselbe Name denselben Platzhalter. Die Zuordnung liegt nur im Browserfenster, beim Neuladen ist sie weg. Mit **Neu beginnen** fängt man frisch an.

Ein kopierter Text verlässt die Praxis. Deshalb gelten dieselben Regeln wie für die Cloud: Wer nicht in die Cloud darf, sieht die Seite nicht, und zu sensible Texte, etwa Person mit Diagnose, lehnt Zwijg ab.

## Dokumente prüfen und befragen

1. Links auf **Dokument**
2. PDF, Textdatei oder Foto hochladen. Scans liest die [Texterkennung](texterkennung.md)
3. Zwijg zeigt zuerst einen Prüfbericht: versteckter Text, Manipulationsversuche, erkannte Patientendaten
4. Rechts steht, wie die KI das Dokument sieht
5. Ist alles in Ordnung, eine Frage stellen

Enthält ein PDF versteckte Anweisungen, zum Beispiel weiße Schrift auf weißem Grund, wird es blockiert und geht nicht an die KI.

## Kleine Tricks

- **Strg+Enter** schickt die Nachricht ab
- "Herr" oder "Frau" vor einem Namen hilft der Erkennung
- Unter dem Eingabefeld lässt sich das Ziel für eine Anfrage auf **Nur lokal** stellen
- Unterhaltungen stehen links unter **Verlauf**. Mit Rechtsklick lassen sie sich anpinnen, umbenennen oder löschen
- Die Titel in der Seitenleiste enthalten keine Patientendaten, man kann also auch am Empfang arbeiten, ohne dass jemand mitliest
- Oben rechts **Neue Unterhaltung** starten, wenn es um einen anderen Patienten geht. Dann vermischen sich die Angaben nicht
