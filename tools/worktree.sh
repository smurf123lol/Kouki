#!/usr/bin/env bash
# Работа с git worktree: отдельное дерево на пакет задач, чтобы не мешать основному.
#
# Правило: одно дерево и одна ветка на пакет задач; в дереве работают несколько
# воркеров с непересекающимися файлами. Деревья лежат вне репозиториев,
# в <workspace>/_scratch/wt/<repo>-<task>-<slug>, чтобы в корне папки не было мусора.
#
# Запуск (из любого каталога):
#   tools/worktree.sh new --repo Kouki --task T50 --slug backup-timer
#   tools/worktree.sh list
#   tools/worktree.sh merge --name kouki-t50-backup-timer    # вливает ветку в master
#   tools/worktree.sh remove --name kouki-t50-backup-timer   # --force — снять с грязным деревом
#
# Параметры:
#   --repo DIR    каталог репозитория в воркспейсе (по умолчанию Kouki)
#   --task ID     идентификатор задачи, например T50
#   --slug NAME   короткое имя ветки латиницей, например backup-timer (по умолчанию task)
#   --name NAME   имя дерева (то, что печатает list) — для remove/merge
#   --base REF    от какой ветки ответвляться (по умолчанию master)
#   --force       удалить дерево с незакоммиченными изменениями (только для remove)
#   --help        эта справка
#
# Коды возврата: 0 — успех, 1 — ошибка, 2 — неверное использование.
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# Скрипт лежит в <repo>/tools; если его положили в корень репозитория — берём корень.
if [ "$(basename -- "$SCRIPT_DIR")" = "tools" ]; then
    SCRIPT_REPO="$(dirname -- "$SCRIPT_DIR")"
else
    SCRIPT_REPO="$SCRIPT_DIR"
fi
# Воркспейс — родитель каталога репозитория, как в tools/worktree.ps1.
WORKSPACE="$(dirname -- "$SCRIPT_REPO")"
# Если скрипт запущен из дерева задачи (<workspace>/_scratch/wt/<name>), то
# «родитель репозитория» — это _scratch/wt; настоящий воркспейс выше на два уровня.
if [ "$(basename -- "$WORKSPACE")" = "wt" ] &&
    [ "$(basename -- "$(dirname -- "$WORKSPACE")")" = "_scratch" ]; then
    WORKSPACE="$(dirname -- "$(dirname -- "$WORKSPACE")")"
fi
WT_ROOT="$WORKSPACE/_scratch/wt"

COMMAND=""
REPO="Kouki"
TASK=""
SLUG=""
NAME=""
BASE="master"
FORCE=0

