# Andere Programme anbinden

Zwijg spricht dieselbe Schnittstelle wie OpenAI. Viele Programme, die ChatGPT unterstützen, können deshalb auch über Zwijg laufen: Chat Oberflächen wie Open WebUI, Plugins für Office oder eigene Skripte. Alles, was so reinkommt, wird genauso geschützt und protokolliert wie im Chat von Zwijg.

## Was man einträgt

| Einstellung | Wert |
|---|---|
| Base URL | `http://zwijg-server:5000/v1` (Adresse des Rechners mit Zwijg) |
| API Key | der persönliche Zugangsschlüssel aus Zwijg |
| Modell | `auto` |

Den Zugangsschlüssel legt ein Admin unter **Benutzer** an: beim jeweiligen Benutzer im Menü **Neuen Schlüssel erzeugen**. Am besten bekommt jedes Programm einen eigenen Benutzer, dann sieht man im Protokoll, woher eine Anfrage kam.

## Lokal oder Cloud festlegen

- Modell `auto`: Zwijg entscheidet nach den Regeln
- Modell `local/qwen2.5:7b`: immer lokal, mit genau diesem Modell
- Kopfzeile `X-Zwijg-Route: local`: immer lokal

In die Cloud zwingen kann ein Programm Zwijg nie, wenn die Regeln es verbieten.

## Beispiel mit curl

```
curl http://localhost:5000/v1/chat/completions \
  -H "Authorization: Bearer DEIN_SCHLÜSSEL" \
  -H "Content-Type: application/json" \
  -d '{"model":"auto","messages":[{"role":"user","content":"Herr Max Mustermann hat Fieber, was tun?"}]}'
```

## Beispiel mit Python

```python
from openai import OpenAI

client = OpenAI(base_url="http://localhost:5000/v1", api_key="DEIN_SCHLÜSSEL")
answer = client.chat.completions.create(
    model="auto",
    messages=[{"role": "user", "content": "Frau Erika Mustermann braucht eine Überweisung zur Kardiologie."}],
)
print(answer.choices[0].message.content)
```

## Gut zu wissen

- Mit `"zwijg": { "patient": "Kowalczyk, Anna, 12.03.1980" }` in der Anfrage versteckt Zwijg Name und Geburtsdatum wie beim Patientenfeld im Chat. Das Feld geht nicht an das Modell.
- Stehen in der Antwort Wirkstoffe, Dosierungen oder Laborwerte, die nicht in der Frage vorkamen, hängt Zwijg einen Hinweis an: `Hinweis von Zwijg: Diese Angaben stehen nicht in der Frage, bitte prüfen: ...`. Die Fundstellen stehen zusätzlich im Feld `zwijg_check` der Antwort, die Anzahl im Kopf `X-Zwijg-Check`. Den Hinweis im Text schalten Admins unter **Regeln**, **Anweisungen** ab.
- Streaming wird unterstützt, die Antwort kommt aber am Stück. Zwijg braucht die ganze Antwort, um die Platzhalter sicher zurückzutauschen.
- Bilder und Dateien in Nachrichten gehen nicht, nur Text. Dokumente laufen über `/v1/documents/ask`.
- Werkzeuge (Function Calling) gehen nicht. Anfragen mit `tools`, `functions`, `tool_calls`, `prediction` oder `response_format` mit `json_schema` lehnt Zwijg mit Fehler 400 ab. Diese Felder enthalten freien Text, den Zwijg nicht schützen kann. In Open WebUI deshalb keine Tools für Zwijg einschalten.
- Erlaubt sind die Rollen `system`, `user` und `assistant`. `developer` behandelt Zwijg wie `system`. Andere Rollen wie `tool` lehnt Zwijg mit Fehler 400 ab.
- Die Schutzregeln der Praxis gelten für alle Nachrichten, auch für Systemanweisungen. Antworten des Modells im Verlauf prüft Zwijg nur auf Regeln mit „nur lokal“.
- An den Anbieter gehen nur das Modell, die Nachrichten (Rolle und Text) und die üblichen Einstellungen wie `temperature`, `top_p`, `max_tokens`, `stop` und `response_format` mit `text` oder `json_object`. Andere Felder wie `metadata` oder `name` entfernt Zwijg.
- Weitere Endpunkte stehen in der [README](../README.md#endpunkte).
