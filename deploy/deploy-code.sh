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
# приложение, если источник и цель не выровнены. Поэтому до rsync проверяются
# маркеры в каждом каталоге (`Kagedama.api.dll`, `Kagedama.mcp.dll`), форма деплоя
# (self-contained и наличие нативок sherpa) и то, что ни один из путей не лежит
# внутри другого. Перед заменой файлов делается копия в `<каталог>.prev`, и при
# провале health или смоука прод возвращается на неё автоматически — если копия
# не отключена флагом `--no-backup` (тогда скрипт честно скажет, что отката нет).
#
# Коды возврата: 0 — успех; 1 — проверка или деплой провалился; 2 — аргументы.
set -euo pipefail

usage() {
    cat <<'EOF'
Использование: deploy-code.sh [--from DIR] [--app-dir DIR] [--mcp-dir DIR]
                              [--dry-run] [--no-restart] [--skip-smoke]
                              [--no-backup] [--allow-no-speech] [--help]

  --from DIR           каталог артефакта (по умолчанию <репозиторий>/publish/linux-x64)
  --app-dir DIR        куда ставить API и бота (по умолчанию /opt/kouki/app)
  --mcp-dir DIR        куда ставить MCP-сервер (по умолчанию /opt/kouki/mcp)
  --dry-run            показать, что изменится, и ничего не менять
  --no-restart         не перезапускать сервисы (только файлы)
  --skip-smoke         не гонять deploy/smoke.sh после перезапуска
  --no-backup          не делать копию <каталог>.prev и, значит, откат
  --allow-no-speech    разрешить убрать распознавание речи из прода (обычно ошибка)
  --help               эта справка

Требования: rsync, curl, `sudo -n kouki-admin` (restart-kouki, restart-bot, api-token).
Перед деплоем артефакт собирается: tools/publish-linux.sh.
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

# Значение флага не должно быть пустым или начинаться с «-»: иначе `--from --dry-run`
# молча приняло бы флаг за путь.
need_value() {
    [ -n "${1:-}" ] || arg_error "у $2 нет значения"
    case "$1" in
        -*) arg_error "значение $2 похоже на флаг: $1" ;;
    esac
}

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FROM="$REPO_ROOT/publish/linux-x64"
APP_DIR=/opt/kouki/app
MCP_DIR=/opt/kouki/mcp
DRY_RUN=0
NO_RESTART=0
SKIP_SMOKE=0
BACKUP=1
ALLOW_NO_SPEECH=0

while [ $# -gt 0 ]; do
    case "$1" in
        --from) [ $# -ge 2 ] || arg_error "у --from нет значения"; need_value "$2" "--from"; FROM="$2"; shift 2 ;;
        --app-dir) [ $# -ge 2 ] || arg_error "у --app-dir нет значения"; need_value "$2" "--app-dir"; APP_DIR="$2"; shift 2 ;;
        --mcp-dir) [ $# -ge 2 ] || arg_error "у --mcp-dir нет значения"; need_value "$2" "--mcp-dir"; MCP_DIR="$2"; shift 2 ;;
        --dry-run) DRY_RUN=1; shift ;;
        --no-restart) NO_RESTART=1; shift ;;
        --skip-smoke) SKIP_SMOKE=1; shift ;;
        --no-backup) BACKUP=0; shift ;;
        --allow-no-speech) ALLOW_NO_SPEECH=1; shift ;;
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
MCP_DIR_REAL=$(cd "$MCP_DIR" 2>/dev/null && pwd -P || true)
[ -n "$MCP_DIR_REAL" ] || die "нет каталога MCP: $MCP_DIR"

for name in Kagedama.api.dll appsettings.json wwwroot/app.js \
            bot/Kouki.Kagedama.Bot.dll bot/appsettings.json \
            mcp/Kagedama.mcp.dll mcp/appsettings.json; do
    [ -e "$FROM/$name" ] || die "в артефакте нет $name — это не тот каталог (нужен корень артефакта publish/linux-x64)"
