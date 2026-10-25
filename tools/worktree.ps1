<#
.SYNOPSIS
Работа с git worktree: отдельное дерево на задачу, чтобы не мешать основному.

.DESCRIPTION
Правило: одна задача — одна ветка — одно дерево. Деревья лежат вне репозиториев,
в <workspace>\_scratch\wt\<repo>-<task>, чтобы в корне папки не было мусора.

Примеры:
  .\tools\worktree.ps1 new -Repo kouki-station -Task T55 -Slug https-caddy
  .\tools\worktree.ps1 new -Repo Kouki -Task T50 -Slug backup-timer
  .\tools\worktree.ps1 list
  .\tools\worktree.ps1 remove -Name kouki-t50-backup-timer
  .\tools\worktree.ps1 merge -Name kouki-t50-backup-timer   # вливает ветку в master

.PARAMETER Command
new | list | remove | merge

.PARAMETER Repo
Имя каталога репозитория в workspace: Kouki или kouki-station.

.PARAMETER Task
Идентификатор задачи, например T50.

.PARAMETER Slug
Короткое имя ветки латиницей, например backup-timer.

.PARAMETER Name
Имя дерева (то, что печатает list) — для remove/merge.

.PARAMETER Base
От какой ветки ответвляться. По умолчанию master.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('new', 'list', 'remove', 'merge')]
    [string]$Command,

    [string]$Repo = '',
    [string]$Task = '',
    [string]$Slug = '',
    [string]$Name = '',
    [string]$Base = 'master'
)

$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$wtRoot = Join-Path $workspace '_scratch\wt'

function Get-Repos {
    Get-ChildItem $workspace -Directory |
        Where-Object { Test-Path (Join-Path $_.FullName '.git') } |
        ForEach-Object { $_.Name }
}

switch ($Command) {
    'new' {
        if ([string]::IsNullOrWhiteSpace($Repo) -or [string]::IsNullOrWhiteSpace($Task)) {
            throw 'Нужны -Repo и -Task (например: -Repo Kouki -Task T50 -Slug backup-timer)'
        }
        $repoPath = Join-Path $workspace $Repo
        if (-not (Test-Path $repoPath)) { throw "Нет такого репозитория: $repoPath" }

        if ([string]::IsNullOrWhiteSpace($Slug)) { $Slug = 'task' }
        $name = ('{0}-{1}-{2}' -f $Repo.ToLowerInvariant(), $Task.ToLowerInvariant(), $Slug).Replace('_', '-')
        $path = Join-Path $wtRoot $name
        if (Test-Path $path) { throw "Дерево уже есть: $path" }

        $branch = "task/$($Task.ToLowerInvariant())-$Slug"
        New-Item -ItemType Directory -Force -Path $wtRoot | Out-Null

        Push-Location $repoPath
        try {
            & git worktree add -b $branch $path $Base
            if ($LASTEXITCODE -ne 0) { throw "git worktree add завершился с кодом $LASTEXITCODE" }
        }
        finally {
            Pop-Location
        }

        Write-Host ''
        Write-Host "Дерево: $path" -ForegroundColor Green
        Write-Host "Ветка:  $branch (от $Base)"
        Write-Host "Тесты:  $Repo\tools\test.ps1 -Repo '$path'" -ForegroundColor DarkGray
        Write-Host "Новый каталог надо проинициализировать: dotnet restore <проект> -m:1 -nodeReuse:false"
    }

    'list' {
        foreach ($r in Get-Repos) {
            $repoPath = Join-Path $workspace $r
            Push-Location $repoPath
            try {
                Write-Host "== $r" -ForegroundColor Cyan
                & git worktree list
            }
            finally { Pop-Location }
        }
    }

    'remove' {
        if ([string]::IsNullOrWhiteSpace($Name)) { throw 'Нужен -Name (см. list)' }
        $path = Join-Path $wtRoot $Name
        if (-not (Test-Path $path)) { throw "Нет такого дерева: $path" }

        $repo = Get-Repos | Where-Object { $Name.ToLowerInvariant().StartsWith($_.ToLowerInvariant() + '-') } | Select-Object -First 1
        if (-not $repo) { throw "Не понял, из какого репозитория дерево $Name — удалите через git worktree remove вручную" }

        Push-Location (Join-Path $workspace $repo)
        try {
            & git worktree remove --force $path
            if ($LASTEXITCODE -ne 0) { throw "git worktree remove завершился с кодом $LASTEXITCODE" }
            Write-Host "Дерево удалено: $path" -ForegroundColor Green
            Write-Host 'Ветка осталась — удалить: git branch -D task/<...>' -ForegroundColor DarkGray
        }
        finally { Pop-Location }
    }

    'merge' {
        if ([string]::IsNullOrWhiteSpace($Name)) { throw 'Нужен -Name (см. list)' }
        $repo = Get-Repos | Where-Object { $Name.ToLowerInvariant().StartsWith($_.ToLowerInvariant() + '-') } | Select-Object -First 1
        if (-not $repo) { throw "Не понял, из какого репозитория дерево $Name" }
        $repoPath = Join-Path $workspace $repo

        Push-Location $repoPath
        try {
            $branch = (git -C (Join-Path $wtRoot $Name) branch --show-current).Trim()
            if (-not $branch) { throw 'Не определил ветку дерева' }
            $dirty = git status --porcelain
            if ($dirty) { throw 'Основное дерево грязное — сначала закоммитьте или спрячьте изменения' }

            & git merge --no-ff $branch -m "merge: $branch"
            if ($LASTEXITCODE -ne 0) { throw "merge завершился с кодом $LASTEXITCODE (разрешите конфликты вручную)" }
            Write-Host "Ветка $branch влита в $(git branch --show-current)" -ForegroundColor Green
        }
        finally { Pop-Location }
    }
}
