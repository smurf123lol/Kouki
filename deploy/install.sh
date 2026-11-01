#!/usr/bin/env bash
# Установка сервера Kouki на Linux (Ubuntu 22.04/24.04, Debian 12).
#
# Что делает:
#   1. создаёт системного пользователя kouki;
#   2. раскладывает артефакт публикации в /opt/kouki/app;
#   3. создаёт /var/lib/kouki/{db,audio,models} и /etc/kouki;
#   4. ставит юнит systemd и (если файла ещё нет) env-файл со случайным токеном;
#   5. запускает сервер и проверяет /api/health;
#   6. ставит и включает ежедневный бэкап — только если есть sqlite3 и rsync;
#      --no-backup выключает таймер, а провал пробного прогона тоже его гасит
#      и завершает установку с кодом 1;
#   7. настраивает git для пользователя kouki и, если дан --git-token/-file/-stdin,
#      кладёт PAT для push;
#   8. по флагам: --smb-user/--smb-password заводят SMB-пользователя, --bot-proxy даёт боту прокси.
#
# Повторный запуск безопасен: артефакт обновляется, env-файлы не перезаписываются.
# Пункты 6–8 добавлены после переезда на NVMe (T87): тогда бэкап после установки
# остался выключенным, SMB-пользователь пропал вместе с passdb.tdb, а бот без
# NO_PROXY ходил к серверу через прокси и получал 503.
#
# Использование:
#   sudo ./deploy/install.sh /path/to/publish
#   sudo ./deploy/install.sh /path/to/publish --urls http://127.0.0.1:5300
#   sudo ./deploy/install.sh /path/to/publish --with-bot --bot-token 123:ABC
#   sudo ./deploy/install.sh /path/to/publish --with-caddy --https-mode internal --site kouki.local
#   sudo ./deploy/install.sh /path/to/publish --with-caddy --https-mode acme \
#        --domain kouki.example.org --acme-email admin@example.org
#   sudo ./deploy/install.sh /path/to/publish --no-backup
#   sudo ./deploy/install.sh /path/to/publish --git-token-file /root/pat   # или --git-token-stdin
#   sudo ./deploy/install.sh /path/to/publish --smb-user smurf123lol --smb-password 'пароль'
#   sudo bash -c './deploy/install.sh /path/to/publish --smb-user smurf123lol --smb-password-stdin < /root/smb.pass'
#   sudo ./deploy/install.sh /path/to/publish --smb-user smurf123lol --smb-password-file /root/smb.pass
#   sudo ./deploy/install.sh /path/to/publish --with-bot --bot-token 123:ABC \
#        --bot-proxy http://127.0.0.1:1080
#
# --smb-user — существующая учётка владельца шары (не придумывайте новую:
# доступ к /srv/files выдан именно ей в smb.conf).
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

# Проверки с побочными эффектами (таймер бэкапа, uid/gid шары) живут отдельно:
# их же вызывают тесты с подменёнными systemctl/getent.
GUARDS="$SCRIPT_DIR/lib/install-guards.sh"
if [ ! -f "$GUARDS" ]; then
    echo "Не найден $GUARDS — установщик неполный (deploy/lib рядом с deploy/install.sh)" >&2
    exit 2
fi
# shellcheck source=deploy/lib/install-guards.sh
. "$GUARDS"

APP_DIR=/opt/kouki/app
DATA_DIR=/var/lib/kouki
CONF_DIR=/etc/kouki
SERVICE=/etc/systemd/system/kouki.service
BOT_SERVICE=/etc/systemd/system/kouki-bot.service
USER_NAME=kouki
URLS="http://0.0.0.0:5300"
WITH_BOT=0
BOT_TOKEN=""

# https-прокси (T55): по умолчанию выключен, режим по умолчанию — локальный CA.
WITH_CADDY=0
HTTPS_MODE=internal
KOUKI_DOMAIN=""
ACME_EMAIL=""
KOUKI_SITE="kouki.local"

# Переездные регрессии (T87): то, что после переустановки молча осталось выключенным.
WITH_BACKUP=1
GIT_TOKEN=""
GIT_TOKEN_GIVEN=0
GIT_TOKEN_FILE=""
GIT_TOKEN_STDIN=0
SMB_USER=""
SMB_PASSWORD=""
SMB_PASSWORD_FILE=""
SMB_PASSWORD_STDIN=0
BOT_PROXY=""

