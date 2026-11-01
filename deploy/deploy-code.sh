#!/usr/bin/env bash
# Код-деплой Kouki без установщика: обновить артефакт в /opt/kouki и перезапустить сервисы.
#
# Зачем: deploy/install.sh требует root (юниты, env, бэкап), а агенту на сервере
# root закрыт. Обновление одного кода — это rsync трёх каталогов и перезапуск
# сервисов через `kouki-admin`, поэтому оно живёт отдельным скриптом.
#
# Раскладка на сервере (проверено 01.11.2026):
#   /opt/kouki/app       — API (kouki.service)
#   /opt/kouki/app/bot   — бот (kouki-bot.service)
#   /opt/kouki/mcp       — MCP-сервер (stdio, сервиса нет)
#
# Ловушка, из-за которой скрипт отказывается работать: `rsync --delete` сносит
# приложение, если источник и цель не выровнены (например, передан
# publish/linux-x64/Kagedama.api вместо publish/linux-x64). Поэтому до rsync
# проверяются обязательные файлы источника и то, что цель — действительно /opt/kouki.
#
# Коды возврата: 0 — успех; 1 — проверка или деплой провалился; 2 — аргументы.
set -euo pipefail

usage() {
    cat <<'EOF'
Использование: deploy-code.sh [--from DIR] [--app-dir DIR] [--mcp-dir DIR]
                              [--dry-run] [--no-restart] [--skip-smoke] [--help]

  --from DIR       каталог артефакта (по умолчанию <репозиторий>/publish/linux-x64)
  --app-dir DIR    куда ставить API и бота (по умолчанию /opt/kouki/app)
  --mcp-dir DIR    куда ставить MCP-сервер (по умолчанию /opt/kouki/mcp)
  --dry-run        показать, что изменится, и ничего не менять
  --no-restart     не перезапускать сервисы (только файлы)
  --skip-smoke     не гонять deploy/smoke.sh после перезапуска
  --help           эта справка

Требования: rsync, curl, `sudo -n kouki-admin` (restart-kouki, restart-bot, api-token).
Перед первым деплоем артефакт собирается: tools/publish-linux.sh.
EOF
}

die() {
    echo "deploy-code: $*" >&2
    exit 1
}

arg_error() {
    echo "deploy-code: $*" >&2
    usage >&2
    exit 2
}

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FROM="$REPO_ROOT/publish/linux-x64"
APP_DIR=/opt/kouki/app
MCP_DIR=/opt/kouki/mcp
DRY_RUN=0
NO_RESTART=0
SKIP_SMOKE=0

while [ $# -gt 0 ]; do
    case "$1" in
        --from) [ $# -ge 2 ] || arg_error "у --from нет значения"; FROM="$2"; shift 2 ;;
        --app-dir) [ $# -ge 2 ] || arg_error "у --app-dir нет значения"; APP_DIR="$2"; shift 2 ;;
        --mcp-dir) [ $# -ge 2 ] || arg_error "у --mcp-dir нет значения"; MCP_DIR="$2"; shift 2 ;;
        --dry-run) DRY_RUN=1; shift ;;
        --no-restart) NO_RESTART=1; shift ;;
        --skip-smoke) SKIP_SMOKE=1; shift ;;
        --help|-h) usage; exit 0 ;;
        *) arg_error "неизвестный аргумент: $1" ;;
    esac
done

command -v rsync >/dev/null 2>&1 || die "нет rsync"
command -v curl >/dev/null 2>&1 || die "нет curl"

[ -d "$FROM" ] || die "нет каталога артефакта: $FROM (соберите tools/publish-linux.sh)"
FROM_REAL=$(cd "$FROM" && pwd -P)
APP_DIR_REAL=$(cd "$APP_DIR" 2>/dev/null && pwd -P || true)
[ -n "$APP_DIR_REAL" ] || die "нет каталога приложения: $APP_DIR"
[ -d "$MCP_DIR" ] || die "нет каталога MCP: $MCP_DIR"
MCP_DIR_REAL=$(cd "$MCP_DIR" && pwd -P)

# Защита от неверного пути источника: --delete в rsync снёс бы /opt/kouki/app.
[ "$FROM_REAL" != "$APP_DIR_REAL" ] || die "источник и цель совпадают: $FROM_REAL"
[ "$FROM_REAL" != "$MCP_DIR_REAL" ] || die "источник и цель совпадают: $FROM_REAL"

