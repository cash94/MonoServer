#!/usr/bin/env bash
# MonoServer (TorrServer на C#) для Linux: служба systemd или запуск в терминале.
#
#   ./monoserver.sh install     — поставить службу и запустить (спросит порт, Enter — 8090)
#   ./monoserver.sh update      — обновить службу до последнего релиза (порт прежний)
#   ./monoserver.sh uninstall   — остановить и удалить службу (данные остаются)
#   ./monoserver.sh status      — состояние службы и адрес
#   ./monoserver.sh log         — журнал службы (Ctrl+C — выход)
#   ./monoserver.sh code        — код первого входа в веб-интерфейс (пока пароль admin не задан)
#   ./monoserver.sh run         — запустить в этом терминале (Ctrl+C — стоп)
#   ./monoserver.sh             — то же, что install
#
# Параметры: -p ПОРТ (без вопроса), -y (порт по умолчанию без вопроса),
# -d КАТАЛОГ (данные; по умолчанию ~/.local/share/monoserver),
# --system (служба для всей системы через sudo, а не пользовательская).
#
# Порт проверяется: занят — скрипт скажет кем и спросит другой. Программа
# MonoServer ищется рядом со скриптом (так лежат файлы в архиве
# MonoServer-linux-*.tar.gz); нет её — скрипт предложит скачать последний
# релиз под архитектуру (x64 или arm64). Служба запускается при загрузке
# компьютера; программа и скрипт для неё копируются в постоянный каталог, так
# что архив после установки можно удалить. Служба прежнего имени
# torrserver-tizen (до 1.0.18) при install/update переносится сюда — с её
# портом и данными.
set -u

NAME="monoserver"
OLD_NAME="torrserver-tizen"   # до 1.0.18
DEFAULT_PORT=8090
REPO="cash94/MonoServer"
HERE="$(cd "$(dirname "$(readlink -f "$0")")" && pwd)"
SRC_BIN="$HERE/MonoServer"
DATA=""
PORT=""
ASK=1
SYSTEM=0
CMD="install"

usage() { sed -n '2,24p' "$0" | sed 's/^# \{0,1\}//'; }
die() { echo "$*" >&2; exit 1; }

while [ $# -gt 0 ]; do
    case "$1" in
        install|update|uninstall|status|log|code|run) CMD="$1"; shift ;;
        -p|--port) PORT="${2:-}"; ASK=0; shift 2 ;;
        -d|--data) DATA="${2:-}"; shift 2 ;;
        -y|--yes) ASK=0; shift ;;
        --system) SYSTEM=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Неизвестный параметр: $1" >&2; usage >&2; exit 2 ;;
    esac
done

# ---------- где что лежит ----------

if [ "$SYSTEM" = 1 ]; then
    SUDO=""; [ "$(id -u)" -ne 0 ] && SUDO="sudo"
    RUN_USER="${SUDO_USER:-$(id -un)}"
    RUN_HOME="$(getent passwd "$RUN_USER" | cut -d: -f6)"
    INSTALL_DIR="/opt/$NAME"
    UNIT_PATH="/etc/systemd/system/$NAME.service"
    OLD_INSTALL_DIR="/opt/$OLD_NAME"
    OLD_UNIT_PATH="/etc/systemd/system/$OLD_NAME.service"
    SCTL() { $SUDO systemctl "$@"; }
    JCTL() { $SUDO journalctl -u "$NAME" "$@"; }
else
    SUDO=""
    RUN_USER="$(id -un)"
    RUN_HOME="$HOME"
    INSTALL_DIR="$HOME/.local/lib/$NAME"
    UNIT_PATH="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user/$NAME.service"
    OLD_INSTALL_DIR="$HOME/.local/lib/$OLD_NAME"
    OLD_UNIT_PATH="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user/$OLD_NAME.service"
    SCTL() { systemctl --user "$@"; }
    JCTL() { journalctl --user -u "$NAME" "$@"; }