SOURCE="${1:-}"
[ $# -gt 0 ] && shift || true
while [ $# -gt 0 ]; do
    case "$1" in
        --urls) URLS="$2"; shift 2 ;;
        --app-dir) APP_DIR="$2"; shift 2 ;;
        --user) USER_NAME="$2"; shift 2 ;;
        --with-bot) WITH_BOT=1; shift ;;
        --bot-token) BOT_TOKEN="$2"; shift 2 ;;
        --with-caddy) WITH_CADDY=1; shift ;;
        --https-mode) HTTPS_MODE="$2"; shift 2 ;;
        --domain) KOUKI_DOMAIN="$2"; shift 2 ;;
        --acme-email) ACME_EMAIL="$2"; shift 2 ;;
        --site) KOUKI_SITE="$2"; shift 2 ;;
        --no-backup) WITH_BACKUP=0; shift ;;
        --git-token) GIT_TOKEN="$2"; GIT_TOKEN_GIVEN=1; shift 2 ;;
        --git-token-file) GIT_TOKEN_FILE="$2"; shift 2 ;;
        --git-token-stdin) GIT_TOKEN_STDIN=1; shift ;;
        --smb-user) SMB_USER="$2"; shift 2 ;;
        --smb-password) SMB_PASSWORD="$2"; shift 2 ;;
        --smb-password-file) SMB_PASSWORD_FILE="$2"; shift 2 ;;
        --smb-password-stdin) SMB_PASSWORD_STDIN=1; shift ;;
        --bot-proxy) BOT_PROXY="$2"; shift 2 ;;
        *) echo "Неизвестный аргумент: $1" >&2; exit 2 ;;
    esac
done

# Проблемы, из-за которых установка «успешна» лишь на бумаге: нехватка
# инструментов для бэкапа, нерабочий пробный прогон, недоступная шара.
# В конце скрипт выходит с кодом 1, чтобы CI и человек это заметили.
PROBLEMS=0

# Пароль SMB: аргументом он виден в ps и оседает в истории, поэтому кроме него
# есть файл и stdin. Источников должно быть ровно один: иначе непонятно, какой
# пароль применился.
if [ -z "$SMB_USER" ] && { [ -n "$SMB_PASSWORD" ] || [ -n "$SMB_PASSWORD_FILE" ] || [ "$SMB_PASSWORD_STDIN" -eq 1 ]; }; then
    echo "Пароль SMB передан без --smb-user: непонятно, какой учётке его ставить" >&2
    exit 2
fi

if [ -n "$SMB_USER" ]; then
    SMB_SOURCES=0
    [ -n "$SMB_PASSWORD" ] && SMB_SOURCES=$((SMB_SOURCES + 1))
    [ -n "$SMB_PASSWORD_FILE" ] && SMB_SOURCES=$((SMB_SOURCES + 1))
    [ "$SMB_PASSWORD_STDIN" -eq 1 ] && SMB_SOURCES=$((SMB_SOURCES + 1))

    if [ "$SMB_SOURCES" -eq 0 ]; then
        echo "Для --smb-user нужен пароль: --smb-password-stdin (рекомендуется)," >&2
        echo "  --smb-password-file /path/file (600) или --smb-password 'пароль'" >&2
        exit 2
    fi
    if [ "$SMB_SOURCES" -gt 1 ]; then
        echo "Пароль SMB передан дважды: выберите что-то одно из --smb-password," >&2
        echo "  --smb-password-file, --smb-password-stdin" >&2
        exit 2
    fi

    if [ -n "$SMB_PASSWORD_FILE" ]; then
        if [ ! -r "$SMB_PASSWORD_FILE" ]; then
            echo "Не читается файл пароля: $SMB_PASSWORD_FILE" >&2
            exit 2
        fi
        # Первая строка файла; CR (файл с Windows) и BOM (блокнот) срезаем.
        IFS= read -r SMB_PASSWORD < "$SMB_PASSWORD_FILE" || true
        SMB_PASSWORD="${SMB_PASSWORD#$'\xef\xbb\xbf'}"
        SMB_PASSWORD="${SMB_PASSWORD%$'\r'}"
    elif [ "$SMB_PASSWORD_STDIN" -eq 1 ]; then
        # Читаем со stdin установщика: в ps пароля нет, в истории — тоже.
        if ! IFS= read -r SMB_PASSWORD; then
            echo "Не удалось прочитать пароль SMB со stdin" >&2
            exit 2
        fi
        SMB_PASSWORD="${SMB_PASSWORD#$'\xef\xbb\xbf'}"
        SMB_PASSWORD="${SMB_PASSWORD%$'\r'}"
    fi

    if [ -z "$SMB_PASSWORD" ]; then
        echo "Пароль SMB пустой — samba его не примет" >&2
        exit 2
    fi
