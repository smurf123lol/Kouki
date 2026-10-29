# Развёртывание сервера Kouki на Linux

Здесь лежит то, что раньше жило в заметке: юнит systemd, пример конфигурации и
установщик. После установки сервер работает как служба — стартует с машиной,
перезапускается после падения, слушает `http://0.0.0.0:5300` (или адрес из
`--urls`).

```
deploy/
  README.md          # этот файл
  install.sh         # установка и обновление (идемпотентно)
  kouki.service      # юнит systemd
  kouki.env.example  # конфигурация: токен, пути, язык
```

## Что нужно

- Ubuntu 22.04/24.04/26.04 LTS или Debian 12 (на 26.04 шаги те же; пакета
  `aspnetcore-runtime-10.0` для свежего релиза может ещё не быть — тогда
  публикуйте self-contained, см. ниже);
- .NET 10 runtime (`aspnetcore-runtime-10.0`) — `dotnet --version` должен отвечать;
- `curl`, `rsync` (желательно), `sqlite3` (для бэкапов);
- артефакт публикации: на Windows `Kouki\tools\publish-linux.ps1`, на Linux
  `dotnet publish Kagedama.api/Kagedama.api.csproj -c Release -r linux-x64 --self-contained false -p:KoukiEnableSherpa=true -o publish/linux-x64`.

Публикация по умолчанию — framework-dependent: сам .NET на сервере ставится
отдельно, зато артефакт 33 МБ. Ключ `-SelfContained` (или
`--self-contained true`) кладёт рантайм внутрь: 330 МБ, но серверу не нужен
`dotnet` в системе и нет зависимости от наличия пакета под конкретный
дистрибутив.

## Установка

```bash
# 1. артефакт на машине (deploy/ едет внутри него — второй архив не нужен)
scp -r publish/linux-x64 kouki@server:/tmp/kouki-publish

# 2. установка (создаст пользователя, каталоги, юнит, токен)
sudo bash /tmp/kouki-publish/deploy/install.sh /tmp/kouki-publish

# 3. модель распознавания (иначе /api/voice отвечает 503)
sudo rsync -a models/asr/sherpa-onnx-whisper-small/ /var/lib/kouki/models/asr/sherpa-onnx-whisper-small/
sudo chown -R kouki:kouki /var/lib/kouki/models

# 4. проверка
sudo systemctl status kouki --no-pager
curl -H "Authorization: Bearer $(sudo grep '^Kouki__Token=' /etc/kouki/kouki.env | cut -d= -f2-)" \
     http://127.0.0.1:5300/api/health

# 5. станция: тот же токен и адрес сервера в firmware/sdkconfig.secret
#    CONFIG_KOUKI_SERVER_URL="http://<ip-сервера>:5300"
#    CONFIG_KOUKI_SERVER_TOKEN="<тот же токен>"
```

Установщик идемпотентен: повторный запуск обновляет артефакт и перезапускает
службу, но **не перезаписывает** `/etc/kouki/kouki.env` (там токен и пути).

## Что где лежит

| Путь | Что |
| --- | --- |
| `/opt/kouki/app` | артефакт публикации (перезаписывается при обновлении; каталоги `bot/` и `mcp/` не трогаются — они публикуются отдельно) |
| `/etc/kouki/kouki.env` | конфигурация, права `640 root:kouki` |
| `/var/lib/kouki/db/kouki.sqlite` | база (WAL рядом) |
| `/var/lib/kouki/audio` | записи, старше `AudioRetentionDays` удаляются |
| `/var/lib/kouki/models/asr/...` | модель распознавания |

Юнит задаёт `Kouki__RequireAbsolutePaths=true`: относительный путь в env-файле
остановит старт с сообщением, какая именно настройка виновата (типичная ошибка
переноса — «сервер запустился, а база пустая»).

## Telegram-бот (необязательно)

Бот — отдельный процесс в том же артефакте, в подкаталоге `bot/` (у него свой
`appsettings.json`). Ставится флагом:

```bash
sudo ./deploy/install.sh /tmp/kouki-publish --with-bot --bot-token 123456:ABC-DEF
```

