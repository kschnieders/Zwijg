# Im Chat arbeiten

Im Chat schreibt man ganz normal, auch mit Namen, Geburtsdaten und Versichertennummern. Zwijg ersetzt sie vor dem Versand und setzt sie in der Antwort wieder ein.

## Patient eintragen

Geht es um einen bestimmten Patienten, oben im Chat bei **Patient** einmal Name und Geburtsdatum eintragen, zum Beispiel `Kowalczyk, Anna, 12.03.1980`. Zwijg versteckt sie dann in der ganzen Unterhaltung:

- auch den Nachnamen allein im Satz, ohne Herr oder Frau davor, etwa "Bitte Kowalczyk zurückrufen"
- das Geburtsdatum in jeder üblichen Schreibweise, etwa 12.3.80 oder 12. März 1980
- nur ganze Wörter, "Anna" also nicht in "Annahme"

Dasselbe Feld gibt es bei **Dokument** (vor dem Hochladen eintragen) und bei **Text schützen**. Das Feld steht nur im Speicher der Seite und verschlüsselt in der gespeicherten Unterhaltung. Der Browser merkt es sich nicht. **Neue Unterhaltung** leert es.

## Die Vorschau rechts lesen

Unter **Das sieht die KI** steht beim Tippen genau der Text, der rausgeht. Ersetzte Stellen sind gelb markiert, zum Beispiel `[NAME_1]`.

Kurz draufschauen lohnt sich immer. Steht dort noch ein Name im Klartext, einfach verstecken, siehe unten.

## Etwas selbst verstecken

Für alles, was Zwijg nicht von allein erkennt, etwa einen Projektnamen, eine Firma oder eine Kontonummer:

1. Den Text im Eingabefeld markieren
2. Auf **Verstecken** klicken oder **Strg+Umschalt+H** drücken
3. Die Art wählen, zum Beispiel Firma oder Nummer, dann **Verstecken**

Der Text ist dann in der ganzen Unterhaltung versteckt, auch in früheren und späteren Nachrichten. Rechts unter **Versteckt in dieser Unterhaltung** steht die Liste, mit × lässt sich ein Eintrag wieder freigeben.

## Antworten prüfen

KI Modelle erfinden manchmal Details, zum Beispiel ein Medikament oder eine Dosierung, die nirgends in der Frage stand. Zwijg markiert solche Stellen in der Antwort gelb:

- Wirkstoffe, etwa Amoxicillin oder Ramipril
- Dosierungen, etwa 1000 mg oder 2 Tabletten
- Einnahmeschemata, etwa 1-0-1 oder 3x täglich
- Laborwerte, etwa CRP 48 mg/l oder RR 140/90

Unter der Antwort steht dann **Bitte prüfen** mit der Liste. Was schon in der Frage, in früheren Nachrichten oder im Dokument stand, wird nicht markiert, auch wenn es anders geschrieben ist (1 g und 1000 mg sind dasselbe).

Das ersetzt keine fachliche Prüfung. Es zeigt nur, wo man vor dem Übernehmen genau hinschauen sollte. Admins können es unter **Regeln**, **Anweisungen** ausschalten.

## Diktieren

Mit **Diktieren** neben Senden oder **Strg+M** spricht man statt zu tippen. Der Text landet im Eingabefeld, man prüft ihn und schickt ihn wie gewohnt ab. Einrichten und HTTPS, siehe [Diktieren](diktieren.md).

## Vorlagen nutzen

Über dem Eingabefeld stehen Vorlagen als Knöpfe, zum Beispiel für Absagen oder Arztbriefe. Manche fragen vorher ein paar Angaben ab, etwa Name und Termin, und schicken die Anweisung dann im Hintergrund. Im Chat steht nur eine kurze Karte.

Neue Vorlagen legen Admins unter **Regeln**, **Vorlagen** an. Felder wie `{{Patient}}` oder `{{Termin:termin}}` werden dann im Chat abgefragt.

