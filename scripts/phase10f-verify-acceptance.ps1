[CmdletBinding()]
param(
    [string]$RuntimeMarkerPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($RuntimeMarkerPath)) {
    $RuntimeMarkerPath = Join-Path $repoRoot "artifacts\phase10d-worker-runtime.json"
}

$acceptedBlockers = @(
    "none",
    "PrivatePlaneStale",
    "PrivatePlaneUnavailable",
    "PrivatePlaneOutOfSync",
    "PilotCredentialValidationUnavailable",
    "PilotCredentialEnvironmentMismatch",
    "ExchangeAccountNotReady",
    "ClockDriftExceeded",
    "BinanceRegionRestricted451",
    "BinanceRejected",
    "ExchangeBalanceUnavailable",
    "SymbolMetadataUnavailable",
    "TestnetOrderRejected",
    "FuturesAccountUnavailable",
    "PilotTestnetEndpointMismatch"
)

function Write-Fail {
    param(
        [Parameter(Mandatory = $true)][string]$Blocker,
        [string]$LatestOrderId = "none",
        [string]$LatestEnvironment = "none",
        [string]$LatestExecutorKind = "none"
    )

    Write-Output "FAIL blocker=$Blocker latestOrderId=$LatestOrderId latestEnvironment=$LatestEnvironment latestExecutorKind=$LatestExecutorKind"
    exit 1
}

function Write-Pass {
    param(
        [Parameter(Mandatory = $true)][string]$Blocker,
        [string]$LatestOrderId = "none",
        [string]$LatestEnvironment = "none",
        [string]$LatestExecutorKind = "none"
    )

    Write-Output "PASS blocker=$Blocker latestOrderId=$LatestOrderId latestEnvironment=$LatestEnvironment latestExecutorKind=$LatestExecutorKind"
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
        if ($lines[$i] -match '__JSON_BEGIN__') {
            $beginIndex = $i
            continue
        }

        if ($lines[$i] -match '__JSON_END__') {
            $endIndex = $i
            break
        }
    }

    if ($beginIndex -lt 0 -or $endIndex -lt 0 -or $endIndex -le $beginIndex) {
        $fallback = (($lines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join [Environment]::NewLine).Trim()
        if ([string]::IsNullOrWhiteSpace($fallback)) {
            return $null
        }

        return $fallback
    }

    $jsonLines = @()
    for ($i = $beginIndex + 1; $i -lt $endIndex; $i++) {
        if (-not [string]::IsNullOrWhiteSpace($lines[$i])) {
            $jsonLines += $lines[$i]
        }
    }

    $json = ($jsonLines -join [Environment]::NewLine).Trim()
    if ([string]::IsNullOrWhiteSpace($json)) {
        return $null
    }

    return $json
}

