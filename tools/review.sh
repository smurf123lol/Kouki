#!/usr/bin/env bash
# Готовит пакет для ревью изменений перед пушем (Linux-версия tools/review.ps1).
#
# Ревью делает отдельный агент (не автор правок) и только по пакету: спека задачи,
# диф, список файлов, тесты. Скрипт собирает этот пакет в один Markdown-файл.
#
# Запуск:
#   tools/review.sh -t T73                 # сравнить с master, задача T73
#   tools/review.sh -t T87 -b origin/master -m 3000
#
# Параметры:
#   -t, --task ID      id задачи из notes-updated/Задачи Kouki.md (например T85)
#   -b, --base REF     с чем сравнивать (по умолчанию master)
#   -H, --head REF     что сравнивать (по умолчанию HEAD)
#   -m, --max-lines N  сколько строк дифа вложить (по умолчанию 4000)
#   -o, --out FILE     куда положить пакет (по умолчанию <workspace>/_scratch/review/<дата>-<задача>.md)
#   -r, --repo DIR     каталог репозитория (по умолчанию — корень, где лежит tools/)
#   -n, --notes FILE   файл задач (по умолчанию ищется вверх от репозитория)
#   --help             эта справка
set -euo pipefail

TASK=""
BASE="master"
HEAD_REF="HEAD"
MAX_LINES=4000
OUT=""
NOTES=""
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# Скрипт обычно лежит в <repo>/tools; если его положили в корень репозитория — берём корень.
if [ "$(basename "$SCRIPT_DIR")" = "tools" ]; then
    REPO="$(dirname "$SCRIPT_DIR")"
else
    REPO="$SCRIPT_DIR"
fi

usage() {
    awk 'NR > 2 && /^set -euo pipefail$/ { exit } NR > 2' "${BASH_SOURCE[0]}" |
        sed 's/^# \{0,1\}//' |
        awk 'NF || seen { seen = 1; print }'
}

need_value() {
    if [ $# -lt 2 ] || [ -z "${2:-}" ]; then
        echo "Параметр $1 требует значения (см. --help)" >&2
        exit 2
    fi
}

while [ $# -gt 0 ]; do
    case "$1" in
        -t|--task) need_value "$1" "${2:-}"; TASK="$2"; shift 2 ;;
        -b|--base) need_value "$1" "${2:-}"; BASE="$2"; shift 2 ;;
        -H|--head) need_value "$1" "${2:-}"; HEAD_REF="$2"; shift 2 ;;
        -m|--max-lines) need_value "$1" "${2:-}"; MAX_LINES="$2"; shift 2 ;;
        -o|--out) need_value "$1" "${2:-}"; OUT="$2"; shift 2 ;;
        -r|--repo) need_value "$1" "${2:-}"; REPO="$2"; shift 2 ;;
        -n|--notes) need_value "$1" "${2:-}"; NOTES="$2"; shift 2 ;;
        --help) usage; exit 0 ;;
        *) echo "Неизвестный аргумент: $1 (см. --help)" >&2; exit 2 ;;
    esac
done

case "$MAX_LINES" in
    ''|*[!0-9]*) echo "Число строк дифа должно быть целым: '$MAX_LINES'" >&2; exit 2 ;;
esac
if [ "$MAX_LINES" -lt 1 ]; then
    echo "Число строк дифа должно быть положительным, а не '$MAX_LINES'" >&2
    exit 2
fi

# Путь из -r может быть относительным (или «.»): нормализуем до абсолютного, иначе
# поиск воркспейса вверх по dirname зацикливается на каталоге «.».
REPO_IN="$REPO"
REPO="$(cd "$REPO" 2>/dev/null && pwd)" || {
    echo "Каталог не найден: $REPO_IN" >&2
    exit 2
}

git -C "$REPO" rev-parse --git-dir >/dev/null 2>&1 || {
    echo "Не репозиторий: $REPO" >&2
    exit 2
}

# Рабочие деревья задач живут в <workspace>/_scratch/wt/<repo>-<task>, поэтому notes
# и каталог пакета нельзя брать от родителя репозитория — ищем корень воркспейса вверх.
find_workspace() {
    local dir="$1"
    while [ "$dir" != "/" ] && [ -n "$dir" ]; do
        if [ -d "$dir/notes-updated" ] || [ -d "$dir/_scratch" ]; then
            printf '%s\n' "$dir"
            return 0
        fi
        local next
        next="$(dirname "$dir")"
        [ "$next" = "$dir" ] && break
        dir="$next"
    done
    dirname "$1"
}