fi

# PAT: тот же класс секрета, что и пароль SMB, поэтому те же три источника.
GIT_SOURCES=0
[ -n "$GIT_TOKEN" ] && GIT_SOURCES=$((GIT_SOURCES + 1))
[ -n "$GIT_TOKEN_FILE" ] && GIT_SOURCES=$((GIT_SOURCES + 1))
[ "$GIT_TOKEN_STDIN" -eq 1 ] && GIT_SOURCES=$((GIT_SOURCES + 1))

if [ "$GIT_SOURCES" -gt 1 ]; then
    echo "Токен GitHub передан дважды: выберите одно из --git-token," >&2
    echo "  --git-token-file, --git-token-stdin" >&2
    exit 2
fi

if [ -n "$GIT_TOKEN_FILE" ]; then
    if [ ! -r "$GIT_TOKEN_FILE" ]; then
        echo "Не читается файл токена: $GIT_TOKEN_FILE" >&2
        exit 2
    fi
    IFS= read -r GIT_TOKEN < "$GIT_TOKEN_FILE" || true
    GIT_TOKEN="${GIT_TOKEN#$'\xef\xbb\xbf'}"
    GIT_TOKEN="${GIT_TOKEN%$'\r'}"
elif [ "$GIT_TOKEN_STDIN" -eq 1 ]; then
    if ! IFS= read -r GIT_TOKEN; then
        echo "Не удалось прочитать токен GitHub со stdin" >&2
        exit 2
    fi
    GIT_TOKEN="${GIT_TOKEN#$'\xef\xbb\xbf'}"
    GIT_TOKEN="${GIT_TOKEN%$'\r'}"
fi

# Пустой PAT — не «тихо без агента», а ошибка ввода: файл мог быть создан
# пустым, а push молча перестал бы работать. То же и с `--git-token ''`.
if { [ "$GIT_SOURCES" -gt 0 ] || [ "$GIT_TOKEN_GIVEN" -eq 1 ]; } && [ -z "$GIT_TOKEN" ]; then
    echo "Токен GitHub пустой — git-креды не будут записаны" >&2
    exit 2
fi

# --bot-proxy без --with-bot законен, если бот уже установлен: обновление
# артефакта не должно требовать повторять --with-bot только ради прокси.
if [ -n "$BOT_PROXY" ] && [ "$WITH_BOT" -ne 1 ] && [ ! -f "$BOT_SERVICE" ]; then
    echo "--bot-proxy имеет смысл только вместе с --with-bot" >&2
    echo "  (или когда бот уже установлен: $BOT_SERVICE есть)" >&2
    exit 2
fi

if [ "$WITH_CADDY" -eq 1 ]; then
    case "$HTTPS_MODE" in
        internal|acme) ;;
        *) echo "Режим https должен быть internal или acme: --https-mode internal" >&2; exit 2 ;;
    esac

    # С прокси наружу смотрит только Caddy: сам сервер прячем на loopback,
    # иначе его достанут по открытому http в обход сертификата.
    if [ "$URLS" = "http://0.0.0.0:5300" ]; then
        URLS="http://127.0.0.1:5300"
    fi

    if [ "$HTTPS_MODE" = "acme" ] && { [ -z "$KOUKI_DOMAIN" ] || [ -z "$ACME_EMAIL" ]; }; then
        echo "Для режима acme нужны --domain и --acme-email: сертификат выпускается на имя" >&2
        exit 2
    fi

    for asset in Caddyfile.internal Caddyfile.acme caddy.env.example; do
        if [ ! -f "$SCRIPT_DIR/$asset" ]; then
            echo "Для --with-caddy нужен $asset (он лежит в deploy/)" >&2
            exit 2
        fi
    done
