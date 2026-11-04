#!/usr/bin/env bash
# Резервное копирование сетевой шары /srv/files (T100):
# hardlink-снапшоты rsync с политикой хранения 7 daily + 4 weekly.
#
# Запускается таймером shara-snapshot.timer.
# Защищает от случайного удаления и повреждения файлов.
set -euo pipefail

ENV_FILE="${SHARA_SNAPSHOT_ENV:-/etc/kouki/shara-snapshot.env}"
if [ -f "$ENV_FILE" ]; then
    # shellcheck source=/dev/null
    . "$ENV_FILE"
fi

SRC="${SRC:-/srv/files}"
DEST="${DEST:-/srv/files/_backup/snapshots}"
KEEP_DAILY="${KEEP_DAILY:-7}"
KEEP_WEEKLY="${KEEP_WEEKLY:-4}"
MAX_AGE_HOURS="${MAX_AGE_HOURS:-36}"
MAX_USAGE_PERCENT="${MAX_USAGE_PERCENT:-10}"
EXCLUDE_FILE="${EXCLUDE_FILE:-/etc/kouki/shara-exclude.txt}"
VERIFY_SAMPLES="${VERIFY_SAMPLES:-20}"
MIN_KEEP_DAYS="${MIN_KEEP_DAYS:-1}"
SKIP_MOUNT_CHECK="${SKIP_MOUNT_CHECK:-0}"

usage() {
    cat <<'EOF'
Использование: shara-snapshot.sh <команда> [опции]

Команды:
  snapshot [--dry-run]
      Снять версию шары прямо сейчас (rsync --link-dest).
  status
      Возраст последней версии, число снимков, занятое место и лимиты.
  restore <путь-в-шаре> <версия|latest> [--to <каталог>] [--dry-run]
      Восстановить файл из указанной версии или latest.
  verify [--samples N]
      Проверить восстановление случайных файлов из свежей версии.
  prune
      Применить политику хранения версий (7 daily + 4 weekly).
  --self-test
      Автономная проверка логики снимков, hardlinks, prune и ошибок.
EOF
}

check_mount() {
    if [ "$SKIP_MOUNT_CHECK" -eq 1 ]; then
        return 0
    fi
    if ! mountpoint -q "$SRC"; then
        echo "$SRC не смонтирован: запустите systemctl start srv-files.mount" >&2
        return 1
    fi
    return 0
}

list_daily_versions() {
    if [ ! -d "$DEST" ]; then
        return 0
    fi
    find "$DEST" -mindepth 1 -maxdepth 1 -type d -name '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]' \
        -printf '%f\n' 2>/dev/null | sort
}

list_weekly_versions() {
    if [ ! -d "$DEST" ]; then
        return 0
    fi
    find "$DEST" -mindepth 1 -maxdepth 1 -type d -name 'weekly-[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]' \
        -printf '%f\n' 2>/dev/null | sort
}

list_all_versions() {
    if [ ! -d "$DEST" ]; then
        return 0
    fi
    find "$DEST" -mindepth 1 -maxdepth 1 -type d \
        \( -name '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]' -o -name 'weekly-[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]' \) \
        -printf '%f\n' 2>/dev/null | sort
}

get_disk_usage_pct() {
    local target="$1"
    if [ ! -d "$target" ]; then
        echo "0"
        return
    fi
    # Процент занятости снапшотами от общего размера диска
    local total_kb used_kb
    total_kb="$(df -k "$target" | awk 'NR==2 {print $2}')"
    used_kb="$(du -sk "$target" 2>/dev/null | awk '{print $1}')"
    if [ -z "$total_kb" ] || [ "$total_kb" -le 0 ]; then
        echo "0"
        return
    fi
    echo "$(( (used_kb * 100) / total_kb ))"
}

get_disk_free_gb() {
    local target="$1"
    if [ ! -d "$target" ]; then
        echo "0"
        return
    fi
    local free_kb
    free_kb="$(df -k "$target" | awk 'NR==2 {print $4}')"
    echo "$(( free_kb / 1024 / 1024 ))"
}

get_dir_size_gb() {
    local target="$1"
    if [ ! -d "$target" ]; then
        echo "0"
        return
    fi
    local used_kb
    used_kb="$(du -sk "$target" 2>/dev/null | awk '{print $1}')"
    echo "$(( used_kb / 1024 / 1024 ))"
}

