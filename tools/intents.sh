#!/usr/bin/env bash
# Проверка и импорт разметки интентов — этап 1 (Linux/сервер; Windows-версия — tools/intents.ps1).
#
# Этап 1 — ручной прогон внешним агентом: он читает input.jsonl и пишет labeled.jsonl.
# Скрипт проверяет файл, сравнивает метки с нашим классификатором и возвращает метки
# в журнал сервера.
#
# Чего здесь НЕ дублируем (аналоги уже есть в проекте — скрипт только склеивает файл и API):
#   GET  /api/classifications            — журнал разбора (команда list_classifications);
#   POST /api/classifications/{id}/label — поставить метку (команда label_classification,
#                                          MCP-инструмент kouki_label_classification);
#   GET  /api/dataset                    — выгрузка уже размеченных в JSON Lines
#                                          (команда export_dataset, DatasetExport);
#   POST /api/classify                   — вердикты классификаторов для одной фразы.
#
# Запуск:
#   tools/intents.sh check   --dir /srv/files/_kouki/intents
#   tools/intents.sh compare --dir /srv/files/_kouki/intents --token kouki-demo
#   tools/intents.sh import  --dir /srv/files/_kouki/intents --token kouki-demo --dry-run
set -euo pipefail

COMMAND="${1:-}"
DIR="."
SERVER="http://127.0.0.1:5300"
TOKEN="${KOUKI_TOKEN:-}"
DRY_RUN=0

# Интенты каталога (Kouki.Yasabi/LlmIntentClassifier.cs, DefaultIntents) плюс служебная
# метка unclassified. Список продублирован строкой: скрипт не тянет .NET ради проверки.
KNOWN="add_task list_tasks complete_task add_reminder list_reminders complete_reminder ask chat"

usage() {
    cat <<'EOF'
Проверка и импорт разметки интентов (этап 1) — Linux-версия tools/intents.ps1.

Использование:
  tools/intents.sh check   [--dir DIR]
  tools/intents.sh compare [--dir DIR] [--server URL] --token TOKEN
  tools/intents.sh import  [--dir DIR] [--server URL] --token TOKEN [--dry-run]

Команды:
  check    проверить labeled.jsonl: JSON, обязательные поля, интент из каталога,
           confidence 0…1; напечатать распределение. В сеть не ходит.
  compare  сверить метки агента с нашим rules+llm через POST /api/classify.
  import   поставить метки в журнал через POST /api/classifications/{id}/label.

Параметры:
  -d, --dir DIR      каталог с labeled.jsonl (по умолчанию текущий)
  -s, --server URL   адрес API (по умолчанию http://127.0.0.1:5300)
  -t, --token TOKEN  токен API (по умолчанию $KOUKI_TOKEN)
  --dry-run          import только печатает, что отправил бы
  -h, --help         эта справка

Метки: восемь интентов каталога; unclassified допустим в файле, но import его пропускает —
это не обучающая метка. Демо-токен прода — kouki-demo, настоящий даёт kouki-admin api-token.

Коды возврата: 0 — чисто, 1 — есть замечания/провалы, 2 — неверное использование.
EOF
}

die() {
    echo "Ошибка: $*" >&2
    exit 1
}

usage_error() {
    echo "Ошибка: $*" >&2
    echo "Справка: $0 --help" >&2
    exit 2
}

need_value() {
    if [ $# -lt 2 ] || [ -z "${2:-}" ] || [[ "${2:-}" == -* ]]; then
        usage_error "параметр $1 требует значения (см. --help)"
    fi
}

case "$COMMAND" in
    check | compare | import) ;;
    -h | --help | help) usage; exit 0 ;;
    "") usage_error "нужна команда: check, compare или import" ;;
    *) usage_error "неизвестная команда '$COMMAND' (check|compare|import)" ;;
esac
shift

while [ "$#" -gt 0 ]; do
    case "$1" in
        -d | --dir) need_value "$@"; DIR="$2"; shift 2 ;;
        -s | --server) need_value "$@"; SERVER="${2%/}"; shift 2 ;;
        -t | --token) need_value "$@"; TOKEN="$2"; shift 2 ;;
        --dry-run) DRY_RUN=1; shift ;;
        -h | --help) usage; exit 0 ;;
        *) usage_error "неизвестный аргумент '$1'" ;;
    esac
done

FILE="$DIR/labeled.jsonl"
[ -f "$FILE" ] || die "нет файла $FILE (внешний агент ещё не разметил?)"
command -v python3 >/dev/null || die "нужен python3: им разбирается JSON"

# Разбирает строку labeled.jsonl и печатает через табуляцию: id, intent, confidence, text.
# Невалидная строка печатает "!<причина>".
read_row() {
    python3 - "$1" <<'PY'
import json, sys
try:
    o = json.loads(sys.argv[1])
except Exception:
    print("!не JSON")
    raise SystemExit(0)
if not isinstance(o, dict):
    print("!не объект")
    raise SystemExit(0)
missing = [k for k in ("id", "text", "intent") if not str(o.get(k) or "").strip()]
if missing:
    print("!нет поля " + ",".join(missing))
    raise SystemExit(0)
conf = o.get("confidence")
print("\t".join([str(o["id"]), str(o["intent"]),
                 "" if conf is None else str(conf),
                 str(o["text"]).replace("\t", " ")]))
PY
}

