#!/usr/bin/env bash
# Spielt Updates mit update.sh durch: gelungenes Update, kaputtes Paket mit automatischer Rückkehr und Zurück von Hand.
# Aufruf: tests/update/test-update.sh <Ordner mit veröffentlichtem Zwijg> <update.sh>
set -euo pipefail
programm="$(cd "$1" && pwd)"
skript="$(cd "$(dirname "$2")" && pwd)/$(basename "$2")"
work="$(mktemp -d)"
dir="$work/zwijg"
trap 'pkill -f "$work/" || true; rm -rf "$work"' EXIT

check() {
  if eval "$1"; then echo "ok: $2"; else echo "FEHLER: $2"; exit 1; fi
}

installed() { cat "$dir/VERSION"; }

# Paket bauen wie im Release: Programm, VERSION und update.sh in einer ZIP
package() {
  local version="$1" folder="$work/paket-$1"
  cp -a "$programm" "$folder"
  echo "$version" > "$folder/VERSION"
  cp "$skript" "$folder/update.sh"
  chmod +x "$folder/update.sh" "$folder/Zwijg.Gateway"
  # Kaputtes Paket: Das Programm ist da, startet aber nicht
  if [ "${2:-}" = kaputt ]; then echo kaputt > "$folder/Zwijg.Gateway.dll"; fi
  (cd "$folder" && zip -qr "$work/zwijg-$version.zip" .)
}

run_update() {
  local code=0
  "$dir/update.sh" "$@" 2>&1 | sed 's/^/  | /' || code=${PIPESTATUS[0]}
  return "$code"
}

package 0.0.1
package 0.0.2
package 0.0.3 kaputt

# Alte Version installieren und einmal starten, damit Daten entstehen
mkdir "$dir"
(cd "$dir" && unzip -q "$work/zwijg-0.0.1.zip")
(cd "$dir" && exec ./Zwijg.Gateway --urls=http://127.0.0.1:45871) > "$work/erststart.log" 2>&1 &
pid=$!
up=0
for _ in $(seq 1 60); do
  sleep 1
  if curl -fsS "http://127.0.0.1:45871/health" > /dev/null 2>&1; then up=1; break; fi
done
kill "$pid"; wait "$pid" || true
check '[ "$up" = 1 ]' "Alte Version startet"
check '[ -f "$dir/data/settings.json" ]' "Daten sind angelegt"
echo "vor dem Update" > "$dir/data/notiz.txt"

check 'run_update --paket "$work/zwijg-0.0.2.zip"' "Update auf 0.0.2 läuft durch"
check '[ "$(installed)" = 0.0.2 ]' "0.0.2 ist installiert"
check '[ "$(ls -1 "$dir/update-sicherungen" | wc -l)" = 1 ]' "Eine Sicherung ist angelegt"
check '[ "$(cat "$dir/data/notiz.txt")" = "vor dem Update" ]' "Daten sind unverändert"
check '[ ! -e "$dir/data/sicherungen" ]' "Beim Probestart keine zweite Sicherung von Zwijg selbst"
check '[ "$(stat -c %a "$dir/update-sicherungen")" = 700 ]' "Sicherungen nur für den eigenen Benutzer lesbar"

echo "nach dem Update" > "$dir/data/notiz.txt"
check '! run_update --paket "$work/zwijg-0.0.3.zip"' "Kaputtes Paket wird abgelehnt"
check '[ "$(installed)" = 0.0.2 ]' "0.0.2 ist wieder installiert"
check '[ "$(cat "$dir/data/notiz.txt")" = "nach dem Update" ]' "Daten sind wie vor dem kaputten Update"

check 'run_update --paket "$work/zwijg-0.0.1.zip"' "Älteres Paket wird nicht installiert"
check '[ "$(installed)" = 0.0.2 ]' "0.0.2 bleibt installiert"

# Zurück nimmt die neueste Sicherung, also den Stand vor dem kaputten Update
check 'run_update --zurueck' "Zurück läuft durch"
check '[ "$(installed)" = 0.0.2 ]' "Stand vor dem kaputten Update"

first="$(ls -1 "$dir/update-sicherungen" | grep -- '-0\.0\.1$' | head -1)"
check 'run_update --zurueck --sicherung "$first"' "Zurück auf eine bestimmte Sicherung"
check '[ "$(installed)" = 0.0.1 ]' "0.0.1 ist wieder installiert"
check '[ "$(cat "$dir/data/notiz.txt")" = "vor dem Update" ]' "Daten von vor dem ersten Update"

echo "Alle Prüfungen bestanden"
