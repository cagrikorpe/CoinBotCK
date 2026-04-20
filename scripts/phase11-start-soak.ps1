[CmdletBinding()]
param(
    [string]$Server = "localhost",
    [string]$Database = "CoinBotDb",
    [string]$UserId = "5a8675cf-cbcb-4c28-82fb-304aee442895",
    [guid]$BotId = "8EA3ED8B-C61F-4A44-A683-168AE9C69C43",
    [string]$Symbol = "SOLUSDT",
    [string]$WorkerPath = "",
    [string]$FuturesRestBaseUrl = "https://testnet.binancefuture.com",
    [string]$FuturesWebSocketBaseUrl = "wss://fstream.binancefuture.com",
    [string]$RuntimeMarkerPath = "",
    [switch]$StopExisting,
    [switch]$PreflightOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($RuntimeMarkerPath)) {
    $RuntimeMarkerPath = Join-Path $repoRoot "artifacts\phase11-soak-runtime.json"
}

if ([string]::IsNullOrWhiteSpace($WorkerPath)) {
    $debugWorkerPath = Join-Path $repoRoot "src\CoinBot.Worker\bin\Debug\net10.0\CoinBot.Worker.exe"
    $phase10dWorkerPath = Join-Path $repoRoot "artifacts\phase10d-worker-build\CoinBot.Worker.exe"
    $WorkerPath = if (Test-Path -LiteralPath $debugWorkerPath) { $debugWorkerPath } else { $phase10dWorkerPath }
}

function Write-Fail {
    param(
        [Parameter(Mandatory = $true)][string]$Blocker,
        [string]$Detail = ""
    )

    Write-Output "FAIL blocker=$Blocker detail=$Detail"
    exit 1
}

