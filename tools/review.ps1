<#
.SYNOPSIS
Готовит пакет для ревью изменений перед пушем.

.DESCRIPTION
Ревью делает отдельный агент (не автор правок) и только по пакету: спека задачи,
диф, список файлов, тесты. Скрипт собирает этот пакет в один Markdown-файл, чтобы
ревьюер не читал весь репозиторий и не «додумывал» контекст.

Пакет содержит:
  - задачу из notes-updated\Задачи Kouki.md (если указан -Task);
  - базу сравнения и список коммитов;
  - статистику и полный диф (с лимитом строк);
  - чек-лист инвариантов из AGENTS.md;
  - шаблон вердикта (blockers / major / minor / nit + «можно пушить»).

.EXAMPLE
.\tools\review.ps1 -Task T73

.EXAMPLE
.\tools\review.ps1 -Task T87 -Base master -MaxDiffLines 3000
#>
[CmdletBinding()]
param(
    # Каталог репозитория (по умолчанию — корень, где лежит tools\).
    [string]$Repo = '',

    # Id задачи из notes-updated\Задачи Kouki.md, например T85.
    [string]$Task = '',

    # С чем сравнивать: master, origin/master или конкретный коммит.
    [string]$Base = 'master',

    # Ветка или ревизия с правками (по умолчанию — текущая HEAD).
    [string]$Head = 'HEAD',

    # Сколько строк дифа вкладывать в пакет (хвост обрезается с пометкой).
    [int]$MaxDiffLines = 4000,

    # Файл задач; по умолчанию ищется вверх от репозитория (notes-updated\Задачи Kouki.md).
    [string]$Notes = '',

    # Куда положить пакет; по умолчанию <workspace>\_scratch\review\<дата>-<задача>.md.
    [string]$Out = ''
)

$ErrorActionPreference = 'Stop'

if ($MaxDiffLines -lt 1) {
    Write-Host "Число строк дифа должно быть положительным, а не $MaxDiffLines" -ForegroundColor Red
    exit 2
}

if ([string]::IsNullOrWhiteSpace($Task)) {
    Write-Host 'Нужен -Task Txx. Справка: Get-Help .\tools\review.ps1 -Detailed' -ForegroundColor Red
    exit 2
}