fi

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
# Каталоги других сервисов живут внутри $APP_DIR, но публикуются отдельно:
# `bot/` — kouki-bot.service, `mcp/` — MCP-сервер. Без исключений `--delete`
# стирал их при каждом обновлении сервера, и бот молча переставал работать
# (systemd падал на «Changing to the requested working directory failed»).
if command -v rsync >/dev/null 2>&1; then
    rsync -a --delete --exclude 'appsettings.Development.json' --exclude 'bot/' --exclude 'mcp/' "$SOURCE"/ "$APP_DIR"/
else
    find "$APP_DIR" -mindepth 1 -maxdepth 1 ! -name bot ! -name mcp -exec rm -rf {} +
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

if [ "$WITH_CADDY" -eq 1 ]; then
    echo "== https: Caddy ($HTTPS_MODE)"
    if ! command -v caddy >/dev/null 2>&1; then
        echo "   ставлю Caddy из официального репозитория"
        apt-get update
        apt-get install -y debian-keyring debian-archive-keyring apt-transport-https curl gnupg
        curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' \
            | gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
        curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' \
            > /etc/apt/sources.list.d/caddy-stable.list
        apt-get update
        apt-get install -y caddy
    fi

    install -d -m 755 /etc/caddy
    install -d -m 755 /var/log/caddy
    chown caddy:caddy /var/log/caddy 2>/dev/null || true

    case "$HTTPS_MODE" in
        internal) CADDYFILE_SRC="$SCRIPT_DIR/Caddyfile.internal" ;;
        acme)     CADDYFILE_SRC="$SCRIPT_DIR/Caddyfile.acme" ;;
    esac

    # Подстановка значений без sed: в Caddyfile есть {$...}, и экранировать это
    # в регулярке проще не пытаться.
    CADDY_CONF="$(cat "$CADDYFILE_SRC")"
    CADDY_CONF="${CADDY_CONF//\{\$KOUKI_SITE:kouki.local\}/$KOUKI_SITE}"
    CADDY_CONF="${CADDY_CONF//\{\$KOUKI_SITE\}/$KOUKI_SITE}"
    CADDY_CONF="${CADDY_CONF//\{\$KOUKI_DOMAIN\}/$KOUKI_DOMAIN}"
    CADDY_CONF="${CADDY_CONF//\{\$KOUKI_ACME_EMAIL\}/$ACME_EMAIL}"
    CADDY_CONF="${CADDY_CONF//\{\$KOUKI_UPSTREAM:127.0.0.1:5300\}/127.0.0.1:$PORT}"
    printf '%s\n' "$CADDY_CONF" > /etc/caddy/Caddyfile

    # Конфиг проверяется до запуска: иначе Caddy молча не поднимется, а сервер
    # останется доступен только на loopback — то есть нигде.
    caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
    systemctl enable --now caddy >/dev/null
    systemctl reload caddy 2>/dev/null || systemctl restart caddy

    echo "   https включён: режим $HTTPS_MODE, апстрим 127.0.0.1:$PORT"
    if [ "$HTTPS_MODE" = "internal" ]; then
        echo "   адрес: https://$KOUKI_SITE (это имя должно резолвиться на сервер: DNS роутера или hosts)"
        echo "   корневой сертификат: /var/lib/caddy/.local/share/caddy/pki/authorities/local/root.crt"
        echo "   поставить на клиентах: sudo caddy trust  (или скопировать файл выше и установить вручную)"
    else
        echo "   адрес: https://$KOUKI_DOMAIN"
    fi
    echo "   открыть порты: ufw allow 80,443/tcp"
fi

MODEL_FILE="$DATA_DIR/models/asr/sherpa-onnx-whisper-small/small-encoder.int8.onnx"
if [ ! -f "$MODEL_FILE" ]; then
    echo
    echo "ВНИМАНИЕ: модель распознавания не найдена:"
    echo "  $MODEL_FILE"
    echo "Сервер поднимется и без неё (health 200), но /api/voice будет отвечать 503."
    echo "Скопируйте каталог модели и выдайте права:"
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
        echo "   ОШИБКА: бот не поднялся — journalctl -u kouki-bot -n 50 --no-pager" >&2
        PROBLEMS=1
    fi
fi

# --- T87: то, что после переезда на NVMe осталось незакрытым ------------------
# Каждый пункт идемпотентен: если ресурса нет, установка не падает, а говорит,
# чего не хватает.