Появляется `/etc/kouki/bot.env` (640, `root:kouki`) и юнит `kouki-bot.service`,
который стартует после `kouki.service`. Токен бота спрашивается у `@BotFather`;
токен сервера подставляется из `/etc/kouki/kouki.env`, адрес — `http://127.0.0.1:<порт>`.

Если бот уже был установлен, env не перезаписывается (токен не потеряется при
обновлении артефакта): меняйте `/etc/kouki/bot.env` руками и делайте
`systemctl restart kouki-bot`.

Проверка: `journalctl -u kouki-bot -n 30 --no-pager` — при старте бот пишет
`Бот Kouki запущен`; на 409 (вебхук у другого процесса) он спрашивает
`getWebhookInfo` и выходит с кодом 2, а не крутит цикл.

## MCP-сервер (для агента на любой машине)

`Kagedama.mcp` — не служба, а stdio-процесс: его запускает клиент (плагин
`dsh-kouki` в DeepSeek Harness, `claude mcp add`, любой MCP-клиент). В артефакте
он лежит в подкаталоге `mcp/`, и на сервере ему нужен только доступ к базе:

```bash
dotnet /opt/kouki/app/mcp/Kagedama.mcp.dll --database=/var/lib/kouki/db/kouki.sqlite
```

Удобнее — через окружение, чтобы плагин не знал пути:

```bash
Kouki__DatabasePath=/var/lib/kouki/db/kouki.sqlite dotnet /opt/kouki/app/mcp/Kagedama.mcp.dll
```

База общая с сервером (WAL разрешает писать обоим), но `busy_timeout` у MCP —
5 секунд: при долгой транзакции приёма инструмент подождёт, а не упадёт.
Если MCP живёт на другой машине, базу ему не отдают — вместо этого агент ходит
через HTTP (`/api/intake`, `/api/tasks`).

## https: Caddy (необязательно)

По умолчанию сервер слушает `http://0.0.0.0:5300` — этого достаточно в закрытой
сети, но токен и аудио идут открытым текстом. `--with-caddy` ставит Caddy,
прячет сервер на loopback и отдаёт наружу только https.

```bash
# режим по умолчанию — локальный CA, домен не нужен
sudo bash app/deploy/install.sh app --with-caddy --site kouki.local

# публичный сертификат (нужен домен, смотрящий на сервер)
sudo bash app/deploy/install.sh app --with-caddy --https-mode acme \
    --domain kouki.example.org --acme-email admin@example.org
```

Режим задаётся `KOUKI_HTTPS_MODE` (`internal` по умолчанию, `acme` — публичный
сертификат); остальные значения установщик отвергает. Конфиги лежат рядом:
`Caddyfile.internal` (`tls internal`), `Caddyfile.acme` (`{$KOUKI_DOMAIN}`,
`{$KOUKI_ACME_EMAIL}`), значения — `caddy.env.example`.

**Почему внутренний CA по умолчанию:** интернета на стенде может не быть, а
`tls internal` работает сразу. Цена — предупреждение браузера, пока корневой
сертификат не установлен на клиенте:

```bash
# путь печатает установщик; на клиенте — либо так,
sudo caddy trust        # на самом сервере: кладёт корень в системное хранилище
# либо вручную: скопировать root.crt
sudo install -m 644 root.crt /usr/local/share/ca-certificates/kouki-local-ca.crt
sudo update-ca-certificates
```

Windows: `certutil -addstore -f Root root.crt`; Android/iOS — «установить
сертификат» из файла. После установки корня `https://kouki.local` открывается
без предупреждений, и дашборд работает как обычно.

**Станция пока остаётся на http внутри LAN.** Это осознанно: у ESP32-S3 в
прошивке есть только публичные корневые сертификаты, поэтому https для неё
потребует либо публичного домена, либо отдельного сертификата в образе — это
отдельная задача, а не забывчивость. Внутри домашней сети http на 5300 с
токеном остаётся допустимым.

Проверка после установки:

```bash
curl -fsS https://kouki.local/api/health
systemctl status caddy --no-pager
journalctl -u caddy -n 30 --no-pager
```

## Резервные копии

Бэкап — отдельный юнит с таймером, ставится руками (это решение администратора,
а не следствие установки сервера):

