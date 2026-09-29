#!/usr/bin/env bash
# Zwijg aktualisieren. Zwijg vorher beenden und im Ordner von Zwijg ausführen: ./update.sh
# Der Ordner data (Einstellungen, Protokoll, Verlauf, Schlüssel) bleibt erhalten und wird vorher gesichert.
set -euo pipefail

# Alles steht in main, damit bash das Skript ganz gelesen hat, bevor es sich selbst überschreibt
main() {
  local repo="${1:-kschnieders/zwijg}"
  local dir current json latest url tmp backup
  dir="$(cd "$(dirname "$0")" && pwd)"
  current="$(cat "$dir/VERSION" 2>/dev/null || echo 0.0.0)"

  json="$(curl -fsSL -H 'User-Agent: Zwijg-Update' "https://api.github.com/repos/$repo/releases/latest")"
  latest="$(printf '%s' "$json" | grep -o '"tag_name": *"[^"]*"' | head -1 | sed -E 's/.*"v?([^"]*)"$/\1/')"

  if [ "$(printf '%s\n%s\n' "$latest" "$current" | sort -V | tail -1)" = "$current" ]; then
    echo "Zwijg $current ist aktuell."
    return 0
  fi

  if pgrep -f "$dir/Zwijg.Gateway" > /dev/null; then
    echo "Zwijg läuft noch. Bitte zuerst beenden, zum Beispiel mit: sudo systemctl stop zwijg"
    return 1
  fi

  echo "Update von $current auf $latest"
  if [ -d "$dir/data" ]; then
    backup="$dir/data-sicherung-$current-$(date +%Y%m%d-%H%M%S).tar.gz"
    tar -czf "$backup" -C "$dir" data
    echo "Daten gesichert in $backup"
  fi

  tmp="$(mktemp -d)"
  trap 'rm -rf "$tmp"' EXIT
  url="https://github.com/$repo/releases/download/v$latest/zwijg-$latest-linux-x64.zip"
  curl -fsSL "$url" -o "$tmp/zwijg.zip"
  unzip -q "$tmp/zwijg.zip" -d "$tmp/neu"

  # appsettings.json wird nur beim ersten Start gelesen, eigene Änderungen bleiben so erhalten
  rm -rf "$tmp/neu/data" "$tmp/neu/appsettings.json"
  cp -a "$tmp/neu/." "$dir/"
  chmod +x "$dir/Zwijg.Gateway" "$dir/update.sh"

  echo "Fertig. Zwijg $latest ist installiert, bitte wieder starten."
}

main "$@"
exit
