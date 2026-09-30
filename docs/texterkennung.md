# Texterkennung für Scans

Viele Befunde kommen als Fax oder Scan. Solche PDFs enthalten nur ein Bild und keinen Text. Zwijg liest sie mit [Tesseract](https://github.com/tesseract-ocr/tesseract), das läuft komplett auf dem eigenen Rechner. Fotos von Befunden (JPG, PNG, TIFF) gehen genauso.

Der erkannte Text wird danach wie jedes andere Dokument geschützt: Namen und Daten werden ersetzt, und Zwijg prüft auf eingeschleuste Anweisungen.

## Tesseract installieren

Windows:

```
winget install UB-Mannheim.TesseractOCR
```

Oder den Installer von der Seite der [UB Mannheim](https://github.com/UB-Mannheim/tesseract/wiki) nehmen. Wichtig: Im Installer bei **Additional language data** die Sprache **German** anhaken. Ohne diesen Haken kann Tesseract nur Englisch.

Linux (Debian, Ubuntu):

```
sudo apt install tesseract-ocr tesseract-ocr-deu
```

Docker: Im Image von Zwijg ist Tesseract mit Deutsch schon drin.

## In Zwijg prüfen

1. Als Admin links auf **Regeln**, dann **Erkennung**
2. Oben in der Karte **Texterkennung für Scans (OCR)** steht, ob Tesseract gefunden wurde
3. Steht dort **Bereit** mit den Sprachen `deu`, ist alles fertig

Wird Tesseract nicht gefunden, den Pfad zur Datei eintragen, unter Windows meist `C:\Program Files\Tesseract-OCR\tesseract.exe`, und **Speichern**.

## Deutsch fehlt?

Steht dort **Sprachdaten fehlen: deu**, gibt es zwei Wege:

- Tesseract noch einmal installieren und dabei German anhaken
- Oder die Datei `deu.traineddata` von [tessdata_fast](https://github.com/tesseract-ocr/tessdata_fast) herunterladen, zusammen mit `eng.traineddata` in einen eigenen Ordner legen und diesen Ordner bei **Ordner mit Sprachdaten** eintragen. Das geht auch ohne Admin Rechte auf dem Rechner.

## Im Alltag

- Unter **Dokument** einfach das PDF oder Foto hochladen. Bei Scans steht im Prüfbericht "per Texterkennung".
- Pro Seite dauert es ein paar Sekunden. Wie viele Seiten höchstens gelesen werden, steht unter **Höchstens so viele Seiten**.
- Bei Scans kommen Lesefehler vor. Einen falsch gelesenen Namen erkennt Zwijg eventuell nicht als Namen. Deshalb rechts in der Vorschau kurz prüfen, bevor man fragt.
- Handschrift liest Tesseract kaum. Schiefe oder sehr blasse Scans werden schlechter erkannt.
- Die Zwischenbilder liegen nur kurz in einem eigenen temporären Ordner und werden gleich wieder gelöscht.
