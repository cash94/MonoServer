#!/bin/sh
# MonoServer на ASUSTOR (ADM): служба без systemd и Docker.
#
#   monoserverctl.sh start     — запустить (App Central делает это сам при загрузке NAS)
#   monoserverctl.sh stop      — остановить
#   monoserverctl.sh restart   — перезапустить
#   monoserverctl.sh status    — запущена ли, версия и адрес
#   monoserverctl.sh log       — журнал в реальном времени (Ctrl+C — выход)
#   monoserverctl.sh code      — код первого входа в веб-интерфейс
#   monoserverctl.sh update    — скачать последний релиз MonoServer с GitHub и перезапустить
#
# Программа — runtime/MonoServer рядом с bin/ (из пакета или из update), данные —
# /volume1/MonoServer (MONOSERVER_DATA — другой каталог), порт 8099 (другой — числом
# в файле monoserver.port в каталоге данных; ярлык ADM при этом ведёт на 8099).
# Служба работает под присмотром: упала — через 10 с запускается снова.
# Журнал — monoserver.log в каталоге данных (больше 5 МБ — в monoserver.log.1).

REPO="cash94/MonoServer"
DEFAULT_PORT=8099
SELF="$(cd "$(dirname "$0")" && pwd)/$(basename "$0")"
PKG_DIR="${APKG_PKG_DIR:-$(cd "$(dirname "$0")/.." && pwd)}"
BIN="$PKG_DIR/runtime/MonoServer"
DATA="${MONOSERVER_DATA:-/volume1/MonoServer}"
LOG="$DATA/monoserver.log"
LOG_MAX=5242880
RUN_DIR=/var/run/monoserver
[ -d /var/run ] && [ -w /var/run ] || RUN_DIR=/tmp/monoserver
SUP_PID="$RUN_DIR/supervisor.pid"
CHILD_PID="$RUN_DIR/monoserver.pid"
STOP_FLAG="$RUN_DIR/stop"

say() { echo "$*"; }
stamp() { date '+%Y-%m-%d %H:%M:%S'; }
logline() { echo "$(stamp) [monoserverctl] $*" >> "$LOG"; }

port() {
    p="$(tr -dc '0-9' < "$DATA/monoserver.port" 2>/dev/null)"
    case "$p" in ''|0*) p="$DEFAULT_PORT" ;; esac
    [ "$p" -ge 1 ] 2>/dev/null && [ "$p" -le 65535 ] || p="$DEFAULT_PORT"
    echo "$p"
}

alive() { [ -n "$1" ] && kill -0 "$1" 2>/dev/null; }
pid_of() { cat "$1" 2>/dev/null | tr -dc '0-9'; }

# Ответ службы на /echo (версия) или пусто
echo_req() {
    url="http://127.0.0.1:$(port)/echo"
    if command -v curl >/dev/null 2>&1; then curl -fs --max-time 3 "$url" 2>/dev/null
    elif command -v wget >/dev/null 2>&1; then wget -q -T 3 -O - "$url" 2>/dev/null
    fi
}

lan_ip() {
    ip="${AS_NAS_INET4_IP1:-}"
    [ -z "$ip" ] && ip="$(ip -4 route get 1.1.1.1 2>/dev/null | sed -n 's/.* src \([0-9.]*\).*/\1/p' | head -n 1)"
    [ -z "$ip" ] && ip="$(hostname -I 2>/dev/null | awk '{print $1}')"
    echo "${ip:-IP_NAS}"
}

prepare() {
    mkdir -p "$DATA" "$RUN_DIR" || return 1
    chmod 755 "$BIN" 2>/dev/null
    return 0
}

# Окружение .NET: распаковка библиотек однофайловой сборки — на том, а не в
# /tmp (там ОЗУ) и не в $HOME, которого у служб ADM может не быть; нет ICU —
# инвариантная культура (как сборка linux-musl)
dotnet_env() {
    [ -n "${HOME:-}" ] && [ -d "$HOME" ] || export HOME="$DATA"
    export DOTNET_BUNDLE_EXTRACT_BASE_DIR="$DATA/.net"
    if ! { ldconfig -p 2>/dev/null | grep -q libicuuc; } && ! ls /lib/libicuuc.so* /lib64/libicuuc.so* /usr/lib/libicuuc.so* /usr/lib64/libicuuc.so* /usr/local/lib/libicuuc.so* /usr/lib/x86_64-linux-gnu/libicuuc.so* /usr/lib/aarch64-linux-gnu/libicuuc.so* >/dev/null 2>&1; then
        export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
        export DOTNET_SYSTEM_GLOBALIZATION_PREDEFINED_CULTURES_ONLY=false
    fi
}

