#!/bin/sh
# App Central: запуск при загрузке NAS, кнопки «Запустить»/«Остановить» — всё в monoserverctl.sh
PKG_DIR="${APKG_PKG_DIR:-/usr/local/AppCentral/monoserver}"
case "$1" in
    start|stop|restart) exec sh "$PKG_DIR/bin/monoserverctl.sh" "$1" ;;
    *) echo "Usage: $0 {start|stop|restart}"; exit 1 ;;
esac