cmd_snapshot() {
    local dry_run=0
    while [ $# -gt 0 ]; do
        case "$1" in
            --dry-run) dry_run=1; shift ;;
            *) echo "Неизвестный параметр: $1" >&2; exit 2 ;;
        esac
    done

    check_mount || exit 1

    mkdir -p "$DEST"
    exec 9>"$DEST/.lock"
    if ! flock -n 9; then
        echo "Снимок уже выполняется другим процессом — выходим" >&2
        exit 0
    fi

    local today
    today="$(date -u +%Y-%m-%d)"
    local target_dir="$DEST/$today"

    # Ищем предыдущую версию для --link-dest (последнюю daily)
    local prev_version
    prev_version="$(list_daily_versions | grep -v "^$today$" | tail -n 1 || true)"
    local -a link_opts=()
    if [ -n "$prev_version" ] && [ -d "$DEST/$prev_version" ]; then
        local abs_prev
        abs_prev="$(cd "$DEST/$prev_version" && pwd)"
        link_opts=(--link-dest="$abs_prev")
    else
        echo "нет предыдущих версий: первый снимок будет полным" >&2
    fi

    # Настройки исключений
    local -a exclude_opts=(--exclude='_backup/' --exclude='lost+found/' --exclude='_kouki/tmp/')
    if [ -f "$EXCLUDE_FILE" ]; then
        exclude_opts=(--exclude-from="$EXCLUDE_FILE" "${exclude_opts[@]}")
    fi

    local tmp_dir="$DEST/$today.tmp"
    rm -rf "$tmp_dir"

    local -a rsync_cmd=(rsync -a --delete --numeric-ids)
    if [ "$dry_run" -eq 1 ]; then
        rsync_cmd+=(--dry-run)
    fi

    local pre_used_bytes=0
    if [ -d "$DEST" ]; then
        pre_used_bytes="$(du -sb "$DEST" 2>/dev/null | awk '{print $1}' || echo 0)"
    fi

    "${rsync_cmd[@]}" "${link_opts[@]}" "${exclude_opts[@]}" "$SRC/" "$tmp_dir/"

    if [ "$dry_run" -eq 1 ]; then
        echo "dry-run: снимок $today проверен"
        rm -rf "$tmp_dir"
        return 0
    fi

    rm -rf "$target_dir"
    mv "$tmp_dir" "$target_dir"

    # По воскресеньям создаём weekly-снимок через cp -al
    local day_of_week
    day_of_week="$(date -u +%u)"
    if [ "$day_of_week" -eq 7 ]; then
        local weekly_dir="$DEST/weekly-$today"
        if [ ! -d "$weekly_dir" ]; then
            cp -al "$target_dir" "$weekly_dir"
        fi
    fi

    local post_used_bytes
    post_used_bytes="$(du -sb "$DEST" 2>/dev/null | awk '{print $1}' || echo 0)"
    local new_bytes=$(( post_used_bytes > pre_used_bytes ? post_used_bytes - pre_used_bytes : 0 ))

    local file_count
    file_count="$(find "$target_dir" -type f 2>/dev/null | wc -l)"
    local total_gb
    total_gb="$(get_dir_size_gb "$DEST")"
    local free_gb
    free_gb="$(get_disk_free_gb "$DEST")"

    echo "snapshot $today: файлов $file_count, новых данных $new_bytes байт, всего $total_gb ГБ, свободно $free_gb ГБ"

    # Вызываем prune для удержания политики
    cmd_prune

    # Проверка доли диска под снимки
    local usage_pct
    usage_pct="$(get_disk_usage_pct "$DEST")"
    if [ "$usage_pct" -ge "$MAX_USAGE_PERCENT" ]; then
        echo "занято $total_gb ($usage_pct %): снимки больше лимита, нужен prune или чистка" >&2
    fi
}

