#!/usr/bin/env bash
# Сторож сервера Kouki (T53): проверяет API и шлёт алерт, когда что-то не так.
#
# Зачем отдельный таймер, а не OnFailure у юнита: сервис может быть «active»,
# но отвечать 500 или таймаутом (например, умерла локальная модель), а бывает и
# наоборот — процесс жив, порт слушается, а база недоступна. Сторож проверяет
# то, чем пользуется человек, и пишет в журнал только смену состояния.
#
# Алерт в Telegram — если заданы KOUKI_ALERT_TOKEN и KOUKI_ALERT_CHAT
# (файл /etc/kouki/monitor.env). Без них пишет только в journal.
#
# Секреты не попадают в argv: и URL с токеном бота, и заголовок с токеном API
# уходят в curl через временный конфиг с правами 600.
set -euo pipefail

BASE_URL="${KOUKI_MONITOR_URL:-http://127.0.0.1:5300}"
STATE_DIR="${KOUKI_MONITOR_STATE:-/var/lib/kouki/monitor}"
TIMEOUT="${KOUKI_MONITOR_TIMEOUT:-8}"
REMIND_AFTER="${KOUKI_MONITOR_REMIND_SECONDS:-3600}"
ENV_FILE="${KOUKI_MONITOR_ENV:-/etc/kouki/monitor.env}"

usage() {
    cat <<'EOF'
Использование: kouki-monitor.sh [--url URL] [--state DIR] [--help]

  --url URL    база API (по умолчанию http://127.0.0.1:5300)
  --state DIR  каталог состояния (по умолчанию /var/lib/kouki/monitor)
  --help       эта справка

Алерты: /etc/kouki/monitor.env с KOUKI_ALERT_TOKEN и KOUKI_ALERT_CHAT.
Проверки: /api/health (200) и /api/classifiers с токеном сервера (200).
Код возврата: 0 — всё в порядке, 1 — проблема (и алерт, если настроен),
2 — неверные аргументы.
EOF
}

need_value() {
    if [ $# -lt 2 ]; then
        echo "Параметр $1 требует значения" >&2
        usage >&2
        exit 2
    fi
}

while [ $# -gt 0 ]; do
    case "$1" in
        --url) need_value "$1" "${2:-}"; BASE_URL="$2"; shift 2 ;;
        --state) need_value "$1" "${2:-}"; STATE_DIR="$2"; shift 2 ;;
        --help|-h) usage; exit 0 ;;
        *) echo "Неизвестный аргумент: $1" >&2; usage >&2; exit 2 ;;
    esac
done

mkdir -p "$STATE_DIR"
STATE_FILE="$STATE_DIR/state"
STAMP_FILE="$STATE_DIR/last-alert"
TMP_FILES=()

# trap не должен менять код возврата: последняя проверка в цикле может быть ложной.
cleanup() {
    for f in ${TMP_FILES[@]+"${TMP_FILES[@]}"}; do
        [ -n "$f" ] || continue
        rm -f "$f" || true
    done
    return 0
}
trap cleanup EXIT

# Переменные алертов — из файла, если он есть.
if [ -r "$ENV_FILE" ]; then
    # shellcheck disable=SC1090
    . "$ENV_FILE"
fi

log() {
    printf '%s %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*"
}

api_token() {
    if [ -n "${KOUKI_TOKEN:-}" ]; then
        printf '%s' "$KOUKI_TOKEN"
    elif [ -r /etc/kouki/kouki.env ]; then
        sed -n 's/^Kouki__Token=//p' /etc/kouki/kouki.env | head -1
    fi
}

# Конфиг для curl: секрет внутри файла 600, а не в аргументах.
secret_config() {
    local content="$1"
    local file
    file="$(mktemp "${TMPDIR:-/tmp}/kouki-monitor.XXXXXX")"
    chmod 600 "$file"
    printf '%s\n' "$content" >"$file"
    TMP_FILES+=("$file")
    printf '%s' "$file"
}

# Возвращает 0, если алерт доставлен (или алерты не настроены),
# 1 — если Telegram не принял сообщение.
notify() {
    local text="$1"
    log "ALERT $text"
    if [ -z "${KOUKI_ALERT_TOKEN:-}" ] || [ -z "${KOUKI_ALERT_CHAT:-}" ]; then
        return 0
    fi
    local conf
    conf="$(secret_config "url = \"https://api.telegram.org/bot${KOUKI_ALERT_TOKEN}/sendMessage\"")"
    if curl -s -K "$conf" -o /dev/null --max-time "$TIMEOUT" -X POST \
        -d "chat_id=${KOUKI_ALERT_CHAT}" --data-urlencode "text=${text}"; then
        return 0
    fi
    log "WARN алерт не доставлен (Telegram недоступен) — повторю при следующей проверке"
    return 1
}

check_health() {
    local code
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time "$TIMEOUT" "$BASE_URL/api/health" 2>/dev/null || true)"
    [ "${code:-000}" = "200" ]
}

# Пустой вывод — «не проверено», и это проблема: зелёный статус без проверки
# классификаторов врёт так же, как красный без причины.
check_api() {
    local tok code conf
    tok="$(api_token)"
    if [ -z "$tok" ]; then
        printf 'нет токена API для /api/classifiers (KOUKI_TOKEN или /etc/kouki/kouki.env)'
        return 1
    fi
    conf="$(secret_config "header = \"Authorization: Bearer ${tok}\"")"
    code="$(curl -s -K "$conf" -o /dev/null -w '%{http_code}' --max-time "$TIMEOUT" \
        "$BASE_URL/api/classifiers" 2>/dev/null || true)"
    if [ "${code:-000}" = "200" ]; then
        return 0
    fi
    printf '/api/classifiers вернул %s' "${code:-000}"
    return 1
}

problem=""
if ! check_health; then
    problem="не отвечает /api/health"
else
    api_problem="$(check_api || true)"
    if [ -n "$api_problem" ]; then
        problem="$api_problem"
    fi
fi

previous="$(cat "$STATE_FILE" 2>/dev/null || echo unknown)"

if [ -z "$problem" ]; then
    printf 'ok\n' >"$STATE_FILE"
    rm -f "$STAMP_FILE"
    if [ "$previous" != "ok" ] && [ "$previous" != "unknown" ]; then
        notify "Kouki снова в порядке: $BASE_URL" || true
    fi
    # Журнал — только на смену состояния: иначе каждые 5 минут «ok».
    if [ "$previous" != "ok" ]; then
        log "ok: $BASE_URL"
    fi
    exit 0
fi

printf 'fail:%s\n' "$problem" >"$STATE_FILE"

now="$(date +%s)"
last="$(cat "$STAMP_FILE" 2>/dev/null || echo 0)"
if [ "$previous" = "ok" ] || [ "$previous" = "unknown" ] || [ $((now - last)) -ge "$REMIND_AFTER" ]; then
    if notify "Kouki: $problem (URL $BASE_URL). Проверьте: systemctl status kouki, journalctl -u kouki -n 50"; then
        printf '%s\n' "$now" >"$STAMP_FILE"
    fi
fi

exit 1
