[CmdletBinding()]
param(
    [string]$WorkerPath = "",
    [string]$Server = "localhost",
    [string]$Database = "CoinBotDb",
    [string]$UserId = "5a8675cf-cbcb-4c28-82fb-304aee442895",
    [guid]$BotId = "8EA3ED8B-C61F-4A44-A683-168AE9C69C43",
    [string]$Symbol = "SOLUSDT",
    [string]$FuturesRestBaseUrl = "https://testnet.binancefuture.com",
    [string]$FuturesWebSocketBaseUrl = "wss://fstream.binancefuture.com",
    [string]$RuntimeMarkerPath = "",
    [switch]$StopExisting
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($WorkerPath)) {
    $phase10cPath = Join-Path $repoRoot "artifacts\phase10c-worker-build\CoinBot.Worker.exe"
    $phase10dPath = Join-Path $repoRoot "artifacts\phase10d-worker-build\CoinBot.Worker.exe"
    $WorkerPath = if (Test-Path -LiteralPath $phase10dPath) { $phase10dPath } else { $phase10cPath }
}

if ([string]::IsNullOrWhiteSpace($RuntimeMarkerPath)) {
    $RuntimeMarkerPath = Join-Path $repoRoot "artifacts\phase10d-worker-runtime.json"
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

if (-not (Test-Path -LiteralPath $WorkerPath)) {
    throw "Worker artifact was not found: $WorkerPath"
}

if (-not (Test-TestnetEndpoint $FuturesRestBaseUrl) -or -not (Test-TestnetEndpoint $FuturesWebSocketBaseUrl)) {
    throw "Testnet-only endpoint guard rejected the configured endpoint. Worker was not started."
}

$existingWorkers = @(Get-Process -Name CoinBot.Worker -ErrorAction SilentlyContinue)
if ($existingWorkers.Count -gt 0) {
    if (-not $StopExisting) {
        $ids = ($existingWorkers | ForEach-Object { $_.Id }) -join ","
        throw "Existing CoinBot.Worker process detected: $ids. Re-run with -StopExisting after confirming the runtime window."
    }

    foreach ($existingProcess in $existingWorkers) {
        Stop-Process -Id $existingProcess.Id -Force
    }
}

$botIdCompact = $BotId.ToString("N")
$connectionString = "Server=$Server;Database=$Database;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True"
$workerInstanceId = "phase10d-" + ([Guid]::NewGuid().ToString("N").Substring(0, 8))

$envVars = [ordered]@{
    DOTNET_ENVIRONMENT = "Development"
    ASPNETCORE_ENVIRONMENT = "Development"
    DOTNET_CLI_HOME = (Join-Path $repoRoot ".dotnet")
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
    DOTNET_NOLOGO = "1"
    ConnectionStrings__DefaultConnection = $connectionString
    JobOrchestration__Enabled = "true"
    JobOrchestration__SchedulerPollIntervalSeconds = "1"
    JobOrchestration__BotExecutionIntervalSeconds = "1"
    JobOrchestration__WorkerInstanceId = $workerInstanceId
    MarketData__Binance__Enabled = "true"
    MarketData__Binance__RestBaseUrl = $FuturesRestBaseUrl
    MarketData__Binance__WebSocketBaseUrl = $FuturesWebSocketBaseUrl
    MarketData__Binance__SeedSymbols__0 = $Symbol
    MarketData__Scanner__Enabled = "true"
    MarketData__Scanner__HandoffEnabled = "true"
    ExchangeSync__Binance__Enabled = "true"
    ExchangeSync__Binance__RestBaseUrl = $FuturesRestBaseUrl
    ExchangeSync__Binance__WebSocketBaseUrl = $FuturesWebSocketBaseUrl
    ExchangeSync__Binance__SessionScanIntervalSeconds = "5"
    ExchangeSync__Binance__ReconnectDelaySeconds = "5"
    ExchangeSync__Binance__ReconciliationIntervalMinutes = "1"
    BotExecutionPilot__Enabled = "true"
    BotExecutionPilot__PilotActivationEnabled = "true"
    BotExecutionPilot__AllowGlobalSwitchBypass = "false"
    BotExecutionPilot__SignalEvaluationMode = "Live"
    BotExecutionPilot__ExecutionDispatchMode = "Live"
    BotExecutionPilot__DefaultSymbol = $Symbol
    BotExecutionPilot__Timeframe = "1m"
    BotExecutionPilot__DefaultLeverage = "1"
    BotExecutionPilot__DefaultMarginType = "ISOLATED"
    BotExecutionPilot__AllowedUserIds__0 = $UserId
    BotExecutionPilot__AllowedBotIds__0 = $botIdCompact
    BotExecutionPilot__AllowedSymbols__0 = $Symbol
    BotExecutionPilot__MaxPilotOrderNotional = "250"
    BotExecutionPilot__MaxOpenPositionsPerUser = "1"
    BotExecutionPilot__PerBotCooldownSeconds = "0"
    BotExecutionPilot__PerSymbolCooldownSeconds = "0"
    BotExecutionPilot__PrivatePlaneFreshnessThresholdSeconds = "120"
    BotExecutionPilot__PrimeHistoricalCandleCount = "200"
}

$previousValues = @{}
foreach ($key in $envVars.Keys) {
    $previousValues[$key] = [Environment]::GetEnvironmentVariable($key, "Process")
    [Environment]::SetEnvironmentVariable($key, [string]$envVars[$key], "Process")
}

try {
    $workingDirectory = Split-Path $WorkerPath -Parent
    $process = Start-Process -FilePath $WorkerPath -WorkingDirectory $workingDirectory -PassThru
}
finally {
    foreach ($key in $previousValues.Keys) {
        [Environment]::SetEnvironmentVariable($key, $previousValues[$key], "Process")
    }
}

$maxWaitSeconds = 90
$pollSeconds = 5
$deadlineUtc = [DateTime]::UtcNow.AddSeconds($maxWaitSeconds)
$heartbeatFresh = $false
$lastHeartbeatUtc = $null

while ([DateTime]::UtcNow -lt $deadlineUtc) {
    Start-Sleep -Seconds $pollSeconds

    $heartbeatRaw = & sqlcmd -S $Server -d $Database -E -h -1 -W -Q @"
SET NOCOUNT ON;
SELECT CONVERT(varchar(33), MAX(LastHeartbeatAtUtc), 126)
FROM BackgroundJobStates
WHERE JobType = 'BotExecution';
"@ 2>$null

    $heartbeatText = (($heartbeatRaw | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) | Select-Object -Last 1).Trim()
    if ([string]::IsNullOrWhiteSpace($heartbeatText)) {
        continue
    }

    try {
        $parsedHeartbeatUtc = [DateTime]::Parse(
            $heartbeatText,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal
        )
    }
    catch {
        continue
    }

    $lastHeartbeatUtc = $parsedHeartbeatUtc
    if ($parsedHeartbeatUtc -ge [DateTime]::UtcNow.AddSeconds(-30)) {
        $heartbeatFresh = $true
        break
    }
}

if (-not $heartbeatFresh) {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    $heartbeatLabel = if ($null -eq $lastHeartbeatUtc) { "none" } else { $lastHeartbeatUtc.ToString("O") }
    throw "Worker failed to produce a fresh BotExecution heartbeat within $maxWaitSeconds seconds. LastHeartbeatAtUtc=$heartbeatLabel"
}

$markerDirectory = Split-Path $RuntimeMarkerPath -Parent
if (-not [string]::IsNullOrWhiteSpace($markerDirectory)) {
    New-Item -ItemType Directory -Path $markerDirectory -Force | Out-Null
}

[ordered]@{
    StartedAtUtc = [DateTime]::UtcNow.ToString("O")
    WorkerPath = (Resolve-Path -LiteralPath $WorkerPath).Path
    ProcessId = $process.Id
    Server = $Server
    Database = $Database
    UserId = $UserId
    BotId = $BotId.ToString()
    Symbol = $Symbol
    TestnetOnly = $true
    FuturesRestBaseUrl = $FuturesRestBaseUrl
    FuturesWebSocketBaseUrl = $FuturesWebSocketBaseUrl
    ExecutionDispatchMode = "Live"
    SignalEvaluationMode = "Live"
    WorkerInstanceId = $workerInstanceId
    LastHeartbeatAtUtc = $(if ($null -eq $lastHeartbeatUtc) { $null } else { $lastHeartbeatUtc.ToString("O") })
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $RuntimeMarkerPath -Encoding UTF8

Write-Output "Started CoinBot.Worker PID=$($process.Id)"
Write-Output "Fresh BotExecution heartbeat observed at $($lastHeartbeatUtc.ToString("O"))"
Write-Output "WorkerPath=$((Resolve-Path -LiteralPath $WorkerPath).Path)"
Write-Output "RuntimeDb=$Server/$Database"
Write-Output "RuntimeMarkerPath=$RuntimeMarkerPath"
