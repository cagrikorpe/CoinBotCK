[CmdletBinding()]
param(
    [string]$Server = "localhost",
    [string]$Database = "CoinBotDb",
    [string]$UserId = "5a8675cf-cbcb-4c28-82fb-304aee442895",
    [guid]$BotId = "8EA3ED8B-C61F-4A44-A683-168AE9C69C43",
    [guid]$StrategyId = "ECF74430-966C-49CA-8EC9-7F31E8F63350",
    [string]$Symbol = "SOLUSDT",
    [string]$FuturesRestBaseUrl = "https://testnet.binancefuture.com",
    [string]$FuturesWebSocketBaseUrl = "wss://fstream.binancefuture.com",
    [string]$RuntimeMarkerPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($RuntimeMarkerPath)) {
    $RuntimeMarkerPath = Join-Path $repoRoot "artifacts\phase10d-worker-runtime.json"
}

$phase10eScript = Join-Path $PSScriptRoot "phase10e-exercise-non-demo-dispatch.ps1"
if (-not (Test-Path -LiteralPath $phase10eScript)) {
    throw "phase10e deterministic dispatch script was not found."
}

$guardOverrides = [ordered]@{
    ExecutionSafety__DataLatencyGuard__StaleDataThresholdSeconds = "60"
    ExecutionSafety__DataLatencyGuard__StopDataThresholdSeconds = "120"
    ExecutionSafety__DataLatencyGuard__ClockDriftThresholdSeconds = "5"
}

$previousValues = @{}
foreach ($key in $guardOverrides.Keys) {
    $previousValues[$key] = [Environment]::GetEnvironmentVariable($key, "Process")
    [Environment]::SetEnvironmentVariable($key, [string]$guardOverrides[$key], "Process")
}

try {
    & $phase10eScript `
        -Server $Server `
        -Database $Database `
        -UserId $UserId `
        -BotId $BotId `
        -StrategyId $StrategyId `
        -Symbol $Symbol `
        -FuturesRestBaseUrl $FuturesRestBaseUrl `
        -FuturesWebSocketBaseUrl $FuturesWebSocketBaseUrl `
        -RuntimeMarkerPath $RuntimeMarkerPath
}
finally {
    foreach ($key in $previousValues.Keys) {
        [Environment]::SetEnvironmentVariable($key, $previousValues[$key], "Process")
    }
}

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