if [ "$WITH_BACKUP" -eq 1 ]; then
    echo "== ежедневный бэкап"
    BACKUP_READY=1
    for asset in kouki-backup.sh kouki-backup.service kouki-backup.timer backup.env.example; do
        if [ ! -f "$ASSET_DIR/$asset" ]; then
            echo "ВНИМАНИЕ: в deploy/ нет $asset — бэкап не настроен" >&2
            BACKUP_READY=0
            break
        fi
    done

    if [ "$BACKUP_READY" -eq 1 ]; then
        BACKUP_ENV="$CONF_DIR/backup.env"
        if [ ! -f "$BACKUP_ENV" ]; then
            install -o root -g "$USER_NAME" -m 640 "$ASSET_DIR/backup.env.example" "$BACKUP_ENV"
            echo "   создан $BACKUP_ENV"
        else
            echo "   $BACKUP_ENV уже есть — не трогаю"
        fi

        # Юнит пишет снимки от kouki, но каталог общий с админом: группа kouki
        # с setgid — так каталог остаётся доступным и человеку, и сервису.
        install -d -o root -g "$USER_NAME" -m 2775 /var/backups/kouki

        # Юнит запускает скрипт из артефакта; если публикация была без deploy/,
        # подкладываем копию, иначе таймер упадёт «No such file».
        install -d -o root -g root -m 755 "$APP_DIR/deploy"
        install -o root -g root -m 755 "$ASSET_DIR/kouki-backup.sh" "$APP_DIR/deploy/kouki-backup.sh"

        install -o root -g root -m 644 "$ASSET_DIR/kouki-backup.service" \
            /etc/systemd/system/kouki-backup.service
        install -o root -g root -m 644 "$ASSET_DIR/kouki-backup.timer" \
            /etc/systemd/system/kouki-backup.timer
        systemctl daemon-reload

        # Без sqlite3/rsync скрипт выходит с кодом 2, и «таймер включён» было бы
        # обещанием, которое сломается в 03:30. Решает apply_backup_timer:
        # не хватает инструментов — таймер не включается, а уже включённый
        # (от прошлой установки) гасится.
        BACKUP_MISSING="$(backup_missing_tools)"
        if [ -n "$BACKUP_MISSING" ]; then
            echo "ВНИМАНИЕ: нет $BACKUP_MISSING — бэкап не заработает:" \
                 "apt install $BACKUP_MISSING" >&2
        fi
        if ! apply_backup_timer "$BACKUP_MISSING"; then
            PROBLEMS=1
        fi
    else
        # Ассетов нет — таймер прошлой установки всё равно может быть включён
        # и молча падать в 03:30.
        if ! stop_backup_timer; then
            echo "   и таймер прошлой установки выключить не удалось" >&2
        fi
        PROBLEMS=1
    fi
else
    # --no-backup должен и выключать уже включённый таймер, иначе админ,
    # прочитав README, оставит копии включёнными.
    # Путь юнита не проверяем: он бывает и в /lib, и симлинком — спрашиваем systemd.
    echo "== ежедневный бэкап: выключаю (--no-backup)"
    if stop_backup_timer; then
        echo "   снимки в /var/backups/kouki остались"
    else
        PROBLEMS=1
    fi
fi

