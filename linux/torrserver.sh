#!/usr/bin/env bash
# Скрипт переименован в monoserver.sh (служба MonoServer, с 1.0.18). Этот —
# переходник для прежних команд: скачивает monoserver.sh рядом с собой и
# запускает с теми же параметрами; служба torrserver-tizen при install/update
# переносится в monoserver с её портом и данными.
set -u
HERE="$(cd "$(dirname "$(readlink -f "$0")")" && pwd)"
URL="https://raw.githubusercontent.com/cash94/MonoServer/main/linux/monoserver.sh"
NEW="$HERE/monoserver.sh"
echo "torrserver.sh теперь monoserver.sh — скачиваю $URL"
if command -v curl >/dev/null 2>&1; then curl -fsSL "$URL" -o "$NEW.tmp"
elif command -v wget >/dev/null 2>&1; then wget -qO "$NEW.tmp" "$URL"
else echo "Нужен curl или wget." >&2; exit 1; fi || { rm -f "$NEW.tmp"; echo "Не скачалось: $URL" >&2; exit 1; }
mv -f "$NEW.tmp" "$NEW" && chmod +x "$NEW"
exec bash "$NEW" "$@"
