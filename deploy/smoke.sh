#!/usr/bin/env bash
# Смоук после установки и каждого деплоя (T85).
#
# Что проверяет: сервисы systemd, HTTP API (health, классификаторы, напоминания,
# синтез), шару SMB, страницу токена DSH, таймер бэкапа и живость бота.
# По флагу --with-writes добавляются проверки, которые оставляют след в базе.
#
# Коды возврата: 0 — всё зелёное; 1 — есть провалы; 2 — неверные аргументы.
#
# Секреты не попадают в argv: заголовок Authorization передаётся curl через
# временный конфиг с правами 600, иначе токен виден в ps и /proc/<pid>/cmdline.
set -euo pipefail

BASE_URL="${KOUKI_SMOKE_URL:-http://127.0.0.1:5300}"
TOKEN_FILE="${KOUKI_TOKEN_FILE:-/etc/kouki/kouki.env}"
DSH_PAGE="${KOUKI_SMOKE_DSH:-http://127.0.0.1:5311/}"
SHARE_DIR="${KOUKI_SMOKE_SHARE:-/srv/files/_kouki}"
SERVICES="${KOUKI_SMOKE_SERVICES:-kouki kouki-bot kouki-llm dsh-web dsh-token}"
TIMEOUT="${KOUKI_SMOKE_TIMEOUT:-10}"
INTAKE_TIMEOUT="${KOUKI_SMOKE_INTAKE_TIMEOUT:-60}"
SPEECH_TIMEOUT="${KOUKI_SMOKE_SPEECH_TIMEOUT:-30}"
# Окно журнала бота: руками — 2 часа, но деплой передаёт время начала выката,
# иначе ошибки, случившиеся до него (например, сразу после перезагрузки),
# валили бы смоук и откатывали успешный деплой.
BOT_WINDOW='за 2 часа'
BOT_JOURNAL_ARGS=(--since '-2 hours')
if [ -n "${KOUKI_SMOKE_SINCE:-}" ]; then
    case "$KOUKI_SMOKE_SINCE" in
        *[!0-9]*)
            echo "KOUKI_SMOKE_SINCE должен быть epoch-секундами, а не '$KOUKI_SMOKE_SINCE'" >&2
            exit 2
            ;;
    esac
    BOT_JOURNAL_ARGS=(--since "@$KOUKI_SMOKE_SINCE")
    BOT_WINDOW='с начала деплоя'
fi
# Штатные строки бота при рестарте и сетевых блипах: старый PID пишет
# `getUpdates: обрыв связи (TaskCanceledException)` на SIGTERM, а Telegram может
# ответить `HttpRequestException` в любую секунду. Это не провал деплоя.
BOT_IGNORE_RE='getUpdates: (обрыв связи|сеть недоступна) \((TaskCanceled|HttpRequest)Exception\)'
QUIET=0
JSON=0
WITH_WRITES=0