for name in Kagedama.api.dll appsettings.json wwwroot/app.js \
            bot/Kouki.Kagedama.Bot.dll bot/appsettings.json \
            mcp/Kagedama.mcp.dll mcp/appsettings.json; do
    [ -e "$FROM/$name" ] || die "в артефакте нет $name — это не тот каталог (нужен корень publish)"
done

[ -e "$APP_DIR_REAL/Kagedama.api.dll" ] || die "$APP_DIR_REAL не похож на установленный сервер (нет Kagedama.api.dll)"

# Форма деплоя должна совпадать: если в проде self-contained (рантайм лежит рядом,
# потому что пакета aspnetcore-runtime для 26.04 может не быть), framework-dependent
# артефакт снёс бы рантайм через --delete, и сервис не поднялся бы.
target_self_contained=0
[ -e "$APP_DIR_REAL/libcoreclr.so" ] && target_self_contained=1
artifact_self_contained=0
[ -e "$FROM_REAL/libcoreclr.so" ] && artifact_self_contained=1
if [ "$target_self_contained" -ne "$artifact_self_contained" ]; then
    die "форма деплоя не совпадает: в $APP_DIR_REAL self-contained=$target_self_contained, в артефакте self-contained=$artifact_self_contained — пересоберите tools/publish-linux.sh тем же флагом (--self-contained)"
fi
[ -w "$APP_DIR_REAL" ] || die "нет прав на запись в $APP_DIR_REAL (нужен пользователь-владелец)"
[ -w "$MCP_DIR_REAL" ] || die "нет прав на запись в $MCP_DIR_REAL"

# Планируемые удаления: показываем до записи, чтобы --delete не был сюрпризом.
planned_deletes=$(
    rsync -a --delete --dry-run --itemize-changes \
        --exclude 'appsettings.Development.json' --exclude 'bot/' --exclude 'mcp/' \
        "$FROM_REAL"/ "$APP_DIR_REAL"/ 2>/dev/null |
        grep -c '^\*deleting' || true
)
echo "Артефакт: $FROM_REAL"
echo "Цель:     $APP_DIR_REAL (+ bot), $MCP_DIR_REAL"
echo "rsync удалит файлов в $APP_DIR_REAL: $planned_deletes (bot/ и mcp/ исключены)"

if [ "$DRY_RUN" -eq 1 ]; then
    echo "--- dry-run: API ---"
    rsync -a --delete --dry-run --itemize-changes \
        --exclude 'appsettings.Development.json' --exclude 'bot/' --exclude 'mcp/' \
        "$FROM_REAL"/ "$APP_DIR_REAL"/
    echo "--- dry-run: bot ---"
    rsync -a --delete --dry-run --itemize-changes "$FROM_REAL/bot/" "$APP_DIR_REAL/bot/"
    echo "--- dry-run: mcp ---"
    rsync -a --delete --dry-run --itemize-changes "$FROM_REAL/mcp/" "$MCP_DIR_REAL"/
    echo "dry-run: ничего не изменено"
    exit 0
fi

rsync -a --delete \
    --exclude 'appsettings.Development.json' --exclude 'bot/' --exclude 'mcp/' \
    "$FROM_REAL"/ "$APP_DIR_REAL"/
rsync -a --delete "$FROM_REAL/bot/" "$APP_DIR_REAL/bot/"
rsync -a --delete "$FROM_REAL/mcp/" "$MCP_DIR_REAL"/
echo "Файлы обновлены"

if [ "$NO_RESTART" -eq 1 ]; then
    echo "Сервисы не перезапускались (--no-restart)"
    exit 0
fi

sudo -n kouki-admin restart-kouki
sudo -n kouki-admin restart-bot

# Ждём подъёма: 401 — тоже признак живого сервера (эндпоинт требует токен).
code=""
for _ in $(seq 1 30); do
    code=$(curl -s -o /dev/null -m 3 -w '%{http_code}' http://127.0.0.1:5300/api/health || true)
    case "$code" in 200|401) break ;; esac
    sleep 2
done
echo "/api/health -> ${code:-нет ответа}"
case "$code" in
    200|401) ;;
    *) die "сервер не поднялся после деплоя (см. sudo -n kouki-admin logs-kouki 80)" ;;
esac

if [ "$SKIP_SMOKE" -eq 0 ]; then
    TOKEN=$(sudo -n kouki-admin api-token) || die "не удалось получить токен API"
    KOUKI_TOKEN="$TOKEN" bash "$REPO_ROOT/deploy/smoke.sh" --quiet
    echo "Смоук пройден"
fi

echo "Готово: код обновлён, сервисы перезапущены"
