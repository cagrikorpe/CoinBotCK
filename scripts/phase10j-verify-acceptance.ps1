[CmdletBinding()]
param(
    [string]$Server = "localhost",
    [string]$Database = "CoinBotDb",
    [string]$UserId = "5a8675cf-cbcb-4c28-82fb-304aee442895",
    [guid]$BotId = "8EA3ED8B-C61F-4A44-A683-168AE9C69C43",
    [string]$Symbol = "SOLUSDT",
    [guid]$CloseOrderId = [guid]::Empty,
    [string]$RuntimeMarkerPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($RuntimeMarkerPath)) {
    $RuntimeMarkerPath = Join-Path $repoRoot "artifacts\phase10j-close-runtime.json"
}

function Write-Fail {
    param(
        [Parameter(Mandatory = $true)][string]$Blocker,
        [string]$LatestCloseOrderId = "none",
        [string]$LatestEnvironment = "none",
        [string]$LatestExecutorKind = "none",
        [string]$LatestCloseOrderState = "none",
        [string]$LatestCloseTransition = "none",
        [string]$LatestPositionParity = "Unknown",
        [string]$LatestBotOpenPositionCount = "unknown",
        [string]$LatestBotOpenOrderCount = "unknown"
    )
    Write-Output "FAIL blocker=$Blocker latestCloseOrderId=$LatestCloseOrderId latestEnvironment=$LatestEnvironment latestExecutorKind=$LatestExecutorKind latestCloseOrderState=$LatestCloseOrderState latestCloseTransition=$LatestCloseTransition latestPositionParity=$LatestPositionParity latestBotOpenPositionCount=$LatestBotOpenPositionCount latestBotOpenOrderCount=$LatestBotOpenOrderCount"
    exit 1
}

function Write-Pass {
    param(
        [Parameter(Mandatory = $true)][string]$Blocker,
        [Parameter(Mandatory = $true)][string]$LatestCloseOrderId,
        [Parameter(Mandatory = $true)][string]$LatestEnvironment,
        [Parameter(Mandatory = $true)][string]$LatestExecutorKind,
        [Parameter(Mandatory = $true)][string]$LatestCloseOrderState,
        [Parameter(Mandatory = $true)][string]$LatestCloseTransition,
        [Parameter(Mandatory = $true)][string]$LatestPositionParity,
        [Parameter(Mandatory = $true)][int]$LatestBotOpenPositionCount,
        [Parameter(Mandatory = $true)][int]$LatestBotOpenOrderCount
    )
    Write-Output "PASS blocker=$Blocker latestCloseOrderId=$LatestCloseOrderId latestEnvironment=$LatestEnvironment latestExecutorKind=$LatestExecutorKind latestCloseOrderState=$LatestCloseOrderState latestCloseTransition=$LatestCloseTransition latestPositionParity=$LatestPositionParity latestBotOpenPositionCount=$LatestBotOpenPositionCount latestBotOpenOrderCount=$LatestBotOpenOrderCount"
    exit 0
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
        if ($lines[$i] -match '__JSON_BEGIN__') { $beginIndex = $i; continue }
        if ($lines[$i] -match '__JSON_END__') { $endIndex = $i; break }
    }
    if ($beginIndex -lt 0 -or $endIndex -le $beginIndex) { return $null }
    $json = (($lines[($beginIndex + 1)..($endIndex - 1)] | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join [Environment]::NewLine).Trim()
    return $(if ([string]::IsNullOrWhiteSpace($json)) { $null } else { $json })
}

