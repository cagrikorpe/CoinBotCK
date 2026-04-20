[CmdletBinding()]
param(
    [string]$Server = "localhost",
    [string]$Database = "CoinBotDb",
    [guid]$OrderId = [guid]::Empty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Fail {
    param(
        [Parameter(Mandatory = $true)][string]$Blocker,
        [string]$LatestOrderId = "none",
        [string]$LatestEnvironment = "none",
        [string]$LatestExecutorKind = "none",
        [string]$LatestOrderState = "none",
        [string]$LatestTransition = "none",
        [string]$LatestParity = "Unknown"
    )
    Write-Output "FAIL blocker=$Blocker latestOrderId=$LatestOrderId latestEnvironment=$LatestEnvironment latestExecutorKind=$LatestExecutorKind latestOrderState=$LatestOrderState latestTransition=$LatestTransition latestParity=$LatestParity"
    exit 1
}

function Write-Pass {
    param(
        [Parameter(Mandatory = $true)][string]$Blocker,
        [Parameter(Mandatory = $true)][string]$LatestOrderId,
        [Parameter(Mandatory = $true)][string]$LatestEnvironment,
        [Parameter(Mandatory = $true)][string]$LatestExecutorKind,
        [Parameter(Mandatory = $true)][string]$LatestOrderState,
        [Parameter(Mandatory = $true)][string]$LatestTransition,
        [Parameter(Mandatory = $true)][string]$LatestParity
    )
    Write-Output "PASS blocker=$Blocker latestOrderId=$LatestOrderId latestEnvironment=$LatestEnvironment latestExecutorKind=$LatestExecutorKind latestOrderState=$LatestOrderState latestTransition=$LatestTransition latestParity=$LatestParity"
    exit 0
}

function Assert-SqlCmd {
    if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
        throw "sqlcmd was not found."
    }
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
SELECT N'__JSON_BEGIN__' AS Phase10iMarker;
$Query
SELECT N'__JSON_END__' AS Phase10iMarker;
"@
    $tempFile = Join-Path $env:TEMP ("phase10i-verify-" + [Guid]::NewGuid().ToString("N") + ".sql")
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

$orderFilter = if ($OrderId -eq [guid]::Empty) {
    ""
} else {
    "AND o.Id = '$($OrderId.ToString())'"
}

$snapshot = Invoke-SqlJson @"
SELECT TOP (1)
    o.Id,
    o.State,
    o.ExecutionEnvironment,
    o.ExecutorKind,
    o.FailureCode,
    o.FilledQuantity,
    o.ReduceOnly,
    o.Side,
    o.Symbol,
    o.ExchangeAccountId,
    o.BotId,
    o.ReconciliationStatus,
    o.LastReconciledAtUtc,
    t.EventCode AS LatestTransition,
    t.State AS LatestTransitionState,
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
    SELECT SUM(Quantity) AS PositionQuantity
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
WHERE o.ExecutionEnvironment = 'Live'
  AND o.ExecutorKind = 'Binance'
  AND o.SubmittedToBroker = 1
  AND o.IsDeleted = 0
  $orderFilter
ORDER BY o.CreatedDate DESC
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES;
"@

if ($null -eq $snapshot) {
    Write-Fail -Blocker "NoLiveBinanceOrderObserved"
}

$latestOrderId = [string]$snapshot.Id
$latestEnvironment = [string]$snapshot.ExecutionEnvironment
$latestExecutorKind = [string]$snapshot.ExecutorKind
$latestOrderState = [string]$snapshot.State
$latestTransition = if ($null -eq $snapshot.LatestTransition) { "none" } else { [string]$snapshot.LatestTransition }
$latestParity = "Unknown"

if ($latestEnvironment -ne "Live" -or $latestExecutorKind -ne "Binance") {
    Write-Fail -Blocker "NonLiveBinanceOrder" -LatestOrderId $latestOrderId -LatestEnvironment $latestEnvironment -LatestExecutorKind $latestExecutorKind -LatestOrderState $latestOrderState -LatestTransition $latestTransition -LatestParity $latestParity
}

if ($latestOrderState -eq "Submitted") {
    Write-Fail -Blocker "OrderStillSubmitted" -LatestOrderId $latestOrderId -LatestEnvironment $latestEnvironment -LatestExecutorKind $latestExecutorKind -LatestOrderState $latestOrderState -LatestTransition $latestTransition -LatestParity $latestParity
}

if ($latestOrderState -eq "Filled") {
    $positionQuantity = [decimal]$snapshot.PositionQuantity
    $openPositionCount = [int]$snapshot.OpenPositionCount
    $openOrderCount = [int]$snapshot.OpenOrderCount
    $finalTransitionCount = [int]$snapshot.FinalTransitionCount
    if ($latestTransition -eq "ExchangeFilled" -and
        $positionQuantity -ne [decimal]0 -and
        $openPositionCount -gt 0 -and
        $openOrderCount -eq 0 -and
        $finalTransitionCount -eq 1) {
        $latestParity = "Matched"
        Write-Pass -Blocker "none" -LatestOrderId $latestOrderId -LatestEnvironment $latestEnvironment -LatestExecutorKind $latestExecutorKind -LatestOrderState $latestOrderState -LatestTransition $latestTransition -LatestParity $latestParity
    }

    Write-Fail -Blocker "FillParityMismatch" -LatestOrderId $latestOrderId -LatestEnvironment $latestEnvironment -LatestExecutorKind $latestExecutorKind -LatestOrderState $latestOrderState -LatestTransition $latestTransition -LatestParity "Mismatched"
}

if ($latestOrderState -in @("Failed", "Rejected", "Cancelled")) {
    $blocker = if ([string]::IsNullOrWhiteSpace([string]$snapshot.FailureCode)) { $latestOrderState } else { [string]$snapshot.FailureCode }
    if ($latestTransition -in @("ExchangeRejected", "ExchangeCancelled", "ExchangeObserved") -or $latestOrderState -eq "Failed") {
        Write-Pass -Blocker $blocker -LatestOrderId $latestOrderId -LatestEnvironment $latestEnvironment -LatestExecutorKind $latestExecutorKind -LatestOrderState $latestOrderState -LatestTransition $latestTransition -LatestParity "Matched"
    }
}

Write-Fail -Blocker "UnexpectedOrderState" -LatestOrderId $latestOrderId -LatestEnvironment $latestEnvironment -LatestExecutorKind $latestExecutorKind -LatestOrderState $latestOrderState -LatestTransition $latestTransition -LatestParity $latestParity
