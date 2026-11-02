#!/usr/bin/env bash
# Карта файлов репозиториев Kouki и kouki-station.
#
# Печатает markdown-список отслеживаемых git файлов — чтобы агент и консультант
# (gemini_ask) видели состав проекта, не ползая по репозиторию.
#
# Использование:
#   bash tools/filemap.sh                       # в stdout
#   bash tools/filemap.sh > ../notes-updated/Процесс/Карта файлов.md
#
# Коды возврата: 0 — успех, 1 — ошибка, 2 — неверные аргументы.
set -euo pipefail

usage() { cat <<'EOF'
Использование: bash tools/filemap.sh [-s КАТАЛОГ]

Печатает в stdout карту файлов: два раздела (Kouki, kouki-station),
пути отсортированы, по одному в строке.

Параметры:
  -s, --workspace КАТАЛОГ  каталог воркспейса (по умолчанию — родитель этого репозитория)

Коды возврата:
  0 — карта напечатана
  1 — репозиторий не найден или git недоступен
  2 — неверные аргументы
EOF
}

die() { printf 'Ошибка: %s\n' "$*" >&2; exit 1; }

WORKSPACE=""
while [ $# -gt 0 ]; do
    case "$1" in
        -s|--workspace) [ $# -ge 2 ] || { printf 'Ошибка: %s требует значения\n' "$1" >&2; exit 2; }; WORKSPACE="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) printf 'Ошибка: неизвестный аргумент %s\n' "$1" >&2; exit 2 ;;
    esac
done

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
[ -n "$WORKSPACE" ] || WORKSPACE="$(dirname "$REPO_ROOT")"

printf '# Карта файлов (обновлено %s)\n\n' "$(date +%d.%m.%Y)"
printf 'Сгенерировано командой bash Kouki/tools/filemap.sh. Вручную не править.\n\n'

emit() {
    local repo="$1" title="$2"
    local dir="$WORKSPACE/$repo"
    [ -d "$dir/.git" ] || die "нет репозитория $dir"
    printf '## %s\n\n```\n' "$title"
    git -C "$dir" ls-files | LC_ALL=C sort
    printf '```\n\n'
}

emit Kouki 'Kouki (сервер .NET)'
emit kouki-station 'kouki-station (прошивка ESP32-S3)'
