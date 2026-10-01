#!/usr/bin/env bash
# Zwijg aktualisieren. Zwijg vorher beenden und im Ordner von Zwijg ausführen:
#   ./update.sh
# Ohne Internet mit einem heruntergeladenen Paket:
#   ./update.sh --paket /pfad/zwijg-1.2.0-linux-x64.zip
# Zurück auf den Stand vor dem letzten Update:
#   ./update.sh --zurueck
#
# Vor dem Update kommen Programm und Daten zusammen in den Ordner update-sicherungen. Danach startet die neue
# Version einmal zur Probe. Klappt das nicht, wird automatisch der alte Stand wiederhergestellt.
set -euo pipefail
# Fehler auch in $(...) beachten, sonst bliebe eine unvollständige Sicherung unbemerkt
shopt -s inherit_errexit

KEEP_BACKUPS=3

# Bleibt beim Update, wie es ist. appsettings.json wird nur beim ersten Start gelesen, eigene Änderungen bleiben so erhalten.
UNTOUCHED="data update-sicherungen appsettings.json"

# Nicht mitsichern: Sprachmodelle ändert ein Update nicht, und die Sicherungen von Zwijg selbst sind schon Sicherungen
DATA_SKIP="models sicherungen"

in_list() { case " $2 " in *" $1 "*) return 0 ;; *) return 1 ;; esac; }

version_of() { cat "$1/VERSION" 2>/dev/null || echo 0.0.0; }

