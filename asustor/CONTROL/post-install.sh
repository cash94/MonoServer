#!/bin/sh
# После установки и обновления: права на программу, каталог данных — и запуск.
# App Central потом сам вызывает start-stop.sh start; повторный start ничего не делает
PKG_DIR="${APKG_PKG_DIR:-/usr/local/AppCentral/monoserver}"
chmod 755 "$PKG_DIR/bin/monoserverctl.sh" "$PKG_DIR/runtime/MonoServer" 2>/dev/null
sh "$PKG_DIR/bin/monoserverctl.sh" prepare
sh "$PKG_DIR/bin/monoserverctl.sh" start
exit 0