function Invoke-SqlJson {
    param(
        [Parameter(Mandatory = $true)][string]$Server,
        [Parameter(Mandatory = $true)][string]$Database,
        [Parameter(Mandatory = $true)][string]$Query
    )

    $wrappedQuery = @"
SET NOCOUNT ON;
SELECT N'__JSON_BEGIN__' AS Phase10fMarker;
$Query
SELECT N'__JSON_END__' AS Phase10fMarker;
"@

    $tempFile = Join-Path $env:TEMP ("phase10f-verify-" + [Guid]::NewGuid().ToString("N") + ".sql")
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

function Test-TestnetEndpoint {
    param([Parameter(Mandatory = $true)][string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $false
    }

    try {
        $uri = [Uri]$Value
    }
    catch {
        return $false
    }

    $endpointHost = $uri.Host.ToLowerInvariant()
    $blockedHosts = @(
        "api.binance.com",
        "fapi.binance.com",
        "dapi.binance.com",
        "stream.binance.com",
        "fstream.binance.com"
    )

    if ($blockedHosts -contains $endpointHost) {
        return $false
    }

    return $endpointHost -eq "localhost" -or
        $endpointHost -eq "127.0.0.1" -or
        $endpointHost -eq "::1" -or
        $endpointHost.Contains("testnet") -or
        $endpointHost.Contains("proxy-testnet") -or
        $endpointHost -eq "binancefuture.com" -or
        $endpointHost.EndsWith(".binancefuture.com")
}

if (-not (Test-Path -LiteralPath $RuntimeMarkerPath)) {
    Write-Fail -Blocker "RuntimeMarkerMissing"
}

$marker = Get-Content -LiteralPath $RuntimeMarkerPath -Raw | ConvertFrom-Json -ErrorAction Stop
if ($null -eq $marker) {
    Write-Fail -Blocker "RuntimeMarkerUnreadable"
}

if (-not (Test-TestnetEndpoint ([string]$marker.FuturesRestBaseUrl)) -or
    -not (Test-TestnetEndpoint ([string]$marker.FuturesWebSocketBaseUrl))) {
    Write-Pass -Blocker "PilotTestnetEndpointMismatch"
}

Assert-SqlCmd

$server = [string]$marker.Server
$database = [string]$marker.Database
$cutoffUtc = [DateTime]::Parse(
    [string]$marker.StartedAtUtc,
    [System.Globalization.CultureInfo]::InvariantCulture,
    [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal
).ToString("yyyy-MM-dd HH:mm:ss.fffffff")

$latestOrder = Invoke-SqlJson -Server $server -Database $database -Query @"
DECLARE @cutoff datetime2(7) = '$cutoffUtc';
SELECT TOP (1)
    Id,
    CreatedDate,
    State,
    ExecutionEnvironment,
    ExecutorKind,
    FailureCode,
    FailureDetail
FROM ExecutionOrders
WHERE CreatedDate >= @cutoff
ORDER BY CreatedDate DESC
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES;
"@

$latestOrderId = "none"
$latestEnvironment = "none"
$latestExecutorKind = "none"

if ($null -ne $latestOrder) {
    if ($null -ne $latestOrder.Id) { $latestOrderId = [string]$latestOrder.Id }
    if ($null -ne $latestOrder.ExecutionEnvironment) { $latestEnvironment = [string]$latestOrder.ExecutionEnvironment }
    if ($null -ne $latestOrder.ExecutorKind) { $latestExecutorKind = [string]$latestOrder.ExecutorKind }
}

$latestLiveBinanceOrder = Invoke-SqlJson -Server $server -Database $database -Query @"
DECLARE @cutoff datetime2(7) = '$cutoffUtc';
SELECT TOP (1)
    Id,
    CreatedDate,
    State,
    ExecutionEnvironment,
    ExecutorKind,
    FailureCode,
    FailureDetail
FROM ExecutionOrders
WHERE CreatedDate >= @cutoff
  AND ExecutionEnvironment = 'Live'
  AND ExecutorKind = 'Binance'
ORDER BY CreatedDate DESC
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES;
"@

if ($null -eq $latestLiveBinanceOrder) {
    Write-Fail -Blocker "NoLiveBinanceDispatchObserved" -LatestOrderId $latestOrderId -LatestEnvironment $latestEnvironment -LatestExecutorKind $latestExecutorKind
}

$latestOrderId = [string]$latestLiveBinanceOrder.Id
$latestEnvironment = [string]$latestLiveBinanceOrder.ExecutionEnvironment
$latestExecutorKind = [string]$latestLiveBinanceOrder.ExecutorKind

$transitions = Invoke-SqlJson -Server $server -Database $database -Query @"
SELECT TOP (10)
    EventCode,
    State,
    OccurredAtUtc,
    SequenceNumber
FROM ExecutionOrderTransitions
WHERE ExecutionOrderId = '$latestOrderId'
ORDER BY SequenceNumber ASC, OccurredAtUtc ASC
FOR JSON PATH, INCLUDE_NULL_VALUES;
"@

if ($null -eq $transitions) {
    Write-Fail -Blocker "NoOrderTransitionsObserved" -LatestOrderId $latestOrderId -LatestEnvironment $latestEnvironment -LatestExecutorKind $latestExecutorKind
}

$transitionList = @($transitions)
$hasReceived = $transitionList | Where-Object { $_.EventCode -eq "Received" -or $_.State -eq "Received" }
if (-not $hasReceived) {
    Write-Fail -Blocker "MissingReceivedTransition" -LatestOrderId $latestOrderId -LatestEnvironment $latestEnvironment -LatestExecutorKind $latestExecutorKind
}

$blocker = "none"
if (-not [string]::IsNullOrWhiteSpace([string]$latestLiveBinanceOrder.FailureCode)) {
    $blocker = [string]$latestLiveBinanceOrder.FailureCode
}
elseif (-not [string]::IsNullOrWhiteSpace([string]$latestLiveBinanceOrder.State) -and
    [string]$latestLiveBinanceOrder.State -notin @("Filled", "Submitted", "Dispatching", "Received", "Accepted", "Open")) {
    $blocker = [string]$latestLiveBinanceOrder.State
}

if ($blocker -eq "StaleMarketData") {
    Write-Fail -Blocker "StaleMarketDataNotAccepted" -LatestOrderId $latestOrderId -LatestEnvironment $latestEnvironment -LatestExecutorKind $latestExecutorKind
}

if ($acceptedBlockers -notcontains $blocker) {
    Write-Fail -Blocker $blocker -LatestOrderId $latestOrderId -LatestEnvironment $latestEnvironment -LatestExecutorKind $latestExecutorKind
}

Write-Pass -Blocker $blocker -LatestOrderId $latestOrderId -LatestEnvironment $latestEnvironment -LatestExecutorKind $latestExecutorKind