# $PSScriptRoot в значениях параметров задаётся не всегда (pwsh -File), поэтому
# каталог репозитория вычисляется уже после старта скрипта.
if ([string]::IsNullOrWhiteSpace($Repo)) {
    $Repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

# Рабочие деревья задач живут в <workspace>\_scratch\wt\<repo>-<task>, поэтому notes
# и каталог пакета нельзя брать от родителя репозитория — ищем корень воркспейса вверх.
function Get-WorkspaceRoot {
    param([string]$From)
    $dir = (Resolve-Path -LiteralPath $From).Path
    while ($dir) {
        if ((Test-Path -LiteralPath (Join-Path $dir 'notes-updated')) -or
            (Test-Path -LiteralPath (Join-Path $dir '_scratch'))) {
            return $dir
        }
        $parent = Split-Path -Parent $dir
        if (-not $parent -or $parent -eq $dir) { break }
        $dir = $parent
    }
    return (Split-Path -Parent $From)
}

function Invoke-Git {
    param([string[]]$Arguments)
    $output = & git -C $Repo @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') — код $LASTEXITCODE`n$output" }
    return ($output | Out-String).TrimEnd()
}

function Get-TaskSection {
    param([string]$Id)
    if ([string]::IsNullOrWhiteSpace($Id)) { return '' }
    $notes = if ([string]::IsNullOrWhiteSpace($Notes)) {
        Join-Path (Get-WorkspaceRoot -From $Repo) 'notes-updated\Задачи Kouki.md'
    } else {
        $Notes
    }
    if (-not (Test-Path -LiteralPath $notes)) { return "_(файл задач не найден: $notes)_" }
    $lines = Get-Content -LiteralPath $notes -Encoding utf8
    $hit = $lines | Where-Object { $_ -match "^\|\s*$([regex]::Escape($Id))\s*\|" } | Select-Object -First 1
    if (-not $hit) { return "_(в заметках нет строки задачи $Id)_" }
    return $hit.Trim()
}

foreach ($ref in @($Base, $Head)) {
    # Нативные команды пишут в stderr, а при $ErrorActionPreference = 'Stop' это
    # становится завершающей ошибкой: на время проверки ревизии глушим строгий режим,
    # иначе для `-Base 'HEAD^{tree}'` скрипт падал кодом 1 вместо понятного сообщения.
    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & git -C $Repo rev-parse --verify --quiet "$ref^{commit}" 2>$null | Out-Null
    $verifyCode = $LASTEXITCODE
    $ErrorActionPreference = $savedPreference
    if ($verifyCode -ne 0) {
        Write-Host "Неизвестная ревизия: $ref" -ForegroundColor Red
        exit 2
    }
}

$branch = Invoke-Git @('rev-parse', '--abbrev-ref', 'HEAD')
$headSha = Invoke-Git @('rev-parse', '--short', $Head)
$baseSha = Invoke-Git @('rev-parse', '--short', $Base)
$baseFull = (Invoke-Git @('rev-parse', "$Base^{commit}")).Trim()
$headFull = (Invoke-Git @('rev-parse', "$Head^{commit}")).Trim()
$commits = Invoke-Git @('log', '--oneline', "$Base..$Head")
$stat = Invoke-Git @('diff', '--stat', "$Base..$Head")
$nameStatus = Invoke-Git @('diff', '--name-status', "$Base..$Head")
$diff = Invoke-Git @('diff', "$Base..$Head")
$raw = Invoke-Git @('-c', 'core.quotePath=false', 'diff', '--raw', '--abbrev=10', "$Base..$Head")

# Отпечаток набора изменений: sha256 по `git diff --raw` (blob-хеши «было→стало» и пути).
# Тот же алгоритм в tools/review.sh — отпечатки совпадают на одной и той же паре коммитов.
$fpInput = (($raw -split "`n") | ForEach-Object { $_.TrimEnd("`r") } |
    Where-Object { $_.Trim().Length -gt 0 } | Sort-Object) -join "`n"
$sha256 = [System.Security.Cryptography.SHA256]::Create()
$fp = ([System.BitConverter]::ToString(
        $sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($fpInput))
    ) -replace '-', '').ToLowerInvariant().Substring(0, 16)

$fileRows = if ([string]::IsNullOrWhiteSpace($raw)) {
    '| _(нет изменений)_ | — | — | — |'
} else {
    (($raw -split "`n") | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object {
        $line = $_.TrimEnd("`r")
        $meta, $path = $line -split "`t", 2
        $m = $meta -split '\s+'
        $oldHash = if ($m[2] -match '^0+$') { '—' } else { $m[2].Substring(0, [Math]::Min(10, $m[2].Length)) }
        $newHash = if ($m[3] -match '^0+$') { '—' } else { $m[3].Substring(0, [Math]::Min(10, $m[3].Length)) }
        $status = $m[4].Substring(0, 1)
        if ($status -eq 'R' -or $status -eq 'C') {
            # У переименования в строке --raw два пути через таб: старое → новое.
            $paths = $path -split "`t"
            if ($paths.Count -ge 2) { $path = "$($paths[0]) → $($paths[1])" }
        }
        "| ``$path`` | $status | $oldHash | $newHash |"
    }) -join "`n"
}

$diffLines = ($diff -split "`n")
$truncated = $false
if ($diffLines.Count -gt $MaxDiffLines) {
    $diff = ($diffLines[0..($MaxDiffLines - 1)] -join "`n")
    $truncated = $true
}

$taskLine = Get-TaskSection -Id $Task
$stamp = Get-Date -Format 'yyyy-MM-dd-HHmmss'
if ([string]::IsNullOrWhiteSpace($Out)) {
    $leaf = if ($Task) { $Task } else { 'review' }
    $dir = Join-Path (Get-WorkspaceRoot -From $Repo) '_scratch\review'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $Out = Join-Path $dir "$stamp-$leaf.md"
} else {
    $parent = Split-Path -Parent $Out
    if ($parent -and -not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }
}