# true, wenn $1 neuer ist als $2
newer() { [ "$1" != "$2" ] && [ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | tail -1)" = "$1" ]; }

# Programm und Daten in einen neuen Ordner unter update-sicherungen kopieren. cp -a behält Besitzer und Rechte.
new_backup() {
  local target name
  target="$BACKUPS/$(date +%Y%m%d-%H%M%S)-$(version_of "$DIR")$1"
  # Zwei Sicherungen in derselben Sekunde bekommen eine Nummer dran
  local base="$target" n=2
  while [ -e "$target" ]; do target="$base-$n"; n=$((n + 1)); done
  # Die Sicherung enthält die Schlüssel, deshalb nur für den eigenen Benutzer lesbar
  (umask 077 && mkdir -p "$target/programm")
  for f in "$DIR"/* "$DIR"/.[!.]*; do
    [ -e "$f" ] || continue
    name="$(basename "$f")"
    in_list "$name" "$UNTOUCHED" || cp -a "$f" "$target/programm/"
  done
  if [ -d "$DIR/data" ]; then
    mkdir -p "$target/data"
    for f in "$DIR"/data/* "$DIR"/data/.[!.]*; do
      [ -e "$f" ] || continue
      name="$(basename "$f")"
      in_list "$name" "$DATA_SKIP" || cp -a "$f" "$target/data/"
    done
  fi
  echo "$target"
}

# Programm und Daten aus einer Sicherung zurückholen. Die Sicherung selbst bleibt liegen.
restore_backup() {
  local source="$1" f name
  cp -a "$source/programm/." "$DIR/"
  if [ -d "$source/data" ]; then
    for f in "$DIR"/data/* "$DIR"/data/.[!.]*; do
      [ -e "$f" ] || continue
      name="$(basename "$f")"
      in_list "$name" "$DATA_SKIP" || rm -rf "$f"
    done
    cp -a "$source/data/." "$DIR/data/"
  fi
}

# Neue Version einmal auf einem freien Port starten und warten, bis sie antwortet
test_start() {
  local port pid i log runas=()
  log="$(mktemp)"
  for _ in $(seq 1 50); do
    port=$((40000 + RANDOM % 20000))
    (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null || break
  done

  # Läuft das Skript als root, der Datenordner gehört aber dem Dienstkonto, dann als dieses starten.
  # Sonst legt der Probestart Dateien an, die Zwijg später nicht mehr ändern darf.
  local owner
  owner="$(stat -c %U "$DIR/data")"
  if [ "$(id -u)" = 0 ] && [ "$owner" != root ]; then
    runas=(runuser -u "$owner" --)
  fi

  (cd "$DIR" && exec ${runas[@]+"${runas[@]}"} ./Zwijg.Gateway "--urls=http://127.0.0.1:$port" --Zwijg:SkipUpdateSnapshot=true) > "$log" 2>&1 &
  pid=$!
  for i in $(seq 1 60); do
    sleep 1
    kill -0 "$pid" 2>/dev/null || break
    if curl -fsS --max-time 2 "http://127.0.0.1:$port/health" 2>/dev/null | grep -q '"status":"ok"'; then
      kill "$pid" 2>/dev/null; wait "$pid" 2>/dev/null || true
      rm -f "$log"
      return 0
    fi
  done
  kill "$pid" 2>/dev/null; wait "$pid" 2>/dev/null || true
  echo "Ausgabe der neuen Version:"
  tail -20 "$log" | sed 's/^/  /'
  rm -f "$log"
  return 1
}

# Alles steht in main, damit bash das Skript ganz gelesen hat, bevor es sich selbst überschreibt
main() {
  local repo="kschnieders/zwijg" paket="" back=0 choice=""
  while [ $# -gt 0 ]; do
    case "$1" in
      --paket) paket="$2"; shift 2 ;;
      --zurueck) back=1; shift ;;
      --sicherung) choice="$2"; shift 2 ;;
      --repo) repo="$2"; shift 2 ;;
      *) echo "Unbekannte Angabe: $1"; return 2 ;;
    esac
  done

  DIR="$(cd "$(dirname "$0")" && pwd)"
  BACKUPS="$DIR/update-sicherungen"

  if pgrep -f "$DIR/Zwijg.Gateway" > /dev/null; then
    echo "Zwijg läuft noch. Bitte zuerst beenden, zum Beispiel mit: sudo systemctl stop zwijg"
    return 1
  fi

  # Zurück auf den Stand vor dem letzten Update
  if [ "$back" = 1 ]; then
    local source before
    if [ -n "$choice" ]; then
      source="$BACKUPS/$choice"
    else
      source="$(ls -1d "$BACKUPS"/*/ 2>/dev/null | grep -v -- '-vor-zurueck/$' | sort -r | head -1 || true)"
      source="${source%/}"
    fi
    if [ -z "$source" ] || [ ! -d "$source/programm" ]; then
      echo "Keine passende Sicherung in $BACKUPS gefunden."
      return 1
    fi
    # Auch den jetzigen Stand sichern, damit sich das Zurückgehen wieder rückgängig machen lässt
    before="$(new_backup -vor-zurueck)"
    restore_backup "$source"
    echo "Fertig. Zwijg $(version_of "$DIR") aus $(basename "$source") ist wiederhergestellt, bitte wieder starten."
    echo "Der Stand davor liegt in $before"
    return 0
  fi

  local current json latest zip digest="" new backup
  current="$(version_of "$DIR")"
  TMP="$(mktemp -d)"
  trap 'rm -rf "$TMP"' EXIT

  if [ -n "$paket" ]; then
    zip="$paket"
  else
    json="$(curl -fsSL -H 'User-Agent: Zwijg-Update' "https://api.github.com/repos/$repo/releases/latest")"
    latest="$(printf '%s' "$json" | grep -o '"tag_name": *"[^"]*"' | head -1 | sed -E 's/.*"v?([^"]*)"$/\1/')"
    if [ -z "$latest" ]; then
      echo "Keine Version bei GitHub gefunden."
      return 1
    fi
    if ! newer "$latest" "$current"; then
      echo "Zwijg $current ist aktuell."
      return 0
    fi
    # Prüfsumme des passenden Pakets aus der Antwort von GitHub
    digest="$(printf '%s' "$json" | grep -o '"name": *"[^"]*"\|"digest": *"[^"]*"' \
      | awk -F'"' -v n="zwijg-$latest-linux-x64.zip" '$2 == "name" { hit = ($4 == n) } $2 == "digest" && hit { print $4; exit }')"
    echo "Lade Zwijg $latest herunter"
    zip="$TMP/zwijg.zip"
    curl -fsSL "https://github.com/$repo/releases/download/v$latest/zwijg-$latest-linux-x64.zip" -o "$zip"
  fi

  # Prüfsumme von GitHub, damit kein beschädigter oder unterwegs veränderter Download installiert wird
  if [ -n "$digest" ]; then
    if [ "sha256:$(sha256sum "$zip" | cut -d' ' -f1)" != "$digest" ]; then
      echo "Der Download ist beschädigt, die Prüfsumme stimmt nicht. Es wurde nichts geändert."
      return 1
    fi
    echo "Prüfsumme stimmt"
  fi

  # Erst auspacken und prüfen, dann erst etwas am installierten Zwijg ändern
  unzip -q "$zip" -d "$TMP/neu"
  if [ ! -f "$TMP/neu/Zwijg.Gateway" ] || [ ! -f "$TMP/neu/VERSION" ]; then
    echo "Das Paket ist unvollständig. Es wurde nichts geändert."
    return 1
  fi
  new="$(version_of "$TMP/neu")"
  if ! newer "$new" "$current"; then
    echo "Das Paket enthält Zwijg $new, installiert ist schon $current. Es wurde nichts geändert."
    return 0
  fi
  echo "Update von $current auf $new"

  backup="$(new_backup "")"
  echo "Programm und Daten gesichert in $backup"

  if ! install_new "$new"; then
    echo "Stelle Zwijg $current wieder her"
    restore_backup "$backup"
    echo "Zwijg $current ist wieder installiert, Daten wie vor dem Update. Bitte wieder starten."
    return 1
  fi

  ls -1d "$BACKUPS"/*/ | sort -r | tail -n +$((KEEP_BACKUPS + 1)) | while IFS= read -r old; do rm -rf "$old"; done
  echo "Fertig. Zwijg $new ist installiert, bitte wieder starten."
  echo "Zurück zu $current geht mit: ./update.sh --zurueck"
}

# Neue Dateien kopieren und zur Probe starten. Gibt einen Fehler zurück, wenn etwas nicht klappt.
install_new() {
  local f name
  for f in "$TMP"/neu/* "$TMP"/neu/.[!.]*; do
    [ -e "$f" ] || continue
    name="$(basename "$f")"
    in_list "$name" "$UNTOUCHED" || cp -a "$f" "$DIR/" || { echo "Fehler beim Kopieren von $name"; return 1; }
  done
  chmod +x "$DIR/Zwijg.Gateway" "$DIR/update.sh"

  # Ohne Daten ist es eine neue Installation, ein Probestart würde nur einen Startschlüssel erzeugen, den keiner sieht
  if [ -f "$DIR/data/settings.json" ]; then
    echo "Starte Zwijg $1 zur Probe"
    if ! test_start; then
      echo "Fehler: Zwijg $1 ist beim Probestart nicht angelaufen."
      return 1
    fi
    echo "Probestart in Ordnung"
  fi
}

main "$@"
exit