```bash
sudo install -o root -g kouki -m 640 deploy/backup.env.example /etc/kouki/backup.env
sudo install -o root -g root -m 644 deploy/kouki-backup.service /etc/systemd/system/
sudo install -o root -g root -m 644 deploy/kouki-backup.timer   /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now kouki-backup.timer
systemctl list-timers kouki-backup.timer          # когда следующий запуск
sudo systemctl start kouki-backup.service         # прогнать сейчас
journalctl -u kouki-backup -n 30 --no-pager
```

Что делает `kouki-backup.sh`: снимок базы через `sqlite3 .backup` (не `cp` — база в
WAL, копия живого файла получается «наполовину»), проверка `PRAGMA integrity_check`
и отказ сохранять битый снимок, `rsync` аудио без `--delete` (пропавший том с аудио
не должен затирать бэкап), чистка старше `KOUKI_BACKUP_RETENTION_DAYS` (по умолчанию
14 дней). Два запуска одновременно не идут — `flock`.

Снимки лежат в `KOUKI_BACKUP_DIR` (`/var/backups/kouki`): `db/kouki-YYYY-MM-DD.sqlite`
и `audio/`. Держите каталог **не на том же диске**, что `/var/lib/kouki`, иначе один
сбой диска уносит и данные, и копии.

### Восстановление

```bash
sudo systemctl stop kouki kouki-bot
sudo install -o kouki -g kouki -m 640 \
    /var/backups/kouki/db/kouki-2026-10-25.sqlite /var/lib/kouki/db/kouki.sqlite
sudo rm -f /var/lib/kouki/db/kouki.sqlite-wal /var/lib/kouki/db/kouki.sqlite-shm
sudo systemctl start kouki kouki-bot
curl -fsS http://127.0.0.1:5300/api/health
```

Проверять восстановление стоит раз в квартал — на копии рядом, чтобы не трогать
живую базу: бэкап, который ни разу не разворачивали, — это не бэкап, а надежда.

## Обновление

```bash
sudo bash /tmp/kouki-deploy/deploy/install.sh /tmp/kouki-publish
```

Перезапуск произойдёт сам. Откат — положить предыдущий артефакт и повторить.

## Логи и диагностика

```bash
journalctl -u kouki -f                 # живой лог
journalctl -u kouki -n 100 --no-pager  # последние 100 строк
systemctl status kouki --no-pager
ss -ltnp | grep 5300                   # слушает ли порт
```

Частые причины «не отвечает»:

| Симптом | Причина |
| --- | --- |
| `Путь 'X' должен быть абсолютным` | в env-файле относительный путь |
| `token-not-configured` в ответе | пустой `Kouki__Token`, а API закрыт по умолчанию |
| `/api/voice` → 503 | нет модели или сервер собран без `-p:KoukiEnableSherpa=true` |
| `Kouki: база данных …` не совпадает | смотрите строку `Путь … относительный` в логе |

## Чего здесь сознательно нет

- **https/прокси.** Пока сеть локальная, станция ходит по `http` + bearer-токен.
  Когда понадобится — Caddy перед сервером и корневой сертификат в прошивке
  станции (отдельная задача).
- **Бэкапы.** `sqlite3 .backup` + rsync аудио — задача T50, скрипт появится рядом.
- **Секреты в git.** Токен живёт только в `/etc/kouki/kouki.env`; в репозитории
  лежит `CHANGE_ME`.
### Обновление сервера без потери бота

Порядок, который не оставляет бота лежать:

```sh
sudo systemctl stop kouki          # PartOf погасит и kouki-bot
sudo rsync -a --delete --exclude 'bot/' --exclude 'mcp/' <артефакт>/ /opt/kouki/app/
sudo chown -R kouki:kouki /opt/kouki/app
sudo systemctl start kouki
sudo systemctl start kouki-bot     # PartOf не поднимает бота обратно
```

`--exclude 'bot/' --exclude 'mcp/'` обязательны: внутри `/opt/kouki/app` живут
каталоги других сервисов, и `--delete` без исключений удалял бота (systemd падал
на `Changing to the requested working directory failed`, бот молча не отвечал).
`deploy/install.sh` делает это сам: он кладёт артефакт с исключениями и
перезапускает бота после сервера.
