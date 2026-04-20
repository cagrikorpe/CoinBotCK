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

$phase10fScript = Join-Path $PSScriptRoot "phase10f-exercise-exchange-readiness.ps1"
if (-not (Test-Path -LiteralPath $phase10fScript)) {
    throw "phase10f exchange readiness exercise script was not found."
}

& $phase10fScript `
    -Server $Server `
    -Database $Database `
    -UserId $UserId `
    -BotId $BotId `
    -StrategyId $StrategyId `
    -Symbol $Symbol `
    -FuturesRestBaseUrl $FuturesRestBaseUrl `
    -FuturesWebSocketBaseUrl $FuturesWebSocketBaseUrl `
    -RuntimeMarkerPath $RuntimeMarkerPath

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