rotate_log() {
    size="$(wc -c < "$LOG" 2>/dev/null || echo 0)"
    if [ "$size" -gt "$LOG_MAX" ] 2>/dev/null; then
        # Копия и обрезка: программа пишет в открытый файл дописыванием
        cp -f "$LOG" "$LOG.1" && : > "$LOG"
    fi
}

# Сторож: запускает программу и перезапускает, если она упала. Его останавливает stop
supervise() {
    echo $$ > "$SUP_PID"
    dotnet_env
    while :; do
        rotate_log
        p="$(port)"
        logline "запуск MonoServer: порт $p, данные $DATA"
        "$BIN" "$p" "$DATA" < /dev/null >> "$LOG" 2>&1 &
        child=$!
        echo "$child" > "$CHILD_PID"
        while alive "$child"; do
            sleep 10
            rotate_log
        done
        wait "$child"
        code=$?
        rm -f "$CHILD_PID"
        [ -f "$STOP_FLAG" ] && break
        logline "MonoServer завершился (код $code) — перезапуск через 10 с"
        sleep 10
        [ -f "$STOP_FLAG" ] && break
    done
    rm -f "$SUP_PID"
}

# Процессы ищутся по /proc, а не только по PID-файлам: файлы в /var/run пропадают,
# а служба, запущенная прежним пакетом (другой сборщик, свой PID-файл), осталась бы
# жить и держать порт. Программа MonoServer — наша, если это файл пакета (и
# подменённый обновлением — «(deleted)») или её аргумент — наш каталог данных.
# Другие экземпляры MonoServer (свои данные, другой путь) не трогаются.
server_pids() {
    real="$(readlink -f "$BIN" 2>/dev/null)"
    for d in /proc/[0-9]*; do
        pid="${d#/proc/}"
        [ "$pid" = "$$" ] && continue
        comm=""
        read -r comm < "$d/comm" 2>/dev/null || continue
        [ "$comm" = "MonoServer" ] || continue
        exe="$(readlink "$d/exe" 2>/dev/null)"
        exe="${exe% (deleted)}"
        args="$(tr '\0' '\n' < "$d/cmdline" 2>/dev/null)"
        argv0="$(echo "$args" | sed -n 1p)"
        if [ -n "$exe" ] && { [ "$exe" = "$BIN" ] || [ "$exe" = "$real" ]; }; then echo "$pid"; continue; fi
        [ "$argv0" = "$BIN" ] && { echo "$pid"; continue; }
        echo "$args" | sed -n '2,$p' | grep -qxF "$DATA" && echo "$pid"
    done
}

# Сторожа этого скрипта (sh … monoserverctl.sh _supervise)
supervisor_pids() {
    for d in /proc/[0-9]*; do
        pid="${d#/proc/}"
        [ "$pid" = "$$" ] && continue
        comm=""
        read -r comm < "$d/comm" 2>/dev/null || continue
        case "$comm" in sh|ash|bash|dash|busybox) ;; *) continue ;; esac
        c="$(tr '\0' ' ' < "$d/cmdline" 2>/dev/null)"
        case "$c" in *"monoserverctl.sh _supervise"*) echo "$pid" ;; esac
    done
}

is_running() { [ -n "$(server_pids)" ]; }

cmd_start() {
    prepare || { say "Не создать каталог данных $DATA"; return 1; }
    if is_running; then
        [ -n "$(supervisor_pids)" ] && { say "MonoServer уже запущен."; return 0; }
        # Работает без сторожа (запущен прежним пакетом или сторож убит) — перезапустить под сторожем
        cmd_stop > /dev/null
    fi
    [ -x "$BIN" ] || { say "Нет программы $BIN — переустановите приложение или: $SELF update"; return 1; }
    rm -f "$STOP_FLAG" "$CHILD_PID"
    if command -v setsid >/dev/null 2>&1; then
        setsid sh "$SELF" _supervise < /dev/null > /dev/null 2>&1 &
    else
        nohup sh "$SELF" _supervise < /dev/null > /dev/null 2>&1 &
    fi
    # Ответила ли (до 20 с) — для того, кто запускал из SSH
    i=0
    while [ $i -lt 20 ]; do
        v="$(echo_req)"
        [ -n "$v" ] && { say "MonoServer запущен: $v, http://$(lan_ip):$(port)/"; return 0; }
        sleep 1; i=$((i + 1))
    done
    say "MonoServer запускается (пока не отвечает) — журнал: $SELF log"
    return 0
}

