#!/usr/bin/env bash
# Публикация сервера Kouki для Linux без PowerShell.
#
# Зачем: на домашнем сервере нет `pwsh`, поэтому tools/publish-linux.ps1 там не
# работает, а публиковать надо с сервера. Скрипт повторяет поведение `.ps1`:
# собирает три артефакта: API в корень, бот в bot/, MCP в mcp/, и проверяет, что в
# них попали нативные библиотеки — иначе `/api/voice` молча отвечает 503.
#
# Отличия от `.ps1`, которые важны на сервере:
#   * self-contained по умолчанию — прод на 26.04 собран так, и deploy-code.sh
#     отказывается класть framework-dependent артефакт поверх;
#   * публикация идёт во временный каталог и подменяет OUTPUT только после успеха:
#     при провале предыдущий артефакт остаётся на месте, а при успешной подмене
#     старый уезжает в `<OUTPUT>.prev`;
#   * OUTPUT вне `publish/` и вне временного каталога требует явного --allow-any-output:
#     иначе `--output <корень репозитория>` мог бы снести репозиторий.
#
# Коды возврата: 0 — успех; 1 — публикация или проверка провалилась; 2 — аргументы.
set -euo pipefail

usage() {
    cat <<'EOF'
Использование: publish-linux.sh [--runtime RID] [--output DIR] [--no-speech]
                                [--framework-dependent] [--allow-any-output] [--help]

  --runtime RID           идентификатор рантайма (по умолчанию linux-x64)
  --output DIR            каталог артефакта (по умолчанию <репозиторий>/publish/<RID>)
  --no-speech             собрать без sherpa-onnx: /api/voice будет отвечать 503
  --self-contained        положить рантайм .NET в артефакт (по умолчанию)
  --framework-dependent   не класть рантайм .NET в артефакт (нужен aspnetcore-runtime-10.0
                          на целевой машине; прод собран self-contained, поэтому deploy
                          такой артефакт отклонит)
  --allow-any-output      разрешить --output вне publish/ и вне временного каталога
  --help                  эта справка

По умолчанию артефакт self-contained: в проде на Ubuntu 26.04 нет пакета
aspnetcore-runtime-10.0, рантайм лежит рядом с приложением.
EOF
}

die() {
    echo "publish-linux: $*" >&2
    exit 1
}

arg_error() {
    echo "publish-linux: $*" >&2
    usage >&2
    exit 2
}

need_value() {
    [ -n "${1:-}" ] || arg_error "у $2 нет значения"
    case "$1" in
        -*) arg_error "значение $2 похоже на флаг: $1" ;;
    esac
}

RUNTIME=linux-x64
OUTPUT=""
SPEECH=true
SELF_CONTAINED=true
ALLOW_ANY_OUTPUT=0

while [ $# -gt 0 ]; do
    case "$1" in
        --runtime) [ $# -ge 2 ] || arg_error "у --runtime нет значения"; need_value "$2" "--runtime"; RUNTIME="$2"; shift 2 ;;
        --output) [ $# -ge 2 ] || arg_error "у --output нет значения"; need_value "$2" "--output"; OUTPUT="$2"; shift 2 ;;
        --no-speech) SPEECH=false; shift ;;
        --self-contained) SELF_CONTAINED=true; shift ;;
        --framework-dependent) SELF_CONTAINED=false; shift ;;
        --allow-any-output) ALLOW_ANY_OUTPUT=1; shift ;;
        --help|-h) usage; exit 0 ;;
        *) arg_error "неизвестный аргумент: $1" ;;
    esac
done

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
API_PROJECT="$REPO_ROOT/Kagedama.api/Kagedama.api.csproj"
BOT_PROJECT="$REPO_ROOT/Kouki.Kagedama.Bot/Kouki.Kagedama.Bot.csproj"
MCP_PROJECT="$REPO_ROOT/Kagedama.mcp/Kagedama.mcp.csproj"
for project in "$API_PROJECT" "$BOT_PROJECT" "$MCP_PROJECT"; do
    [ -f "$project" ] || die "не найден проект: $project"
done

if [ -z "$OUTPUT" ]; then
    OUTPUT="$REPO_ROOT/publish/$RUNTIME"
fi