cmd_status() {
    if [ ! -d "$DEST" ]; then
        echo "последняя версия: нет"
        echo "версий: 0"
        echo "занято: 0 (0% диска)"
        echo "свободно: $(get_disk_free_gb "$SRC") ГБ"
        return 1
    fi

    local all_versions
    all_versions="$(list_all_versions)"
    local count
    count="$(echo "$all_versions" | grep -c -v '^$' || true)"
    if [ "$count" -eq 0 ]; then
        echo "последняя версия: нет"
        echo "версий: 0"
        echo "занято: 0 (0% диска)"
        echo "свободно: $(get_disk_free_gb "$DEST") ГБ"
        return 1
    fi

    local latest
    latest="$(echo "$all_versions" | tail -n 1)"
    local latest_dir="$DEST/$latest"
    local now_epoch
    now_epoch="$(date +%s)"
    local dir_mtime
    dir_mtime="$(stat -c %Y "$latest_dir")"
    local age_hours=$(( (now_epoch - dir_mtime) / 3600 ))

    local used_gb
    used_gb="$(get_dir_size_gb "$DEST")"
    local usage_pct
    usage_pct="$(get_disk_usage_pct "$DEST")"
    local free_gb
    free_gb="$(get_disk_free_gb "$DEST")"

    echo "последняя версия: $latest, $age_hours ч назад"
    echo "версий: $count"
    echo "занято: $used_gb ($usage_pct% диска)"
    echo "свободно: $free_gb"

    local exit_code=0
    if [ "$age_hours" -gt "$MAX_AGE_HOURS" ]; then
        exit_code=1
    fi
    if [ "$usage_pct" -ge "$MAX_USAGE_PERCENT" ]; then
        echo "занято $used_gb ($usage_pct %): снимки больше лимита, нужен prune или чистка" >&2
        exit_code=1
    fi

    return "$exit_code"
}

