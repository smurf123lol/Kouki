# Сетевой диск (Samba + NFS)

Один каталог, две двери: Windows и macOS ходят по SMB, Linux — по NFS.
Данные при этом одни и те же, права — тоже.

## Что где

| Что | Путь |
| --- | --- |
| Точка монтирования | `/srv/files` |
| Раздел | `LABEL=files` (ext4), строка в `/etc/fstab` с `nofail` |
| Конфиг Samba | `/etc/samba/smb.conf` (пример — `smb.conf.example`) |
| Экспорт NFS | `/etc/exports` (пример — `exports.example`) |
| Логи Samba | `/var/log/samba/log.<клиент>` |

## Установка с нуля

```bash
sudo apt install -y samba nfs-kernel-server
sudo mkfs.ext4 -L files /dev/sdX          # ОДИН раз: стирает всё на разделе
sudo install -d /srv/files
echo 'LABEL=files /srv/files ext4 defaults,noatime,nofail 0 2' | sudo tee -a /etc/fstab
sudo mount /srv/files
sudo chown <логин>:<логин> /srv/files && sudo chmod 2775 /srv/files

sudo cp smb.conf.example /etc/samba/smb.conf
sudo smbpasswd -a <логин>                 # пароль SMB, отдельный от системного
sudo systemctl restart smbd nmbd

sudo cp exports.example /etc/exports      # подставьте свою подсеть
sudo exportfs -ra && sudo systemctl enable --now nfs-server
```

## Как подключиться

Windows (проводник): `\\<ip-сервера>\files`, вход `логин` + пароль SMB.
Из PowerShell можно проверить без проводника:

```powershell
net use Z: \\192.168.1.157\files /user:smurf123lol
```

macOS: Finder → «Подключиться к серверу» → `smb://<ip-сервера>/files`.
Linux:

```bash
sudo mount -t nfs <ip-сервера>:/srv/files /mnt/files
```

Постоянно — строка в `/etc/fstab`:

```
192.168.1.157:/srv/files  /mnt/files  nfs  defaults,_netdev,noauto,x-systemd.automount  0 0
```

## Сетевая реальность этой машины

Замерено на сервере (`ip -br a`):

| Интерфейс | Состояние | Адрес |
| --- | --- | --- |
| `wlx90de80ae8d26` | **UP** | `192.168.1.157/24` — единственный рабочий канал (USB-Wi-Fi) |
| `enp4s0` | DOWN | проводной Ethernet не подключён |
| `wlo1` | DOWN | встроенный Wi-Fi не используется |
| `lo` | UNKNOWN | петля |

Отсюда два решения в `smb.conf.example`, которые выглядят «недонастроенными», но
сделаны осознанно:

- **Нет `interfaces` / `bind interfaces only`.** Эти строки привязывают smbd к
  конкретным картам, а не «пускают только своих»: трафик в любом случае уходит
  через роутер. Привязка к имени интерфейса опасна именно здесь — у USB-адаптера
  имя `wlx<mac>`, и после переподключения SMB молча перестанет отвечать.
  Если привязка когда-нибудь понадобится: `ip -br a` → `interfaces = <iface>` +
  `bind interfaces only = yes`.
- **`os level` не задан** (у Samba по умолчанию 20), участие в выборах браузера
  выключено (`local master = no`, `domain master = no`). Доступ к шаре от этого
  не зависит — подключаемся по адресу или имени хоста.

Соседние Wi-Fi и LTE тут ни при чём: у машины нет другого uplink, поэтому smbd
физически не может выйти за LAN, а внутри LAN он доступен ровно тем, кто в неё
входит. Изоляция гостевых сетей — задача роутера, а не Samba.

## Что важно знать

- **Пароль SMB и системный — разные.** `sudo smbpasswd <логин>` меняет только
  SMB; `passwd` — только вход в систему.
- **Корень SMB-шары — не корень диска.** `/srv/files` подмонтирован, поэтому
  данные живут на своём разделе, а не на системном.
- **`root_squash` у NFS — это защита.** Root на клиенте приезжает как `nobody`;
  писать под своим uid — нормально, «от рута» — нет.
- **Один и тот же файл из SMB и NFS** держите открытым в одном месте: блокировки
  SMB и NFS друг о друге не знают, одновременная запись двух клиентов может
  перетереть данные.
- **SMB1 выключен** (`server min protocol = SMB2_10`): старые NAS-клиенты и
  Windows XP/2003 в сети не подключатся — это осознанно.
- **Бэкапы.** `kouki-backup.sh` кладёт снимки в `/var/backups/kouki`; если диск
  с `srv/files` переживёт систему, есть смысл указать там `BACKUP_DIR` на него.
