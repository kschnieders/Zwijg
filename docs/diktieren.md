# Diktieren

Im Chat kann man per Mikrofon sprechen statt zu tippen. Zwijg schreibt die Sprache mit [Whisper](https://github.com/ggerganov/whisper.cpp) mit. Das läuft komplett auf dem eigenen Rechner, die Aufnahme verlässt die Praxis nicht.

Der Text landet im Eingabefeld und wird nicht von selbst abgeschickt. So sieht man Hörfehler vorher. Beim Senden wird er wie jede andere Eingabe geschützt.

## Sprachmodell laden

Einmal als Admin:

1. Links auf **Regeln**, dann **Erkennung**
2. In der Karte **Diktieren** ein Modell auswählen
3. **Modell laden** klicken. Das Modell wird einmal heruntergeladen und läuft danach ohne Internet
4. Steht oben **Bereit**, erscheint im Chat der Knopf **Diktieren**

| Modell | Größe | Passt für |
|---|---|---|
| Schnell (base) | 148 MB | ältere Rechner, kurze Sätze |
| Gut (small) | 488 MB | die meisten Praxisrechner |
| Sehr gut (large-v3-turbo) | 574 MB | schnelle Rechner, viele Fachbegriffe |

Ohne Internet am Server die Datei, zum Beispiel `ggml-small.bin`, von [Hugging Face](https://huggingface.co/ggerganov/whisper.cpp/tree/main) holen und in den Ordner `models` im data Ordner legen.

## Im Chat

- **Diktieren** klicken oder **Strg+M** drücken und sprechen
- Noch einmal klicken oder Strg+M, dann wird der Text geschrieben
- **Esc** bricht die Aufnahme ab, nichts wird geschrieben
- Eine Aufnahme geht höchstens 5 Minuten

Beim ersten Mal fragt der Browser, ob er das Mikrofon benutzen darf.

## HTTPS nötig

Browser geben das Mikrofon nur frei, wenn die Seite über HTTPS läuft oder direkt auf dem Rechner über `localhost` geöffnet wird. Wer Zwijg im Praxisnetz über eine Adresse wie `http://192.168.1.10:5247` öffnet, sieht beim Diktieren einen Hinweis. Wie HTTPS eingerichtet wird, steht unter [Betrieb in der Praxis](betrieb.md).

## Für andere Programme

Zwijg bietet dafür die Schnittstelle `POST /v1/audio/transcriptions` wie bei OpenAI. Erwartet wird eine WAV Datei im Feld `file`. Im Protokoll steht nur, dass diktiert wurde, nicht der Text.