# Куда разрешено писать. `publish/` — обычный путь; временный каталог — для проверок.
# Всё остальное только с явным флагом: `rm -rf` по опечатке в пути стоит репозитория.
#
# Нормализация не требует существующего каталога: в свежем дереве задачи `publish/`
# ещё нет, а первый же запуск должен работать (realpath -m не проверяет путь).
normalize_path() {
    local path="$1"
    case "$path" in
        /*) ;;
        *) path="$PWD/$path" ;;
    esac
    if command -v realpath >/dev/null 2>&1; then
        realpath -m "$path"
    elif command -v readlink >/dev/null 2>&1; then
        readlink -m "$path" 2>/dev/null || printf '%s\n' "$path"
    else
        printf '%s\n' "$path"
    fi
}

OUTPUT_NORMALIZED="$(normalize_path "$OUTPUT")"
REPO_REAL="$(normalize_path "$REPO_ROOT")"
TMP_REAL="$(normalize_path "${TMPDIR:-/tmp}")"
case "$OUTPUT_NORMALIZED" in
    "$REPO_REAL"/publish/*) ;;
    "$TMP_REAL"/*) ;;
    *)
        [ "$ALLOW_ANY_OUTPUT" -eq 1 ] || die "output вне publish/ и вне временного каталога: $OUTPUT_NORMALIZED (осознанно — с --allow-any-output)"
        ;;
esac

command -v dotnet >/dev/null 2>&1 || die "нет dotnet в PATH (нужен /etc/profile.d/dotnet.sh)"

mkdir -p "$(dirname "$OUTPUT_NORMALIZED")"
STAGE="$(mktemp -d "$(dirname "$OUTPUT_NORMALIZED")/.publish.XXXXXX")" || die "не удалось создать временный каталог рядом с $OUTPUT_NORMALIZED"
cleanup() {
    # Функция обязана вернуть 0: EXIT-trap с `set -e` иначе перевернёт код успешной
    # публикации в 1 (после `mv "$STAGE"` каталога уже нет).
    if [ -d "$STAGE" ]; then
        rm -rf "$STAGE" || true
    fi
    return 0
}
trap cleanup EXIT

# Окно подмены: между переносом старого артефакта в .prev и mv staging в OUTPUT
# Ctrl+C оставил бы OUTPUT отсутствующим. Возвращаем .prev на место.
PREV_OUTPUT=""
# shellcheck disable=SC2329 # вызывается через trap
interrupted() {
    trap - INT TERM
    if [ -n "$PREV_OUTPUT" ] && [ ! -e "$OUTPUT_NORMALIZED" ] && [ -e "$PREV_OUTPUT" ]; then
        mv "$PREV_OUTPUT" "$OUTPUT_NORMALIZED" || true
        echo "Публикация прервана: прежний артефакт возвращён в $OUTPUT_NORMALIZED" >&2
    else
        echo "Публикация прервана: артефакт не подменён (сборка во временном каталоге)" >&2
    fi
    exit 1
}
trap interrupted INT TERM

echo "Публикация $RUNTIME (sherpa=$SPEECH, self-contained=$SELF_CONTAINED) -> $OUTPUT_NORMALIZED"
if [ "$SELF_CONTAINED" = false ]; then
    echo "ВНИМАНИЕ: framework-dependent артефакт; прод собран self-contained, deploy-code.sh его отклонит" >&2
fi

# Общие флаги MSBuild: однопоточная сборка, иначе в ограниченном окружении она
# падает молча с «Ошибок: 0» и ненулевым кодом.
dotnet publish "$API_PROJECT" \
    -c Release \
    -p:KoukiEnableSherpa="$SPEECH" \
    -r "$RUNTIME" \
    --self-contained "$SELF_CONTAINED" \
    -m:1 -nodeReuse:false \
    -o "$STAGE" || die "dotnet publish API завершился с кодом $?"

echo "Публикация бота -> $STAGE/bot"
dotnet publish "$BOT_PROJECT" \
    -c Release \
    -r "$RUNTIME" \
    --self-contained "$SELF_CONTAINED" \
    -m:1 -nodeReuse:false \
    -o "$STAGE/bot" || die "dotnet publish бота завершился с кодом $?"

echo "Публикация MCP -> $STAGE/mcp"
dotnet publish "$MCP_PROJECT" \
    -c Release \
    -p:KoukiEnableSherpa="$SPEECH" \
    -r "$RUNTIME" \
    --self-contained "$SELF_CONTAINED" \
    -m:1 -nodeReuse:false \
    -o "$STAGE/mcp" || die "dotnet publish MCP завершился с кодом $?"

missing=()
for name in Kagedama.api.dll appsettings.json wwwroot/app.js \
            bot/Kouki.Kagedama.Bot.dll bot/appsettings.json \
            mcp/Kagedama.mcp.dll mcp/appsettings.json; do
    [ -e "$STAGE/$name" ] || missing+=("$name")
done

if [ "$SPEECH" = true ]; then
    for name in libonnxruntime.so libsherpa-onnx-c-api.so; do
        # Нативки могут лежать в подкаталоге рантайма, поэтому ищем по всему артефакту.
        if ! find "$STAGE" -type f -name "$name" -print -quit | grep -q .; then
            missing+=("$name")
        fi
    done
fi

if ! find "$STAGE" -type f -name 'libe_sqlite3*' -print -quit | grep -q .; then
    missing+=('libe_sqlite3.so')
fi

if [ "${#missing[@]}" -gt 0 ]; then
    die "в публикации не хватает: ${missing[*]} (предыдущий артефакт не тронут: $OUTPUT_NORMALIZED)"
fi

# Подмена только после успешной проверки: неудачная сборка не должна уничтожать
# артефакт, на который можно откатиться.
if [ -e "$OUTPUT_NORMALIZED" ]; then
    PREV_OUTPUT="$OUTPUT_NORMALIZED.prev"
    rm -rf "$PREV_OUTPUT"
    mv "$OUTPUT_NORMALIZED" "$PREV_OUTPUT"
    echo "Предыдущий артефакт сохранён: $PREV_OUTPUT"
fi
mv "$STAGE" "$OUTPUT_NORMALIZED"

files=$(find "$OUTPUT_NORMALIZED" -type f 2>/dev/null | wc -l | tr -d ' ' || true)
size_mb=$(du -sm "$OUTPUT_NORMALIZED" 2>/dev/null | awk '{print $1}' || true)
echo "Готово: $files файлов, ${size_mb} МБ — $OUTPUT_NORMALIZED"
