[CmdletBinding()]
param(
    [string]$Server = "localhost",
    [string]$Database = "CoinBotDb",
    [string]$UserId = "5a8675cf-cbcb-4c28-82fb-304aee442895",
    [guid]$BotId = "8EA3ED8B-C61F-4A44-A683-168AE9C69C43",
    [string]$Symbol = "SOLUSDT",
    [string]$EvidencePath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
    $EvidencePath = Join-Path $repoRoot "artifacts\phase10k-position-truth.json"
}

function Write-Fail {
    param(
        [Parameter(Mandatory = $true)][string]$Blocker,
        [string]$PositionParity = "unknown",
        [string]$ExchangeNetQuantity = "unknown",
        [string]$DbNetQuantity = "unknown",
        [string]$BotOpenPositionCount = "unknown",
        [string]$BotOpenOrderCount = "unknown",
        [string]$Environment = "unknown",
        [string]$ExecutorKind = "unknown"
    )

    Write-Output "FAIL blocker=$Blocker latestPositionParity=$PositionParity latestExchangeNetQuantity=$ExchangeNetQuantity latestDbNetQuantity=$DbNetQuantity latestBotOpenPositionCount=$BotOpenPositionCount latestBotOpenOrderCount=$BotOpenOrderCount latestEnvironment=$Environment latestExecutorKind=$ExecutorKind"
    exit 1
}

function Write-Pass {
    param(
        [Parameter(Mandatory = $true)][string]$Blocker,
        [Parameter(Mandatory = $true)][string]$PositionParity,
        [Parameter(Mandatory = $true)][decimal]$ExchangeNetQuantity,
        [Parameter(Mandatory = $true)][decimal]$DbNetQuantity,
        [Parameter(Mandatory = $true)][int]$BotOpenPositionCount,
        [Parameter(Mandatory = $true)][int]$BotOpenOrderCount,
        [Parameter(Mandatory = $true)][string]$Environment,
        [Parameter(Mandatory = $true)][string]$ExecutorKind
    )

    Write-Output "PASS blocker=$Blocker latestPositionParity=$PositionParity latestExchangeNetQuantity=$ExchangeNetQuantity latestDbNetQuantity=$DbNetQuantity latestBotOpenPositionCount=$BotOpenPositionCount latestBotOpenOrderCount=$BotOpenOrderCount latestEnvironment=$Environment latestExecutorKind=$ExecutorKind"
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

    if ($beginIndex -lt 0 -or $endIndex -lt 0 -or $endIndex -le $beginIndex) {
        return $null
    }

    $json = (($lines[($beginIndex + 1)..($endIndex - 1)] | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join [Environment]::NewLine).Trim()
    if ([string]::IsNullOrWhiteSpace($json)) { return $null }
    return $json
}

function Invoke-SqlJson {
    param([Parameter(Mandatory = $true)][string]$Query)

    $wrappedQuery = @"
SET NOCOUNT ON;
SELECT N'__JSON_BEGIN__' AS Phase10kMarker;
$Query
SELECT N'__JSON_END__' AS Phase10kMarker;
"@

    $tempFile = Join-Path $env:TEMP ("phase10k-verify-" + [Guid]::NewGuid().ToString("N") + ".sql")
    Set-Content -LiteralPath $tempFile -Value $wrappedQuery -Encoding UTF8
    try {
        $output = & sqlcmd -S $Server -d $Database -E -b -r 1 -y 0 -w 65535 -i $tempFile 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw ($output -join [Environment]::NewLine)
        }

        $json = Get-SqlJsonPayload -Output $output
        if ([string]::IsNullOrWhiteSpace($json)) {
            return $null
        }

        return $json | ConvertFrom-Json -ErrorAction Stop
    }
    finally {
        Remove-Item -LiteralPath $tempFile -Force -ErrorAction SilentlyContinue
    }
}

Assert-SqlCmd

if (-not (Test-Path -LiteralPath $EvidencePath)) {
    Write-Fail -Blocker "MissingPositionTruthEvidence"
}

