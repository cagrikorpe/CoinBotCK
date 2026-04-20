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
    "BinanceRejected",
    "ExchangeRejected",
    "ExchangeSubmitRejected",
    "SymbolMetadataUnavailable",
    "EndpointScopeMismatch",
    "CredentialScopeMismatch",
    "TestnetEndpointMismatch",
    "SignatureRejected",
    "TimestampRejected",
    "ReduceOnlyRejected",
    "MarginModeRejected",
    "LeverageRejected",
    "FuturesAccountUnavailable",
    "ExchangeBalanceUnavailable",
    "BinanceRegionRestricted451",
    "InsufficientMargin",
    "FuturesMarginInsufficient",
    "OrderNotionalBelowMinimum",
    "BinanceExchangeInfoUnavailable",
    "ClockDriftExceeded"
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
        if ($lines[$i] -match '__JSON_BEGIN__') { $beginIndex = $i; continue }
        if ($lines[$i] -match '__JSON_END__') { $endIndex = $i; break }
    }
    if ($beginIndex -lt 0 -or $endIndex -le $beginIndex) { return $null }
    $json = (($lines[($beginIndex + 1)..($endIndex - 1)] | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join [Environment]::NewLine).Trim()
    return $(if ([string]::IsNullOrWhiteSpace($json)) { $null } else { $json })
}

function Invoke-SqlJson {
    param(
        [Parameter(Mandatory = $true)][string]$Server,
        [Parameter(Mandatory = $true)][string]$Database,
        [Parameter(Mandatory = $true)][string]$Query
    )
    $wrappedQuery = @"
SET NOCOUNT ON;
SELECT N'__JSON_BEGIN__' AS Phase10hMarker;
$Query
SELECT N'__JSON_END__' AS Phase10hMarker;
"@
    $tempFile = Join-Path $env:TEMP ("phase10h-verify-" + [Guid]::NewGuid().ToString("N") + ".sql")
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

if (-not (Test-Path -LiteralPath $RuntimeMarkerPath)) {
    Write-Fail -Blocker "RuntimeMarkerMissing"
}

$marker = Get-Content -LiteralPath $RuntimeMarkerPath -Raw | ConvertFrom-Json -ErrorAction Stop
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
  AND ExecutionEnvironment = 'Live'
  AND ExecutorKind = 'Binance'
ORDER BY CreatedDate DESC
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES;
"@

if ($null -eq $latestOrder) {
    Write-Fail -Blocker "NoLiveBinanceDispatchObserved"
}

$latestOrderId = [string]$latestOrder.Id
$latestEnvironment = [string]$latestOrder.ExecutionEnvironment
$latestExecutorKind = [string]$latestOrder.ExecutorKind

$transitions = Invoke-SqlJson -Server $server -Database $database -Query @"
SELECT TOP (20)
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
if (-not ($transitionList | Where-Object { $_.EventCode -eq "Received" -or $_.State -eq "Received" })) {
    Write-Fail -Blocker "MissingReceivedTransition" -LatestOrderId $latestOrderId -LatestEnvironment $latestEnvironment -LatestExecutorKind $latestExecutorKind
}
if (-not ($transitionList | Where-Object { $_.EventCode -eq "Dispatching" -or $_.State -eq "Dispatching" })) {
    Write-Fail -Blocker "MissingDispatchingTransition" -LatestOrderId $latestOrderId -LatestEnvironment $latestEnvironment -LatestExecutorKind $latestExecutorKind
}

$dispatchAudit = Invoke-SqlJson -Server $server -Database $database -Query @"
DECLARE @cutoff datetime2(7) = '$cutoffUtc';
SELECT TOP (1)
    CreatedDate,
    Outcome,
    Context
FROM AuditLogs
WHERE CreatedDate >= @cutoff
  AND Action = 'TradeExecution.Dispatch'
  AND Context LIKE '%ExecutionOrderId=$latestOrderId%'
ORDER BY CreatedDate DESC
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES;
"@

if ($null -ne $dispatchAudit -and $null -ne $dispatchAudit.Context) {
    $contextText = [string]$dispatchAudit.Context
    if ($contextText.IndexOf("EndpointScopes=PrivateRest:Demo", [StringComparison]::Ordinal) -ge 0 -or
        $contextText.IndexOf("CredentialEnvironmentScope=Demo", [StringComparison]::Ordinal) -ge 0 -or
        $contextText.IndexOf('"PrivateRestEnvironmentScope":"Demo"', [StringComparison]::Ordinal) -ge 0 -or
        $contextText.IndexOf('"PrivateSocketEnvironmentScope":"Demo"', [StringComparison]::Ordinal) -ge 0 -or
        $contextText.IndexOf('"MarketDataRestEnvironmentScope":"Demo"', [StringComparison]::Ordinal) -ge 0 -or
        $contextText.IndexOf('"MarketDataSocketEnvironmentScope":"Demo"', [StringComparison]::Ordinal) -ge 0 -or
        $contextText.IndexOf('"CredentialEnvironmentScope":"Demo"', [StringComparison]::Ordinal) -ge 0) {
        Write-Fail -Blocker "DemoScopeDriftObserved" -LatestOrderId $latestOrderId -LatestEnvironment $latestEnvironment -LatestExecutorKind $latestExecutorKind
    }
}

$credentialAudit = Invoke-SqlJson -Server $server -Database $database -Query @"
DECLARE @cutoff datetime2(7) = '$cutoffUtc';
SELECT TOP (1)
    CreatedDate,
    Outcome,
    Context
FROM AuditLogs
WHERE CreatedDate >= @cutoff
  AND Action = 'ExchangeCredential.Accessed'
  AND Actor = 'system:phase10e-testnet-dispatch'
ORDER BY CreatedDate DESC
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES;
"@

if ($null -ne $credentialAudit -and [string]$credentialAudit.Outcome -like '*DecryptFailed*') {
    Write-Fail -Blocker "DecryptFailed" -LatestOrderId $latestOrderId -LatestEnvironment $latestEnvironment -LatestExecutorKind $latestExecutorKind
}

$blocker = "none"
if (-not [string]::IsNullOrWhiteSpace([string]$latestOrder.FailureCode)) {
    $blocker = [string]$latestOrder.FailureCode
}
elseif (-not [string]::IsNullOrWhiteSpace([string]$latestOrder.State) -and
    [string]$latestOrder.State -notin @("Filled", "Submitted", "Dispatching", "Received", "Accepted", "Open")) {
    $blocker = [string]$latestOrder.State
}

$failureDetail = [string]$latestOrder.FailureDetail
if ($blocker -in @("StaleMarketData", "PrivatePlaneStale", "NoNonDemoDispatchObserved", "DecryptFailed", "CredentialsUnavailableBecauseDecryptionFailed")) {
    Write-Fail -Blocker $blocker -LatestOrderId $latestOrderId -LatestEnvironment $latestEnvironment -LatestExecutorKind $latestExecutorKind
}
if ($blocker -eq "DispatchFailed" -or $failureDetail.IndexOf("decryption failed", [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    Write-Fail -Blocker "DispatchFailedDecryptOrGeneric" -LatestOrderId $latestOrderId -LatestEnvironment $latestEnvironment -LatestExecutorKind $latestExecutorKind
}
if ($acceptedBlockers -notcontains $blocker) {
    Write-Fail -Blocker $blocker -LatestOrderId $latestOrderId -LatestEnvironment $latestEnvironment -LatestExecutorKind $latestExecutorKind
}

Write-Pass -Blocker $blocker -LatestOrderId $latestOrderId -LatestEnvironment $latestEnvironment -LatestExecutorKind $latestExecutorKind
