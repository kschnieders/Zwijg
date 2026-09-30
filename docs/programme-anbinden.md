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

- Streaming wird unterstützt, die Antwort kommt aber am Stück. Zwijg braucht die ganze Antwort, um die Platzhalter sicher zurückzutauschen.
- Bilder und Dateien in Nachrichten gehen nicht, nur Text. Dokumente laufen über `/v1/documents/ask`.
- Weitere Endpunkte stehen in der [README](../README.md#endpunkte).