if command -v git >/dev/null 2>&1 && id -u "$USER_NAME" >/dev/null 2>&1; then
    echo "== git для агента ($USER_NAME)"
    sudo -u "$USER_NAME" git config --global user.name "Kouki Agent" 2>/dev/null || true
    sudo -u "$USER_NAME" git config --global user.email "agent@kouki.local" 2>/dev/null || true

    # Репозитории могли создать от другого владельца — git откажется с ними
    # работать; добавляем точечно, а не «*». Повторный запуск не должен копить
    # дубликаты: чистим и уже накопленные (иначе список растёт с каждой установкой).
    for dir in "$APP_DIR" /srv/kouki-workspace; do
        [ -d "$dir" ] || continue
        for path in "$dir" "$dir"/*/.git "$dir"/.git; do
            case "$path" in
                */.git) path="${path%/.git}" ;;
            esac
            [ -d "$path" ] || continue
            sudo -u "$USER_NAME" git config --global --unset-all safe.directory "$path" 2>/dev/null || true
            sudo -u "$USER_NAME" git config --global --add safe.directory "$path" 2>/dev/null || true
        done
    done

    if [ -n "$GIT_TOKEN" ]; then
        # PAT даёт агенту право пушить; файл читает только kouki.
        creds_home="$(getent passwd "$USER_NAME" | cut -d: -f6)"
        if [ -n "$creds_home" ]; then
            # Права уже существующего домашнего каталога не переписываем: админ
            # мог настроить их сам (0750, ACL и т.п.).
            if [ ! -d "$creds_home" ]; then
                install -d -o "$USER_NAME" -g "$USER_NAME" -m 700 "$creds_home"
            fi
            creds="$creds_home/.git-credentials"
            # Креды других хостов не теряем: заменяем только строку github.com.
            saved_umask="$(umask)"
            umask 077
            tmp="$creds.tmp"
            if [ -f "$creds" ]; then
                grep -v 'github\.com' "$creds" > "$tmp" 2>/dev/null || : > "$tmp"
            else
                : > "$tmp"
            fi
            printf 'https://x-access-token:%s@github.com\n' "$GIT_TOKEN" >> "$tmp"
            mv "$tmp" "$creds"
            chown "$USER_NAME:$USER_NAME" "$creds"
            chmod 600 "$creds"
            umask "$saved_umask"
            # Помощник только для github.com: иначе git отдаёт этот же файл
            # любому хосту, к которому обратится агент.
            sudo -u "$USER_NAME" git config --global credential.https://github.com.helper store \
                2>/dev/null || true
            echo "   PAT записан в $creds (600) — агент сможет пушить"
        fi
    fi
elif ! command -v git >/dev/null 2>&1; then
    echo "== git: не установлен — настройки агента пропущены (apt install git)" >&2
    if [ -n "$GIT_TOKEN" ]; then
        echo "   PAT не записан: агент не сможет пушить" >&2
        PROBLEMS=1
    fi
else
    echo "== git: пользователь $USER_NAME ещё не создан — настройки агента пропущены" >&2
    if [ -n "$GIT_TOKEN" ]; then
        echo "   PAT не записан: агент не сможет пушить" >&2
        PROBLEMS=1
    fi
fi

