[CmdletBinding()]
param(
    [string]$Server = "localhost",
    [string]$Database = "CoinBotDb",
    [string]$RuntimeMarkerPath = "",
    [string]$PositionTruthEvidencePath = "",
    [string]$SummaryPath = "",
    [int]$StuckOrderThresholdMinutes = 5,
    [int]$FreshnessThresholdSeconds = 900
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($RuntimeMarkerPath)) {
    $RuntimeMarkerPath = Join-Path $repoRoot "artifacts\phase11-soak-runtime.json"
}
if ([string]::IsNullOrWhiteSpace($PositionTruthEvidencePath)) {
    $PositionTruthEvidencePath = Join-Path $repoRoot "artifacts\phase10k-position-truth.json"
}
if ([string]::IsNullOrWhiteSpace($SummaryPath)) {
    $SummaryPath = Join-Path $repoRoot "artifacts\phase11-soak-summary.json"
}

function Assert-SqlCmd {
    if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
        throw "sqlcmd was not found."
    }
}

function Escape-SqlLiteral {
    param([Parameter(Mandatory = $true)][string]$Value)
    return $Value.Replace("'", "''")
}

function Get-SqlJsonPayload {
    param([Parameter(Mandatory = $true)][object[]]$Output)
    $lines = @($Output | ForEach-Object { [string]$_ })
    $beginIndex = -1
    $endIndex = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match "__JSON_BEGIN__") { $beginIndex = $i; continue }
        if ($lines[$i] -match "__JSON_END__") { $endIndex = $i; break }
    }
    if ($beginIndex -lt 0 -or $endIndex -le $beginIndex) { return $null }
    $json = (($lines[($beginIndex + 1)..($endIndex - 1)] | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join [Environment]::NewLine).Trim()
    if ([string]::IsNullOrWhiteSpace($json)) { return $null }
    return $json
}

function Invoke-SqlJson {
    param([Parameter(Mandatory = $true)][string]$Query)
    $wrappedQuery = @"
SET NOCOUNT ON;
SELECT N'__JSON_BEGIN__' AS Phase11Marker;
$Query
SELECT N'__JSON_END__' AS Phase11Marker;
"@
    $tempFile = Join-Path $env:TEMP ("phase11-summary-" + [Guid]::NewGuid().ToString("N") + ".sql")
    Set-Content -LiteralPath $tempFile -Value $wrappedQuery -Encoding UTF8
    try {
        $output = & sqlcmd -S $Server -d $Database -E -b -r 1 -y 0 -w 65535 -i $tempFile 2>&1
        if ($LASTEXITCODE -ne 0) { throw ($output -join [Environment]::NewLine) }
        $json = Get-SqlJsonPayload -Output $output
        if ([string]::IsNullOrWhiteSpace($json)) { return $null }
        return $json | ConvertFrom-Json -ErrorAction Stop
    }
    finally {
        Remove-Item -LiteralPath $tempFile -Force -ErrorAction SilentlyContinue
    }
}

Assert-SqlCmd

if (-not (Test-Path -LiteralPath $RuntimeMarkerPath)) {
    Write-Output "FAIL blocker=MissingSoakMarker marker=$RuntimeMarkerPath"
    exit 1
}