fi
DATA_DEFAULT=0
[ -z "$DATA" ] && [ -z "${TS_DATA:-}" ] && DATA_DEFAULT=1
[ -z "$DATA" ] && DATA="${TS_DATA:-$RUN_HOME/.local/share/$NAME}"
OLD_DATA="$RUN_HOME/.local/share/$OLD_NAME"
CONF="$INSTALL_DIR/port"

lan_ip() { hostname -I 2>/dev/null | awk '{print $1}'; }

print_addresses() {
    local ip; ip="$(lan_ip)"
    echo "  в этом компьютере: http://127.0.0.1:$1/"
    [ -n "$ip" ] && echo "  в домашней сети:   http://$ip:$1/  (адрес для TorrStream, Lampa)"
}

# ---------- порт ----------

valid_port() {
    case "$1" in ''|*[!0-9]*) return 1 ;; esac
    [ "$1" -ge 1 ] && [ "$1" -le 65535 ]
}

# Кто слушает порт (TCP): пусто — свободен
port_owner() {
    local p="$1" out=""
    if command -v ss >/dev/null 2>&1; then
        out="$(ss -Hltnp "sport = :$p" 2>/dev/null)"
        [ -z "$out" ] && out="$(ss -Hltn "sport = :$p" 2>/dev/null)"
    elif command -v netstat >/dev/null 2>&1; then
        out="$(netstat -ltnp 2>/dev/null | awk -v p=":$p" '$4 ~ p"$"')"
    elif command -v lsof >/dev/null 2>&1; then
        out="$(lsof -nP -iTCP:"$p" -sTCP:LISTEN 2>/dev/null | tail -n +2)"
    else
        if (exec 3<>"/dev/tcp/127.0.0.1/$p") 2>/dev/null; then out="занят"; fi
    fi
    printf '%s' "$out"
}

describe_owner() {
    # users:(("TorrServer",pid=123,fd=7)) → TorrServer (pid 123)
    local who
    who="$(printf '%s' "$1" | grep -o 'users:(("[^"]*",pid=[0-9]*' | head -1 | sed 's/users:(("\([^"]*\)",pid=\([0-9]*\)/\1 (pid \2)/')"
    [ -n "$who" ] && printf '%s' "$who" || printf 'другая программа'
}

choose_port() {
    local suggest="$DEFAULT_PORT" owner
    [ -f "$CONF" ] && suggest="$(cat "$CONF" 2>/dev/null || echo "$DEFAULT_PORT")"
    valid_port "$suggest" || suggest="$DEFAULT_PORT"
    while :; do
        if [ "$ASK" = 1 ] && [ -z "$PORT" ] && [ -t 0 ]; then
            read -r -p "Порт MonoServer [$suggest]: " PORT
        fi
        [ -z "$PORT" ] && PORT="$suggest"
        if ! valid_port "$PORT"; then
            echo "«$PORT» — не номер порта (нужно число 1–65535)." >&2
            [ "$ASK" = 1 ] && [ -t 0 ] && { PORT=""; continue; }
            exit 1
        fi
        owner="$(port_owner "$PORT")"
        if [ -n "$owner" ]; then
            echo "Порт $PORT занят: $(describe_owner "$owner")." >&2
            [ "$ASK" = 1 ] && [ -t 0 ] && { PORT=""; continue; }
            die "Укажите другой: $0 $CMD -p <порт>"
        fi
        return 0
    done
}

# ---------- программа ----------

# Последний релиз: программа (и скрипт) — в каталог $1 (по умолчанию рядом со скриптом)
download() {
    local dest="${1:-$HERE}" arch asset url tmp
    case "$(uname -m)" in
        x86_64|amd64) arch=x64 ;;
        aarch64|arm64) arch=arm64 ;;
        *) echo "Архитектура $(uname -m) не поддерживается: есть сборки для x86_64 и aarch64 (64-битная ARM)." >&2; return 1 ;;
    esac
    asset="MonoServer-linux-$arch.tar.gz"
    url="https://github.com/$REPO/releases/latest/download/$asset"
    tmp="$(mktemp -d)" || return 1
    echo "Скачиваю $url"
    if command -v curl >/dev/null 2>&1; then
        curl -fL --progress-bar -o "$tmp/$asset" "$url" || { rm -rf "$tmp"; return 1; }
    elif command -v wget >/dev/null 2>&1; then
        wget -q --show-progress -O "$tmp/$asset" "$url" || { rm -rf "$tmp"; return 1; }
    else
        echo "Нужен curl или wget." >&2; rm -rf "$tmp"; return 1
    fi
    tar xzf "$tmp/$asset" -C "$tmp" || { rm -rf "$tmp"; return 1; }
    [ -f "$tmp/MonoServer" ] || { echo "В архиве нет программы MonoServer." >&2; rm -rf "$tmp"; return 1; }
    mkdir -p "$dest" && cp "$tmp/MonoServer" "$dest/MonoServer" && chmod +x "$dest/MonoServer" || { rm -rf "$tmp"; return 1; }
    # Скрипт из архива (1.0.12 и новее) — рядом, если обновляем не его же
    [ -f "$tmp/monoserver.sh" ] && [ "$dest" != "$HERE" ] && cp "$tmp/monoserver.sh" "$dest/monoserver.sh" && chmod +x "$dest/monoserver.sh"
    rm -rf "$tmp"
}

