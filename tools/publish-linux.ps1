<#
.SYNOPSIS
Публикует сервер Kouki для Linux (framework-dependent, с нативными библиотеками).

.DESCRIPTION
Собирает Kagedama.api под указанный RID и проверяет, что в каталоге оказались
нативные библиотеки, статика дашборда и конфигурация. Модели ASR/TTS в пакет
не входят: они качаются скриптами и лежат отдельно (см. notes-updated\Перенос на сервер.md).

.PARAMETER Runtime
RID публикации. По умолчанию linux-x64.

.PARAMETER Output
Каталог результата. По умолчанию <корень репозитория>\publish\<RID>.

.PARAMETER NoSpeech
Собрать без sherpa-onnx (сервер без распознавания речи, /api/voice отвечает 503).

.EXAMPLE
.\tools\publish-linux.ps1
.EXAMPLE
.\tools\publish-linux.ps1 -Runtime linux-arm64 -Output \\nas\kouki\publish
#>
[CmdletBinding()]
param(
    [string]$Runtime = 'linux-x64',
    [string]$Output = '',
    [switch]$NoSpeech
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'Kagedama.api\Kagedama.api.csproj'
if (-not (Test-Path $project)) { throw "Не найден проект: $project" }

if ([string]::IsNullOrWhiteSpace($Output)) { $Output = Join-Path $root "publish\$Runtime" }
if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Output | Out-Null

$speech = if ($NoSpeech) { 'false' } else { 'true' }
$publishArgs = @(
    'publish', $project,
    '-c', 'Release',
    "-p:KoukiEnableSherpa=$speech",
    '-r', $Runtime,
    '--self-contained', 'false',
    '-m:1', '-nodeReuse:false',
    '-o', $Output
)

Write-Host "Публикация $Runtime (sherpa=$speech) -> $Output" -ForegroundColor Cyan
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с кодом $LASTEXITCODE" }

$required = @(
    'Kagedama.api.dll',
    'appsettings.json',
    'wwwroot\app.js',
    'webroot-placeholder'
) | Where-Object { $_ -ne 'webroot-placeholder' }

$missing = @()
foreach ($name in $required) {
    if (-not (Test-Path (Join-Path $Output $name))) { $missing += $name }
}
if (-not $NoSpeech) {
    $natives = @(Get-ChildItem $Output -Recurse -File -Filter '*.so' | ForEach-Object { $_.Name })
    foreach ($name in 'libonnxruntime.so', 'libsherpa-onnx-c-api.so') {
        if ($natives -notcontains $name) { $missing += $name }
    }
}
$sqlite = @(Get-ChildItem $Output -Recurse -File -Filter 'libe_sqlite3*' )
if ($sqlite.Count -eq 0) { $missing += 'libe_sqlite3.so' }

if ($missing.Count -gt 0) { throw "В публикации не хватает: $($missing -join ', ')" }

$files = Get-ChildItem $Output -Recurse -File
$sizeMb = [math]::Round((($files | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host ("Готово: {0} файлов, {1} МБ" -f $files.Count, $sizeMb) -ForegroundColor Green
Write-Host "На сервере: dotnet Kagedama.api.dll --urls http://0.0.0.0:5300" -ForegroundColor DarkGray