function Invoke-SqlJson {
    param([Parameter(Mandatory = $true)][string]$Query)
    $wrappedQuery = @"
SET NOCOUNT ON;
SELECT N'__JSON_BEGIN__' AS Phase10jMarker;
$Query
SELECT N'__JSON_END__' AS Phase10jMarker;
"@
    $tempFile = Join-Path $env:TEMP ("phase10j-verify-" + [Guid]::NewGuid().ToString("N") + ".sql")
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

$userIdSql = Escape-SqlLiteral $UserId
$symbolSql = Escape-SqlLiteral $Symbol.ToUpperInvariant()
$startedAtFilter = ""
if ($CloseOrderId -eq [guid]::Empty) {
    if (-not (Test-Path -LiteralPath $RuntimeMarkerPath)) {
        Write-Fail -Blocker "MissingFreshCloseOrderMarker"
    }

    $marker = Get-Content -Raw -LiteralPath $RuntimeMarkerPath | ConvertFrom-Json -ErrorAction Stop
    if ($null -eq $marker.CloseOrderId -or -not [guid]::TryParse([string]$marker.CloseOrderId, [ref]$CloseOrderId)) {
        Write-Fail -Blocker "InvalidFreshCloseOrderMarker"
    }

    if ($null -ne $marker.StartedAtUtc) {
        $startedAtUtc = [datetime]::Parse([string]$marker.StartedAtUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
        $startedAtSql = $startedAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff")
        $startedAtFilter = "AND o.CreatedDate >= '$startedAtSql'"
    }
}

$orderFilter = "AND o.Id = '$($CloseOrderId.ToString())'"

$snapshot = Invoke-SqlJson @"
SELECT TOP (1)
    o.Id,
    o.State,
    o.ExecutionEnvironment,
    o.ExecutorKind,
    o.FailureCode,
    o.ReduceOnly,
    o.Side,
    o.Symbol,
    o.ExchangeAccountId,
    o.BotId,
    o.ReconciliationStatus,
    t.EventCode AS LatestTransition,
    bot.OpenPositionCount,
    bot.OpenOrderCount,
    ISNULL(pos.PositionQuantity, 0) AS PositionQuantity,
    ISNULL(finals.FinalTransitionCount, 0) AS FinalTransitionCount
FROM ExecutionOrders o
OUTER APPLY (
    SELECT TOP (1) EventCode, State
    FROM ExecutionOrderTransitions
    WHERE ExecutionOrderId = o.Id AND IsDeleted = 0
    ORDER BY SequenceNumber DESC
) t
OUTER APPLY (
    SELECT SUM(CASE WHEN UPPER(ISNULL(PositionSide, 'BOTH')) = 'SHORT' THEN -ABS(Quantity) ELSE Quantity END) AS PositionQuantity
    FROM ExchangePositions
    WHERE ExchangeAccountId = o.ExchangeAccountId
      AND Symbol = o.Symbol
      AND Plane = o.Plane
      AND IsDeleted = 0
) pos
OUTER APPLY (
    SELECT COUNT(*) AS FinalTransitionCount
    FROM ExecutionOrderTransitions
    WHERE ExecutionOrderId = o.Id
      AND IsDeleted = 0
      AND EventCode IN ('ExchangeFilled', 'ExchangeCancelled', 'ExchangeRejected')
) finals
LEFT JOIN TradingBots bot ON bot.Id = o.BotId AND bot.IsDeleted = 0
WHERE o.OwnerUserId = N'$userIdSql'
  AND o.BotId = '$($BotId.ToString())'
  AND o.Symbol = N'$symbolSql'
  AND o.ExecutionEnvironment = 'Live'
  AND o.ExecutorKind = 'Binance'
  AND o.ReduceOnly = 1
  AND o.SignalType = 'Exit'
  AND o.IsDeleted = 0
  $orderFilter
  $startedAtFilter
ORDER BY o.CreatedDate DESC
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES;
"@

if ($null -eq $snapshot) {
    Write-Fail -Blocker "NoCloseDispatchObserved"
}

$latestCloseOrderId = [string]$snapshot.Id
$latestEnvironment = [string]$snapshot.ExecutionEnvironment
$latestExecutorKind = [string]$snapshot.ExecutorKind
$latestCloseOrderState = [string]$snapshot.State
$latestCloseTransition = if ($null -eq $snapshot.LatestTransition) { "none" } else { [string]$snapshot.LatestTransition }
$positionQuantity = [decimal]$snapshot.PositionQuantity
$openPositionCount = [int]$snapshot.OpenPositionCount
$openOrderCount = [int]$snapshot.OpenOrderCount
$finalTransitionCount = [int]$snapshot.FinalTransitionCount
$latestPositionParity = if ($positionQuantity -eq [decimal]0) { "Flat" } else { "Open" }

if ($latestEnvironment -ne "Live" -or $latestExecutorKind -ne "Binance") {
    Write-Fail -Blocker "NonLiveBinanceCloseOrder" -LatestCloseOrderId $latestCloseOrderId -LatestEnvironment $latestEnvironment -LatestExecutorKind $latestExecutorKind -LatestCloseOrderState $latestCloseOrderState -LatestCloseTransition $latestCloseTransition -LatestPositionParity $latestPositionParity -LatestBotOpenPositionCount ([string]$openPositionCount) -LatestBotOpenOrderCount ([string]$openOrderCount)
}

if ($snapshot.ReduceOnly -ne $true) {
    Write-Fail -Blocker "CloseOrderNotReduceOnly" -LatestCloseOrderId $latestCloseOrderId -LatestEnvironment $latestEnvironment -LatestExecutorKind $latestExecutorKind -LatestCloseOrderState $latestCloseOrderState -LatestCloseTransition $latestCloseTransition -LatestPositionParity $latestPositionParity -LatestBotOpenPositionCount ([string]$openPositionCount) -LatestBotOpenOrderCount ([string]$openOrderCount)
}

if ($latestCloseOrderState -eq "Filled" -and
    $latestCloseTransition -eq "ExchangeFilled" -and
    $positionQuantity -eq [decimal]0 -and
    $openPositionCount -eq 0 -and
    $openOrderCount -eq 0 -and
    $finalTransitionCount -eq 1) {
    Write-Pass -Blocker "none" -LatestCloseOrderId $latestCloseOrderId -LatestEnvironment $latestEnvironment -LatestExecutorKind $latestExecutorKind -LatestCloseOrderState $latestCloseOrderState -LatestCloseTransition $latestCloseTransition -LatestPositionParity "Flat" -LatestBotOpenPositionCount $openPositionCount -LatestBotOpenOrderCount $openOrderCount
}

if ($latestCloseOrderState -in @("Failed", "Rejected", "Cancelled")) {
    $blocker = if ([string]::IsNullOrWhiteSpace([string]$snapshot.FailureCode)) { "CloseOrderNotFilled" } else { [string]$snapshot.FailureCode }
    Write-Fail -Blocker $blocker -LatestCloseOrderId $latestCloseOrderId -LatestEnvironment $latestEnvironment -LatestExecutorKind $latestExecutorKind -LatestCloseOrderState $latestCloseOrderState -LatestCloseTransition $latestCloseTransition -LatestPositionParity $latestPositionParity -LatestBotOpenPositionCount ([string]$openPositionCount) -LatestBotOpenOrderCount ([string]$openOrderCount)
}

Write-Fail -Blocker "CloseParityMismatch" -LatestCloseOrderId $latestCloseOrderId -LatestEnvironment $latestEnvironment -LatestExecutorKind $latestExecutorKind -LatestCloseOrderState $latestCloseOrderState -LatestCloseTransition $latestCloseTransition -LatestPositionParity $latestPositionParity -LatestBotOpenPositionCount ([string]$openPositionCount) -LatestBotOpenOrderCount ([string]$openOrderCount)