WORKSPACE="$(find_workspace "$REPO")"

for ref in "$BASE" "$HEAD_REF"; do
    git -C "$REPO" rev-parse --verify --quiet "$ref^{commit}" >/dev/null 2>&1 || {
        echo "Неизвестная ревизия: $ref" >&2
        exit 2
    }
done

BRANCH="$(git -C "$REPO" rev-parse --abbrev-ref HEAD)"
HEAD_SHA="$(git -C "$REPO" rev-parse --short "$HEAD_REF")"
BASE_SHA="$(git -C "$REPO" rev-parse --short "$BASE")"
COMMITS="$(git -C "$REPO" log --oneline "$BASE..$HEAD_REF" || true)"
STAT="$(git -C "$REPO" diff --stat "$BASE..$HEAD_REF" || true)"
NAME_STATUS="$(git -C "$REPO" diff --name-status "$BASE..$HEAD_REF" || true)"
DIFF="$(git -C "$REPO" diff "$BASE..$HEAD_REF" || true)"
RAW="$(git -C "$REPO" -c core.quotePath=false diff --raw --abbrev=10 "$BASE..$HEAD_REF" || true)"
BASE_FULL="$(git -C "$REPO" rev-parse "$BASE^{commit}")"
HEAD_FULL="$(git -C "$REPO" rev-parse "$HEAD_REF^{commit}")"

# Отпечаток набора изменений: sha256 по `git diff --raw` (blob-хеши «было→стало» и пути).
# При повторном ревью достаточно сверить отпечаток: если он тот же — набор изменений тот же.
FP_INPUT="$(printf '%s\n' "$RAW" | tr -d '\r' | sed '/^[[:space:]]*$/d' | LC_ALL=C sort)"
FP="$(printf '%s' "$FP_INPUT" | sha256sum | cut -c1-16)"

# Пустой дифф — это пустая строка: git diff заканчивает вывод переводом строки,
# команда подстановки его срезает. Проверка через printf|grep здесь не годится:
# grep -q закрывает канал раньше, и pipefail роняет сроку как «пустой дифф».
if [ -z "$DIFF" ]; then
    DIFF_LINES=0
    DIFF=""
else
    DIFF_LINES="$(printf '%s\n' "$DIFF" | grep -c '')"
fi

TRUNCATED=""
if [ "$DIFF_LINES" -gt "$MAX_LINES" ]; then
    # awk читает весь вход (в отличие от head), иначе printf ловит EPIPE и set -e
    # убивает скрипт до записи пакета — ровно на больших диффах, где обрезка нужна.
    DIFF="$(printf '%s\n' "$DIFF" | awk -v n="$MAX_LINES" 'NR <= n')"
    TRUNCATED="> Диф обрезан до $MAX_LINES строк — остальное: \`git diff $BASE..$HEAD_REF\`."
fi

if [ -z "$NOTES" ]; then
    NOTES="$WORKSPACE/notes-updated/Задачи Kouki.md"
fi
TASK_LINE="_(задача не указана)_"
if [ -n "$TASK" ]; then
    if [ -f "$NOTES" ]; then
        TASK_LINE="$(grep -E "^\| *$TASK *\|" "$NOTES" | head -n 1 || true)"
        [ -z "$TASK_LINE" ] && TASK_LINE="_(в заметках нет строки задачи $TASK)_"
    else
        TASK_LINE="_(файл задач не найден: $NOTES)_"
    fi
fi

STAMP="$(date +%Y-%m-%d-%H%M%S)"
if [ -z "$OUT" ]; then
    LEAF="${TASK:-review}"
    OUT="$WORKSPACE/_scratch/review/$STAMP-$LEAF.md"
fi
mkdir -p "$(dirname "$OUT")"

if [ -z "${NAME_STATUS//[[:space:]]/}" ]; then
    FILES_COUNT=0
else
    FILES_COUNT="$(printf '%s\n' "$NAME_STATUS" | grep -c '')"
fi