ensure_binary() {
    [ -x "$SRC_BIN" ] && return 0
    # Скрипт уже из каталога службы — программа там
    [ -x "$INSTALL_DIR/MonoServer" ] && { SRC_BIN="$INSTALL_DIR/MonoServer"; return 0; }
    echo "Рядом со скриптом нет программы MonoServer ($SRC_BIN)."
    local answer="n"
    if [ -t 0 ]; then read -r -p "Скачать последний релиз с GitHub? [Y/n]: " answer; answer="${answer:-y}"; fi
    case "$answer" in
        y|Y|д|Д|yes|да) download || die "Не скачалось. Архив вручную: https://github.com/$REPO/releases/latest" ;;
        *) die "Архив: https://github.com/$REPO/releases/latest — распакуйте рядом со скриптом." ;;
    esac
}

# ---------- команды ----------

service_exists() { [ -f "$UNIT_PATH" ] || $SUDO test -f "$UNIT_PATH"; }
old_service_exists() { [ -f "$OLD_UNIT_PATH" ] || $SUDO test -f "$OLD_UNIT_PATH"; }

# Служба прежнего имени (torrserver-tizen, до 1.0.18) → monoserver: тот же
# порт и данные; старая служба и её каталог удаляются
migrate_old() {
    old_service_exists || return 0
    local oldport
    oldport="$($SUDO cat "$OLD_INSTALL_DIR/port" 2>/dev/null)"
    echo "Найдена служба прежнего имени $OLD_NAME — переношу в $NAME."
    SCTL disable --now "$OLD_NAME" 2>/dev/null
    $SUDO rm -f "$OLD_UNIT_PATH"
    SCTL daemon-reload
    if [ "$DATA_DEFAULT" = 1 ] && [ -d "$OLD_DATA" ] && [ ! -e "$DATA" ]; then
        $SUDO mv "$OLD_DATA" "$DATA" && echo "Данные: $OLD_DATA → $DATA"
    fi
    $SUDO rm -rf "$OLD_INSTALL_DIR"
    # Прежний порт — без вопроса
    if valid_port "$oldport" && [ -z "$PORT" ]; then PORT="$oldport"; ASK=0; fi
}