$evidence = Get-Content -LiteralPath $EvidencePath -Raw | ConvertFrom-Json -ErrorAction Stop
$generatedAtUtc = [DateTime]::Parse([string]$evidence.GeneratedAtUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
if ($generatedAtUtc -lt [DateTime]::UtcNow.AddMinutes(-30)) {
    Write-Fail -Blocker "StalePositionTruthEvidence"
}

$userIdSql = Escape-SqlLiteral $UserId
$botIdText = $BotId.ToString()
$symbolSql = Escape-SqlLiteral ($Symbol.ToUpperInvariant())

$snapshot = Invoke-SqlJson @"
SELECT TOP (1)
    b.Id AS BotId,
    b.OpenPositionCount,
    b.OpenOrderCount,
    ISNULL(pos.DbOpenPositionCount, 0) AS DbOpenPositionCount,
    ISNULL(pos.DbNetQuantity, 0) AS DbNetQuantity,
    latest.ExecutionEnvironment,
    latest.ExecutorKind
FROM TradingBots b
OUTER APPLY (
    SELECT
        COUNT(*) AS DbOpenPositionCount,
        SUM(CASE WHEN PositionSide = 'SHORT' THEN -ABS(Quantity) ELSE Quantity END) AS DbNetQuantity
    FROM ExchangePositions
    WHERE OwnerUserId = b.OwnerUserId
      AND ExchangeAccountId = b.ExchangeAccountId
      AND Plane = 'Futures'
      AND Symbol = N'$symbolSql'
      AND IsDeleted = 0
      AND Quantity <> 0
) pos
OUTER APPLY (
    SELECT TOP (1)
        ExecutionEnvironment,
        ExecutorKind
    FROM ExecutionOrders
    WHERE OwnerUserId = b.OwnerUserId
      AND ExecutionEnvironment = 'Live'
      AND ExecutorKind = 'Binance'
      AND IsDeleted = 0
    ORDER BY CreatedDate DESC
) latest
WHERE b.Id = '$botIdText'
  AND b.OwnerUserId = N'$userIdSql'
  AND b.IsDeleted = 0
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES;
"@

if ($null -eq $snapshot) {
    Write-Fail -Blocker "TargetBotNotFound"
}

$exchangeNetQuantity = [decimal]::Parse([string]$evidence.latestExchangeNetQuantity, [Globalization.CultureInfo]::InvariantCulture)
$dbNetQuantity = [decimal]$snapshot.DbNetQuantity
$botOpenPositionCount = [int]$snapshot.OpenPositionCount
$botOpenOrderCount = [int]$snapshot.OpenOrderCount
$dbOpenPositionCount = [int]$snapshot.DbOpenPositionCount
$latestEnvironment = [string]$snapshot.ExecutionEnvironment
$latestExecutorKind = [string]$snapshot.ExecutorKind

if ($latestEnvironment -ne "Live" -or $latestExecutorKind -ne "Binance") {
    Write-Fail -Blocker "NoLiveBinanceOrderEvidence" -ExchangeNetQuantity ([string]$exchangeNetQuantity) -DbNetQuantity ([string]$dbNetQuantity) -BotOpenPositionCount ([string]$botOpenPositionCount) -BotOpenOrderCount ([string]$botOpenOrderCount) -Environment $latestEnvironment -ExecutorKind $latestExecutorKind
}

if ($exchangeNetQuantity -ne $dbNetQuantity) {
    Write-Fail -Blocker "ExchangeDbPositionMismatch" -PositionParity "Mismatched" -ExchangeNetQuantity ([string]$exchangeNetQuantity) -DbNetQuantity ([string]$dbNetQuantity) -BotOpenPositionCount ([string]$botOpenPositionCount) -BotOpenOrderCount ([string]$botOpenOrderCount) -Environment $latestEnvironment -ExecutorKind $latestExecutorKind
}

if ($exchangeNetQuantity -eq [decimal]0 -and $dbNetQuantity -eq [decimal]0 -and $botOpenPositionCount -eq 0 -and $botOpenOrderCount -eq 0) {
    Write-Pass -Blocker "NoOpenExchangePosition" -PositionParity "Flat" -ExchangeNetQuantity $exchangeNetQuantity -DbNetQuantity $dbNetQuantity -BotOpenPositionCount $botOpenPositionCount -BotOpenOrderCount $botOpenOrderCount -Environment $latestEnvironment -ExecutorKind $latestExecutorKind
}

if ($exchangeNetQuantity -ne [decimal]0 -and $dbNetQuantity -ne [decimal]0 -and $botOpenPositionCount -eq $dbOpenPositionCount -and $botOpenOrderCount -eq 0) {
    Write-Pass -Blocker "none" -PositionParity "Matched" -ExchangeNetQuantity $exchangeNetQuantity -DbNetQuantity $dbNetQuantity -BotOpenPositionCount $botOpenPositionCount -BotOpenOrderCount $botOpenOrderCount -Environment $latestEnvironment -ExecutorKind $latestExecutorKind
}

Write-Fail -Blocker "PositionCounterParityMismatch" -PositionParity "Mismatched" -ExchangeNetQuantity ([string]$exchangeNetQuantity) -DbNetQuantity ([string]$dbNetQuantity) -BotOpenPositionCount ([string]$botOpenPositionCount) -BotOpenOrderCount ([string]$botOpenOrderCount) -Environment $latestEnvironment -ExecutorKind $latestExecutorKind
