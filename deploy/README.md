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

- Ubuntu 22.04/24.04 или Debian 12;
- .NET 10 runtime (`aspnetcore-runtime-10.0`) — `dotnet --version` должен отвечать;
- `curl`, `rsync` (желательно), `sqlite3` (для бэкапов);
- артефакт публикации: на Windows `Kouki\tools\publish-linux.ps1`, на Linux
  `dotnet publish Kagedama.api/Kagedama.api.csproj -c Release -r linux-x64 --self-contained false -p:KoukiEnableSherpa=true -o publish/linux-x64`.

Публикация — framework-dependent: сам .NET на сервере ставится отдельно, зато
артефакт 33 МБ, а не 100+.

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
| `/opt/kouki/app` | артефакт публикации (перезаписывается при обновлении) |
| `/etc/kouki/kouki.env` | конфигурация, права `640 root:kouki` |
| `/var/lib/kouki/db/kouki.sqlite` | база (WAL рядом) |
| `/var/lib/kouki/audio` | записи, старше `AudioRetentionDays` удаляются |
| `/var/lib/kouki/models/asr/...` | модель распознавания |

Юнит задаёт `Kouki__RequireAbsolutePaths=true`: относительный путь в env-файле
остановит старт с сообщением, какая именно настройка виновата (типичная ошибка
переноса — «сервер запустился, а база пустая»).

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
