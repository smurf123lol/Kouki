# Развёртывание сервера Kouki на Linux

Здесь лежит то, что раньше жило в заметке: юнит systemd, пример конфигурации и
установщик. После установки сервер работает как служба — стартует с машиной,
перезапускается после падения, слушает `http://0.0.0.0:5300` (или адрес из
`--urls`).

```
deploy/
  README.md          # этот файл
  install.sh         # установка и обновление (идемпотентно)
  lib/
    install-guards.sh  # решения «включать ли бэкап», «какие uid/gid у шары»
  kouki.service      # юнит systemd
  kouki.env.example  # конфигурация: токен, пути, язык
```

## Что нужно

- Ubuntu 22.04/24.04/26.04 LTS или Debian 12 (на 26.04 шаги те же; пакета
  `aspnetcore-runtime-10.0` для свежего релиза может ещё не быть — тогда
  публикуйте self-contained, см. ниже);
- .NET 10 runtime (`aspnetcore-runtime-10.0`) — `dotnet --version` должен отвечать;
- `curl`, `rsync` и `sqlite3` — без `rsync`/`sqlite3` установщик **не включит**
  бэкап и завершится с кодом 1 (снимки делаются `sqlite3 .backup` + `rsync`);
- артефакт публикации: на Windows `Kouki\tools\publish-linux.ps1`, на Linux
  `tools/publish-linux.sh` (он собирает API, `bot/` и `mcp/` и проверяет нативные `.so`).

На Linux-сервере скрипт по умолчанию собирает **self-contained**: в проде на
Ubuntu 26.04 пакета `aspnetcore-runtime-10.0` может не быть, поэтому рантайм
лежит рядом с приложением (330 МБ). `tools/publish-linux.sh --framework-dependent`
делает артефакт на 33 МБ — тогда `dotnet` ставится в систему отдельно, но
`deploy/deploy-code.sh` такой артефакт поверх self-contained прода отклонит
(форма деплоя должна совпадать). На Windows у `publish-linux.ps1` то же
переключение — ключ `-SelfContained`.

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
| `/opt/kouki/app` | артефакт публикации API (перезаписывается при обновлении; каталог `bot/` не трогается — обновляется отдельным rsync) |
| `/opt/kouki/mcp` | MCP-сервер (`Kagedama.mcp`), отдельный каталог: `install.sh` его не копирует |
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

### Прокси для бота

Если Telegram с сервера доступен только через прокси, боту его надо передать
вместе с исключениями: без `NO_PROXY` он гонит через прокси и собственные
запросы к `127.0.0.1:5300`, получает 503 и пишет «сервер недоступен».

```bash
sudo ./deploy/install.sh /tmp/kouki-publish --with-bot --bot-token 123:ABC \
     --bot-proxy http://127.0.0.1:1080
```

Установщик кладёт drop-in `/etc/systemd/system/kouki-bot.service.d/proxy.conf`
с `HTTP_PROXY`/`HTTPS_PROXY`/`ALL_PROXY` и `NO_PROXY=127.0.0.1,localhost,::1,.local,192.168.0.0/16,10.0.0.0/8`
(файл `600`: в прокси могут быть логин и пароль).

## Пользователь агента, PAT и SMB

Агенту на сервере нужны git-настройки (иначе он не коммитит), иногда PAT для
push и доступ на запись в сетевую шару. Это три флага установщика:

```bash
# git config для kouki + safe.directory на его репозитории
sudo ./deploy/install.sh /tmp/kouki-publish

# то же + PAT в /home/kouki/.git-credentials (600), чтобы агент мог пушить сам
# (токен читается из файла; stdin — через root-shell, иначе редирект сделает
#  непривилегированный shell и не прочитает /root/pat:
#  sudo bash -c './deploy/install.sh … --git-token-stdin < /root/pat')
sudo ./deploy/install.sh /tmp/kouki-publish --git-token-file /root/pat

# завести SMB-пользователя (после переустановки passdb.tdb пропадает вместе с ним)
sudo ./deploy/install.sh /tmp/kouki-publish --smb-user smurf123lol --smb-password-file /root/smb.pass
```

Пароль и токен аргументами (`--smb-password`, `--git-token`) тоже работают, но
видны в `ps` и оседают в истории shell — поэтому в примерах выше файлы.

SMB-пользователь создаётся и в системе (`useradd`), и в Samba (`smbpasswd -s -a`),
а `kouki` добавляется в его группу — иначе агент видит шару, но не пишет в неё.
PAT даёт агенту право пушить в репозиторий: выдавайте токен с минимальным
набором прав (`contents: write` достаточно). Помощник хранения кредов
настраивается только для `github.com` — иначе git отдавал бы файл любому хосту;
креды других хостов в файле сохраняются, заменяется только строка GitHub.

`--smb-user` — это **существующая** учётка владельца шары (в нашем случае
`smurf123lol`): доступ к `/srv/files` выдан именно ей в `smb.conf`. Новое имя
создаст пользователя с uid/gid владельца каталога (если номера ещё свободны),
но **доступа к шаре не даст**: `valid users` в `smb.conf` по-прежнему указывает
на прежнюю учётку — добавьте новое имя туда и перезапустите Samba. Если номер
уже занят другим пользователем, установщик предупредит и завершится с кодом 1.

