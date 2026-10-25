#!/usr/bin/env bash
# Установка сервера Kouki на Linux (Ubuntu 22.04/24.04, Debian 12).
#
# Что делает:
#   1. создаёт системного пользователя kouki;
#   2. раскладывает артефакт публикации в /opt/kouki/app;
#   3. создаёт /var/lib/kouki/{db,audio,models} и /etc/kouki;
#   4. ставит юнит systemd и (если файла ещё нет) env-файл со случайным токеном;
#   5. запускает сервер и проверяет /api/health.
#
# Повторный запуск безопасен: артефакт обновляется, env-файл не перезаписывается.
#
# Использование:
#   sudo ./deploy/install.sh /path/to/publish
#   sudo ./deploy/install.sh /path/to/publish --urls http://127.0.0.1:5300
#   sudo ./deploy/install.sh /path/to/publish --with-bot --bot-token 123:ABC
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

APP_DIR=/opt/kouki/app
DATA_DIR=/var/lib/kouki
CONF_DIR=/etc/kouki
SERVICE=/etc/systemd/system/kouki.service
BOT_SERVICE=/etc/systemd/system/kouki-bot.service
USER_NAME=kouki
URLS="http://0.0.0.0:5300"
WITH_BOT=0
BOT_TOKEN=""

SOURCE="${1:-}"
[ $# -gt 0 ] && shift || true
while [ $# -gt 0 ]; do
    case "$1" in
        --urls) URLS="$2"; shift 2 ;;
        --app-dir) APP_DIR="$2"; shift 2 ;;
        --user) USER_NAME="$2"; shift 2 ;;
        --with-bot) WITH_BOT=1; shift ;;
        --bot-token) BOT_TOKEN="$2"; shift 2 ;;
        *) echo "Неизвестный аргумент: $1" >&2; exit 2 ;;
    esac
done

if [ -z "$SOURCE" ] || [ ! -f "$SOURCE/Kagedama.api.dll" ]; then
    echo "Нужен каталог публикации с Kagedama.api.dll: sudo $0 /path/to/publish" >&2
    echo "Собрать: tools/publish-linux.ps1 (Windows) или" >&2
    echo "  dotnet publish Kagedama.api/Kagedama.api.csproj -c Release -r linux-x64 --self-contained false -p:KoukiEnableSherpa=true -o publish/linux-x64" >&2
    exit 2
fi

if [ "$(id -u)" -ne 0 ]; then
    echo "Запускать от root: sudo $0 $*" >&2
    exit 2
fi

if ! command -v dotnet >/dev/null 2>&1; then
    echo "Нет dotnet. Поставьте runtime: apt install aspnetcore-runtime-10.0" >&2
    exit 2
fi

# Юнит и пример конфигурации берём рядом со скриптом, а если их там нет -
# из артефакта публикации (deploy/ уезжает в него при сборке).
ASSET_DIR="$SCRIPT_DIR"
if [ ! -f "$ASSET_DIR/kouki.service" ] && [ -f "$SOURCE/deploy/kouki.service" ]; then
    ASSET_DIR="$SOURCE/deploy"
fi
if [ ! -f "$ASSET_DIR/kouki.service" ] || [ ! -f "$ASSET_DIR/kouki.env.example" ]; then
    echo "Рядом со скриптом нужны kouki.service и kouki.env.example (каталог deploy)" >&2
    exit 2
fi
if [ "$WITH_BOT" -eq 1 ] && { [ ! -f "$ASSET_DIR/kouki-bot.service" ] || [ ! -f "$ASSET_DIR/kouki-bot.env.example" ]; }; then
    echo "Для --with-bot нужны kouki-bot.service и kouki-bot.env.example (каталог deploy)" >&2
    exit 2
fi

echo "== пользователь $USER_NAME"
if ! id "$USER_NAME" >/dev/null 2>&1; then
    useradd --system --create-home --home-dir "/home/$USER_NAME" --shell /usr/sbin/nologin "$USER_NAME"
fi

echo "== каталоги"
install -d -o "$USER_NAME" -g "$USER_NAME" -m 755 "$APP_DIR"
install -d -o "$USER_NAME" -g "$USER_NAME" -m 750 "$DATA_DIR" "$DATA_DIR/db" "$DATA_DIR/audio" "$DATA_DIR/models"
install -d -o root -g "$USER_NAME" -m 750 "$CONF_DIR"

echo "== артефакт: $SOURCE -> $APP_DIR"
if command -v rsync >/dev/null 2>&1; then
    rsync -a --delete --exclude 'appsettings.Development.json' "$SOURCE"/ "$APP_DIR"/
else
    find "$APP_DIR" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
    cp -a "$SOURCE"/. "$APP_DIR"/
