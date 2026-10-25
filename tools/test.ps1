<#
.SYNOPSIS
Гоняет тесты Kouki.Tests из указанного checkout'а (обычного или git worktree).

.DESCRIPTION
VSTest-хост в ограниченном окружении не стартует (OpenProcess → access denied),
поэтому тесты запускает самописный раннер: он ссылается на проект Kouki.Tests,
и путь к этому проекту можно переназначить свойством KoukiTestsProject.

Порядок: restore → build тестов → build раннера под нужный checkout → прогон.

.PARAMETER Repo
Корень репозитория Kouki. По умолчанию — каталог, в котором лежит tools\.

.PARAMETER Runner
Путь к проекту раннера. По умолчанию <workspace>\_scratch\testrun\testrun.csproj.

.PARAMETER NoRestore
Не делать restore (быстрее, когда пакеты уже на месте).

.EXAMPLE
.\tools\test.ps1
.EXAMPLE
.\tools\test.ps1 -Repo '..\_scratch\wt\kouki-t50\Kouki'
#>
[CmdletBinding()]
param(
    [string]$Repo = '',
    [string]$Runner = '',
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Repo)) {
    $Repo = Split-Path -Parent $PSScriptRoot
}
$Repo = (Resolve-Path $Repo).Path

# Воркспейс ищем вверх от checkout'а: дерево задачи лежит в _scratch\wt\<name>,
# и «просто родитель каталога» там уже не воркспейс.
$workspace = $Repo
for ($i = 0; $i -lt 6 -and $workspace; $i++) {
    if (Test-Path (Join-Path $workspace '_scratch\testrun\testrun.csproj')) { break }
    $workspace = Split-Path -Parent $workspace
}
if (-not $workspace) { throw "Не нашёл воркспейс с _scratch\testrun выше $Repo" }

if ([string]::IsNullOrWhiteSpace($Runner)) {
    $Runner = Join-Path $workspace '_scratch\testrun\testrun.csproj'
}
if (-not (Test-Path $Runner)) { throw "Не найден раннер: $Runner" }

$tests = Join-Path $Repo 'Kouki.Tests\Kouki.Tests.csproj'
if (-not (Test-Path $tests)) { throw "Не найден проект тестов: $tests" }

$msbuild = @('-m:1', '-nodeReuse:false')

if (-not $NoRestore) {
    Write-Host "restore $Repo" -ForegroundColor DarkGray
    & dotnet restore $tests @msbuild
    if ($LASTEXITCODE -ne 0) { throw "restore завершился с кодом $LASTEXITCODE" }
}

Write-Host "build тестов" -ForegroundColor DarkGray
& dotnet build $tests --no-restore @msbuild
if ($LASTEXITCODE -ne 0) { throw "сборка тестов завершилась с кодом $LASTEXITCODE" }

# Раннер собираем в отдельный каталог на каждый checkout: иначе его bin держит
# ссылку на предыдущий Kouki.Tests и тесты «из другого дерева» не подхватятся.
$runnerOut = Join-Path $workspace ("_scratch\testrun\out\" + (Split-Path -Leaf $Repo))
if (Test-Path $runnerOut) { Remove-Item $runnerOut -Recurse -Force }
New-Item -ItemType Directory -Force -Path $runnerOut | Out-Null

Write-Host "build раннера под $Repo" -ForegroundColor DarkGray
# -t:Rebuild обязателен: obj у раннера общий, и без пересборки он может
# остаться с ссылкой на Kouki.Tests предыдущего checkout'а.
& dotnet build $Runner @msbuild -t:Rebuild `
    "-p:KoukiTestsProject=$tests" `
    "-p:OutputPath=$runnerOut"
if ($LASTEXITCODE -ne 0) { throw "сборка раннера завершилась с кодом $LASTEXITCODE" }

$dll = Join-Path $runnerOut 'Kouki.Tests.Runner.dll'
if (-not (Test-Path $dll)) { throw "Не нашёл $dll" }

Write-Host "прогон $dll" -ForegroundColor DarkGray
& dotnet $dll
exit $LASTEXITCODE