Пароль и токен аргументами (`--smb-password`, `--git-token`) на время установки
видны в `ps` и оседают в истории shell. Если это важно — берите их из файла или
stdin:

```bash
sudo bash -c './deploy/install.sh /tmp/kouki-publish --smb-user smurf123lol --smb-password-stdin < /root/smb.pass'
sudo ./deploy/install.sh /tmp/kouki-publish --smb-user smurf123lol --smb-password-file /root/smb.pass
sudo ./deploy/install.sh /tmp/kouki-publish --git-token-file /root/pat
```

Редирект `<` выполняет вызывающий shell, поэтому под `sudo` его надо оборачивать
в `bash -c` (иначе не-root не прочитает `/root/smb.pass`). Источников должно быть
ровно один: два одновременно отвергаются, чтобы не гадать, какой применился.
После установки удалите файл с паролем, а PAT при утечке отзовите в настройках
GitHub.

## MCP-сервер (для агента на любой машине)

`Kagedama.mcp` — не служба, а stdio-процесс: его запускает клиент (плагин
`dsh-kouki` в DeepSeek Harness, `claude mcp add`, любой MCP-клиент). В артефакте
он лежит в подкаталоге `mcp/`, а на сервере ставится **отдельным каталогом**
`/opt/kouki/mcp` (не внутрь `/opt/kouki/app`: там живёт только бот). Ему нужен
только доступ к базе:

```bash
dotnet /opt/kouki/mcp/Kagedama.mcp.dll --database=/var/lib/kouki/db/kouki.sqlite
```

Удобнее — через окружение, чтобы плагин не знал пути:

```bash
Kouki__DatabasePath=/var/lib/kouki/db/kouki.sqlite dotnet /opt/kouki/mcp/Kagedama.mcp.dll
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

Бэкап ставит сам установщик (`install.sh`): копирует `kouki-backup.sh` в артефакт,
создаёт `/etc/kouki/backup.env` из примера (если его ещё нет) и каталог
`/var/backups/kouki` (`2775 root:kouki`).

Дальше — по обстановке: если `sqlite3` и `rsync` на месте, установщик включает
`kouki-backup.timer` и делает один пробный прогон (сразу видно, что копия
получается, а не «должна получиться»). Если инструментов нет или пробный прогон
упал — таймер **не остаётся включённым** (а включённый ранее гасится), установка
завершается с кодом 1 и подсказкой, что поставить.

Выключить (в том числе уже включённый прошлой установкой таймер): `--no-backup`.
Установщик погасит и отключит `kouki-backup.timer`, снимки в `/var/backups/kouki`
останутся на месте.

Так было не всегда: до T87 бэкап предлагалось ставить руками, и после переустановки
сервера таймер остался `disabled` — то есть сутки данных жили без копий. Теперь
«поставил сервер» и «бэкап идёт» — одно действие.

Руками (если нужно повторить или поставить отдельно):

```bash
sudo install -o root -g kouki -m 640 deploy/backup.env.example /etc/kouki/backup.env
sudo install -d -o root -g kouki -m 2775 /var/backups/kouki
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
и `audio/`. Каталог **зафиксирован в юните** (`ReadWritePaths=/var/backups/kouki`),
поэтому переносить данные на другой диск нужно правкой юнита, а не только
`backup.env`; иначе система защиты systemd сделает каталог read-only. Держите его
**не на том же диске**, что `/var/lib/kouki`, иначе один
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

`deploy/smoke.sh` проверяет то, что ломалось при переезде: сервисы, `/api/health`,
классификаторы с токеном, очередь напоминаний, синтез (`/api/speech`), шару
`/srv/files`, страницу токена DSH, включённый таймер бэкапа и отсутствие ошибок
у бота за два часа. По флагу `--with-writes` добавляется ответчик
(`/api/intake` с `intent=ask`).

```bash
sudo ./deploy/smoke.sh                 # только чтение, код 1 при провалах
sudo ./deploy/smoke.sh --with-writes   # плюс проверка ответчика (пишет в базу)
sudo ./deploy/smoke.sh --quiet         # только провалы — удобно в cron
sudo ./deploy/smoke.sh --json          # машинный отчёт
KOUKI_TOKEN=... ./deploy/smoke.sh      # токен не из /etc/kouki/kouki.env
```

По умолчанию смоук **только читает**: GET-запросы, `systemctl is-active`, чтение
журнала и пробный файл в шаре (создаётся и сразу удаляется — иначе «запись
доступна» не проверить).

`--with-writes` добавляет `POST /api/classify` и `POST /api/intake` с
`intent=ask`. Оба оставляют след: `classify` пишет строку журнала
классификаций, `intake` создаёт задачу «Смоук: …» и строку журнала. Это мешает
датасету для интентов, поэтому по умолчанию выключено — включайте на стенде или
когда как раз проверяете ответчик.