fi
chown -R "$USER_NAME:$USER_NAME" "$APP_DIR"

echo "== конфигурация"
ENV_FILE="$CONF_DIR/kouki.env"
if [ ! -f "$ENV_FILE" ]; then
    TOKEN="$(head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n')"
    sed -e "s|^ASPNETCORE_URLS=.*|ASPNETCORE_URLS=$URLS|" \
        -e "s|^Kouki__Token=.*|Kouki__Token=$TOKEN|" \
        "$ASSET_DIR/kouki.env.example" > "$ENV_FILE"
    chown root:"$USER_NAME" "$ENV_FILE"
    chmod 640 "$ENV_FILE"
    echo "   создан $ENV_FILE (токен сгенерирован)"
else
    echo "   $ENV_FILE уже есть — не трогаю"
fi

echo "== юнит systemd"
install -o root -g root -m 644 "$ASSET_DIR/kouki.service" "$SERVICE"
# Адрес из аргумента: меняем в копии в /etc, исходник в git не трогаем.
sed -i "s|^Environment=ASPNETCORE_URLS=.*|Environment=ASPNETCORE_URLS=$URLS|" "$SERVICE"
systemctl daemon-reload
systemctl enable kouki.service >/dev/null
systemctl restart kouki.service

PORT="${URLS##*:}"
echo "== проверка http://127.0.0.1:$PORT/api/health"
OK=0
for _ in $(seq 1 20); do
    if curl -fsS -o /dev/null "http://127.0.0.1:$PORT/api/health" 2>/dev/null; then OK=1; break; fi
    sleep 1
done
if [ "$OK" -ne 1 ]; then
    echo "Сервер не ответил на /api/health. Смотрите: journalctl -u kouki -n 50 --no-pager" >&2
    exit 1
fi

MODEL_FILE="$DATA_DIR/models/asr/sherpa-onnx-whisper-small/small-encoder.int8.onnx"
if [ ! -f "$MODEL_FILE" ]; then
    echo
    echo "ВНИМАНИЕ: модель распознавания не найдена:"
    echo "  $MODEL_FILE"
    echo "/api/voice будет отвечать 503. Скопируйте каталог модели и выдайте права:"
    echo "  rsync -a models/asr/sherpa-onnx-whisper-small/ $DATA_DIR/models/asr/sherpa-onnx-whisper-small/"
    echo "  chown -R $USER_NAME:$USER_NAME $DATA_DIR/models"
fi

TOKEN_HINT="$(grep '^Kouki__Token=' "$ENV_FILE" | cut -d= -f2- || true)"

if [ "$WITH_BOT" -eq 1 ]; then
    echo "== Telegram-бот"
    if [ ! -f "$SOURCE/bot/Kouki.Kagedama.Bot.dll" ]; then
        echo "В публикации нет bot/Kouki.Kagedama.Bot.dll — пересоберите артефакт (tools/publish-linux.ps1 публикует бота сам)" >&2
        exit 2
    fi
    if [ -z "$BOT_TOKEN" ]; then
        echo "Нужен --bot-token <токен от @BotFather>" >&2
        exit 2
    fi

    BOT_ENV="$CONF_DIR/bot.env"
    if [ ! -f "$BOT_ENV" ]; then
        sed -e "s|^KOUKI_BOT_TOKEN=.*|KOUKI_BOT_TOKEN=$BOT_TOKEN|" \
            -e "s|^KOUKI_SERVER_URL=.*|KOUKI_SERVER_URL=http://127.0.0.1:$PORT|" \
            -e "s|^KOUKI_SERVER_TOKEN=.*|KOUKI_SERVER_TOKEN=$TOKEN_HINT|" \
            "$ASSET_DIR/kouki-bot.env.example" > "$BOT_ENV"
        chown root:"$USER_NAME" "$BOT_ENV"
        chmod 640 "$BOT_ENV"
        echo "   создан $BOT_ENV"
    else
        echo "   $BOT_ENV уже есть — не трогаю"
    fi

    install -o root -g root -m 644 "$ASSET_DIR/kouki-bot.service" "$BOT_SERVICE"
    systemctl daemon-reload
    systemctl enable kouki-bot.service >/dev/null
    systemctl restart kouki-bot.service

    sleep 2
    if systemctl is-active --quiet kouki-bot.service; then
        echo "   бот запущен"
    else
        echo "   бот не поднялся — смотрите: journalctl -u kouki-bot -n 50 --no-pager" >&2
    fi
fi

echo
echo "Готово. Токен для станции, бота и дашборда: $TOKEN_HINT"
echo "Проверка: curl -H 'Authorization: Bearer $TOKEN_HINT' http://127.0.0.1:$PORT/api/health"
echo "Логи:     journalctl -u kouki -f"