usage() {
    cat <<'EOF'
Использование: smoke.sh [--url URL] [--with-writes] [--quiet] [--json] [--help]

  --url URL      база API Kouki (по умолчанию http://127.0.0.1:5300)
  --with-writes  добавить проверку ответчика (POST /api/intake)
  --quiet        печатать только провалы
  --json         вывести отчёт в JSON и выйти
  --help         эта справка

По умолчанию смоук только читает. GET-проверки базу не меняют; POST /api/speech
только синтезирует звук, а POST /api/classify и POST /api/intake оставляют след
(строка журнала классификаций и запись задачи) — они входят в --with-writes.

Токен берётся из KOUKI_TOKEN, иначе из /etc/kouki/kouki.env (Kouki__Token).
Переменные: KOUKI_SMOKE_SINCE (epoch начала деплоя — журнал бота с этого момента
вместо двух часов), KOUKI_SKIP_BOT=1 (не проверять журнал бота вовсе).
EOF
}

need_value() {
    local name="$1"
    local value="${2:-}"
    if [ $# -lt 2 ] || [ -z "$value" ] || [ "${value#-}" != "$value" ]; then
        echo "Параметр $name требует значения" >&2
        usage >&2
        exit 2
    fi
}

while [ $# -gt 0 ]; do
    case "$1" in
        --url) need_value "$1" "${2:-}"; BASE_URL="$2"; shift 2 ;;
        --with-writes) WITH_WRITES=1; shift ;;
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
AUTH_CONFIG=""
TMP_FILES=()

# shellcheck disable=SC2329 # вызывается через trap
cleanup() {
    for f in ${TMP_FILES[@]+"${TMP_FILES[@]}"}; do
        [ -n "$f" ] || continue
        rm -f "$f" || true
    done
    return 0
}
trap cleanup EXIT

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

read_token() {
    if [ -n "${KOUKI_TOKEN:-}" ]; then
        printf '%s' "$KOUKI_TOKEN"
        return 0
    fi
    if [ -r "$TOKEN_FILE" ]; then
        sed -n 's/^Kouki__Token=//p' "$TOKEN_FILE" | head -1
    fi
}

TOKEN="$(read_token)"
if [ -n "$TOKEN" ]; then
    AUTH_CONFIG="$(mktemp "${TMPDIR:-/tmp}/kouki-smoke.XXXXXX")"
    chmod 600 "$AUTH_CONFIG"
    printf 'header = "Authorization: Bearer %s"\n' "$TOKEN" >"$AUTH_CONFIG"
    TMP_FILES+=("$AUTH_CONFIG")
fi

http_code() {
    local max="$1"
    shift
    local code
    # curl сам печатает 000 при провале соединения; второй «000» дал бы «000000».
    if [ -n "$AUTH_CONFIG" ]; then
        code="$(curl -s -K "$AUTH_CONFIG" --max-time "$max" -o /dev/null -w '%{http_code}' "$@" 2>/dev/null || true)"
    else
        code="$(curl -s --max-time "$max" -o /dev/null -w '%{http_code}' "$@" 2>/dev/null || true)"
    fi
    printf '%s' "${code:-000}"
}

# Код ответа и тело сразу: нужно для проверок, где важна полезная нагрузка.
http_code_to() {
    local max="$1"
    local out="$2"
    shift 2
    local code
    if [ -n "$AUTH_CONFIG" ]; then
        code="$(curl -s -K "$AUTH_CONFIG" --max-time "$max" -o "$out" -w '%{http_code}' "$@" 2>/dev/null || true)"
    else
        code="$(curl -s --max-time "$max" -o "$out" -w '%{http_code}' "$@" 2>/dev/null || true)"
    fi
    printf '%s' "${code:-000}"
}
http_body() {
    local max="$1"
    shift
    if [ -n "$AUTH_CONFIG" ]; then
        curl -s -K "$AUTH_CONFIG" --max-time "$max" "$@" 2>/dev/null || true
    else
        curl -s --max-time "$max" "$@" 2>/dev/null || true
    fi
}

json_escape() {
    # Кавычки, слэши и управляющие символы — иначе отчёт перестаёт быть JSON.
    printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' | tr '\n\r\t' '   '
}

say "Смоук Kouki: $BASE_URL"
if [ "$WITH_WRITES" -eq 0 ]; then
    say "Режим: только чтение (запись в базу — под --with-writes)"
fi
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
code="$(http_code "$TIMEOUT" "$BASE_URL/api/health")"
if [ "$code" = "200" ]; then
    ok "GET /api/health → 200"
else
    bad "GET /api/health" "код $code"
fi

if [ -z "$TOKEN" ]; then
    bad "токен API" "не найден: задайте KOUKI_TOKEN или читайте $TOKEN_FILE (нужен root)"
else
    code="$(http_code "$TIMEOUT" "$BASE_URL/api/classifiers")"
    if [ "$code" = "200" ]; then
        body="$(http_body "$TIMEOUT" "$BASE_URL/api/classifiers")"
        if printf '%s' "$body" | grep -q 'rules'; then
            ok "GET /api/classifiers → 200 (есть rules)"
        else
            bad "GET /api/classifiers" "нет классификатора rules в ответе"
        fi
    elif [ "$code" = "401" ]; then
        bad "GET /api/classifiers" "401 — токен не принят сервером"
    else
        bad "GET /api/classifiers" "код $code"
    fi
fi

code="$(http_code "$TIMEOUT" "$BASE_URL/api/reminders/due?limit=1")"
if [ "$code" = "200" ]; then
    ok "GET /api/reminders/due → 200"
elif [ "$code" = "401" ] && [ -z "$TOKEN" ]; then
    bad "GET /api/reminders/due" "401 (токен не найден)"
else
    bad "GET /api/reminders/due" "код $code"
fi

say "Синтез речи:"
code="$(http_code "$SPEECH_TIMEOUT" -X POST -H 'Content-Type: application/json' \
    --data '{"text":"проверка"}' "$BASE_URL/api/speech")"
if [ "$code" = "200" ]; then
    ok "POST /api/speech → 200"
elif [ "$code" = "503" ]; then
    bad "POST /api/speech" "503 — голосовой каталог не найден"
else
    bad "POST /api/speech" "код $code"
fi

if [ "$WITH_WRITES" -eq 1 ]; then
    say "Запись в базу (--with-writes):"
    code="$(http_code "$TIMEOUT" -X POST -H 'Content-Type: application/json' \
        --data '{"text":"сколько минут варить яйцо"}' "$BASE_URL/api/classify")"
    if [ "$code" = "200" ]; then
        ok "POST /api/classify → 200 (строка журнала)"
    else
        bad "POST /api/classify" "код $code"
    fi

    # Первый вызов ответчика после простоя грузит модель — у него свой таймаут.
    intake_body="$(mktemp "${TMPDIR:-/tmp}/kouki-intake.XXXXXX")"
    TMP_FILES+=("$intake_body")
    code="$(http_code_to "$INTAKE_TIMEOUT" "$intake_body" -X POST \
        -H 'Content-Type: application/json' \
        --data '{"text":"Смоук: сколько минут варить яйцо","source":"dashboard","intent":"ask"}' \
        "$BASE_URL/api/intake")"
    if [ "$code" = "200" ]; then
        # Смоук не должен копить мусор в базе: задача «Смоук: …» отменяется сразу.
        task_id="$(grep -o '"id"[[:space:]]*:[[:space:]]*"[^"]*"' "$intake_body" | head -1 |
            sed 's/.*"\([^"]*\)"$/\1/')"
        cancel_code=""
        if [ -n "$task_id" ]; then
            cancel_code="$(http_code "$TIMEOUT" -X PATCH -H 'Content-Type: application/json' \
                --data '{"status":"cancelled"}' "$BASE_URL/api/tasks/$task_id")"
        fi
        if [ "$cancel_code" = "200" ]; then
            ok "POST /api/intake (intent=ask) → 200, задача отменена"
        elif [ -n "$task_id" ]; then
            bad "POST /api/intake (intent=ask)" "ответ 200, но задачу $task_id не отменить ($cancel_code)"
        else
            ok "POST /api/intake (intent=ask) → 200"
        fi
    elif [ "$code" = "503" ]; then
        bad "POST /api/intake (intent=ask)" "503 — ответчик недоступен (kouki-llm?)"
    else
        bad "POST /api/intake (intent=ask)" "код $code"
    fi
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

code="$(http_code "$TIMEOUT" "$DSH_PAGE")"
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
elif journal="$(journalctl -u kouki-bot "${BOT_JOURNAL_ARGS[@]}" --no-pager 2>&1)"; then
    if [ -z "$journal" ]; then
        if [ -n "${KOUKI_SMOKE_SINCE:-}" ]; then
            # В деплое окно начинается до рестартов и всегда содержит строки старта:
            # пусто значит, что юнит пишет от другого пользователя или в другой journald.
            bad "бот" "журнал пуст $BOT_WINDOW — ошибки искать негде (бот пишет от другого uid?)"
        else
            # Ручной прогон за 2 часа: молчащий бот — это норма, периодических строк нет.
            ok "бот: журнал пуст $BOT_WINDOW — ошибок нет"
        fi
    else
        errors="$(
            printf '%s\n' "$journal" |
                grep -vE "$BOT_IGNORE_RE" |
                grep -cE 'FAIL|Ошибка|Exception' || true
        )"
        if [ "${errors:-0}" -eq 0 ]; then
            ok "бот без ошибок $BOT_WINDOW"
        else
            bad "бот" "ошибок в журнале $BOT_WINDOW: $errors"
        fi
    fi
else
    bad "бот" "журнал не прочитан (нет прав на journalctl или юнита нет)"
fi

say ""
if [ "$JSON" -eq 1 ]; then
    writes=false
    [ "$WITH_WRITES" -eq 1 ] && writes=true
    printf '{"passed":%d,"failed":%d,"withWrites":%s,"checks":[' \
        "$PASS" "$FAIL" "$writes"
    first=1
    for line in "${REPORT[@]}"; do
        status="${line%%|*}"
        rest="${line#*|}"
        name="${rest%%|*}"
        detail="${rest#*|}"
        [ "$first" -eq 0 ] && printf ','
        first=0
        printf '{"status":"%s","check":"%s","detail":"%s"}' \
            "$status" "$(json_escape "$name")" "$(json_escape "$detail")"
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
