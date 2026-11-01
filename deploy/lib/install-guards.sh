#!/usr/bin/env bash
# shellcheck disable=SC2034  # SHARE_UID/SHARE_GID/SHARE_GROUP/SHARE_CREATE_GROUP
# заполняет read_share_ids, читает вызывающий install.sh — это контракт, а не мёртвые переменные.
# Проверки установщика с побочными эффектами, вынесенные из install.sh.
#
# Зачем отдельно: эти ветки решают, «работает ли обещанное». Строковые тесты
# («в файле есть слово») проходят и когда логика сломана, поэтому вместо них
# тесты вызывают эти функции с подменёнными systemctl/stat/getent и смотрят,
# что реально произошло.
#
# Файл подключается (`source`) и из install.sh, и из тестов.

# Каких инструментов не хватает для резервного копирования.
# Печатает имена через пробел; пустой вывод — всё на месте.
backup_missing_tools() {
    local tool missing=""
    for tool in sqlite3 rsync; do
        if ! command -v "$tool" >/dev/null 2>&1; then
            missing="${missing:+$missing }$tool"
        fi
    done
    printf '%s' "$missing"
}

# Включён ли таймер (по любому признаку: enabled или active).
backup_timer_touched() {
    systemctl is-enabled --quiet kouki-backup.timer 2>/dev/null ||
        systemctl is-active --quiet kouki-backup.timer 2>/dev/null
}

# Гасит таймер и сообщает, удалось ли. 0 — выключен (или не был включён).
stop_backup_timer() {
    if ! backup_timer_touched; then
        return 0
    fi
    if systemctl disable --now kouki-backup.timer; then
        echo "   таймер выключен"
        return 0
    fi
    echo "   ОШИБКА: таймер остался включённым — systemctl disable --now kouki-backup.timer" >&2
    return 1
}

# Приводит таймер бэкапа в соответствие с наличием инструментов.
#
# $1 — список недостающих инструментов (может быть пустым).
# 0 — таймер включён и пробный прогон прошёл.
# 1 — таймер не работает; всё, что было включено, погашено (если это удалось).
apply_backup_timer() {
    local missing="${1:-}"

    if [ -n "$missing" ]; then
        echo "   бэкап не включён: нет $missing"
        # Таймер мог остаться от прошлой установки: с ним копии падают в 03:30.
        stop_backup_timer || return 1
        return 1
    fi

    if ! systemctl enable --now kouki-backup.timer >/dev/null 2>&1; then
        echo "   ОШИБКА: таймер не включился" >&2
        stop_backup_timer || true
        return 1
    fi
    if ! systemctl is-active --quiet kouki-backup.timer; then
        echo "   ОШИБКА: таймер не активен после enable" >&2
        # enabled, но не работает — это тот же «обещанный» бэкап, которого нет.
        stop_backup_timer || true
        return 1
    fi

    echo "   таймер включён (03:30, разброс 15 мин)"

    if ! systemctl start kouki-backup.service; then
        echo "   ОШИБКА: пробный бэкап упал — journalctl -u kouki-backup -n 30 --no-pager" >&2
        if ! stop_backup_timer; then
            echo "   копий нет, а выключить таймер не удалось — выключите вручную" >&2
        fi
        return 1
    fi
    echo "   пробный бэкап прошёл"
    return 0
}

# Разбирает числового владельца каталога шары, чтобы создаваемый SMB-пользователь
# получил те же uid/gid, что были до переустановки системы.
#
# Печатает строку: uid:gid:group:create_group
#   пустые uid/gid — каталога нет (тогда — обычный useradd без привязки);
#   uid пустой при непустом gid — каталог принадлежит root, но группу шары
#                                  использовать можно (в неё добавим kouki и SMB);
#   group — существующая группа с этим GID (тогда везде используем её имя);
#   create_group=1 — группы с таким GID нет, её надо создать;
#   create_group=2 — каталог на root-группе (gid 0): шару надо перевесить;
#   create_group=3 — группа системная (gid < 1000): пользователей в неё не суём.
#
# Разделитель — двоеточие, чтобы вызывающий читал поля через `read`, а не `eval`:
# имя группы приходит из /etc/group и не должно исполняться как shell-код.
# Группы, членство в которых даёт лишние права: шару на них вешать нельзя.
# Список — основное правило; для остальных групп порог задаёт SYS_GID_MAX
# (в Ubuntu 999, порог включительно). На системах с GID_MIN=500 обычная группа шары может иметь
# gid 600 — тогда запускайте установщик с SYS_GID_MAX=499.
SYS_GID_MAX="${SYS_GID_MAX:-999}"
is_privileged_group() {
    local gid="$1" name="$2"
    case "$name" in
        root|sudo|adm|disk|docker|lxd|shadow|trusted|wheel|systemd-journal|plugdev|kvm|libvirt)
            return 0
            ;;
    esac

    [ -n "$gid" ] && [ "$gid" -le "$SYS_GID_MAX" ] 2>/dev/null
}