# Печатает строки файла, пропуская пустые. Отдельная функция — чтобы не повторять
# `while read` в каждой команде и одинаково обрабатывать файл без перевода строки в конце.
each_row() {
    while IFS= read -r line || [ -n "$line" ]; do
        [ -n "${line//[[:space:]]/}" ] || continue
        printf '%s\n' "$line"
    done <"$FILE"
}

case "$COMMAND" in
    check)
        total=0
        bad=0
        declare -A counts=()
        while IFS= read -r line; do
            total=$((total + 1))
            row="$(read_row "$line")"
            if [[ "$row" == !* ]]; then
                bad=$((bad + 1))
                echo "  строка ${total}: ${row#!}" >&2
                continue
            fi
            id="$(cut -f1 <<<"$row")"
            intent="$(cut -f2 <<<"$row")"
            conf="$(cut -f3 <<<"$row")"
            if [[ ! " $KNOWN unclassified " == *" $intent "* ]]; then
                bad=$((bad + 1))
                echo "  строка ${total} ($id): неизвестный интент '$intent'" >&2
            fi
            if [ -z "$conf" ]; then
                bad=$((bad + 1))
                echo "  строка ${total} ($id): нет confidence" >&2
            elif ! python3 -c 'import sys; v=float(sys.argv[1]); sys.exit(0 if 0.0 <= v <= 1.0 else 1)' "$conf" 2>/dev/null; then
                bad=$((bad + 1))
                echo "  строка ${total} ($id): confidence вне 0..1 ('$conf')" >&2
            fi
            counts["$intent"]=$(( ${counts["$intent"]:-0} + 1 ))
        done < <(each_row)

        echo "строк: $total, замечаний: $bad"
        for key in "${!counts[@]}"; do
            printf '%s\t%s\n' "${counts[$key]}" "$key"
        done | sort -nr | while IFS=$'\t' read -r n name; do
            printf '  %-18s %s\n' "$name" "$n"
        done
        unclassified="${counts[unclassified]:-0}"
        if [ "$total" -gt 0 ] && [ $((unclassified * 10)) -gt "$total" ]; then
            echo "  внимание: unclassified больше 10% набора — это сигнал, а не норма" >&2
        fi
        [ "$bad" -eq 0 ] || die "замечаний: $bad"
        ;;

    compare)
        [ -n "$TOKEN" ] || usage_error "нужен --token (или KOUKI_TOKEN): compare ходит в API"
        total=0
        diff=0
        while IFS= read -r line; do
            total=$((total + 1))
            row="$(read_row "$line")"
            [[ "$row" == !* ]] && continue
            id="$(cut -f1 <<<"$row")"
            intent="$(cut -f2 <<<"$row")"
            text="$(cut -f4- <<<"$row")"
            body="$(python3 -c 'import json,sys; print(json.dumps({"text": sys.argv[1], "classifiers": ["rules+llm"]}, ensure_ascii=False))' "$text")"
            ours="$(curl -sS --max-time 30 -X POST -H "Authorization: Bearer $TOKEN" \
                -H "Content-Type: application/json" -d "$body" "$SERVER/api/classify" |
                python3 -c 'import json,sys; d=json.load(sys.stdin); print(next((r["intent"] for r in d["results"] if r["classifier"] == "rules+llm"), "?"))' 2>/dev/null || echo "?")"
            if [ "$ours" != "$intent" ]; then
                diff=$((diff + 1))
                echo "  расход $id: агент=$intent, наш=$ours — «$text»"
            fi
        done < <(each_row)
        echo "расхождений с rules+llm: $diff из $total"
        ;;

    import)
        [ -n "$TOKEN" ] || usage_error "нужен --token (или KOUKI_TOKEN): import пишет в журнал"
        ok=0
        failed=0
        skipped=0
        while IFS= read -r line; do
            row="$(read_row "$line")"
            if [[ "$row" == !* ]]; then
                skipped=$((skipped + 1))
                echo "  пропуск: ${row#!}" >&2
                continue
            fi
            id="$(cut -f1 <<<"$row")"
            intent="$(cut -f2 <<<"$row")"
            # unclassified — не обучающая метка: в датасет её нельзя, поэтому не импортируем.
            if [[ ! " $KNOWN " == *" $intent "* ]]; then
                skipped=$((skipped + 1))
                echo "  пропуск $id: '$intent' — не интент каталога" >&2
                continue
            fi
            if [ "$DRY_RUN" -eq 1 ]; then
                echo "  отправил бы $id → $intent"
                continue
            fi
            body="$(python3 -c 'import json,sys; print(json.dumps({"label": sys.argv[1]}, ensure_ascii=False))' "$intent")"
            code="$(curl -sS -o /dev/null -w '%{http_code}' --max-time 30 -X POST \
                -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
                --data-binary "$body" "$SERVER/api/classifications/$id/label" 2>/dev/null || echo 000)"
            if [ "$code" = "200" ]; then
                ok=$((ok + 1))
            else
                failed=$((failed + 1))
                echo "  не принято $id: HTTP $code" >&2
            fi
        done < <(each_row)
        if [ "$DRY_RUN" -eq 1 ]; then
            echo "dry-run: ничего не отправлено, пропущено $skipped"
        else
            echo "импортировано: $ok, не принято: $failed, пропущено: $skipped"
        fi
        [ "$failed" -eq 0 ] || die "не принято меток: $failed"
        ;;
esac