{
    echo "# Ревью перед пушем — ${TASK:-$BRANCH}"
    echo
    echo "Собрано: $(date '+%Y-%m-%d %H:%M') · ветка \`$BRANCH\` · $BASE_SHA..$HEAD_SHA"
    echo
    echo "## Задача"
    echo
    echo "$TASK_LINE"
    echo
    echo "## Что сравниваем"
    echo
    echo "| Что | Значение |"
    echo "| --- | --- |"
    echo "| База | \`$BASE\` ($BASE_SHA, \`$BASE_FULL\`) |"
    echo "| Голова | \`$HEAD_REF\` ($HEAD_SHA, \`$HEAD_FULL\`) |"
    echo "| Ветка | \`$BRANCH\` |"
    echo "| Репозиторий | \`$REPO\` |"
    echo "| **Отпечаток** | \`$FP\` (sha256 по \`git diff --raw\`, 16 символов) |"
    echo
    echo "> Повторное ревью запрашивайте по отпечатку: если \`$FP\` не изменился,"
    echo "> набор изменений тот же, даже если пакет собран заново."
    echo
    echo "### Коммиты"
    echo
    echo '```'
    echo "${COMMITS:-_(нет коммитов)_}"
    echo '```'
    echo
    echo "### Файлы и blob-хеши"
    echo
    echo "| Файл | Статус | Было | Стало |"
    echo "| --- | --- | --- | --- |"
    if [ -z "$RAW" ]; then
        echo "| _(нет изменений)_ | — | — | — |"
    else
        printf '%s\n' "$RAW" | tr -d '\r' | awk -F"\t" '{
            split($1, meta, " ");
            old = substr(meta[3], 1, 10); new = substr(meta[4], 1, 10);
            if (old == "" || old ~ /^0+$/) old = "—";
            if (new == "" || new ~ /^0+$/) new = "—";
            status = substr(meta[5], 1, 1);
            path = $2;
            if (status == "R" || status == "C") path = $2 " → " $3;
            printf "| `%s` | %s | %s | %s |\n", path, status, old, new
        }'
    fi
    echo
    echo "### Список файлов"
    echo
    echo '```'
    echo "${NAME_STATUS:-_(нет изменений)_}"
    echo '```'
    echo
    echo "### Сводка"
    echo
    echo '```'
    echo "${STAT:-_(нет изменений)_}"
    echo '```'
    echo
    echo "## Диф"
    echo
    [ -n "$TRUNCATED" ] && { echo "$TRUNCATED"; echo; }
    if [ -z "$DIFF" ]; then
        echo "_(изменений нет)_"
    else
        echo '```diff'
        echo "$DIFF"
        echo '```'
    fi
    echo
    cat <<'CHECKLIST'
## Чек-лист ревьюера

Проверять по порядку, нарушение — блокер.

1. **Границы слоёв.** `Contracts` без зависимостей; `Mugura` без предметных типов;
   хост не содержит бизнес-логики; новых ссылок в ядро нет.
2. **Инварианты `AGENTS.md` (1–14).** Задачу создаёт только `TaskFactory`; источник —
   `TaskSources`; статусы — `TaskStatuses`; вердикт классификатора не перезаписывается
   (`hinted_intent`); многотабличная запись — в `IUnitOfWork`; DI — через `KoukiHosting`;
   чтение/запись — командами (кроме `GET /api/tasks/{id}` с комментарием); токен
   fail-closed; `[Command]` на обработчике; фразу не режем; магических чисел нет;
   пользователю — человеческий текст; правила приёма — в `IntakeRequestPolicy`;
   версии — в `Directory.Packages.props` / `Directory.Build.props`.
3. **Тесты.** Новое поведение покрыто; тесты падали до правки; нет отключённых проверок,
   `Skip` и `catch { }` без лога.
4. **Ошибки и тексты.** Наружу не уходит `ex.Message`; у отказа есть код и человеческое
   сообщение; логи содержат контекст, но не секреты.
5. **Доки и заметки.** Менялись контракты — обновлены `notes-updated` (архитектура,
   датафлоу) и `Задачи Kouki.md`.
6. **Секреты и мусор.** Нет токенов/паролей в диффе, нет `bin/obj`, `*.sqlite`,
   `station-audio`, временных файлов; нет «заодно» вне задачи.
7. **Доказательства.** На каждый пункт «сделано» — команда и вывод/цифра.

## Шаблон вердикта

```
Вердикт: можно пушить | нужны правки

Blockers (пушить нельзя):
- файл:строка — что не так, какое нарушение, что сделать

Major (можно после правки в этом PR):
- …

Minor / nit (отдельной задачей):
- …

Что проверено и в порядке:
- …

Открытые вопросы к автору:
- …
```
CHECKLIST
} > "$OUT"

echo "Пакет для ревью: $OUT · отпечаток $FP"
echo "Файлов в дифе: $FILES_COUNT · строк дифа: $DIFF_LINES${TRUNCATED:+ (обрезано)}"