# Создаёт SMB-пользователя шары. uid/gid передаём только если они известны:
# при пустых значениях `useradd --gid ""` падает «group '' does not exist».
create_share_user() {
    local user="$1" uid="$2" gid="$3"
    if [ -n "$uid" ] && [ -n "$gid" ]; then
        useradd --create-home --shell /usr/sbin/nologin --uid "$uid" --gid "$gid" "$user"
    elif [ -n "$gid" ]; then
        useradd --create-home --shell /usr/sbin/nologin --gid "$gid" "$user"
    else
        useradd --create-home --shell /usr/sbin/nologin "$user"
    fi
}

# Единый формат ответа: `kouki-id:uid:gid:group:create_group`, пустые поля — «-».
# Префикс и заполнители обязательны: `IFS=: read` срезает ведущие разделители,
# поэтому «::0» разбиралось как uid=0, а «:1000:smurf:0» — со сдвигом полей.
emit_share_ids() {
    printf 'kouki-id:%s:%s:%s:%s\n' \
        "${1:--}" "${2:--}" "${3:--}" "${4:--}"
}

# Разбирает строку от resolve_share_ids в SHARE_UID/SHARE_GID/SHARE_GROUP/
# SHARE_CREATE_GROUP. Возвращает 1, если строка не того формата.
read_share_ids() {
    local tag uid gid group create
    # Сбрасываем поля до любых проверок: при ошибке вызывающий видит пустые
    # значения, а не остатки от предыдущего вызова (установщик идёт под `set -u`).
    # shellcheck disable=SC2034  # эти переменные читает вызывающий (install.sh)
    SHARE_UID=""
    SHARE_GID=""
    SHARE_GROUP=""
    SHARE_CREATE_GROUP=""

    # Поле «create» может быть пустым только как «-», поэтому обрезанная строка
    # (меньше пяти полей) — ошибка, а не «всё пустое».
    if [ "$(printf '%s' "$1" | awk -F: '{print NF}')" -lt 5 ]; then
        return 1
    fi

    IFS=: read -r tag uid gid group create <<< "$1"
    if [ "$tag" != "kouki-id" ]; then
        return 1
    fi

    if [ -n "$uid" ] && [ "$uid" != "-" ]; then
        SHARE_UID="$uid"
    fi
    if [ -n "$gid" ] && [ "$gid" != "-" ]; then
        SHARE_GID="$gid"
    fi
    if [ -n "$group" ] && [ "$group" != "-" ]; then
        SHARE_GROUP="$group"
    fi
    if [ -n "$create" ] && [ "$create" != "-" ]; then
        SHARE_CREATE_GROUP="$create"
    fi
    return 0
}

# Разбор для установщика: сбрасывает поля, затем читает строку. Возвращает 1,
# если строку не разобрать — вызывающий обязан пропустить SMB-блок.
apply_share_ids() {
    # shellcheck disable=SC2034  # эти переменные читает вызывающий (install.sh)
    SHARE_UID=""
    SHARE_GID=""
    SHARE_GROUP=""
    SHARE_CREATE_GROUP=""
    read_share_ids "$1"
}

resolve_share_ids() {
    local dir="$1"
    local uid gid group=""

    if [ ! -d "$dir" ]; then
        emit_share_ids "" "" "" 0
        return 0
    fi

    uid="$(stat -c %u "$dir" 2>/dev/null || echo "")"
    gid="$(stat -c %g "$dir" 2>/dev/null || echo "")"
    if [ -z "$gid" ] || [ -z "$uid" ]; then
        # stat не ответил (нет прав, сломанная ФС): молчать нельзя — иначе
        # установщик создаст пользователя без группы шары и «успешно» завершится.
        echo "ВНИМАНИЕ: не удалось узнать владельца $dir — SMB-доступ к шаре не настроить" >&2
        # create_group=4: владелец неизвестен, установщик должен пропустить SMB
        # и завершиться с кодом 1, а не создать пользователя без группы шары.
        emit_share_ids "" "" "" 4
        return 0
    fi

    if [ "$uid" = "0" ] && [ "$gid" = "0" ]; then
        # Каталог root:root: писать в него не сможет никто, кроме root —
        # это то же «шару надо перевесить», что и root-группа.
        emit_share_ids "" "" "" 2
        return 0
    fi

    if [ "$uid" = "0" ]; then
        # Каталог на root-пользователе: создавать пользователя с uid 0 нельзя,
        # но группу шары сохраняем — иначе записи в 2775-каталог не будет.
        uid=""
    fi

    if [ "$gid" = "0" ]; then
        # Файлы шары под root-группой: добавлять пользователей в root нельзя.
        emit_share_ids "$uid" "0" "" 2
        return 0
    fi

    group="$(getent group "$gid" 2>/dev/null | cut -d: -f1 || true)"
    if is_privileged_group "$gid" "$group"; then
        # Членство в такой группе даёт лишние права (sudo, adm, disk, docker…),
        # шару надо перевесить на обычную группу.
        emit_share_ids "$uid" "$gid" "$group" 3
        return 0
    fi

    if [ -n "$group" ]; then
        emit_share_ids "$uid" "$gid" "$group" 0
    else
        emit_share_ids "$uid" "$gid" "" 1
    fi
}

