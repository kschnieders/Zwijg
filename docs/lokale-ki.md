# Lokale KI einbinden

Eine lokale KI läuft auf einem Rechner in der Praxis. Nichts verlässt das Haus, und es kostet nichts außer Strom. Dafür eignet sich [Ollama](https://ollama.com).

## Was man braucht

Einen Rechner mit Grafikkarte. Mit 8 GB Grafikspeicher läuft ein Modell mit etwa 7 Milliarden Parametern flüssig, zum Beispiel `qwen2.5:7b`. Mehr Speicher erlaubt größere Modelle, und die schreiben meist besseres Deutsch. Einen Versuch wert sind dann `gemma2:9b` oder `mistral-nemo`. Ohne Grafikkarte geht es auch, dann aber deutlich langsamer.

## Ollama installieren

Windows:

```
winget install Ollama.Ollama
ollama pull qwen2.5:7b
```

Linux:

```
curl -fsSL https://ollama.com/install.sh | sh
ollama pull qwen2.5:7b
```

Kurz testen, ob das Modell antwortet:

```
ollama run qwen2.5:7b "Sag Hallo auf Deutsch"
```

## In Zwijg verbinden

1. Als Admin links auf **Verbindungen**, dann **Neue Verbindung**
2. **Ollama** auswählen
3. Läuft Ollama auf demselben Rechner wie Zwijg, passt die Adresse schon: `http://localhost:11434/v1`
4. Bei Modell auf **Modelle laden** klicken und das Modell auswählen
5. **Verbindung testen**, dann **Speichern**
6. Oben unter **Zuordnung** die Verbindung bei **Für sensible Daten (lokal)** auswählen und **Zuordnung speichern**

Unten links in der Seitenleiste steht jetzt ein grüner Punkt bei Lokal.

## Ollama auf einem anderen Rechner im Praxisnetz

Ollama hört anfangs nur auf den eigenen Rechner. Für das Netz einmal umstellen:

- Windows: Umgebungsvariable `OLLAMA_HOST` mit dem Wert `0.0.0.0` anlegen und Ollama neu starten
- Linux: `sudo systemctl edit ollama`, dort unter `[Service]` die Zeile `Environment="OLLAMA_HOST=0.0.0.0"` eintragen, dann `sudo systemctl restart ollama`

In Zwijg bei der Verbindung die Adresse des Rechners eintragen, zum Beispiel `http://192.168.1.50:11434/v1`.

Ollama hat selbst keine Anmeldung. In der Firewall den Port 11434 deshalb nur für den Rechner freigeben, auf dem Zwijg läuft.

## Tipps

- Bei **Temperatur** 0.3 eintragen. Dann antwortet das Modell ruhiger und bleibt eher bei Deutsch.
- Unter **Regeln**, **Weiterleitung** auf **Nur lokal** stellen, wenn gar nichts in die Cloud soll.
- Unter **Regeln**, **Erkennung** kann das lokale Modell zusätzlich nach Namen und Orten suchen. Das kostet etwas Zeit, findet aber auch seltene Namen ohne Zusammenhang.

LM Studio oder ein eigener Server funktionieren genauso, dafür in Schritt 2 **LM Studio** oder **Eigener Server** wählen.
