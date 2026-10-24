<#
.SYNOPSIS
    Собирает Kagedama.api с распознаванием речи и запускает сервер.

.DESCRIPTION
    Зачем отдельный скрипт: флаг -p:KoukiEnableSherpa=true нужен и для restore,
    и для build. Если после сборки с распознаванием запустить обычный
    `dotnet restore`/`dotnet build` (например, для тестов), assets перезапишутся
    и в сборке снова окажется заглушка — станция молча получает 503. Скрипт
    делает обе операции с флагом и запускает сервер.

.EXAMPLE
    .\tools\run-server.ps1 -Token $env:KOUKI_TOKEN -ModelDirectory ..\models\asr\sherpa-onnx-whisper-small

.EXAMPLE
    .\tools\run-server.ps1 -NoSpeech -Port 5301   # без распознавания, только API
#>
[CmdletBinding()]
param(
    [string] $Port = '5300',
    # Токен не хранится в репозитории: -Token или переменная окружения KOUKI_TOKEN
    [string] $Token = $env:KOUKI_TOKEN,
    [string] $DatabasePath,
    [string] $AudioDirectory,
    [string] $ModelDirectory = 'models\asr\sherpa-onnx-whisper-small',
    [string] $Language = 'ru',
    [switch] $NoSpeech,
    [switch] $NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'Kagedama.api\Kagedama.api.csproj'
$apiDir = Join-Path $repo 'Kagedama.api\bin\Debug\net10.0'
$sherpaArg = if ($NoSpeech) { '' } else { '-p:KoukiEnableSherpa=true' }

if (-not $Token) {
    throw 'Токен не задан: передайте -Token или задайте переменную окружения KOUKI_TOKEN.'
}

if (-not $NoBuild) {
    Write-Host "сборка: $project $sherpaArg"

    $restoreArgs = @('restore', $project, '-m:1', '-nodeReuse:false')
    if ($sherpaArg) { $restoreArgs += $sherpaArg }
    & dotnet @restoreArgs
    if ($LASTEXITCODE -ne 0) { throw "restore завершился с кодом $LASTEXITCODE" }

    $buildArgs = @('build', $project, '--no-restore', '-m:1', '-nodeReuse:false')
    if ($sherpaArg) { $buildArgs += $sherpaArg }
    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "build завершился с кодом $LASTEXITCODE" }
}

if (-not $DatabasePath) { $DatabasePath = Join-Path $repo 'kouki.sqlite' }
if (-not $AudioDirectory) { $AudioDirectory = Join-Path $repo 'audio' }

$env:Kouki__Token = $Token
$env:Kouki__DatabasePath = [IO.Path]::GetFullPath($DatabasePath)
$env:Kouki__AudioDirectory = [IO.Path]::GetFullPath($AudioDirectory)
$env:Kouki__SpeechLanguage = $Language
if (-not $NoSpeech) {
    $resolved = $ModelDirectory
    if (-not [IO.Path]::IsPathRooted($resolved)) { $resolved = Join-Path $repo $ModelDirectory }
    $env:Kouki__SpeechModelDirectory = [IO.Path]::GetFullPath($resolved)
    if (-not (Test-Path -LiteralPath $env:Kouki__SpeechModelDirectory)) {
        Write-Warning "каталога модели нет: $($env:Kouki__SpeechModelDirectory) — скачайте scripts/fetch-asr-model.ps1"
    }
}

Write-Host "запуск: http://0.0.0.0:$Port (токен задан, БД $($env:Kouki__DatabasePath))"
Set-Location -LiteralPath $apiDir
& dotnet Kagedama.api.dll --urls "http://0.0.0.0:$Port"