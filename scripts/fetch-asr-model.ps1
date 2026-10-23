<#
    Downloads a speech recognition model for Kouki.Un (sherpa-onnx, Whisper).

    Run on a machine with internet access and tar (Windows 10+ ships tar.exe).
    The model is not stored in the repository: it is large and versioned separately.

    Examples:
        .\scripts\fetch-asr-model.ps1
        .\scripts\fetch-asr-model.ps1 -Model whisper-base -Target D:\kouki\models\asr
#>
[CmdletBinding()]
param(
    [ValidateSet('whisper-tiny', 'whisper-base', 'whisper-small')]
    [string]$Model = 'whisper-small',

    [string]$Target = (Join-Path (Split-Path -Parent $PSScriptRoot) 'models\asr')
)

$ErrorActionPreference = 'Stop'

$archive = "sherpa-onnx-$Model"
$url = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/$archive.tar.bz2"
$tarball = Join-Path $env:TEMP "$archive.tar.bz2"

New-Item -ItemType Directory -Path $Target -Force | Out-Null

Write-Host "Downloading $url"
Invoke-WebRequest -Uri $url -OutFile $tarball

Write-Host "Extracting into $Target"
tar -xjf $tarball -C $Target
Remove-Item $tarball -Force

$modelDirectory = Join-Path $Target $archive
Write-Host ''
Write-Host "Done. Model directory: $modelDirectory"
Write-Host 'Point the server at it:'
Write-Host "    `$env:Kouki__SpeechModelDirectory = '$modelDirectory'"
Write-Host 'and build with speech recognition enabled:'
Write-Host '    dotnet build -p:KoukiEnableSherpa=true'