cmd_install() {
    command -v systemctl >/dev/null 2>&1 || die "Нет systemd (systemctl) — запустите в терминале: $0 run"
    migrate_old
    ensure_binary
    # Переустановка: остановить старую службу, чтобы её порт не считался занятым
    if service_exists; then
        echo "Служба $NAME уже стоит — обновляю."
        SCTL stop "$NAME" 2>/dev/null
    fi
    choose_port

    $SUDO mkdir -p "$INSTALL_DIR" || exit 1
    if [ "$(readlink -f "$SRC_BIN")" != "$(readlink -f "$INSTALL_DIR/MonoServer")" ]; then
        $SUDO install -m 755 "$SRC_BIN" "$INSTALL_DIR/MonoServer" || exit 1
    fi
    if [ "$(readlink -f "$0")" != "$(readlink -f "$INSTALL_DIR/monoserver.sh")" ]; then
        $SUDO install -m 755 "$0" "$INSTALL_DIR/monoserver.sh" || exit 1
    fi
    [ -f "$HERE/THIRD-PARTY-NOTICES.txt" ] && $SUDO install -m 644 "$HERE/THIRD-PARTY-NOTICES.txt" "$INSTALL_DIR/" 2>/dev/null
    echo "$PORT" | $SUDO tee "$CONF" >/dev/null
    if [ "$SYSTEM" = 1 ]; then
        $SUDO mkdir -p "$DATA" && $SUDO chown "$RUN_USER" "$DATA"
    else
        mkdir -p "$DATA"
    fi

    local unit_user="" wanted="default.target"
    if [ "$SYSTEM" = 1 ]; then unit_user="User=$RUN_USER"; wanted="multi-user.target"; fi
    $SUDO mkdir -p "$(dirname "$UNIT_PATH")"
    $SUDO tee "$UNIT_PATH" >/dev/null <<EOF
# Создано monoserver.sh ($REPO). Порт и каталог — повторный ./monoserver.sh install
[Unit]
Description=MonoServer — TorrServer на C# (порт $PORT)
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
$unit_user
ExecStart="$INSTALL_DIR/MonoServer" $PORT "$DATA"
Restart=on-failure
RestartSec=5

[Install]
WantedBy=$wanted
EOF
    SCTL daemon-reload || exit 1
    SCTL enable --now "$NAME" || die "Служба не запустилась — журнал: $0 log"

    # Пользовательская служба без входа в систему стартует, только если включено «linger»
    if [ "$SYSTEM" = 0 ] && command -v loginctl >/dev/null 2>&1; then
        if [ "$(loginctl show-user "$RUN_USER" -p Linger --value 2>/dev/null)" != "yes" ]; then
            if loginctl enable-linger "$RUN_USER" 2>/dev/null || sudo loginctl enable-linger "$RUN_USER"; then
                echo "Автозапуск при загрузке включён (loginctl enable-linger)."
            else
                echo "Внимание: без «loginctl enable-linger $RUN_USER» служба стартует только после входа в систему." >&2
            fi
        fi
    fi

    # Ответила ли
    local i
    for i in 1 2 3 4 5 6 7 8 9 10; do
        if curl -fs --max-time 2 "http://127.0.0.1:$PORT/echo" >/dev/null 2>&1; then break; fi
        sleep 1
    done
    echo "Служба $NAME запущена и включена при загрузке: порт $PORT, данные $DATA"
    print_addresses "$PORT"
    echo "  состояние: $INSTALL_DIR/monoserver.sh status$([ "$SYSTEM" = 1 ] && echo ' --system'), журнал: … log"
}

cmd_update() {
    command -v systemctl >/dev/null 2>&1 || die "Нет systemd (systemctl)."
    # Стоит служба прежнего имени — перенести и поставить свежий релиз
    if ! service_exists && old_service_exists; then
        migrate_old
        [ -x "$SRC_BIN" ] || download "$HERE" || die "Не скачалось. Архив вручную: https://github.com/$REPO/releases/latest"
        cmd_install
        return
    fi
    service_exists || die "Служба $NAME не установлена$([ "$SYSTEM" = 0 ] && echo ' (для системной — с --system)'). Поставить: $0 install"
    local p tmp before after
    p="$(cat "$CONF" 2>/dev/null)"
    before="$(curl -fs --max-time 2 "http://127.0.0.1:$p/echo" 2>/dev/null)"
    tmp="$(mktemp -d)" || exit 1
    download "$tmp" || { rm -rf "$tmp"; die "Не скачалось — служба не тронута."; }
    SCTL stop "$NAME"
    $SUDO install -m 755 "$tmp/MonoServer" "$INSTALL_DIR/MonoServer" || { SCTL start "$NAME"; rm -rf "$tmp"; die "Не удалось заменить программу — служба запущена прежняя."; }
    [ -f "$tmp/monoserver.sh" ] && $SUDO install -m 755 "$tmp/monoserver.sh" "$INSTALL_DIR/monoserver.sh"
    rm -rf "$tmp"
    SCTL start "$NAME" || die "Служба не запустилась — журнал: $0 log"
    local i
    for i in 1 2 3 4 5 6 7 8 9 10; do
        after="$(curl -fs --max-time 2 "http://127.0.0.1:$p/echo" 2>/dev/null)" && [ -n "$after" ] && break
        sleep 1
    done
    echo "Обновлено: ${before:-?} → ${after:-не отвечает (журнал: $0 log)}"
    print_addresses "$p"
}