$diffBlock = if ($diff.Trim().Length -eq 0) { '_(изменений нет)_' } else { "``````diff`n$diff`n``````" }
$truncNote = if ($truncated) { "`n> Диф обрезан до $MaxDiffLines строк — остальное смотрите командой ``git diff $Base..$Head``.`n" } else { '' }

$title = if ([string]::IsNullOrWhiteSpace($Task)) { $branch } else { $Task }

$packet = @"
# Ревью перед пушем — $title

Собрано: $(Get-Date -Format 'yyyy-MM-dd HH:mm') · ветка ``$branch`` · $baseSha..$headSha

## Задача

$taskLine

## Что сравниваем

| Что | Значение |
| --- | --- |
| База | ``$Base`` ($baseSha, ``$baseFull``) |
| Голова | ``$Head`` ($headSha, ``$headFull``) |
| Ветка | ``$branch`` |
| **Отпечаток** | ``$fp`` (sha256 по ``git diff --raw``, 16 символов) |

> Повторное ревью запрашивайте по отпечатку: если ``$fp`` не изменился,
> набор изменений тот же, даже если пакет собран заново.

### Коммиты

``````
$commits
``````

### Файлы и blob-хеши

| Файл | Статус | Было | Стало |
| --- | --- | --- | --- |
$fileRows

### Список файлов

``````
$nameStatus
``````

### Сводка

``````
$stat
``````

## Диф
$truncNote
$diffBlock

## Чек-лист ревьюера

Проверять по порядку, нарушение — блокер.

1. **Границы слоёв.** ``Contracts`` без зависимостей; ``Mugura`` без предметных типов;
   хост не содержит бизнес-логики; новых ``ProjectReference`` в ядро нет.
2. **Инварианты ``AGENTS.md`` (1–14).** Задачу создаёт только ``TaskFactory``; источник —
   ``TaskSources``; статусы — ``TaskStatuses``; вердикт классификатора не перезаписывается
   (есть ``hinted_intent``); многотабличная запись — в ``IUnitOfWork``; DI — через
   ``KoukiHosting``; чтение/запись — командами (кроме ``GET /api/tasks/{id}`` с комментарием);
   токен fail-closed; ``[Command]`` на обработчике; фразу не режем; магических чисел нет;
   пользователю — человеческий текст; правила приёма — в ``IntakeRequestPolicy``; версии —
   в ``Directory.Packages.props``.
3. **Тесты.** Новое поведение покрыто; тесты падали до правки; нет ``[Theory]``, ``Skip``,
   закомментированных проверок и ``catch { }`` без лога.
4. **Ошибки и тексты.** Наружу не уходит ``ex.Message``; у отказов есть код и человеческое
   сообщение; логи содержат контекст, но не секреты.
5. **Доки и заметки.** Если менялись контракты — обновлены ``notes-updated`` (архитектура,
   датафлоу) и ``Задачи Kouki.md`` со статусом.
6. **Секреты и мусор.** Нет токенов/паролей в диффе, нет ``bin/obj``, ``*.sqlite``,
   ``station-audio``, ``_scratch``-файлов, нет «заодно» правок вне задачи.
7. **Доказательства.** В отчёте по задаче есть команда + вывод/цифра на каждый пункт
   «сделано», а не «работает».

## Шаблон вердикта

``````
Вердикт: можно пушить | нужны правки

Blockers (пушить нельзя):
- файл:строка — что не так, почему это нарушение (инвариант/контракт), что сделать

Major (пушить можно после правки в этом же PR):
- …

Minor / nit (можно отдельной задачей):
- …

Что проверено и в порядке:
- …

Открытые вопросы к автору:
- …
``````
"@

[IO.File]::WriteAllText($Out, $packet, (New-Object Text.UTF8Encoding($false)))
Write-Host "Пакет для ревью: $Out · отпечаток $fp"
$filesCount = if ([string]::IsNullOrWhiteSpace($nameStatus)) { 0 } else { ($nameStatus -split "`n").Length }
$diffCount = if ([string]::IsNullOrWhiteSpace($diff)) { 0 } else { $diffLines.Length }
Write-Host ("Файлов в дифе: {0} · строк дифа: {1}{2}" -f $filesCount, $diffCount, ($(if ($truncated) { ' (обрезано)' } else { '' })))
