<#
.SYNOPSIS
Проверка и импорт разметки интентов — этап 1 (Windows-версия tools/intents.sh).

.DESCRIPTION
Внешний агент (Antigravity и т.п.) читает input.jsonl и пишет labeled.jsonl. Скрипт
проверяет файл, сравнивает метки с нашим rules+llm и возвращает метки в журнал сервера.

Чего здесь НЕ дублируем (аналоги уже есть в проекте — скрипт только склеивает файл и API):
  GET  /api/classifications            — журнал разбора (команда list_classifications);
  POST /api/classifications/{id}/label — метка (команда label_classification,
                                         MCP-инструмент kouki_label_classification);
  GET  /api/dataset                    — выгрузка размеченных в JSON Lines (export_dataset);
  POST /api/classify                   — вердикты классификаторов для одной фразы.

.EXAMPLE
.\tools\intents.ps1 check -Dir '\\192.168.1.157\files\_kouki\intents'

.EXAMPLE
.\tools\intents.ps1 compare -Dir '\\192.168.1.157\files\_kouki\intents' -Token kouki-demo

.EXAMPLE
.\tools\intents.ps1 import -Dir '\\192.168.1.157\files\_kouki\intents' -Token kouki-demo -DryRun
#>
[CmdletBinding()]
param(
    # Команда: check (без сети), compare (сверка с rules+llm), import (метки в журнал).
    [Parameter(Position = 0)]
    [ValidateSet('check', 'compare', 'import')]
    [string]$Command = 'check',

    # Каталог с labeled.jsonl.
    [string]$Dir = '.',

    # Адрес API.
    [string]$Server = 'http://127.0.0.1:5300',

    # Токен API; по умолчанию из KOUKI_TOKEN. Демо-токен прода — kouki-demo.
    [string]$Token = $env:KOUKI_TOKEN,

    # import: только показать, что отправили бы.
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# Интенты каталога (Kouki.Yasabi/LlmIntentClassifier.cs, DefaultIntents) плюс служебная
# метка unclassified.
$Known = @('add_task', 'list_tasks', 'complete_task', 'add_reminder', 'list_reminders',
    'complete_reminder', 'ask', 'chat')
$Allowed = $Known + 'unclassified'

$File = Join-Path $Dir 'labeled.jsonl'
if (-not (Test-Path $File)) {
    Write-Error "нет файла $File (внешний агент ещё не разметил?)" -ErrorAction Continue
    exit 1
}
if ($Command -ne 'check' -and -not $Token) {
    Write-Error "нужен -Token (или KOUKI_TOKEN): $Command ходит в API" -ErrorAction Continue
    exit 2
}

$Lines = Get-Content -Path $File -Encoding UTF8
$Rows = @()
$Bad = @()
$N = 0
foreach ($Line in $Lines) {
    if ([string]::IsNullOrWhiteSpace($Line)) { continue }
    $N++
    try { $O = $Line | ConvertFrom-Json } catch { $Bad += "строка ${N}: не JSON"; continue }
    if (-not $O.id) { $Bad += "строка ${N}: нет id"; continue }
    if (-not $O.text) { $Bad += "строка ${N}: нет text"; continue }
    if ($Allowed -notcontains $O.intent) { $Bad += "строка ${N} ($($O.id)): неизвестный интент '$($O.intent)'" }
    if ($null -eq $O.confidence) { $Bad += "строка ${N} ($($O.id)): нет confidence" }
    elseif ([double]$O.confidence -lt 0 -or [double]$O.confidence -gt 1) {
        $Bad += "строка ${N} ($($O.id)): confidence вне 0..1"
    }
    $Rows += $O
}

switch ($Command) {
    'check' {
        Write-Host "строк: $($Rows.Count), замечаний: $($Bad.Count)"
        $Rows | Group-Object intent | Sort-Object Count -Descending |
            ForEach-Object { Write-Host ("  {0,-18} {1}" -f $_.Name, $_.Count) }
        $Unclassified = @($Rows | Where-Object { $_.intent -eq 'unclassified' }).Count
        if ($Rows.Count -gt 0 -and ($Unclassified * 10) -gt $Rows.Count) {
            Write-Host '  внимание: unclassified больше 10% набора — это сигнал, а не норма' -ForegroundColor Yellow
        }
        if ($Bad.Count) {
            $Bad | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
            exit 1
        }
    }

    'compare' {
        $diff = 0
        foreach ($R in $Rows) {
            if (-not $R.text) { continue }
            $body = @{ text = $R.text; classifiers = @('rules+llm') } | ConvertTo-Json -Compress
            try {
                $resp = Invoke-WebRequest "$Server/api/classify" -Method Post -Body $body `
                    -ContentType 'application/json' -Headers @{ Authorization = "Bearer $Token" } `
                    -UseBasicParsing -TimeoutSec 30
                $json = [Text.Encoding]::UTF8.GetString($resp.RawContentStream.ToArray()) | ConvertFrom-Json
                $ours = (@($json.results) | Where-Object { $_.classifier -eq 'rules+llm' } |
                    Select-Object -First 1).intent
            }
            catch {
                Write-Host "  сверка недоступна: $($_.Exception.Message)" -ForegroundColor DarkGray
                break
            }
            if ($ours -ne $R.intent) {
                $diff++
                Write-Host ("  расход {0}: агент={1}, наш={2} — «{3}»" -f $R.id, $R.intent, $ours, $R.text) -ForegroundColor DarkYellow
            }
        }
        Write-Host "расхождений с rules+llm: $diff из $($Rows.Count)"
    }

    'import' {
        $ok = 0
        $failed = 0
        $skipped = 0
        foreach ($R in $Rows) {
            # unclassified — не обучающая метка: в датасет её нельзя, поэтому не импортируем.
            if ($Known -notcontains $R.intent) {
                $skipped++
                Write-Host "  пропуск $($R.id): '$($R.intent)' — не интент каталога" -ForegroundColor DarkGray
                continue
            }
            if ($DryRun) {
                Write-Host "  отправил бы $($R.id) → $($R.intent)"
                continue
            }
            $body = @{ label = $R.intent } | ConvertTo-Json -Compress
            try {
                $resp = Invoke-WebRequest "$Server/api/classifications/$($R.id)/label" -Method Post `
                    -Body $body -ContentType 'application/json' `
                    -Headers @{ Authorization = "Bearer $Token" } -UseBasicParsing -TimeoutSec 30
                if ($resp.StatusCode -eq 200) { $ok++ } else { $failed++ }
            }
            catch {
                $failed++
                Write-Host "  не принято $($R.id): $($_.Exception.Message)" -ForegroundColor Yellow
            }
        }
        if ($DryRun) {
            Write-Host "dry-run: ничего не отправлено, пропущено $skipped"
        }
        else {
            Write-Host "импортировано: $ok, не принято: $failed, пропущено: $skipped"
            if ($failed -gt 0) { exit 1 }
        }
    }
}

if ($Command -eq 'check' -and $Bad.Count -gt 0) { exit 1 }