done

# Маркеры целей: иначе --delete доберётся до чужого каталога (например, --mcp-dir /opt/kouki
# снесёт и app, и mcp). Проверяются оба каталога, а не только app.
[ -e "$APP_DIR_REAL/Kagedama.api.dll" ] || die "$APP_DIR_REAL не похож на установленный сервер (нет Kagedama.api.dll)"
[ -e "$MCP_DIR_REAL/Kagedama.mcp.dll" ] || die "$MCP_DIR_REAL не похож на MCP-сервер (нет Kagedama.mcp.dll)"

# Вложенность в любую сторону: rsync --delete по родителю снёс бы соседа, а цель внутри
# источника удалила бы артефакт во время чтения.
inside() {
    [ "$1" = "$2" ] && return 0
    case "$1/" in
        "$2"/*) return 0 ;;
    esac
    return 1
}

if inside "$MCP_DIR_REAL" "$APP_DIR_REAL" || inside "$APP_DIR_REAL" "$MCP_DIR_REAL"; then
    die "каталоги app и mcp пересекаются: $APP_DIR_REAL и $MCP_DIR_REAL"
fi
if inside "$FROM_REAL" "$APP_DIR_REAL" || inside "$FROM_REAL" "$MCP_DIR_REAL"; then
    die "артефакт $FROM_REAL лежит внутри цели деплоя"
fi
if inside "$APP_DIR_REAL" "$FROM_REAL" || inside "$MCP_DIR_REAL" "$FROM_REAL"; then
    die "цель деплоя лежит внутри артефакта $FROM_REAL"
fi

# Форма деплоя должна совпадать: если в проде self-contained (рантайм лежит рядом,
# потому что пакета aspnetcore-runtime для 26.04 может не быть), framework-dependent
# артефакт снёс бы рантайм через --delete, и сервис не поднялся бы.
target_self_contained=0
[ -e "$APP_DIR_REAL/libcoreclr.so" ] && target_self_contained=1
artifact_self_contained=0
[ -e "$FROM_REAL/libcoreclr.so" ] && artifact_self_contained=1
if [ "$target_self_contained" -ne "$artifact_self_contained" ]; then
    if [ "$target_self_contained" -eq 1 ]; then
        die "в $APP_DIR_REAL self-contained сборка, а в артефакте — framework-dependent: пересоберите tools/publish-linux.sh (self-contained там по умолчанию)"
    fi
    die "в $APP_DIR_REAL framework-dependent сборка, а в артефакте — self-contained: пересоберите tools/publish-linux.sh --framework-dependent"
fi

# Распознавание речи: без него /api/voice отвечает 503, поэтому потеря нативок sherpa
# в проде — это ошибка, а не «уменьшили размер». Тот же флаг, что при публикации.
has_native() {
    find "$1" -type f -name "$2" -print -quit | grep -q .
}

missing_natives=()
for native in libsherpa-onnx-c-api.so libonnxruntime.so; do
    if has_native "$APP_DIR_REAL" "$native" && ! has_native "$FROM_REAL" "$native"; then
        missing_natives+=("$native")
    fi
done
if [ "${#missing_natives[@]}" -gt 0 ]; then
    [ "$ALLOW_NO_SPEECH" -eq 1 ] || die "в проде есть ${missing_natives[*]}, а в артефакте нет: соберите без --no-speech (или --allow-no-speech, если распознавание убирается осознанно)"
    echo "ВНИМАНИЕ: распознавание речи будет удалено из прода (--allow-no-speech)" >&2
fi

# Планируемые удаления: показываем до записи, чтобы --delete не был сюрпризом.
# Считаем только API-каталог; bot/ и mcp/ исключены и обновляются своими rsync.
if ! rsync_dry="$(rsync -a --delete --dry-run --itemize-changes \
        --exclude 'appsettings.Development.json' --exclude 'bot/' --exclude 'mcp/' \
        "$FROM_REAL"/ "$APP_DIR_REAL"/ 2>/dev/null)"; then
    # Ошибку rsync не глушим: «0 удалений» при сломанном rsync — ложь, за которой
    # легко не заметить, что --delete вот-вот снесёт файлы.
    echo "ВНИМАНИЕ: rsync --dry-run не смог спланировать удаления (продолжаю)" >&2
    planned_deletes="?"
else
    planned_deletes="$(printf '%s\n' "$rsync_dry" | grep -c '^\*deleting' || true)"
fi
echo "Артефакт: $FROM_REAL"
echo "Цель:     $APP_DIR_REAL (+ bot), $MCP_DIR_REAL"
echo "rsync удалит файлов в API-каталоге: $planned_deletes (bot/ и mcp/ обновляются отдельными rsync по своим каталогам)"

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

# Права на запись нужны только реальному деплою: read-only предпросмотр должен
# работать и от пользователя без права записи в /opt.
[ -w "$APP_DIR_REAL" ] || die "нет прав на запись в $APP_DIR_REAL (нужен пользователь-владелец)"
[ -w "$MCP_DIR_REAL" ] || die "нет прав на запись в $MCP_DIR_REAL"

PREV_APP="$APP_DIR_REAL.prev"
PREV_MCP="$MCP_DIR_REAL.prev"
# Копия не должна оказаться источником, целью или их родителем: `rm -rf` ниже
# удалил бы живой каталог.
for prev in "$PREV_APP" "$PREV_MCP"; do
    if inside "$FROM_REAL" "$prev" || inside "$prev" "$FROM_REAL" ||
       inside "$APP_DIR_REAL" "$prev" || inside "$MCP_DIR_REAL" "$prev"; then
        die "копия для отката пересекается с $FROM_REAL или целями деплоя: $prev"
    fi
done
ROLLBACK_READY=0

# sudoers проверяем до копирования 300+ МБ: иначе деплой упадёт на рестарте, а откат
# не сможет перезапустить сервис — при живых старых файлах и мёртвом процессе.
if [ "$NO_RESTART" -eq 0 ]; then
    sudo -n kouki-admin status >/dev/null 2>&1 ||
        die "нет sudo -n kouki-admin status: деплой не сможет перезапустить сервисы (см. deploy/README.md)"
fi

if [ "$BACKUP" -eq 1 ]; then
    echo "Копия текущего артефакта: $PREV_APP, $PREV_MCP"
    rm -rf "$PREV_APP" "$PREV_MCP" || die "не удалось убрать старую копию"
    cp -a "$APP_DIR_REAL" "$PREV_APP" || die "не удалось сохранить копию $APP_DIR_REAL"
    cp -a "$MCP_DIR_REAL" "$PREV_MCP" || die "не удалось сохранить копию $MCP_DIR_REAL"
    ROLLBACK_READY=1
else
    echo "Копия не делается (--no-backup): откат только вручную" >&2
fi

# Коды отката: 0 — восстановлено, 2 — копии нет (--no-backup), 1 — откат не удался.
# Глушить ошибки rsync нельзя: сообщение «прод возвращён» при неудавшемся восстановлении
# хуже, чем честное «восстанавливайте руками».
rollback() {
    if [ "$ROLLBACK_READY" -eq 0 ]; then
        echo "Откат невозможен: копии нет (--no-backup)." >&2
        return 2
    fi
    echo "Откат к предыдущему артефакту…" >&2
    local failed=0
    rsync -a --delete "$PREV_APP"/ "$APP_DIR_REAL"/ || { echo "откат app не удался ($PREV_APP)" >&2; failed=1; }
    rsync -a --delete "$PREV_MCP"/ "$MCP_DIR_REAL"/ || { echo "откат mcp не удался ($PREV_MCP)" >&2; failed=1; }
    sudo -n kouki-admin restart-kouki >/dev/null 2>&1 || { echo "restart-kouki после отката не удался" >&2; failed=1; }
    sudo -n kouki-admin restart-bot >/dev/null 2>&1 || { echo "restart-bot после отката не удался" >&2; failed=1; }
    return "$failed"
}

# Провал после замены файлов: откатиться, если есть копия, и сказать правду в обоих случаях.
fail_after_change() {
    local reason="$1" rb=0
    rollback || rb=$?
    case "$rb" in
        0) die "$reason — прод возвращён к предыдущему артефакту ($PREV_APP)" ;;
        2) die "$reason — откат отключён (--no-backup): прод остался на новом артефакте" ;;
        *) die "$reason — откат НЕ удался: восстановите вручную из $PREV_APP и $PREV_MCP" ;;
    esac
}

# Ctrl+C, systemd stop или обрыв SSH (SIGHUP) между rsync'ами оставляли бы смешанное
# дерево: откатываемся тем же путём, что и при ошибке.
# shellcheck disable=SC2329 # вызывается через trap
interrupted() {
    trap - INT TERM HUP
    fail_after_change "деплой прерван (INT/TERM/HUP)"
}
trap interrupted INT TERM HUP

rsync -a --delete \
    --exclude 'appsettings.Development.json' --exclude 'bot/' --exclude 'mcp/' \
    "$FROM_REAL"/ "$APP_DIR_REAL"/ || fail_after_change "rsync API завершился с ошибкой"
rsync -a --delete "$FROM_REAL/bot/" "$APP_DIR_REAL/bot/" || fail_after_change "rsync бота завершился с ошибкой"
rsync -a --delete "$FROM_REAL/mcp/" "$MCP_DIR_REAL"/ || fail_after_change "rsync MCP завершился с ошибкой"
echo "Файлы обновлены"

if [ "$NO_RESTART" -eq 1 ]; then
    if [ "$BACKUP" -eq 1 ]; then
        echo "Сервисы не перезапускались (--no-restart); копия для отката — $PREV_APP и $PREV_MCP"
    else
        echo "Сервисы не перезапускались (--no-restart), копии нет (--no-backup)"
    fi
    exit 0
fi

# Метка начала выката: смоук смотрит журнал бота только с этого момента.
# Иначе ошибки, случившиеся до деплоя (например, сразу после перезагрузки),
# валят смоук, и deploy откатывает уже успешный выкат. Метка ставится до рестарта,
# чтобы в окно попали сбои старта нового бота; штатные строки остановки старого PID
# смоук отбрасывает сам (см. BOT_IGNORE_RE в smoke.sh).
SMOKE_SINCE=$(date +%s)
sudo -n kouki-admin restart-kouki || fail_after_change "restart-kouki завершился с ошибкой"
# kouki-bot входит в PartOf=kouki.service, поэтому бот уже перезапущен; явный вызов —
# страховка на случай, если PartOf из юнита уберут.
sudo -n kouki-admin restart-bot || fail_after_change "restart-bot завершился с ошибкой"

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
    *) fail_after_change "сервер не поднялся после деплоя (журнал: sudo -n kouki-admin logs-kouki 80)" ;;
esac

if [ "$SKIP_SMOKE" -eq 0 ]; then
    TOKEN=$(sudo -n kouki-admin api-token) || fail_after_change "не удалось получить токен API"
    if ! KOUKI_TOKEN="$TOKEN" KOUKI_SMOKE_SINCE="$SMOKE_SINCE" bash "$REPO_ROOT/deploy/smoke.sh" --quiet; then
        fail_after_change "смоук провалился, смотрите вывод выше"
    fi
    echo "Смоук пройден"
fi

echo "Готово: код обновлён, сервисы перезапущены"
if [ "$BACKUP" -eq 1 ]; then
    echo "Предыдущий артефакт сохранён: $PREV_APP, $PREV_MCP (откат вручную — rsync из них)"
fi
exit 0
