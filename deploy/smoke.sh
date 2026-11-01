#!/usr/bin/env bash
# Смоук после установки и каждого деплоя (T85).
#
# Что проверяет: сервисы systemd, HTTP API (health, классификаторы, напоминания,
# синтез), ответчик, шару SMB, страницу токена DSH, таймер бэкапа и живость бота.
# Смысл — «после переезда ничего не забыли», а не полный регресс: глубина в тестах.
#
# Коды возврата: 0 — всё зелёное; 1 — есть провалы (перечислены в конце).
# --quiet печатает только провалы, --json отдаёт машинный отчёт.
set -euo pipefail

BASE_URL="${KOUKI_SMOKE_URL:-http://127.0.0.1:5300}"
TOKEN_FILE="/etc/kouki/kouki.env"
DSH_PAGE="${KOUKI_SMOKE_DSH:-http://127.0.0.1:5311/}"
SHARE_DIR="${KOUKI_SMOKE_SHARE:-/srv/files/_kouki}"
SERVICES="${KOUKI_SMOKE_SERVICES:-kouki kouki-bot kouki-llm dsh-web dsh-token}"
QUIET=0
JSON=0
TIMEOUT=10

usage() {
    cat <<'EOF'
Использование: smoke.sh [--url URL] [--quiet] [--json] [--help]

  --url URL   база API Kouki (по умолчанию http://127.0.0.1:5300)
  --quiet     печатать только провалы
  --json      вывести отчёт в JSON и выйти
  --help      эта справка

Токен берётся из KOUKI_TOKEN, иначе из /etc/kouki/kouki.env (Kouki__Token).
Смоук ничего не меняет: только GET-запросы и чтение статусов.
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --url) BASE_URL="$2"; shift 2 ;;
        --quiet) QUIET=1; shift ;;
        --json) JSON=1; shift ;;
        --help|-h) usage; exit 0 ;;
        *) echo "Неизвестный аргумент: $1" >&2; usage >&2; exit 2 ;;
    esac
done

PASS=0
FAIL=0
declare -a FAILURES=()
declare -a REPORT=()

say() {
    if [ "$QUIET" -eq 0 ] && [ "$JSON" -eq 0 ]; then
        printf '%s\n' "$*"
    fi
}

ok() {
    PASS=$((PASS + 1))
    REPORT+=("ok|$1|")
    say "  ✅ $1"
}

bad() {
    FAIL=$((FAIL + 1))
    FAILURES+=("$1: $2")
    REPORT+=("fail|$1|$2")
    say "  ❌ $1 — $2"
}

# token из env файла без эха значения
read_token() {
    if [ -n "${KOUKI_TOKEN:-}" ]; then
        printf '%s' "$KOUKI_TOKEN"
        return 0
    fi
    if [ -r "$TOKEN_FILE" ]; then
        sed -n 's/^Kouki__Token=//p' "$TOKEN_FILE" | head -1
        return 0
    fi
    return 0
}

TOKEN="$(read_token)"
AUTH=()
if [ -n "$TOKEN" ]; then
    AUTH=(-H "Authorization: Bearer $TOKEN")
fi

http_code() {
    # curl сам печатает 000 при провале соединения; второй «000» дал бы «000000».
    local code
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time "$TIMEOUT" "$@" 2>/dev/null || true)"
    printf '%s' "${code:-000}"
}

http_body() {
    curl -s --max-time "$TIMEOUT" "$@" || true
}

say "Смоук Kouki: $BASE_URL"
say ""

say "Сервисы:"
for svc in $SERVICES; do
    state="$(systemctl is-active "$svc" 2>/dev/null || true)"
    if [ "$state" = "active" ]; then
        ok "сервис $svc активен"
    else
        bad "сервис $svc" "состояние: ${state:-нет юнита}"
    fi
done

say "API:"
code="$(http_code "$BASE_URL/api/health")"
if [ "$code" = "200" ]; then
    ok "GET /api/health → 200"
else
    bad "GET /api/health" "код $code"
fi

code="$(http_code "${AUTH[@]}" "$BASE_URL/api/classifiers")"
if [ "$code" = "200" ]; then
    body="$(http_body "${AUTH[@]}" "$BASE_URL/api/classifiers")"
    if printf '%s' "$body" | grep -q 'rules'; then
        ok "GET /api/classifiers → 200 (есть rules)"
    else
        bad "GET /api/classifiers" "нет классификатора rules в ответе"
    fi