function Write-Pass {
    param(
        [Parameter(Mandatory = $true)][string]$SoakId,
        [Parameter(Mandatory = $true)][string]$Mode,
        [string]$WorkerProcessId = "none",
        [string]$WorkerPathValue = "none"
    )

    Write-Output "PASS blocker=none mode=$Mode soakId=$SoakId workerProcessId=$WorkerProcessId workerPath=$WorkerPathValue marker=$RuntimeMarkerPath"
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

function Test-TestnetEndpoint {
    param([Parameter(Mandatory = $true)][string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return $false }
    try { $uri = [Uri]$Value } catch { return $false }

    $endpointHost = $uri.Host.ToLowerInvariant()
    $blockedHosts = @("api.binance.com", "fapi.binance.com", "dapi.binance.com", "stream.binance.com", "fstream.binance.com")
    if ($blockedHosts -contains $endpointHost) { return $false }

    return $endpointHost -eq "localhost" -or
        $endpointHost -eq "127.0.0.1" -or
        $endpointHost -eq "::1" -or
        $endpointHost.Contains("testnet") -or
        $endpointHost.Contains("proxy-testnet") -or
        $endpointHost -eq "binancefuture.com" -or
        $endpointHost.EndsWith(".binancefuture.com")
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

    $tempFile = Join-Path $env:TEMP ("phase11-start-" + [Guid]::NewGuid().ToString("N") + ".sql")
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

if (-not (Test-TestnetEndpoint $FuturesRestBaseUrl) -or -not (Test-TestnetEndpoint $FuturesWebSocketBaseUrl)) {
    Write-Fail -Blocker "TestnetEndpointGuardRejected" -Detail "Configured endpoint is not testnet-safe."
}

if (-not $PreflightOnly -and -not (Test-Path -LiteralPath $WorkerPath)) {
    Write-Fail -Blocker "WorkerArtifactMissing" -Detail $WorkerPath
}

$userIdSql = Escape-SqlLiteral $UserId
$symbolSql = Escape-SqlLiteral $Symbol.ToUpperInvariant()
$botIdText = $BotId.ToString()
$snapshot = Invoke-SqlJson @"
SELECT TOP (1)
    SYSUTCDATETIME() AS SqlUtcNow,
    b.Id AS BotId,
    b.OpenPositionCount,
    b.OpenOrderCount,
    ISNULL(pos.DbNetQuantity, 0) AS DbNetQuantity,
    ISNULL(pos.DbOpenPositionCount, 0) AS DbOpenPositionCount,
    ISNULL(openOrders.ActualOpenOrderCount, 0) AS ActualOpenOrderCount,
    latest.ExecutionEnvironment AS LatestEnvironment,
    latest.ExecutorKind AS LatestExecutorKind,
    sync.LastPositionSyncedAtUtc,
    sync.LastBalanceSyncedAtUtc
FROM TradingBots b
OUTER APPLY (
    SELECT
        COUNT(*) AS DbOpenPositionCount,
        SUM(CASE WHEN UPPER(ISNULL(PositionSide, 'BOTH')) = 'SHORT' THEN -ABS(Quantity) ELSE Quantity END) AS DbNetQuantity
    FROM ExchangePositions
    WHERE OwnerUserId = b.OwnerUserId
      AND ExchangeAccountId = b.ExchangeAccountId
      AND Plane = 'Futures'
      AND Symbol = N'$symbolSql'
      AND IsDeleted = 0
      AND Quantity <> 0
) pos
OUTER APPLY (
    SELECT COUNT(*) AS ActualOpenOrderCount
    FROM ExecutionOrders
    WHERE BotId = b.Id
      AND IsDeleted = 0
      AND State IN ('Received', 'GatePassed', 'Dispatching', 'Submitted', 'PartiallyFilled', 'CancelRequested')
) openOrders
OUTER APPLY (
    SELECT TOP (1) ExecutionEnvironment, ExecutorKind
    FROM ExecutionOrders
    WHERE OwnerUserId = b.OwnerUserId
      AND BotId = b.Id
      AND Symbol = N'$symbolSql'
      AND ExecutionEnvironment = 'Live'
      AND ExecutorKind = 'Binance'
      AND IsDeleted = 0
    ORDER BY CreatedDate DESC
) latest
LEFT JOIN ExchangeAccountSyncStates sync
    ON sync.OwnerUserId = b.OwnerUserId
   AND sync.ExchangeAccountId = b.ExchangeAccountId
   AND sync.Plane = 'Futures'
   AND sync.IsDeleted = 0
WHERE b.Id = '$botIdText'
  AND b.OwnerUserId = N'$userIdSql'
  AND b.Symbol = N'$symbolSql'
  AND b.IsDeleted = 0
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES;
"@

if ($null -eq $snapshot) {
    Write-Fail -Blocker "TargetBotNotFound" -Detail "No target bot row matched the soak scope."
}

$dbNetQuantity = [decimal]$snapshot.DbNetQuantity
$botOpenPositionCount = [int]$snapshot.OpenPositionCount
$botOpenOrderCount = [int]$snapshot.OpenOrderCount
$actualOpenOrderCount = [int]$snapshot.ActualOpenOrderCount
$latestEnvironment = if ($null -eq $snapshot.LatestEnvironment) { "none" } else { [string]$snapshot.LatestEnvironment }
$latestExecutorKind = if ($null -eq $snapshot.LatestExecutorKind) { "none" } else { [string]$snapshot.LatestExecutorKind }

if ($latestEnvironment -ne "Live" -or $latestExecutorKind -ne "Binance") {
    Write-Fail -Blocker "NoLiveBinanceEvidence" -Detail "latestEnvironment=$latestEnvironment latestExecutorKind=$latestExecutorKind"
}

if ($dbNetQuantity -ne [decimal]0 -or $botOpenPositionCount -ne 0) {
    Write-Fail -Blocker "PreflightOpenPositionNotFlat" -Detail "dbNetQuantity=$dbNetQuantity botOpenPositionCount=$botOpenPositionCount"
}

if ($botOpenOrderCount -ne 0 -or $actualOpenOrderCount -ne 0) {
    Write-Fail -Blocker "PreflightOpenOrderNotFlat" -Detail "botOpenOrderCount=$botOpenOrderCount actualOpenOrderCount=$actualOpenOrderCount"
}

$soakId = [Guid]::NewGuid().ToString("N")
$startedAtUtc = [DateTime]::UtcNow
$workerMarkerPath = Join-Path $repoRoot "artifacts\phase11-worker-runtime.json"
$workerProcessId = $null
$resolvedWorkerPath = $null
$lastHeartbeatAtUtc = $null
$mode = if ($PreflightOnly) { "PreflightOnly" } else { "Started" }

if (-not $PreflightOnly) {
    $runner = Join-Path $PSScriptRoot "phase10d-run-testnet-worker.ps1"
    & $runner `
        -WorkerPath $WorkerPath `
        -Server $Server `
        -Database $Database `
        -UserId $UserId `
        -BotId $BotId `
        -Symbol $Symbol `
        -FuturesRestBaseUrl $FuturesRestBaseUrl `
        -FuturesWebSocketBaseUrl $FuturesWebSocketBaseUrl `
        -RuntimeMarkerPath $workerMarkerPath `
        -StopExisting:$StopExisting.IsPresent

    if ($LASTEXITCODE -ne 0) {
        Write-Fail -Blocker "WorkerStartFailed" -Detail "phase10d-run-testnet-worker failed."
    }

    $workerMarker = Get-Content -LiteralPath $workerMarkerPath -Raw | ConvertFrom-Json -ErrorAction Stop
    $workerProcessId = [string]$workerMarker.ProcessId
    $resolvedWorkerPath = [string]$workerMarker.WorkerPath
    $lastHeartbeatAtUtc = [string]$workerMarker.LastHeartbeatAtUtc
}

$markerDirectory = Split-Path $RuntimeMarkerPath -Parent
if (-not [string]::IsNullOrWhiteSpace($markerDirectory)) {
    New-Item -ItemType Directory -Path $markerDirectory -Force | Out-Null
}

[ordered]@{
    SoakId = $soakId
    StartedAtUtc = $startedAtUtc.ToString("O")
    Server = $Server
    Database = $Database
    UserId = $UserId
    BotId = $BotId.ToString()
    Symbol = $Symbol.ToUpperInvariant()
    TestnetOnly = $true
    Mode = $mode
    WorkerProcessId = $workerProcessId
    WorkerPath = $resolvedWorkerPath
    WorkerRuntimeMarkerPath = $(if ($PreflightOnly) { $null } else { $workerMarkerPath })
    LastHeartbeatAtUtc = $lastHeartbeatAtUtc
    LatestEnvironment = $latestEnvironment
    LatestExecutorKind = $latestExecutorKind
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $RuntimeMarkerPath -Encoding UTF8

Write-Pass -SoakId $soakId -Mode $mode -WorkerProcessId $(if ($null -eq $workerProcessId) { "none" } else { $workerProcessId }) -WorkerPathValue $(if ($null -eq $resolvedWorkerPath) { "none" } else { $resolvedWorkerPath })