cmd_stop() {
    mkdir -p "$RUN_DIR" 2>/dev/null
    touch "$STOP_FLAG" 2>/dev/null
    # Сначала сторожа — чтобы не перезапустили
    for p in $(pid_of "$SUP_PID") $(supervisor_pids); do
        alive "$p" && kill "$p" 2>/dev/null
    done
    pids="$(server_pids)"
    if [ -n "$pids" ]; then
        # SIGTERM: программа закрывает торренты и сохраняет кэш DHT (до 15 с)
        kill -TERM $pids 2>/dev/null
        i=0
        while [ -n "$(server_pids)" ] && [ $i -lt 20 ]; do sleep 1; i=$((i + 1)); done
        pids="$(server_pids)"
        [ -n "$pids" ] && kill -9 $pids 2>/dev/null
        logline "MonoServer остановлен"
    fi
    rm -f "$SUP_PID" "$CHILD_PID" "$STOP_FLAG" "$DATA/.monoserver-adm.pid"
    say "MonoServer остановлен."
    return 0
}

cmd_status() {
    pids="$(server_pids | tr '\n' ' ' | sed 's/ $//')"
    if [ -n "$pids" ]; then
        v="$(echo_req)"
        if [ -n "$v" ]; then
            say "MonoServer работает: $v (pid $pids)"
        else
            say "MonoServer запущен (pid $pids), но на порту $(port) пока не отвечает — журнал: $SELF log"
        fi
        [ -z "$(supervisor_pids)" ] && say "  без сторожа (после падения сам не поднимется) — перезапустите: $SELF restart"
        say "  в домашней сети: http://$(lan_ip):$(port)/"
        say "  данные: $DATA"
        return 0
    fi
    say "MonoServer не запущен."
    return 3
}

cmd_log() {
    [ -f "$LOG" ] || { say "Журнала ещё нет: $LOG"; return 1; }
    tail -n 100 -f "$LOG"
}

cmd_code() {
    f="$DATA/setup-code.txt"
    if [ -f "$f" ]; then
        say "Код первого входа: $(tr -d '\r\n' < "$f")"
        say "Откройте http://$(lan_ip):$(port)/, введите код и задайте пароль пользователя admin."
    else
        say "Кода нет ($f): пароль admin уже задан — входите им, или служба ещё не запускалась."
    fi
}

cmd_update() {
    case "$(uname -m)" in
        x86_64|amd64) arch=x64 ;;
        aarch64|arm64) arch=arm64 ;;
        *) say "Архитектура $(uname -m) не поддерживается."; return 1 ;;
    esac
    asset="MonoServer-linux-$arch.tar.gz"
    url="https://github.com/$REPO/releases/latest/download/$asset"
    prepare || return 1
    tmp="$DATA/.update"
    rm -rf "$tmp" && mkdir -p "$tmp" || return 1
    say "Скачиваю $url"
    if command -v curl >/dev/null 2>&1; then
        curl -fL -o "$tmp/$asset" "$url"
    else
        wget -O "$tmp/$asset" "$url"
    fi || { rm -rf "$tmp"; say "Не скачалось — служба не тронута."; return 1; }
    tar xzf "$tmp/$asset" -C "$tmp" && [ -f "$tmp/MonoServer" ] || { rm -rf "$tmp"; say "В архиве нет программы MonoServer — служба не тронута."; return 1; }
    before="$(echo_req)"
    cmd_stop > /dev/null
    mkdir -p "$PKG_DIR/runtime"
    cp -f "$tmp/MonoServer" "$BIN" && chmod 755 "$BIN" || { rm -rf "$tmp"; say "Не заменить программу."; cmd_start; return 1; }
    [ -f "$tmp/libclrgc.so" ] && cp -f "$tmp/libclrgc.so" "$PKG_DIR/runtime/libclrgc.so"
    [ -f "$tmp/THIRD-PARTY-NOTICES.txt" ] && cp -f "$tmp/THIRD-PARTY-NOTICES.txt" "$PKG_DIR/THIRD-PARTY-NOTICES.txt"
    rm -rf "$tmp"
    cmd_start > /dev/null
    i=0; after=""
    while [ $i -lt 20 ]; do after="$(echo_req)"; [ -n "$after" ] && break; sleep 1; i=$((i + 1)); done
    say "Обновлено: ${before:-?} → ${after:-не отвечает (журнал: $SELF log)}"
}

case "${1:-}" in
    start) cmd_start ;;
    stop) cmd_stop ;;
    restart) cmd_stop > /dev/null; cmd_start ;;
    status) cmd_status ;;
    log) cmd_log ;;
    code) cmd_code ;;
    update) cmd_update ;;
    prepare) prepare ;;
    _supervise) supervise ;;
    *) sed -n '2,17p' "$SELF" | sed 's/^# \{0,1\}//'; exit 1 ;;
esac