usage() {
    cat <<'EOF'
Работа с git worktree: одно дерево на пакет; в дереве — несколько воркеров.

Использование:
  tools/worktree.sh new    [--repo Kouki] --task T50 [--slug backup-timer] [--base master]
  tools/worktree.sh list
  tools/worktree.sh merge  --name kouki-t50-backup-timer
  tools/worktree.sh remove --name kouki-t50-backup-timer [--force]

Параметры:
  -Repo, --repo DIR   каталог репозитория в воркспейсе (по умолчанию Kouki)
  -Task, --task ID    идентификатор задачи, например T50
  -Slug, --slug NAME  короткое имя ветки латиницей (по умолчанию task)
  -Name, --name NAME  имя дерева для remove/merge (то, что печатает list)
  -Base, --base REF   от какой ветки ответвляться (по умолчанию master)
  -f, --force         удалить дерево с незакоммиченными изменениями (только remove)
  -h, --help          эта справка

Деревья: <workspace>/_scratch/wt/<repo>-<task>-<slug> (в нижнем регистре).
Ветки:   task/<task>-<slug> (в нижнем регистре).
merge вливает ветку в master основного дерева и НЕ пушит.
remove без --force не трогает дерево с незакоммиченными изменениями.

Коды возврата: 0 — успех, 1 — ошибка, 2 — неверное использование.
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
    if [ $# -lt 2 ] || [ -z "${2:-}" ]; then
        usage_error "параметр $1 требует значения (см. --help)"
    fi
}

lower() {
    printf '%s' "$1" | tr '[:upper:]' '[:lower:]'
}

# Имена попадают в пути и имена веток: не пускаем пробелы, слэши и «..».
validate_name() {
    local label="$1" value="$2"
    if ! [[ "$value" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]]; then
        usage_error "$label: допустимы латиница, цифры, '.', '_' и '-'; имя не должно начинаться с '-', '.' или '_': '$value'"
    fi
}

# Репозитории воркспейса — каталоги с .git (каталогом или файлом worktree).
list_repos() {
    local dir
    for dir in "$WORKSPACE"/*; do
        [ -d "$dir" ] || continue
        [ -e "$dir/.git" ] || continue
        basename -- "$dir"
    done
}

# Имя дерева начинается с имени репозитория: kouki-t50-backup-timer → Kouki.
# Берём самое длинное совпадение: иначе «kouki-station-...» прилипло бы к Kouki,
# потому что «kouki» — его префикс (в worktree.ps1 это первый попавшийся репозиторий).
find_repo_for_name() {
    local name_lower="$1" repo repo_lower
    local best="" best_len=0
    while IFS= read -r repo; do
        repo_lower="$(lower "$repo")"
        case "$name_lower" in
            "$repo_lower"-*)
                if [ "${#repo_lower}" -gt "$best_len" ]; then
                    best="$repo"
                    best_len="${#repo_lower}"
                fi
                ;;
        esac
    done < <(list_repos)
    [ -n "$best" ] || return 1
    printf '%s\n' "$best"
}

require_git_repo() {
    local path="$1"
    [ -d "$path" ] || die "Нет такого репозитория: $path"
    git -C "$path" rev-parse --git-dir >/dev/null 2>&1 ||
        die "Не git-репозиторий: $path"
}

require_existing_worktree() {
    local path="$1"
    [ -e "$path" ] || die "Нет такого дерева: $path"
}

cmd_new() {
    [ -n "$TASK" ] || usage_error "нужен --task (например: --repo Kouki --task T50 --slug backup-timer)"
    validate_name "repo" "$REPO"
    validate_name "task" "$TASK"
    [ -z "$SLUG" ] || validate_name "slug" "$SLUG"

    local repo_path="$WORKSPACE/$REPO"
    require_git_repo "$repo_path"

    local slug="${SLUG:-task}"
    local name branch path
    name="$(lower "$REPO")-$(lower "$TASK")-$(lower "$slug")"
    # В имени дерева подчёркивания заменяются на дефисы, как в worktree.ps1.
    name="${name//_/-}"
    path="$WT_ROOT/$name"

    [ ! -e "$path" ] || die "Дерево уже есть: $path"

    branch="task/$(lower "$TASK")-$(lower "$slug")"
    if git -C "$repo_path" show-ref --verify --quiet "refs/heads/$branch"; then
        die "Ветка уже есть: $branch (сначала удалите её или возьмите другой slug)"
    fi
    if ! git -C "$repo_path" rev-parse --verify --quiet "$BASE^{commit}" >/dev/null; then
        die "Нет такой базы: $BASE (в репозитории $repo_path)"
    fi

    mkdir -p "$WT_ROOT"
    local rc=0
    git -C "$repo_path" worktree add -b "$branch" "$path" "$BASE" || rc=$?
    if [ "$rc" -ne 0 ]; then
        die "git worktree add завершился с кодом $rc"
    fi

    echo
    echo "Дерево: $path"
    echo "Ветка:  $branch (от $BASE)"
    if [ -f "$path/Kouki.Tests/Kouki.Tests.csproj" ]; then
        echo "Тесты:  cd '$path' && dotnet test Kouki.Tests/Kouki.Tests.csproj"
    else
        echo "Тесты:  см. AGENTS.md репозитория $REPO (в дереве $path)"
    fi
    if [ -f "$path/Kouki.slnx" ]; then
        echo "Restore: cd '$path' && dotnet restore Kouki.slnx -m:1 -nodeReuse:false"
    fi
}

cmd_list() {
    local -a repos=()
    local repo
    while IFS= read -r repo; do
        repos+=("$repo")
    done < <(list_repos)

    if [ "${#repos[@]}" -eq 0 ]; then
        die "В воркспейсе нет репозиториев: $WORKSPACE"
    fi

    for repo in "${repos[@]}"; do
        echo "== $repo"
        git -C "$WORKSPACE/$repo" worktree list ||
            die "git worktree list завершился с ошибкой для $repo"
    done
}

cmd_remove() {
    [ -n "$NAME" ] || usage_error "нужен --name (см. list)"
    validate_name "name" "$NAME"

    local name_lower path repo repo_path
    # new заменяет `_` на `-`; merge/remove обязаны нормализовать имя до поиска
    # репозитория, иначе find_repo_for_name получает сырое `kouki_t2_demo`
    # и не находит дерево `kouki-t2-demo`.
    name_lower="$(lower "$NAME")"
    name_lower="${name_lower//_/-}"
    path="$WT_ROOT/$name_lower"
    require_existing_worktree "$path"

    if ! repo="$(find_repo_for_name "$name_lower")"; then
        die "Не понял, из какого репозитория дерево $NAME — удалите через git worktree remove вручную"
    fi
    repo_path="$WORKSPACE/$repo"

    local branch
    branch="$(git -C "$path" branch --show-current 2>/dev/null)" || branch=""

    if [ "$FORCE" -eq 0 ]; then
        local status
        status="$(git -C "$path" status --porcelain)" ||
            die "git status завершился с ошибкой в $path"
        if [ -n "$status" ]; then
            printf '%s\n' "$status" >&2
            die "в дереве $path есть незакоммиченные изменения; удаление отменено (повторите с --force)"
        fi
    fi

    local -a args=(worktree remove)
    if [ "$FORCE" -eq 1 ]; then
        args+=(--force)
    fi
    local rc=0
    git -C "$repo_path" "${args[@]}" "$path" || rc=$?
    if [ "$rc" -ne 0 ]; then
        die "git worktree remove завершился с кодом $rc"
    fi

    echo "Дерево удалено: $path"
    if [ -n "$branch" ]; then
        echo "Ветка осталась — удалить: git -C '$repo_path' branch -D '$branch'"
    else
        echo "Ветка осталась — удалить: git -C '$repo_path' branch -D task/<...>"
    fi
}

cmd_merge() {
    [ -n "$NAME" ] || usage_error "нужен --name (см. list)"
    validate_name "name" "$NAME"

    local name_lower path repo repo_path
    # new заменяет `_` на `-`; merge/remove обязаны нормализовать имя до поиска
    # репозитория, иначе find_repo_for_name получает сырое `kouki_t2_demo`
    # и не находит дерево `kouki-t2-demo`.
    name_lower="$(lower "$NAME")"
    name_lower="${name_lower//_/-}"
    path="$WT_ROOT/$name_lower"
    require_existing_worktree "$path"

    if ! repo="$(find_repo_for_name "$name_lower")"; then
        die "Не понял, из какого репозитория дерево $NAME"
    fi
    repo_path="$WORKSPACE/$repo"

    local branch
    branch="$(git -C "$path" branch --show-current 2>/dev/null)" || branch=""
    [ -n "$branch" ] || die "Не определил ветку дерева $path (detached HEAD?)"

    local current
    current="$(git -C "$repo_path" branch --show-current)"
    if [ "$current" != "master" ]; then
        die "основное дерево $repo_path на ветке '$current', а merge вливает только в master"
    fi

    local status
    status="$(git -C "$repo_path" status --porcelain)" ||
        die "git status завершился с ошибкой в $repo_path"
    if [ -n "$status" ]; then
        printf '%s\n' "$status" >&2
        die "основное дерево грязное — сначала закоммитьте или спрячьте изменения"
    fi

    # Дерево задачи тоже должно быть чистым: иначе merge вольёт ветку без
    # незакоммиченной работы и отчитается об успехе, а правок в master не будет.
    local task_status
    task_status="$(git -C "$path" status --porcelain)" ||
        die "git status завершился с ошибкой в дереве задачи $path"
    if [ -n "$task_status" ]; then
        printf '%s\n' "$task_status" >&2
        die "дерево задачи грязное — сначала закоммитьте изменения в $path"
    fi

    local rc=0
    git -C "$repo_path" merge --no-ff "$branch" -m "merge: $branch" || rc=$?
    if [ "$rc" -ne 0 ]; then
        die "merge завершился с кодом $rc: разрешите конфликты вручную в $repo_path (откат — git merge --abort)"
    fi

    echo "Ветка $branch влита в master"
}

# Разбор аргументов: команда — единственный позиционный аргумент, остальное — опции.
POSITIONAL=()
while [ $# -gt 0 ]; do
    case "$1" in
        -h|--help)
            usage
            exit 0
            ;;
        -r|-repo|-Repo|--repo)
            need_value "$1" "${2:-}"
            REPO="$2"
            shift 2
            ;;
        -t|-task|-Task|--task)
            need_value "$1" "${2:-}"
            TASK="$2"
            shift 2
            ;;
        -s|-slug|-Slug|--slug)
            need_value "$1" "${2:-}"
            SLUG="$2"
            shift 2
            ;;
        -n|-name|-Name|--name)
            need_value "$1" "${2:-}"
            NAME="$2"
            shift 2
            ;;
        -b|-base|-Base|--base)
            need_value "$1" "${2:-}"
            BASE="$2"
            shift 2
            ;;
        -f|-Force|-force|--force)
            FORCE=1
            shift
            ;;
        -*)
            usage_error "неизвестный параметр: $1"
            ;;
        *)
            POSITIONAL+=("$1")
            shift
            ;;
    esac
done

if [ "${#POSITIONAL[@]}" -ne 1 ]; then
    usage >&2
    usage_error "нужна ровно одна команда: new, list, remove или merge"
fi

COMMAND="$(lower "${POSITIONAL[0]}")"
case "$COMMAND" in
    new|list|remove|merge) ;;
    *)
        usage >&2
        usage_error "неизвестная команда: ${POSITIONAL[0]} (new, list, remove, merge)"
        ;;
esac

[ -d "$WORKSPACE" ] || die "Не найден воркспейс: $WORKSPACE"

if [ "$FORCE" -eq 1 ] && [ "$COMMAND" != "remove" ]; then
    usage_error "--force имеет смысл только для remove"
fi

case "$COMMAND" in
    new) cmd_new ;;
    list) cmd_list ;;
    remove) cmd_remove ;;
    merge) cmd_merge ;;
esac
