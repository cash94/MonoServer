#!/bin/sh
# Перед удалением и обновлением: остановить службу (данные в /volume1/MonoServer остаются)
PKG_DIR="${APKG_PKG_DIR:-/usr/local/AppCentral/monoserver}"
[ -f "$PKG_DIR/bin/monoserverctl.sh" ] && sh "$PKG_DIR/bin/monoserverctl.sh" stop
exit 0
