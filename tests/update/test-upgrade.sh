#!/usr/bin/env bash
# Update von der zuletzt veröffentlichten Version auf den aktuellen Stand, mit echten Daten.
# Legt in der alten Version Unterhaltungen und Protokoll an, spielt mit update.sh die neue ein
# und prüft danach Anmeldung, Verlauf und Echtheit des Protokolls.
# Aufruf: tests/update/test-upgrade.sh <Ordner mit veröffentlichtem Zwijg für linux-x64>
set -euo pipefail
programm="$(cd "$1" && pwd)"
repo="${GITHUB_REPOSITORY:-kschnieders/zwijg}"
work="$(mktemp -d)"
dir="$work/zwijg"
port=45873
key="upgrade-test-admin-schluessel"
url="http://127.0.0.1:$port"
trap 'pkill -f "$work/" || true; rm -rf "$work"' EXIT

check() {
  if eval "$1"; then echo "ok: $2"; else echo "FEHLER: $2"; exit 1; fi
}

# Wie in einer Praxis: fester Schlüssel für den Admin, Echo statt echtem Modell
export Zwijg__ApiKeys__0__Key="$key" Zwijg__ApiKeys__0__User=admin Zwijg__ApiKeys__0__Admin=true
export Zwijg__Providers__Local__Type=Echo Zwijg__Routing__Mode=LocalOnly

start() {
  (cd "$dir" && exec ./Zwijg.Gateway "--urls=$url") > "$work/$1.log" 2>&1 &
  pid=$!
  for _ in $(seq 1 60); do
    sleep 1
    if curl -fsS "$url/health" > /dev/null 2>&1; then return 0; fi
  done
  tail -30 "$work/$1.log"
  return 1
}

stop() { kill "$pid"; wait "$pid" || true; }

api() { curl -fsS -H "Authorization: Bearer $key" -H "Content-Type: application/json" "$@"; }

# Letzte Veröffentlichung holen
auth=()
if [ -n "${GH_TOKEN:-}" ]; then auth=(-H "Authorization: Bearer $GH_TOKEN"); fi
tag="$(curl -fsSL ${auth[@]+"${auth[@]}"} "https://api.github.com/repos/$repo/releases/latest" | grep -o '"tag_name": *"[^"]*"' | sed -E 's/.*"v?([^"]*)"$/\1/')"
echo "Letzte Version: $tag"
mkdir -p "$dir"
curl -fsSL -o "$work/alt.zip" "https://github.com/$repo/releases/download/v$tag/zwijg-$tag-linux-x64.zip"
(cd "$dir" && unzip -q "$work/alt.zip" && chmod +x Zwijg.Gateway update.sh)

# Alte Version mit Daten füllen
check 'start alt' "Version $tag startet"
for i in 1 2 3; do
  api -X POST "$url/v1/chat/completions" -d "{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"Frage $i zu Herrn Max Mustermann\"}],\"zwijg\":{\"conversation\":\"new\"}}" > /dev/null
done
before_convs="$(api "$url/v1/conversations" | grep -o '"id"' | wc -l)"
check '[ "$before_convs" -ge 3 ]' "Drei Unterhaltungen gespeichert"
check 'api "$url/admin/audit/verify" | grep -q "\"ok\":true"' "Protokoll in Ordnung"
stop

# Neue Version als Paket, so wie das Release es baut
new="$(cat "$programm/VERSION" 2>/dev/null || echo 99.0.0)"
cp -a "$programm" "$work/paket"
echo "$new" > "$work/paket/VERSION"
cp "$(dirname "$0")/../../scripts/update.sh" "$work/paket/update.sh"
chmod +x "$work/paket/Zwijg.Gateway" "$work/paket/update.sh"
(cd "$work/paket" && zip -qr "$work/neu.zip" .)

# Update mit dem Skript der alten Version, wie in der Praxis
check '"$dir/update.sh" --paket "$work/neu.zip"' "Update von $tag auf $new läuft durch"
check '[ "$(cat "$dir/VERSION")" = "$new" ]' "$new ist installiert"

# Neue Version prüfen
check 'start neu' "Neue Version startet mit den alten Daten"
check 'api "$url/v1/me" | grep -q "\"admin\":true"' "Anmeldung mit dem alten Schlüssel"
after_convs="$(api "$url/v1/conversations" | grep -o '"id"' | wc -l)"
check '[ "$after_convs" = "$before_convs" ]' "Alle Unterhaltungen noch da"
first="$(api "$url/v1/conversations" | grep -o '"id":"[^"]*"' | head -1 | cut -d'"' -f4)"
check 'api "$url/v1/conversations/$first" | grep -q "Mustermann"' "Unterhaltung lesbar, Inhalt entschlüsselt"
check 'api "$url/admin/audit/verify" | grep -q "\"ok\":true"' "Protokoll nach dem Update in Ordnung"
api -X POST "$url/v1/chat/completions" -d '{"model":"auto","messages":[{"role":"user","content":"Neue Frage nach dem Update"}]}' > /dev/null
check 'api "$url/admin/audit/verify" | grep -q "\"ok\":true"' "Neue Einträge passen an die alte Kette"
stop

echo "Alle Prüfungen bestanden"
