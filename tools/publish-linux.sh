#!/usr/bin/env bash
# Публикация сервера Kouki для Linux без PowerShell.
#
# Зачем: на домашнем сервере нет `pwsh`, поэтому tools/publish-linux.ps1 там не
# работает, а публиковать надо с сервера. Скрипт повторяет поведение `.ps1`:
# собирает три артефакта в подкаталоги (api, bot/, mcp/) и проверяет, что в них
# попали нативные библиотеки — иначе `/api/voice` молча отвечает 503.
#
# Коды возврата: 0 — успех; 1 — публикация или проверка провалилась; 2 — аргументы.
set -euo pipefail

usage() {
    cat <<'EOF'
Использование: publish-linux.sh [--runtime RID] [--output DIR] [--no-speech]
                                [--self-contained] [--help]

  --runtime RID       идентификатор рантайма (по умолчанию linux-x64)
  --output DIR        каталог артефакта (по умолчанию <репозиторий>/publish/<RID>)
  --no-speech         собрать без sherpa-onnx: /api/voice будет отвечать 503
  --self-contained    положить рантайм .NET в артефакт (~80 МБ)
  --help              эта справка

Без --self-contained нужен установленный aspnetcore-runtime-10.0 на целевой машине.
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

RUNTIME=linux-x64
OUTPUT=""
SPEECH=true
SELF_CONTAINED=false

while [ $# -gt 0 ]; do
    case "$1" in
        --runtime) [ $# -ge 2 ] || arg_error "у --runtime нет значения"; RUNTIME="$2"; shift 2 ;;
        --output) [ $# -ge 2 ] || arg_error "у --output нет значения"; OUTPUT="$2"; shift 2 ;;
        --no-speech) SPEECH=false; shift ;;
        --self-contained) SELF_CONTAINED=true; shift ;;
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

command -v dotnet >/dev/null 2>&1 || die "нет dotnet в PATH (нужен /etc/profile.d/dotnet.sh)"

echo "Публикация $RUNTIME (sherpa=$SPEECH, self-contained=$SELF_CONTAINED) -> $OUTPUT"
rm -rf "$OUTPUT"
mkdir -p "$OUTPUT"

# Общие флаги MSBuild: однопоточная сборка, иначе в ограниченном окружении она
# падает молча с «Ошибок: 0» и ненулевым кодом.
dotnet publish "$API_PROJECT" \
    -c Release \
    -p:KoukiEnableSherpa="$SPEECH" \
    -r "$RUNTIME" \
    --self-contained "$SELF_CONTAINED" \
    -m:1 -nodeReuse:false \
    -o "$OUTPUT"

echo "Публикация бота -> $OUTPUT/bot"
dotnet publish "$BOT_PROJECT" \
    -c Release \
    -r "$RUNTIME" \
    --self-contained "$SELF_CONTAINED" \
    -m:1 -nodeReuse:false \
    -o "$OUTPUT/bot"

echo "Публикация MCP -> $OUTPUT/mcp"
dotnet publish "$MCP_PROJECT" \
    -c Release \
    -p:KoukiEnableSherpa="$SPEECH" \
    -r "$RUNTIME" \
    --self-contained "$SELF_CONTAINED" \
    -m:1 -nodeReuse:false \
    -o "$OUTPUT/mcp"

missing=()
for name in Kagedama.api.dll appsettings.json wwwroot/app.js \
            bot/Kouki.Kagedama.Bot.dll bot/appsettings.json \
            mcp/Kagedama.mcp.dll mcp/appsettings.json; do
    [ -e "$OUTPUT/$name" ] || missing+=("$name")
done

if [ "$SPEECH" = true ]; then
    for name in libonnxruntime.so libsherpa-onnx-c-api.so; do
        # Нативки могут лежать в подкаталоге рантайма, поэтому ищем по всему артефакту.
        if ! find "$OUTPUT" -type f -name "$name" -print -quit | grep -q .; then
            missing+=("$name")
        fi
    done
fi

if ! find "$OUTPUT" -type f -name 'libe_sqlite3*' -print -quit | grep -q .; then
    missing+=('libe_sqlite3.so')
fi

if [ "${#missing[@]}" -gt 0 ]; then
    die "в публикации не хватает: ${missing[*]}"
fi

files=$(find "$OUTPUT" -type f | wc -l | tr -d ' ')
size_mb=$(du -sm "$OUTPUT" | awk '{print $1}')
echo "Готово: $files файлов, ${size_mb} МБ — $OUTPUT"
