<#
    Downloads a Russian text-to-speech model for Kouki.SpeechLab (sherpa-onnx, VITS/Piper).

    Нужен только для проверок: фразу синтезируем, прогоняем через тот же распознаватель,
    что стоит в сервере, и сравниваем текст. Микрофон и запись не требуются.
    Модель в репозиторий не кладётся: она большая и версионируется отдельно.

    Examples:
        .\scripts\fetch-tts-model.ps1
        .\scripts\fetch-tts-model.ps1 -Voice ru_RU-denis-medium -Target D:\kouki\models\tts
#>
[CmdletBinding()]
param(
    [ValidateSet(
        'ru_RU-irina-medium',
        'ru_RU-ruslan-medium',
        'ru_RU-dmitri-medium',
        'ru_RU-denis-medium')]
    [string]$Voice = 'ru_RU-irina-medium',

    [string]$Target = (Join-Path (Split-Path -Parent $PSScriptRoot) 'models\tts')
)

$ErrorActionPreference = 'Stop'

$archive = "vits-piper-$Voice"
$url = "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/$archive.tar.bz2"
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
Write-Host 'Build the lab with speech enabled:'
Write-Host '    dotnet build tools\Kouki.SpeechLab -p:KoukiEnableSherpa=true'
Write-Host 'and run a round trip:'
Write-Host '    dotnet tools\Kouki.SpeechLab\bin\Debug\net10.0\Kouki.SpeechLab.dll roundtrip "Запиши задачу купить хлеб"'
