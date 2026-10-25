#!/usr/bin/env bash
# Ежедневный бэкап Kouki: согласованный снимок SQLite и копия аудио.
#
# Почему .backup, а не cp: база работает в WAL, и копирование файла на живой
# базе даёт снимок «наполовину» (страницы в -wal ещё не в основном файле).
#
# Запускается таймером kouki-backup.timer от пользователя kouki.
# Права: читает /var/lib/kouki, пишет в KOUKI_BACKUP_DIR.
set -euo pipefail

BACKUP_DIR="${KOUKI_BACKUP_DIR:-/var/backups/kouki}"
DB_PATH="${KOUKI_DATABASE_PATH:-/var/lib/kouki/db/kouki.sqlite}"
AUDIO_DIR="${KOUKI_AUDIO_DIR:-/var/lib/kouki/audio}"
RETENTION="${KOUKI_BACKUP_RETENTION_DAYS:-14}"

usage() {
    cat <<'EOF'
Использование: kouki-backup.sh [--db PATH] [--audio DIR] [--dir DIR] [--retention DAYS]

  --db PATH         база SQLite (по умолчанию /var/lib/kouki/db/kouki.sqlite)
  --audio DIR       каталог записей (по умолчанию /var/lib/kouki/audio)
  --dir DIR         куда класть бэкапы (по умолчанию /var/backups/kouki)
  --retention DAYS  сколько дней хранить снимки и аудио (по умолчанию 14)
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --db) DB_PATH="$2"; shift 2 ;;
        --audio) AUDIO_DIR="$2"; shift 2 ;;
        --dir) BACKUP_DIR="$2"; shift 2 ;;
        --retention) RETENTION="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Неизвестный аргумент: $1" >&2; usage >&2; exit 2 ;;
    esac
done

if [ ! -f "$DB_PATH" ]; then
    echo "Нет базы: $DB_PATH" >&2
    exit 2
fi
if ! command -v sqlite3 >/dev/null 2>&1; then
    echo "Нет sqlite3: apt install sqlite3" >&2
    exit 2
fi
if ! command -v rsync >/dev/null 2>&1; then
    echo "Нет rsync: apt install rsync" >&2
    exit 2
fi
if ! [[ "$RETENTION" =~ ^[0-9]+$ ]] || [ "$RETENTION" -lt 1 ]; then
    echo "Срок хранения должен быть положительным числом дней, а не '$RETENTION'" >&2
    exit 2
fi

mkdir -p "$BACKUP_DIR/db" "$BACKUP_DIR/audio"

# Один процесс за раз: второй запуск (ручной поверх таймера) просто выходит.
exec 9>"$BACKUP_DIR/.lock"
if ! flock -n 9; then
    echo "Бэкап уже идёт — выходим"
    exit 0
fi

STAMP="$(date +%F)"
SNAPSHOT="$BACKUP_DIR/db/kouki-$STAMP.sqlite"

echo "== снимок базы: $DB_PATH -> $SNAPSHOT"
sqlite3 "$DB_PATH" ".backup '$SNAPSHOT'"

# Снимок без ошибок целостности бесполезен: лучше упасть, чем хранить битый файл.
INTEGRITY="$(sqlite3 "$SNAPSHOT" 'PRAGMA integrity_check;')"
if [ "$INTEGRITY" != "ok" ]; then
    echo "Снимок не прошёл integrity_check: $INTEGRITY" >&2
    rm -f "$SNAPSHOT"
    exit 1
fi

echo "== аудио: $AUDIO_DIR -> $BACKUP_DIR/audio"
# Без --delete: если том с аудио пропал, бэкап не должен его затереть.
rsync -a --exclude '.lock' "$AUDIO_DIR/" "$BACKUP_DIR/audio/"

echo "== чистка старше $RETENTION дней"
find "$BACKUP_DIR/db" -name 'kouki-*.sqlite' -mtime +"$RETENTION" -delete
find "$BACKUP_DIR/audio" -type f -mtime +"$RETENTION" -delete

DB_COUNT="$(find "$BACKUP_DIR/db" -name 'kouki-*.sqlite' | wc -l)"
DB_SIZE="$(du -sh "$BACKUP_DIR/db" | cut -f1)"
echo "готово: $DB_COUNT снимков, $DB_SIZE"