cmd_uninstall() {
    service_exists || die "Служба $NAME не установлена$([ "$SYSTEM" = 0 ] && echo ' (для системной — с --system)')."
    SCTL disable --now "$NAME" 2>/dev/null
    $SUDO rm -f "$UNIT_PATH"
    SCTL daemon-reload
    $SUDO rm -rf "$INSTALL_DIR"
    echo "Служба $NAME удалена. Данные остались: $DATA"
}

cmd_status() {
    service_exists || die "Служба $NAME не установлена$([ "$SYSTEM" = 0 ] && echo ' (для системной — с --system)')."
    SCTL --no-pager status "$NAME" | head -n 5
    local p; p="$(cat "$CONF" 2>/dev/null)"
    if [ -n "$p" ]; then
        if curl -fs --max-time 2 "http://127.0.0.1:$p/echo" >/dev/null 2>&1; then
            echo "Отвечает: $(curl -fs --max-time 2 "http://127.0.0.1:$p/echo")"
            print_addresses "$p"
        else
            echo "На порту $p не отвечает — журнал: $0 log$([ "$SYSTEM" = 1 ] && echo ' --system')"
        fi
    fi
}

cmd_log() { JCTL -f -n 100; }

# Код первого входа: служба кладёт его в setup-code.txt в каталоге данных (и в
# журнал, но журнал пользовательской службы сохраняется не везде). Каталог —
# из ExecStart установленной службы (её или системной), иначе по умолчанию
cmd_code() {
    local data="" unit f p
    for unit in "$UNIT_PATH" "/etc/systemd/system/$NAME.service" "${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user/$NAME.service"; do
        [ -f "$unit" ] || continue
        data="$(sed -n 's/^ExecStart=.*"[[:space:]][[:space:]]*[0-9][0-9]*[[:space:]][[:space:]]*"\(.*\)"[[:space:]]*$/\1/p' "$unit" | head -n 1)"
        [ -n "$data" ] && break
    done
    [ -n "$data" ] || data="$DATA"
    f="$data/setup-code.txt"
    p="$(cat "$CONF" 2>/dev/null)"
    [ -n "$p" ] || p="$DEFAULT_PORT"
    if [ -f "$f" ] || $SUDO test -f "$f" 2>/dev/null; then
        echo "Код первого входа: $($SUDO cat "$f" | tr -d '\r\n')"
        echo "Откройте веб-интерфейс, введите код и задайте пароль пользователя admin:"
        print_addresses "$p"
    else
        echo "Кода нет ($f): пароль admin уже задан — входите им, или служба ещё не запускалась."
        echo "Служба до 1.0.27 код в файл не пишет — он только в журнале: $0 log"
    fi
}

cmd_run() {
    ensure_binary
    choose_port
    mkdir -p "$DATA" || exit 1
    echo "MonoServer: порт $PORT, данные $DATA"
    print_addresses "$PORT"
    echo "  остановить: Ctrl+C"
    exec "$SRC_BIN" "$PORT" "$DATA"
}

case "$CMD" in
    install) cmd_install ;;
    update) cmd_update ;;
    uninstall) cmd_uninstall ;;
    status) cmd_status ;;
    log) cmd_log ;;
    code) cmd_code ;;
    run) cmd_run ;;
esac
