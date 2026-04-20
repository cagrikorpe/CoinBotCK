[CmdletBinding()]
param(
    [string]$RuntimeMarkerPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($RuntimeMarkerPath)) {
    $RuntimeMarkerPath = Join-Path $repoRoot "artifacts\phase11-soak-runtime.json"
}

if (-not (Test-Path -LiteralPath $RuntimeMarkerPath)) {
    Write-Output "FAIL blocker=MissingSoakMarker marker=$RuntimeMarkerPath"
    exit 1
}

$marker = Get-Content -LiteralPath $RuntimeMarkerPath -Raw | ConvertFrom-Json -ErrorAction Stop
$processIdText = if ($null -eq $marker.WorkerProcessId) { "" } else { [string]$marker.WorkerProcessId }

if ([string]::IsNullOrWhiteSpace($processIdText)) {
    $marker | Add-Member -NotePropertyName StoppedAtUtc -NotePropertyValue ([DateTime]::UtcNow.ToString("O")) -Force
    $marker | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $RuntimeMarkerPath -Encoding UTF8
    Write-Output "PASS blocker=none stopped=false reason=NoWorkerProcessInMarker marker=$RuntimeMarkerPath"
    exit 0
}

$workerProcessId = [int]$processIdText
$process = Get-Process -Id $workerProcessId -ErrorAction SilentlyContinue
if ($null -eq $process) {
    $marker | Add-Member -NotePropertyName StoppedAtUtc -NotePropertyValue ([DateTime]::UtcNow.ToString("O")) -Force
    $marker | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $RuntimeMarkerPath -Encoding UTF8
    Write-Output "PASS blocker=none stopped=false reason=ProcessAlreadyExited workerProcessId=$workerProcessId"
    exit 0
}

$expectedPath = if ($null -eq $marker.WorkerPath) { "" } else { [string]$marker.WorkerPath }
$actualPath = if ($null -eq $process.Path) { "" } else { [string]$process.Path }
if ($process.ProcessName -ne "CoinBot.Worker" -and -not ([string]::IsNullOrWhiteSpace($expectedPath) -or $actualPath -eq $expectedPath)) {
    Write-Output "FAIL blocker=WorkerProcessIdentityMismatch workerProcessId=$workerProcessId"
    exit 1
}

Stop-Process -Id $workerProcessId -Force
$marker | Add-Member -NotePropertyName StoppedAtUtc -NotePropertyValue ([DateTime]::UtcNow.ToString("O")) -Force
$marker | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $RuntimeMarkerPath -Encoding UTF8

Write-Output "PASS blocker=none stopped=true workerProcessId=$workerProcessId marker=$RuntimeMarkerPath"