## Text für andere Programme schützen

Manche Programme lassen sich nicht an Zwijg anbinden, zum Beispiel ChatGPT im Browser oder ein Schreibprogramm mit KI. Dafür gibt es links **Text schützen**:

1. Text einfügen und **Schützen** klicken. Was Zwijg nicht von allein erkennt, etwa eine Firma oder ein Projekt, vorher markieren und auf **Verstecken** klicken oder **Strg+Umschalt+H** drücken, genau wie im Chat
2. Die geschützte Fassung **Kopieren** und im anderen Programm einfügen
3. Die Antwort von dort kopieren und rechts einfügen
4. Zwijg setzt die echten Daten wieder ein, zum **Kopieren**

Schützt man danach einen weiteren Text zum selben Patienten, behält derselbe Name denselben Platzhalter. Die Zuordnung liegt nur im Browserfenster, beim Neuladen ist sie weg. Mit **Neu beginnen** fängt man frisch an.

Ein kopierter Text verlässt die Praxis. Deshalb gelten dieselben Regeln wie für die Cloud: Wer nicht in die Cloud darf, sieht die Seite nicht, und zu sensible Texte, etwa Person mit Diagnose, lehnt Zwijg ab.

## Dateien verschlüsseln

Für Empfänger ohne KIM, etwa Patienten oder Gutachter. Admins schalten den Bereich unter **Verwaltung**, **Dateien** ein und legen dort die Höchstgröße fest.

1. Links auf **Dateien verschlüsseln**, Dateien hineinziehen
2. **Erzeugen** klicken, das Kennwort ist gut diktierbar
3. **Verschlüsseln und speichern**. Heraus kommt eine ZIP Datei mit AES-256, auch die Dateinamen sind verschlüsselt
4. Datei verschicken, das Kennwort **getrennt** weitergeben, am besten am Telefon oder per SMS

Der Empfänger öffnet die Datei mit 7-Zip, WinRAR oder auf dem Mac mit Keka, darin liegt `dokumente.zip` mit den Dateien. Verschlüsselte ZIP Dateien, die man selbst bekommt, öffnet Zwijg rechts unter **Verschlüsselte Datei öffnen**.

## Dokumente prüfen und befragen

1. Links auf **Dokument**
2. PDF, Textdatei oder Foto hochladen. Scans liest die [Texterkennung](texterkennung.md)
3. Zwijg zeigt zuerst einen Prüfbericht: versteckter Text, Manipulationsversuche, erkannte Patientendaten
4. Rechts steht, wie die KI das Dokument sieht
5. Ist alles in Ordnung, eine Frage stellen

Enthält ein PDF versteckte Anweisungen, zum Beispiel weiße Schrift auf weißem Grund, wird es blockiert und geht nicht an die KI.

Als versteckt gilt weiße oder winzige Schrift, unsichtbar gesetzter Text und Text außerhalb der Seite. Das ist eine Heuristik: Text unter einem Bild, Schrift in der Farbe eines farbigen Hintergrunds oder stark gestauchte Schrift erkennt Zwijg nicht als versteckt. Bei Dokumenten aus fremder Quelle lohnt sich deshalb ein Blick nach rechts, wie die KI das Dokument sieht.

## Kleine Tricks

- **Strg+Enter** schickt die Nachricht ab
- "Herr" oder "Frau" vor einem Namen hilft der Erkennung
- Unter dem Eingabefeld lässt sich das Ziel für eine Anfrage auf **Nur lokal** stellen
- Unterhaltungen stehen links unter **Verlauf**. Mit Rechtsklick lassen sie sich anpinnen, umbenennen oder löschen
- Die Titel in der Seitenleiste enthalten keine Patientendaten, man kann also auch am Empfang arbeiten, ohne dass jemand mitliest
- Oben rechts **Neue Unterhaltung** starten, wenn es um einen anderen Patienten geht. Dann vermischen sich die Angaben nicht