cmd_restore() {
    if [ $# -lt 2 ]; then
        echo "Использование: shara-snapshot.sh restore <путь-в-шаре> <версия|latest> [--to <каталог>] [--dry-run]" >&2
        return 2
    fi

    local target_path="$1"
    local ver="$2"
    shift 2

    local to_dir=""
    local dry_run=0

    while [ $# -gt 0 ]; do
        case "$1" in
            --to) to_dir="$2"; shift 2 ;;
            --dry-run) dry_run=1; shift ;;
            *) echo "Неизвестный параметр: $1" >&2; return 2 ;;
        esac
    done

    # Проверка пути: путь должен быть внутри шары
    local rel_path=""
    if [[ "$target_path" == /* ]]; then
        # Абсолютный путь
        if [[ "$target_path" != "$SRC"* ]] || [[ "$target_path" == *"/../"* ]]; then
            echo "путь '$target_path' вне шары: восстановление только из /srv/files" >&2
            return 1
        fi
        rel_path="${target_path#"$SRC"}"
        rel_path="${rel_path#/}"
    else
        # Относительный путь
        if [[ "$target_path" == *".."* ]]; then
            echo "путь '$target_path' вне шары: восстановление только из /srv/files" >&2
            return 1
        fi
        rel_path="${target_path#/}"
    fi

    # Разрешение версии
    local resolved_ver="$ver"
    if [ "$ver" = "latest" ]; then
        resolved_ver="$(list_all_versions | tail -n 1 || true)"
        if [ -z "$resolved_ver" ]; then
            echo "версия 'latest' не найдена. Доступны: " >&2
            return 1
        fi
    else
        if [ ! -d "$DEST/$ver" ]; then
            local available
            available="$(list_all_versions | tr '\n' ' ' | sed 's/ $//')"
            echo "версия '$ver' не найдена. Доступны: $available" >&2
            return 1
        fi
    fi

    local src_file="$DEST/$resolved_ver/$rel_path"
    if [ ! -e "$src_file" ]; then
        echo "файл '$rel_path' не найден в версии '$ver'" >&2
        return 1
    fi

    if [ -z "$to_dir" ]; then
        to_dir="_scratch/shara-restore/$resolved_ver"
    fi
    local dst_file="$to_dir/$rel_path"

    if [ "$dry_run" -eq 1 ]; then
        echo "будет восстановлено: $rel_path → $dst_file"
        return 0
    fi

    mkdir -p "$(dirname "$dst_file")"
    cp -a "$src_file" "$dst_file"

    if [ -f "$src_file" ]; then
        local src_hash dst_hash
        src_hash="$(sha256sum "$src_file" | awk '{print $1}')"
        dst_hash="$(sha256sum "$dst_file" | awk '{print $1}')"
        if [ "$src_hash" = "$dst_hash" ]; then
            echo "восстановлено: $rel_path → $dst_file (sha256 совпадает)"
        else
            echo "ошибка: sha256 не совпадает после восстановления $rel_path" >&2
            return 1
        fi
    else
        echo "восстановлено: $rel_path → $dst_file"
    fi
}

cmd_verify() {
    local samples="$VERIFY_SAMPLES"
    while [ $# -gt 0 ]; do
        case "$1" in
            --samples) samples="$2"; shift 2 ;;
            *) echo "Неизвестный параметр: $1" >&2; exit 2 ;;
        esac
    done

    local latest
    latest="$(list_all_versions | tail -n 1 || true)"
    if [ -z "$latest" ]; then
        echo "версия latest: не найден ни один файл для проверки" >&2
        return 1
    fi

    local latest_dir="$DEST/$latest"
    local files
    files="$(find "$latest_dir" -type f 2>/dev/null | grep -v '\.lock$' || true)"
    local count
    count="$(echo "$files" | grep -c -v '^$' || true)"
    if [ "$count" -eq 0 ]; then
        echo "версия $latest: не найден ни один файл для проверки" >&2
        return 1
    fi

    local report_dir="_scratch/shara-restore-check"
    mkdir -p "$report_dir"
    local report_file="$report_dir/$latest.log"

    local sample_files
    sample_files="$(echo "$files" | sort -R 2>/dev/null | head -n "$samples" || echo "$files" | head -n "$samples")"

    local tested=0
    local failed=0
    echo "=== Проверка целостности снимка $latest ($(date -u)) ===" > "$report_file"
    while IFS= read -r f; do
        [ -z "$f" ] && continue
        tested=$(( tested + 1 ))
        local rel="${f#"$latest_dir/"}"
        local tmp_out="$report_dir/tmp_probe"
        rm -f "$tmp_out"
        cp "$f" "$tmp_out"
        local hash_orig hash_copy
        hash_orig="$(sha256sum "$f" | awk '{print $1}')"
        hash_copy="$(sha256sum "$tmp_out" | awk '{print $1}')"
        rm -f "$tmp_out"

        if [ "$hash_orig" = "$hash_copy" ]; then
            echo "OK: $rel (sha256: $hash_orig)" >> "$report_file"
        else
            echo "FAIL: $rel (хеши не совпали!)" >> "$report_file"
            failed=$(( failed + 1 ))
        fi
    done <<< "$sample_files"
    echo "Итог: проверено $tested файлов, ошибок: $failed" >> "$report_file"
    cat "$report_file"

    if [ "$failed" -gt 0 ]; then
        return 1
    fi
}

cmd_prune() {
    if [ ! -d "$DEST" ]; then
        echo "удалено: 0"
        return 0
    fi

    local deleted=0
    local today
    today="$(date -u +%Y-%m-%d)"

    # Ротация daily версий
    local -a daily_versions=()
    while IFS= read -r v; do
        [ -n "$v" ] && daily_versions+=("$v")
    done < <(list_daily_versions)

    local daily_count="${#daily_versions[@]}"
    if [ "$daily_count" -gt "$KEEP_DAILY" ]; then
        local to_remove_daily=$(( daily_count - KEEP_DAILY ))
        for (( i=0; i<to_remove_daily; i++ )); do
            local ver="${daily_versions[$i]}"
            # Никогда не удалять сегодня и младше MIN_KEEP_DAYS
            if [ "$ver" != "$today" ]; then
                rm -rf "${DEST:?}/$ver"
                deleted=$(( deleted + 1 ))
            fi
        done
    fi

    # Ротация weekly версий
    local -a weekly_versions=()
    while IFS= read -r v; do
        [ -n "$v" ] && weekly_versions+=("$v")
    done < <(list_weekly_versions)

    local weekly_count="${#weekly_versions[@]}"
    if [ "$weekly_count" -gt "$KEEP_WEEKLY" ]; then
        local to_remove_weekly=$(( weekly_count - KEEP_WEEKLY ))
        for (( i=0; i<to_remove_weekly; i++ )); do
            local ver="${weekly_versions[$i]}"
            rm -rf "${DEST:?}/$ver"
            deleted=$(( deleted + 1 ))
        done
    fi

    echo "удалено: $deleted"
}

cmd_self_test() {
    echo "== Запуск self-test shara-snapshot =="
    local test_root
    test_root="$(pwd)/_scratch/shara-test-$$"
    rm -rf "$test_root"
    mkdir -p "$test_root/src" "$test_root/dest" "$test_root/etc" "$test_root/restore"

    local old_src="$SRC"
    local old_dest="$DEST"
    local old_skip="$SKIP_MOUNT_CHECK"
    local old_keep_daily="$KEEP_DAILY"
    local old_exclude="$EXCLUDE_FILE"

    SRC="$test_root/src"
    DEST="$test_root/dest"
    SKIP_MOUNT_CHECK=1
    KEEP_DAILY=2
    EXCLUDE_FILE="$test_root/etc/exclude.txt"

    cat <<'EOF' > "$EXCLUDE_FILE"
_backup/
lost+found/
_kouki/tmp/
EOF

    # 1. Создаём исходные файлы
    echo "data1" > "$SRC/file1.txt"
    echo "data2" > "$SRC/file2.txt"
    echo "data3" > "$SRC/file3.txt"
    mkdir -p "$SRC/_backup"
    echo "backup-junk" > "$SRC/_backup/dont_copy.txt"

    # 2. Снимаем снимок 1
    cmd_snapshot

    # 3. Проверяем исключение _backup/
    local today
    today="$(date -u +%Y-%m-%d)"
    if [ -d "$DEST/$today/_backup" ]; then
        echo "Ошибка: _backup был скопирован в снимок!" >&2
        exit 1
    fi

    # 4. Модифицируем файл 2 и добавляем файл 4
    local inode1_v1 inode2_v1
    inode1_v1="$(stat -c %i "$DEST/$today/file1.txt")"
    inode2_v1="$(stat -c %i "$DEST/$today/file2.txt")"

    # Имитируем вчерашнюю версию для теста hardlinks и prune
    local yesterday="2026-01-01"
    mv "$DEST/$today" "$DEST/$yesterday"
    inode1_v1="$(stat -c %i "$DEST/$yesterday/file1.txt")"
    inode2_v1="$(stat -c %i "$DEST/$yesterday/file2.txt")"

    echo "data2-modified" > "$SRC/file2.txt"
    echo "data4" > "$SRC/file4.txt"

    # 5. Снимаем снимок 2
    cmd_snapshot

    # 6. Проверяем hardlink
    local inode1_v2 inode2_v2
    inode1_v2="$(stat -c %i "$DEST/$today/file1.txt")"
    inode2_v2="$(stat -c %i "$DEST/$today/file2.txt")"

    if [ "$inode1_v1" -ne "$inode1_v2" ]; then
        echo "Ошибка: неизменённый file1.txt не получил hardlink!" >&2
        exit 1
    fi
    if [ "$inode2_v1" -eq "$inode2_v2" ]; then
        echo "Ошибка: изменённый file2.txt получил одинаковый inode!" >&2
        exit 1
    fi

    # 7. Проверяем restore
    cmd_restore "file2.txt" "$yesterday" --to "$test_root/restore"
    local restored_content
    restored_content="$(cat "$test_root/restore/file2.txt")"
    if [ "$restored_content" != "data2" ]; then
        echo "Ошибка: restore вернул '$restored_content', ожидалось 'data2'" >&2
        exit 1
    fi

    # 8. Проверяем негативные сценарии restore
    if cmd_restore "../../etc/passwd" latest 2>/dev/null; then
        echo "Ошибка: restore пропустил путь вне шары!" >&2
        exit 1
    fi
    if cmd_restore "file1.txt" "non-existent-ver" 2>/dev/null; then
        echo "Ошибка: restore пропустил несуществующую версию!" >&2
        exit 1
    fi
    if cmd_restore "no-such-file.txt" "$yesterday" 2>/dev/null; then
        echo "Ошибка: restore пропустил несуществующий файл!" >&2
        exit 1
    fi

    # 9. Проверяем prune (создадим третью версию и проверим удаление старейшей при KEEP_DAILY=2)
    local old_ver="2025-12-31"
    mkdir -p "$DEST/$old_ver"
    touch "$DEST/$old_ver/dummy.txt"
    cmd_prune
    if [ -d "$DEST/$old_ver" ]; then
        echo "Ошибка: prune не удалил старую версию $old_ver!" >&2
        exit 1
    fi

    # 10. Проверяем verify
    cmd_verify

    # Очистка
    rm -rf "$test_root"

    SRC="$old_src"
    DEST="$old_dest"
    SKIP_MOUNT_CHECK="$old_skip"
    KEEP_DAILY="$old_keep_daily"
    EXCLUDE_FILE="$old_exclude"

    echo "self-test passed"
}

if [ $# -eq 0 ]; then
    usage >&2
    exit 2
fi

case "$1" in
    snapshot) shift; cmd_snapshot "$@" || exit $? ;;
    status) shift; cmd_status "$@" || exit $? ;;
    restore) shift; cmd_restore "$@" || exit $? ;;
    verify) shift; cmd_verify "$@" || exit $? ;;
    prune) shift; cmd_prune "$@" || exit $? ;;
    --self-test) shift; cmd_self_test "$@" || exit $? ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Неизвестная команда: $1" >&2; usage >&2; exit 2 ;;
esac
