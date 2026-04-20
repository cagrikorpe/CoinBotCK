[CmdletBinding()]
param(
    [string]$SummaryPath = "",
    [int]$SummaryFreshnessMinutes = 15
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($SummaryPath)) {
    $SummaryPath = Join-Path $repoRoot "artifacts\phase11-soak-summary.json"
}

function Write-Fail {
    param(
        [Parameter(Mandatory = $true)][string]$Blocker,
        [object]$Summary = $null
    )

    $suffix = ""
    if ($null -ne $Summary) {
        $suffix = " soakId=$($Summary.SoakId) entryFilled=$($Summary.TotalEntryFilled) closeFilled=$($Summary.TotalReduceOnlyCloseFilled) stuckSubmitted=$($Summary.StuckSubmittedCount) counterParityMismatch=$($Summary.CounterParityMismatchCount) currentBotOpenPositionCount=$($Summary.CurrentBotOpenPositionCount) currentBotOpenOrderCount=$($Summary.CurrentBotOpenOrderCount) currentExchangeNetQuantity=$($Summary.CurrentExchangeNetQuantity) currentDbNetQuantity=$($Summary.CurrentDbNetQuantity) latestEnvironment=$($Summary.LatestEnvironment) latestExecutorKind=$($Summary.LatestExecutorKind)"
    }

    Write-Output "FAIL blocker=$Blocker$suffix"
    exit 1
}

function Write-Pass {
    param([Parameter(Mandatory = $true)][object]$Summary)

    Write-Output "PASS blocker=none soakId=$($Summary.SoakId) entryDispatched=$($Summary.TotalEntryDispatched) entryFilled=$($Summary.TotalEntryFilled) closeDispatched=$($Summary.TotalReduceOnlyCloseDispatched) closeFilled=$($Summary.TotalReduceOnlyCloseFilled) rejectedFailedCancelled=$($Summary.TotalRejectedFailedCancelled) stuckSubmitted=$($Summary.StuckSubmittedCount) reconciliationDrift=$($Summary.ReconciliationDriftCount) counterParityMismatch=$($Summary.CounterParityMismatchCount) privatePlaneStale=$($Summary.PrivatePlaneStaleCount) marketDataStale=$($Summary.MarketDataStaleFailureCount) avgEntryFillLatencySeconds=$($Summary.AverageEntryFillLatencySeconds) avgCloseFillLatencySeconds=$($Summary.AverageCloseFillLatencySeconds) maxOpenOrderDurationSeconds=$($Summary.MaxOpenOrderDurationSeconds) maxPositionOpenDurationSeconds=$($Summary.MaxPositionOpenDurationSeconds) lastSuccessfulCycleAtUtc=$($Summary.LastSuccessfulCycleAtUtc) currentBotOpenPositionCount=$($Summary.CurrentBotOpenPositionCount) currentBotOpenOrderCount=$($Summary.CurrentBotOpenOrderCount) currentExchangeNetQuantity=$($Summary.CurrentExchangeNetQuantity) currentDbNetQuantity=$($Summary.CurrentDbNetQuantity) latestEnvironment=$($Summary.LatestEnvironment) latestExecutorKind=$($Summary.LatestExecutorKind)"
    exit 0
}

if (-not (Test-Path -LiteralPath $SummaryPath)) {
    Write-Fail -Blocker "MissingSoakSummary"
}

$summary = Get-Content -LiteralPath $SummaryPath -Raw | ConvertFrom-Json -ErrorAction Stop
$generatedAtUtc = [DateTime]::Parse([string]$summary.GeneratedAtUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
if ($generatedAtUtc -lt [DateTime]::UtcNow.AddMinutes(-1 * $SummaryFreshnessMinutes)) {
    Write-Fail -Blocker "StaleSoakSummary" -Summary $summary
}

if ([string]$summary.LatestEnvironment -ne "Live" -or [string]$summary.LatestExecutorKind -ne "Binance") {
    Write-Fail -Blocker "NoLiveBinanceSoakEvidence" -Summary $summary
}

if ([int]$summary.StuckSubmittedCount -ne 0) {
    Write-Fail -Blocker "StuckSubmittedOrders" -Summary $summary
}

if ([int]$summary.CounterParityMismatchCount -ne 0) {
    Write-Fail -Blocker "CounterParityMismatch" -Summary $summary
}

if ([int]$summary.PrivatePlaneStaleCount -ne 0) {
    Write-Fail -Blocker "PrivatePlaneStale" -Summary $summary
}

if ([decimal]$summary.CurrentExchangeNetQuantity -ne [decimal]$summary.CurrentDbNetQuantity) {
    Write-Fail -Blocker "ExchangeDbPositionMismatch" -Summary $summary
}

if ([int]$summary.CurrentBotOpenOrderCount -ne [int]$summary.CurrentActualOpenOrderCount) {
    Write-Fail -Blocker "OpenOrderCounterMismatch" -Summary $summary
}

$expectedOpenPositionCount = if ([decimal]$summary.CurrentDbNetQuantity -eq [decimal]0) { 0 } else { [int]$summary.CurrentDbOpenPositionCount }
if ([int]$summary.CurrentBotOpenPositionCount -ne $expectedOpenPositionCount) {
    Write-Fail -Blocker "OpenPositionCounterMismatch" -Summary $summary
}

Write-Pass -Summary $summary