if [ -n "$SMB_USER" ]; then
    echo "== SMB-пользователь $SMB_USER"
    if ! command -v smbpasswd >/dev/null 2>&1; then
        echo "ОШИБКА: samba не установлена — SMB-пользователь не создан (apt install samba)" >&2
        PROBLEMS=1
    else
        # Шара на отдельном диске переживает переустановку системы вместе с
        # числовым владельцем. Новый useradd дал бы другой uid/gid, kouki попал
        # бы в «правильную» по имени, но чужую по номеру группу — и запись в
        # /srv/files осталась бы запрещённой. Разбор делает resolve_share_ids;
        # читаем поля через read, а не eval: имя группы приходит из /etc/group
        # (LDAP, ручная правка) и не должно исполняться как shell-код.
        share_ids="$(resolve_share_ids /srv/files)"
        # Разбор — в guards: `read` с IFS=: съедает ведущие разделители, поэтому
        # пустые поля кодируются «-» и разбираются только через read_share_ids.
        skip_smb=0
        if ! read_share_ids "$share_ids"; then
            echo "ОШИБКА: не удалось разобрать сведения о /srv/files — SMB не настроен" >&2
            PROBLEMS=1
            skip_smb=1
        fi
        share_uid="$SHARE_UID"
        share_gid="$SHARE_GID"
        share_group="$SHARE_GROUP"
        share_create_group="${SHARE_CREATE_GROUP:-0}"
        SMB_GROUP="$SMB_USER"
        case "$share_create_group" in
            2)
                # Каталог на root-группе: добавлять kouki в root нельзя.
                echo "ОШИБКА: /srv/files принадлежит root-группе — исправьте владельца:" \
                     "sudo chown -R $SMB_USER:$SMB_USER /srv/files" >&2
                PROBLEMS=1
                skip_smb=1
                ;;
            3)
                echo "ОШИБКА: /srv/files принадлежит системной группе ${share_group:-gid $share_gid} —" \
                     "членство в ней даёт лишние права, шару надо перевесить:" \
                     "sudo chown -R $SMB_USER:$SMB_USER /srv/files" >&2
                PROBLEMS=1
                skip_smb=1
                ;;
        esac

        if [ "$skip_smb" -eq 1 ]; then
            echo "   SMB-пользователь и пароль не настроены: сначала исправьте владельца шары" >&2
        fi

        if [ "$skip_smb" -eq 0 ] && [ -n "$share_gid" ]; then
            if [ "$share_create_group" -eq 1 ]; then
                # Группы с таким GID нет: создаём её с нужным номером, иначе
                # useradd --gid 1000 упадёт «group '1000' does not exist».
                groupadd --gid "$share_gid" "$SMB_USER" 2>/dev/null || true
                actual_group="$(getent group "$share_gid" | cut -d: -f1 || true)"
                if [ -n "$actual_group" ]; then
                    SMB_GROUP="$actual_group"
                    echo "   группа $SMB_GROUP получила gid $share_gid владельца шары"
                else
                    echo "ОШИБКА: не удалось создать группу с gid $share_gid" >&2
                    PROBLEMS=1
                    skip_smb=1
                fi
            else
                SMB_GROUP="$share_group"
            fi
        fi

        if [ "$skip_smb" -eq 0 ]; then
            if ! id -u "$SMB_USER" >/dev/null 2>&1; then
                eff_uid="$share_uid"
                eff_gid="$share_gid"
                if [ -n "$share_uid" ] && getent passwd "$share_uid" >/dev/null 2>&1; then
                    # UID занят другим пользователем: создавать с ним нельзя,
                    # иначе меняется владелец чужих файлов. Группу шары всё равно
                    # передаём — иначе SMB-пользователь не запишет файлы.
                    echo "ВНИМАНИЕ: uid $share_uid уже занят ($(getent passwd "$share_uid" | cut -d: -f1))," \
                         "создаю $SMB_USER с другим uid — владельцем шары останется прежний" >&2
                    eff_uid=""
                    PROBLEMS=1
                fi
                # uid/gid передаются только когда известны: при пустых значениях
                # `useradd --gid ""` падает «group '' does not exist».
                if create_share_user "$SMB_USER" "$eff_uid" "$eff_gid"; then
                    echo "   создан $SMB_USER (uid ${eff_uid:-авто}, gid ${eff_gid:-по умолчанию})"
                else
                    echo "ОШИБКА: не удалось создать $SMB_USER" >&2
                    PROBLEMS=1
                fi
            fi
            # Пользователь шары должен быть в её группе, даже если существовал
            # раньше и его primary group другая.
            if [ -n "$SMB_GROUP" ] && id -u "$SMB_USER" >/dev/null 2>&1; then
                if ! id -Gn "$SMB_USER" 2>/dev/null | tr ' ' '\n' | grep -qxF "$SMB_GROUP"; then
                    if usermod -aG "$SMB_GROUP" "$SMB_USER" 2>/dev/null; then
                        echo "   $SMB_USER добавлен в группу шары $SMB_GROUP"
                    else
                        echo "ОШИБКА: не удалось добавить $SMB_USER в группу $SMB_GROUP" >&2
                        PROBLEMS=1
                    fi
                fi
            fi

            if printf '%s\n%s\n' "$SMB_PASSWORD" "$SMB_PASSWORD" | smbpasswd -s -a "$SMB_USER" >/dev/null; then
                if ! smbpasswd -e "$SMB_USER" >/dev/null 2>&1; then
                    echo "ВНИМАНИЕ: пароль SMB задан, но учётка не включена (smbpasswd -e)" >&2
                    PROBLEMS=1
                fi
                # Агенту нужна запись в шару: он kouki, владелец шары — SMB_USER
                # (или группа, которой принадлежит каталог).
                if usermod -aG "$SMB_GROUP" "$USER_NAME" 2>/dev/null; then
                    echo "   пароль SMB задан, $USER_NAME добавлен в группу $SMB_GROUP"
                else
                    echo "ВНИМАНИЕ: пароль SMB задан, но добавить $USER_NAME в группу $SMB_GROUP не вышло" >&2
                    PROBLEMS=1
                fi
                # Группа применится только в новой сессии, поэтому проверяем запись
                # от имени агента через sg — иначе «успех» при запрете записи.
                if [ -d /srv/files ]; then
                    if command -v sg >/dev/null 2>&1 && command -v sudo >/dev/null 2>&1; then
                        if ! sg "$SMB_GROUP" -c "sudo -u $USER_NAME test -w /srv/files" 2>/dev/null; then
                            echo "ВНИМАНИЕ: $USER_NAME не может писать в /srv/files — проверьте владельца каталога" >&2
                            PROBLEMS=1
                        fi
                    else
                        echo "ОШИБКА: sg/sudo не найдены — запись в /srv/files не проверена" >&2
                        PROBLEMS=1
                    fi
                fi
                if systemctl restart smbd 2>/dev/null; then
                    echo "   samba перезапущена"
                else
                    echo "ОШИБКА: smbd не перезапустился — systemctl status smbd" >&2
                    PROBLEMS=1
                fi
            else
                echo "ВНИМАНИЕ: не удалось задать пароль SMB для $SMB_USER" >&2
                PROBLEMS=1
            fi
        fi
    fi
