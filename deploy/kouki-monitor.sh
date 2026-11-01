#!/usr/bin/env bash
# Сторож сервера Kouki (T53): проверяет API, выключает/поляризует алерты.
#
# Зачем отдельный таймер, а не OnFailure у юнита: сервис может быть «active»,
# но отвечать 500 или таймаутом (например, умерла локальная модель), а бывает и
# наоборот — процесс жив, порт слушается, а база недоступна. Сторож проверяет
# то, чем пользуется человек, и пишет в журнал только смену состояния.
#
# Алерт в Telegram — если заданы KOUKI_ALERT_TOKEN и KOUKI_ALERT_CHAT
# (файл /etc/kouki/monitor.env). Без них пишет только в journal.
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
Проверки: /api/health (200), /api/classifiers с токеном сервера (200).
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --url) BASE_URL="$2"; shift 2 ;;
        --state) STATE_DIR="$2"; shift 2 ;;
        --help|-h) usage; exit 0 ;;
        *) echo "Неизвестный аргумент: $1" >&2; usage >&2; exit 2 ;;
    esac
done

mkdir -p "$STATE_DIR"
STATE_FILE="$STATE_DIR/state"
STAMP_FILE="$STATE_DIR/last-alert"

# Переменные алертов — из файла, если он есть.
if [ -r "$ENV_FILE" ]; then
    # shellcheck disable=SC1090
    . "$ENV_FILE"
fi

log() {
    printf '%s %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*"
}

token() {
    if [ -n "${KOUKI_TOKEN:-}" ]; then
        printf '%s' "$KOUKI_TOKEN"
    elif [ -r /etc/kouki/kouki.env ]; then
        sed -n 's/^Kouki__Token=//p' /etc/kouki/kouki.env | head -1
    fi
}

notify() {
    local text="$1"
    log "ALERT $text"
    if [ -z "${KOUKI_ALERT_TOKEN:-}" ] || [ -z "${KOUKI_ALERT_CHAT:-}" ]; then
        return 0
    fi
    curl -s --max-time "$TIMEOUT" -o /dev/null \
        -X POST "https://api.telegram.org/bot${KOUKI_ALERT_TOKEN}/sendMessage" \
        -d "chat_id=${KOUKI_ALERT_CHAT}" \
        --data-urlencode "text=${text}" || true
}

check_health() {
    local code
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time "$TIMEOUT" "$BASE_URL/api/health" 2>/dev/null || true)"
    [ -n "$code" ] || code=000
    [ "$code" = "200" ]
}

check_api() {
    local code tok
    tok="$(token)"
    if [ -z "$tok" ]; then
        # Без токена проверяем только то, что открыто; считается «не проверено».
        return 0
    fi
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time "$TIMEOUT" \
        -H "Authorization: Bearer $tok" "$BASE_URL/api/classifiers" 2>/dev/null || true)"
    [ -n "$code" ] || code=000
    [ "$code" = "200" ]
}

problem=""
check_health || problem="не отвечает /api/health"
if [ -z "$problem" ]; then
    check_api || problem="не отвечает /api/classifiers (200 с токеном)"
fi

previous="$(cat "$STATE_FILE" 2>/dev/null || echo unknown)"

if [ -z "$problem" ]; then
    printf 'ok\n' >"$STATE_FILE"
    rm -f "$STAMP_FILE"
    if [ "$previous" != "ok" ] && [ "$previous" != "unknown" ]; then
        notify "Kouki снова в порядке: $BASE_URL"
    else
        log "ok: $BASE_URL"
    fi
    exit 0
fi

printf 'fail:%s\n' "$problem" >"$STATE_FILE"

now="$(date +%s)"
last="$(cat "$STAMP_FILE" 2>/dev/null || echo 0)"
if [ "$previous" = "ok" ] || [ "$previous" = "unknown" ] || [ $((now - last)) -ge "$REMIND_AFTER" ]; then
    notify "Kouki: $problem (URL $BASE_URL). Проверьте: systemctl status kouki, journalctl -u kouki -n 50"
    printf '%s\n' "$now" >"$STAMP_FILE"
fi

exit 1