**Секреты.** Токен API передаётся curl через временный конфиг с правами 600
(`curl -K`), а не аргументом `-H`: иначе он виден в `ps` и
`/proc/<pid>/cmdline` любому локальному пользователю. То же правило в
`kouki-monitor.sh` для токена бота — URL собирается внутри конфига.

Запускать после `install.sh` и каждого деплоя — так «поставил» и «работает» не
расходятся.

## Сторож живости

`kouki-monitor.timer` раз в пять минут запускает `kouki-monitor.sh`: проверяет
`/api/health` и `/api/classifiers` с токеном. Состояние (`ok`/`fail`) хранится в
`/var/lib/kouki/monitor/state` — алерт уходит **только на смене состояния**, а
пока проблема не ушла, напоминание не чаще раза в час
(`KOUKI_MONITOR_REMIND_SECONDS`, по умолчанию 3600).

Установщик ставит сторожа сам (`--no-monitor` отключает) вместе с пробным
прогоном. Алерты — по желанию:

```bash
sudo install -o root -g kouki -m 640 deploy/monitor.env.example /etc/kouki/monitor.env
sudo editor /etc/kouki/monitor.env      # KOUKI_ALERT_TOKEN, KOUKI_ALERT_CHAT
sudo systemctl restart kouki-monitor.timer
sudo systemctl start kouki-monitor.service && journalctl -u kouki-monitor -n 20
```

Без `monitor.env` сторож пишет только в журнал — это нормальный режим для
машины, которая и так под присмотром. Почему таймер, а не `OnFailure` у юнита:
сервис бывает `active`, но отвечает 500 или таймаутом (например, умерла
локальная модель), а бывает и наоборот — процесс жив, а база недоступна.
Сторож проверяет то, чем пользуется человек.

Подробности поведения, которые легко принять за баги:

- **Журнал — только на смену состояния.** «ok» пишется один раз при выходе из
  проблемы, а не каждые пять минут; `state` в `/var/lib/kouki/monitor/state`
  всегда текущий.
- **Нет токена API — это проблема, а не «ok».** Иначе зелёный статус врал бы:
  `/api/classifiers` не проверялся.
- **`last-alert` пишется только при доставке.** Если Telegram недоступен, алерт
  повторяется на следующей проверке, а не через час молчания.
- **Секреты не в argv.** Токен бота уходит в URL внутри временного конфига
  (`curl -K`, файл 600), токен API — в заголовке там же. В `ps` их не видно.
- **Пробная проверка при установке смотрит `state`, а не код systemctl.**
  У юнита `SuccessExitStatus=0 1` (сторож вправе вернуть 1 — это его вывод), так
  что «start успешен» ещё не значит «сервер жив»: установщик читает
  `/var/lib/kouki/monitor/state` и при `fail:…` завершает установку с кодом 1.

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
- **Секреты в git.** Токен живёт только в `/etc/kouki/kouki.env`; в репозитории
  лежит `CHANGE_ME`.
### Обновление сервера без потери бота

Обычный способ на сервере — `deploy/deploy-code.sh` (не требует root): сначала
`--dry-run`, потом без флага. Скрипт проверяет маркеры каталогов, форму деплоя
(self-contained и нативки sherpa) и вложенность путей, делает копию `<каталог>.prev`,
раскладывает артефакт, перезапускает сервисы и откатывается сам, если сервер не
поднялся или смоук провалился.

Ручной путь, который не оставляет бота лежать (если скрипта нет под рукой).
Выполняйте от пользователя-владельца `/opt/kouki` (обычно `kouki`), а не от root:
иначе файлы в `/opt/kouki/app` станут root-owned и следующий `deploy-code.sh`
не сможет писать:

```sh
# первый раз: каталога MCP ещё нет (install.sh его не создаёт)
sudo install -d -o kouki -g kouki /opt/kouki/mcp

sudo systemctl stop kouki          # PartOf погасит и kouki-bot
rsync -a --delete --exclude 'bot/' --exclude 'mcp/' --exclude 'appsettings.Development.json' \
      <артефакт>/ /opt/kouki/app/
rsync -a --delete <артефакт>/bot/ /opt/kouki/app/bot/
rsync -a --delete <артефакт>/mcp/ /opt/kouki/mcp/
sudo systemctl start kouki
sudo systemctl start kouki-bot     # PartOf не поднимает бота обратно
```

Все три `--exclude` — те же, что в `deploy-code.sh`. `bot/`: внутри `/opt/kouki/app` живёт каталог бота,
и `--delete` без исключения удалял его (systemd падал на
`Changing to the requested working directory failed`, бот молча не отвечал).
`mcp/`: артефакт содержит подкаталог `mcp/`, но MCP-сервер лежит отдельно, в
`/opt/kouki/mcp`, и обновляется своим rsync — без исключения копия `mcp/`
оседает внутри `/opt/kouki/app` (потом `deploy-code.sh` исключает её из
`--delete`, и дубль остаётся навсегда). `appsettings.Development.json`: dev-конфиг
в прод не кладём. `install.sh` MCP не копирует и каталог `/opt/kouki/mcp` не создаёт,
поэтому первый раз его создают руками (см. `install -d` выше), и `deploy-code.sh`
без существующего каталога откажется работать (проверка `MCP_DIR`).