elif [ "$code" = "401" ]; then
    bad "GET /api/classifiers" "401 — токен не найден (KOUKI_TOKEN или $TOKEN_FILE)"
else
    bad "GET /api/classifiers" "код $code"
fi

code="$(http_code "${AUTH[@]}" "$BASE_URL/api/reminders/due?limit=1")"
if [ "$code" = "200" ]; then
    ok "GET /api/reminders/due → 200"
else
    bad "GET /api/reminders/due" "код $code"
fi

code="$(http_code "${AUTH[@]}" -X POST -H 'Content-Type: application/json' \
    --data '{"text":"сколько минут варить яйцо"}' "$BASE_URL/api/classify")"
if [ "$code" = "200" ]; then
    ok "POST /api/classify → 200"
else
    bad "POST /api/classify" "код $code"
fi

# Ответчик: вопрос должен получить ответ, а не ошибку. Локальная модель отвечает
# за 1–3 с, но первый вызов после простоя дольше — ждём до 60 с.
code="$(http_code "${AUTH[@]}" -X POST -H 'Content-Type: application/json' \
    --data '{"text":"Скажи одно слово: проверка","source":"dashboard","intent":"ask"}' \
    "$BASE_URL/api/intake")"
if [ "$code" = "200" ]; then
    ok "POST /api/intake (intent=ask) → 200"
elif [ "$code" = "503" ]; then
    bad "POST /api/intake (intent=ask)" "503 — ответчик недоступен (kouki-llm?)"
else
    bad "POST /api/intake (intent=ask)" "код $code"
fi

say "Синтез речи:"
code="$(http_code "${AUTH[@]}" -X POST -H 'Content-Type: application/json' \
    --data '{"text":"проверка"}' "$BASE_URL/api/speech")"
if [ "$code" = "200" ]; then
    ok "POST /api/speech → 200"
elif [ "$code" = "503" ]; then
    bad "POST /api/speech" "503 — голосовой каталог не найден"
else
    bad "POST /api/speech" "код $code"
fi

say "Инфраструктура:"
if mountpoint -q /srv/files 2>/dev/null || [ -d "$SHARE_DIR" ]; then
    probe="$SHARE_DIR/.smoke-probe"
    if ( : >"$probe" ) 2>/dev/null; then
        rm -f "$probe"
        ok "шара $SHARE_DIR доступна на запись"
    else
        bad "шара $SHARE_DIR" "нет записи (права или не смонтирована)"
    fi
else
    bad "шара $SHARE_DIR" "каталог не найден"
fi

code="$(http_code "$DSH_PAGE")"
if [ "$code" = "200" ]; then
    ok "страница токена DSH → 200"
else
    bad "страница токена DSH" "код $code"
fi

state="$(systemctl is-enabled kouki-backup.timer 2>/dev/null || true)"
if [ "$state" = "enabled" ]; then
    ok "таймер бэкапа включён"
else
    bad "таймер бэкапа" "состояние: ${state:-нет юнита}"
fi

if [ -n "${KOUKI_SKIP_BOT:-}" ]; then
    say "Бот: пропущен (KOUKI_SKIP_BOT)"
else
    last_error="$(journalctl -u kouki-bot --since '-2 hours' --no-pager 2>/dev/null \
        | grep -cE 'FAIL|Ошибка|Exception' || true)"
    if [ "${last_error:-0}" -eq 0 ]; then
        ok "бот без ошибок за 2 часа"
    else
        bad "бот" "ошибок в журнале за 2 часа: $last_error"
    fi
fi

say ""
if [ "$JSON" -eq 1 ]; then
    printf '{"passed":%d,"failed":%d,"checks":[' "$PASS" "$FAIL"
    first=1
    for line in "${REPORT[@]}"; do
        status="${line%%|*}"
        rest="${line#*|}"
        name="${rest%%|*}"
        detail="${rest#*|}"
        [ "$first" -eq 0 ] && printf ','
        first=0
        printf '{"status":"%s","check":"%s","detail":"%s"}' \
            "$status" "$name" "$(printf '%s' "$detail" | sed 's/"/\\"/g')"
    done
    printf ']}\n'
fi

if [ "$FAIL" -eq 0 ]; then
    say "Итог: $PASS проверок, провалов нет."
    exit 0
fi

echo "Итог: $PASS пройдено, $FAIL провал(ов):" >&2
for f in "${FAILURES[@]}"; do
    echo "  - $f" >&2
done
exit 1