fi

if [ -n "$BOT_PROXY" ]; then
    echo "== прокси для бота: $BOT_PROXY"
    PROXY_DIR="/etc/systemd/system/kouki-bot.service.d"
    install -d -o root -g root -m 755 "$PROXY_DIR"
    # В юните значение живёт в кавычках: обратный слеш и кавычку экранируем,
    # а «%» systemd считает спецификатором и требует «%%».
    proxy_unit="$(printf '%s' "$BOT_PROXY" | sed 's/\\/\\\\/g; s/"/\\"/g; s/%/%%/g')"
    # NO_PROXY обязателен: без него бот гонит через прокси свои же вызовы к
    # серверу (127.0.0.1:5300) и получает 503 «сервер недоступен».
    no_proxy_unit="127.0.0.1,localhost,::1,.local,192.168.0.0/16,10.0.0.0/8"
    # В прокси могут быть логин и пароль: создаём файл сразу с 600, чтобы не
    # было окна, когда его читает любой локальный пользователь.
    (
        umask 077
        {
            echo "[Service]"
            echo "Environment=\"HTTP_PROXY=$proxy_unit\""
            echo "Environment=\"HTTPS_PROXY=$proxy_unit\""
            echo "Environment=\"ALL_PROXY=$proxy_unit\""
            echo "Environment=\"NO_PROXY=$no_proxy_unit\""
            echo "Environment=\"no_proxy=$no_proxy_unit\""
        } > "$PROXY_DIR/proxy.conf"
    )
    chmod 600 "$PROXY_DIR/proxy.conf"
    systemctl daemon-reload
    if systemctl is-enabled --quiet kouki-bot.service 2>/dev/null; then
        if systemctl restart kouki-bot.service; then
            echo "   бот перезапущен с прокси"
        else
            echo "ВНИМАНИЕ: бот не перезапустился — journalctl -u kouki-bot -n 30 --no-pager" >&2
            PROBLEMS=1
        fi
    else
        echo "   файл записан; бот подхватит прокси при запуске"
    fi
else
    # Прокси убрали — drop-in не должен остаться от прошлой установки, иначе
    # бот продолжит ходить через давно закрытый прокси.
    PROXY_DIR="/etc/systemd/system/kouki-bot.service.d"
    if [ -f "$PROXY_DIR/proxy.conf" ]; then
        echo "== прокси для бота: убираю (--bot-proxy не задан)"
        rm -f "$PROXY_DIR/proxy.conf"
        rmdir "$PROXY_DIR" 2>/dev/null || true
        systemctl daemon-reload
        if systemctl is-enabled --quiet kouki-bot.service 2>/dev/null; then
            if systemctl restart kouki-bot.service; then
                echo "   бот перезапущен без прокси"
            else
                echo "ВНИМАНИЕ: бот не перезапустился — journalctl -u kouki-bot -n 30 --no-pager" >&2
                PROBLEMS=1
            fi
        fi
    fi
fi

# Установка, в которой что-то заведомо не работает, не должна выглядеть успешной.
if [ "$PROBLEMS" -ne 0 ]; then
    echo
    echo "ВНИМАНИЕ: установка завершилась с замечаниями (см. сообщения выше)." >&2
    echo "Сервер при этом поднят: curl -H 'Authorization: Bearer $TOKEN_HINT'" \
         "http://127.0.0.1:$PORT/api/health" >&2
    exit 1
fi

echo
echo "Готово. Токен для станции, бота и дашборда: $TOKEN_HINT"
echo "Проверка: curl -H 'Authorization: Bearer $TOKEN_HINT' http://127.0.0.1:$PORT/api/health"
echo "Логи:     journalctl -u kouki -f"