$marker = Get-Content -LiteralPath $RuntimeMarkerPath -Raw | ConvertFrom-Json -ErrorAction Stop
$startedAtUtc = [DateTime]::Parse([string]$marker.StartedAtUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
$endedAtUtc = [DateTime]::UtcNow
$stoppedAtProperty = $marker.PSObject.Properties["StoppedAtUtc"]
if ($null -ne $stoppedAtProperty -and $null -ne $stoppedAtProperty.Value) {
    $endedAtUtc = [DateTime]::Parse([string]$stoppedAtProperty.Value, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
}

$userIdSql = Escape-SqlLiteral ([string]$marker.UserId)
$botIdText = [string]$marker.BotId
$symbolSql = Escape-SqlLiteral ([string]$marker.Symbol)
$startSql = $startedAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff")
$endSql = $endedAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff")

$exchangeNetQuantity = $null
$positionTruthGeneratedAtUtc = $null
if (Test-Path -LiteralPath $PositionTruthEvidencePath) {
    $evidence = Get-Content -LiteralPath $PositionTruthEvidencePath -Raw | ConvertFrom-Json -ErrorAction Stop
    if ($null -ne $evidence.GeneratedAtUtc) {
        $positionTruthGeneratedAtUtc = [DateTime]::Parse([string]$evidence.GeneratedAtUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
    }
    if ($null -ne $evidence.latestExchangeNetQuantity) {
        $exchangeNetQuantity = [decimal]::Parse([string]$evidence.latestExchangeNetQuantity, [Globalization.CultureInfo]::InvariantCulture)
    }
}

$snapshot = Invoke-SqlJson @"
DECLARE @StartUtc datetime2 = '$startSql';
DECLARE @EndUtc datetime2 = '$endSql';
DECLARE @NowUtc datetime2 = SYSUTCDATETIME();

WITH TargetOrders AS (
    SELECT *
    FROM ExecutionOrders
    WHERE OwnerUserId = N'$userIdSql'
      AND BotId = '$botIdText'
      AND Symbol = N'$symbolSql'
      AND ExecutionEnvironment = 'Live'
      AND ExecutorKind = 'Binance'
      AND IsDeleted = 0
      AND CreatedDate >= @StartUtc
      AND CreatedDate <= @EndUtc
),
OrderLatency AS (
    SELECT
        o.Id,
        o.SignalType,
        submitted.CreatedDate AS SubmittedAtUtc,
        filled.CreatedDate AS FilledAtUtc,
        DATEDIFF_BIG(millisecond, submitted.CreatedDate, filled.CreatedDate) / 1000.0 AS FillLatencySeconds
    FROM TargetOrders o
    OUTER APPLY (
        SELECT TOP (1) CreatedDate
        FROM ExecutionOrderTransitions
        WHERE ExecutionOrderId = o.Id AND IsDeleted = 0 AND EventCode = 'Submitted'
        ORDER BY SequenceNumber
    ) submitted
    OUTER APPLY (
        SELECT TOP (1) CreatedDate
        FROM ExecutionOrderTransitions
        WHERE ExecutionOrderId = o.Id AND IsDeleted = 0 AND EventCode = 'ExchangeFilled'
        ORDER BY SequenceNumber DESC
    ) filled
),
Latest AS (
    SELECT TOP (1) ExecutionEnvironment, ExecutorKind
    FROM ExecutionOrders
    WHERE OwnerUserId = N'$userIdSql'
      AND BotId = '$botIdText'
      AND Symbol = N'$symbolSql'
      AND ExecutionEnvironment = 'Live'
      AND ExecutorKind = 'Binance'
      AND IsDeleted = 0
    ORDER BY CreatedDate DESC
)
SELECT
    SYSUTCDATETIME() AS GeneratedAtUtc,
    @StartUtc AS StartedAtUtc,
    @EndUtc AS EndedAtUtc,
    COUNT(CASE WHEN o.SignalType = 'Entry' THEN 1 END) AS TotalEntryDispatched,
    COUNT(CASE WHEN o.SignalType = 'Entry' AND o.State = 'Filled' THEN 1 END) AS TotalEntryFilled,
    COUNT(CASE WHEN o.SignalType = 'Exit' AND o.ReduceOnly = 1 THEN 1 END) AS TotalReduceOnlyCloseDispatched,
    COUNT(CASE WHEN o.SignalType = 'Exit' AND o.ReduceOnly = 1 AND o.State = 'Filled' THEN 1 END) AS TotalReduceOnlyCloseFilled,
    COUNT(CASE WHEN o.State IN ('Rejected', 'Failed', 'Cancelled') THEN 1 END) AS TotalRejectedFailedCancelled,
    COUNT(CASE WHEN o.State IN ('Submitted', 'Dispatching', 'PartiallyFilled', 'CancelRequested') AND DATEDIFF(minute, o.CreatedDate, @NowUtc) >= $StuckOrderThresholdMinutes THEN 1 END) AS StuckSubmittedCount,
    COUNT(CASE WHEN o.ReconciliationStatus = 'DriftDetected' THEN 1 END) AS ReconciliationDriftCount,
    COUNT(CASE WHEN o.FailureCode = 'PrivatePlaneStale' THEN 1 END) AS PrivatePlaneStaleFailureCount,
    COUNT(CASE WHEN o.FailureCode = 'StaleMarketData' THEN 1 END) AS MarketDataStaleFailureCount,
    AVG(CASE WHEN latency.SignalType = 'Entry' THEN latency.FillLatencySeconds END) AS AverageEntryFillLatencySeconds,
    AVG(CASE WHEN latency.SignalType = 'Exit' THEN latency.FillLatencySeconds END) AS AverageCloseFillLatencySeconds,
    MAX(CASE WHEN o.State IN ('Submitted', 'Dispatching', 'PartiallyFilled', 'CancelRequested') THEN DATEDIFF_BIG(second, o.CreatedDate, @NowUtc) END) AS MaxOpenOrderDurationSeconds,
    MAX(CASE WHEN o.SignalType = 'Entry' AND o.State = 'Filled' THEN DATEDIFF_BIG(second, o.CreatedDate, COALESCE(closeOrder.CreatedDate, @NowUtc)) END) AS MaxPositionOpenDurationSeconds,
    MAX(CASE WHEN o.SignalType = 'Exit' AND o.ReduceOnly = 1 AND o.State = 'Filled' THEN o.UpdatedDate END) AS LastSuccessfulCycleAtUtc,
    bot.OpenPositionCount AS CurrentBotOpenPositionCount,
    bot.OpenOrderCount AS CurrentBotOpenOrderCount,
    ISNULL(openOrders.ActualOpenOrderCount, 0) AS CurrentActualOpenOrderCount,
    ISNULL(pos.DbOpenPositionCount, 0) AS CurrentDbOpenPositionCount,
    ISNULL(pos.DbNetQuantity, 0) AS CurrentDbNetQuantity,
    CASE WHEN sync.LastPositionSyncedAtUtc IS NULL OR DATEDIFF(second, sync.LastPositionSyncedAtUtc, @NowUtc) > $FreshnessThresholdSeconds THEN 1 ELSE 0 END AS PrivatePlaneStaleCount,
    CASE
        WHEN bot.OpenOrderCount <> ISNULL(openOrders.ActualOpenOrderCount, 0) THEN 1
        WHEN bot.OpenPositionCount <> ISNULL(pos.DbOpenPositionCount, 0) THEN 1
        ELSE 0
    END AS CounterParityMismatchCount,
    latest.ExecutionEnvironment AS LatestEnvironment,
    latest.ExecutorKind AS LatestExecutorKind
FROM TradingBots bot
LEFT JOIN TargetOrders o ON 1 = 1
LEFT JOIN OrderLatency latency ON latency.Id = o.Id
OUTER APPLY (
    SELECT TOP (1) CreatedDate
    FROM TargetOrders nextClose
    WHERE nextClose.SignalType = 'Exit'
      AND nextClose.ReduceOnly = 1
      AND nextClose.State = 'Filled'
      AND nextClose.CreatedDate > o.CreatedDate
    ORDER BY nextClose.CreatedDate
) closeOrder
OUTER APPLY (
    SELECT COUNT(*) AS ActualOpenOrderCount
    FROM ExecutionOrders
    WHERE BotId = bot.Id
      AND IsDeleted = 0
      AND State IN ('Received', 'GatePassed', 'Dispatching', 'Submitted', 'PartiallyFilled', 'CancelRequested')
) openOrders
OUTER APPLY (
    SELECT
        COUNT(*) AS DbOpenPositionCount,
        SUM(CASE WHEN UPPER(ISNULL(PositionSide, 'BOTH')) = 'SHORT' THEN -ABS(Quantity) ELSE Quantity END) AS DbNetQuantity
    FROM ExchangePositions
    WHERE OwnerUserId = bot.OwnerUserId
      AND ExchangeAccountId = bot.ExchangeAccountId
      AND Plane = 'Futures'
      AND Symbol = N'$symbolSql'
      AND IsDeleted = 0
      AND Quantity <> 0
) pos
LEFT JOIN ExchangeAccountSyncStates sync
    ON sync.OwnerUserId = bot.OwnerUserId
   AND sync.ExchangeAccountId = bot.ExchangeAccountId
   AND sync.Plane = 'Futures'
   AND sync.IsDeleted = 0
OUTER APPLY (SELECT * FROM Latest) latest
WHERE bot.Id = '$botIdText'
  AND bot.OwnerUserId = N'$userIdSql'
  AND bot.IsDeleted = 0
GROUP BY
    bot.OpenPositionCount,
    bot.OpenOrderCount,
    openOrders.ActualOpenOrderCount,
    pos.DbOpenPositionCount,
    pos.DbNetQuantity,
    sync.LastPositionSyncedAtUtc,
    latest.ExecutionEnvironment,
    latest.ExecutorKind
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES;
"@

if ($null -eq $snapshot) {
    Write-Output "FAIL blocker=SoakSummaryUnavailable"
    exit 1
}

$exchangeValue = if ($null -eq $exchangeNetQuantity) { [decimal]$snapshot.CurrentDbNetQuantity } else { $exchangeNetQuantity }
$snapshot | Add-Member -NotePropertyName SoakId -NotePropertyValue ([string]$marker.SoakId) -Force
$snapshot | Add-Member -NotePropertyName CurrentExchangeNetQuantity -NotePropertyValue $exchangeValue -Force
$snapshot | Add-Member -NotePropertyName PositionTruthGeneratedAtUtc -NotePropertyValue $(if ($null -eq $positionTruthGeneratedAtUtc) { $null } else { $positionTruthGeneratedAtUtc.ToString("O") }) -Force

$summaryDirectory = Split-Path $SummaryPath -Parent
if (-not [string]::IsNullOrWhiteSpace($summaryDirectory)) {
    New-Item -ItemType Directory -Path $summaryDirectory -Force | Out-Null
}
$snapshot | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $SummaryPath -Encoding UTF8

Write-Output ("SUMMARY soakId={0} entryDispatched={1} entryFilled={2} closeDispatched={3} closeFilled={4} rejectedFailedCancelled={5} stuckSubmitted={6} reconciliationDrift={7} counterParityMismatch={8} privatePlaneStale={9} marketDataStale={10} currentBotOpenPositionCount={11} currentBotOpenOrderCount={12} currentExchangeNetQuantity={13} currentDbNetQuantity={14} latestEnvironment={15} latestExecutorKind={16}" -f `
    $snapshot.SoakId,
    $snapshot.TotalEntryDispatched,
    $snapshot.TotalEntryFilled,
    $snapshot.TotalReduceOnlyCloseDispatched,
    $snapshot.TotalReduceOnlyCloseFilled,
    $snapshot.TotalRejectedFailedCancelled,
    $snapshot.StuckSubmittedCount,
    $snapshot.ReconciliationDriftCount,
    $snapshot.CounterParityMismatchCount,
    $snapshot.PrivatePlaneStaleCount,
    $snapshot.MarketDataStaleFailureCount,
    $snapshot.CurrentBotOpenPositionCount,
    $snapshot.CurrentBotOpenOrderCount,
    $snapshot.CurrentExchangeNetQuantity,
    $snapshot.CurrentDbNetQuantity,
    $snapshot.LatestEnvironment,
    $snapshot.LatestExecutorKind)
Write-Output "SummaryPath=$SummaryPath"
