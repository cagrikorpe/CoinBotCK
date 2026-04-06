param(
    [string]$Root = 'D:\CkBot\Code\CoinBot',
    [string]$UserId = '7895009f-4918-4498-8676-a162b67c873c',
    [string]$BotId = '2a008c7d-8294-4423-8359-fcac999dddab',
    [string]$Symbol = 'BTCUSDT'
)

$ErrorActionPreference = 'Stop'
$artifactRoot = Join-Path $Root 'artifacts\pilot1-blocker-close-rerun'
if (-not (Test-Path $artifactRoot)) { New-Item -ItemType Directory -Path $artifactRoot | Out-Null }

$secretsOutput = dotnet user-secrets list --project (Join-Path $Root 'src\CoinBot.Worker\CoinBot.Worker.csproj')
$connectionLine = $secretsOutput | Where-Object { $_ -like 'ConnectionStrings:DefaultConnection*' } | Select-Object -First 1
if (-not $connectionLine) { throw 'DefaultConnection user-secret not found.' }
$connectionString = ($connectionLine -split '=', 2)[1].Trim()

function Invoke-SqlRows {
    param([string]$Query)
    $connection = [System.Data.SqlClient.SqlConnection]::new($connectionString)
    $command = $connection.CreateCommand()
    $command.CommandText = $Query
    $command.CommandTimeout = 60
    $connection.Open()
    try {
        $reader = $command.ExecuteReader()
        $rows = New-Object System.Collections.Generic.List[object]
        while ($reader.Read()) {
            $row = [ordered]@{}
            for ($i = 0; $i -lt $reader.FieldCount; $i++) {
                $row[$reader.GetName($i)] = if ($reader.IsDBNull($i)) { $null } else { $reader.GetValue($i) }
            }
            $rows.Add([pscustomobject]$row) | Out-Null
        }
        return $rows
    }
    finally {
        $connection.Dispose()
    }
}

function Invoke-SqlScalar {
    param([string]$Query)
    $connection = [System.Data.SqlClient.SqlConnection]::new($connectionString)
    $command = $connection.CreateCommand()
    $command.CommandText = $Query
    $command.CommandTimeout = 60
    $connection.Open()
    try { return $command.ExecuteScalar() }
    finally { $connection.Dispose() }
}

function Invoke-WorkerRun {
    param(
        [string]$Name,
        [bool]$PilotActivationEnabled,
        [int]$DurationSeconds
    )

    $stdoutPath = Join-Path $artifactRoot "$Name.stdout.log"
    $stderrPath = Join-Path $artifactRoot "$Name.stderr.log"
    Remove-Item $stdoutPath,$stderrPath -ErrorAction SilentlyContinue

    $envOverrides = [ordered]@{
        DOTNET_ENVIRONMENT = 'Development'
        ASPNETCORE_ENVIRONMENT = 'Development'
        JobOrchestration__Enabled = 'true'
        JobOrchestration__BotExecutionIntervalSeconds = '10'
        MarketData__Binance__Enabled = 'true'
        MarketData__Binance__SeedSymbols__0 = $Symbol
        MarketData__Scanner__Enabled = 'false'
        MarketData__Scanner__HandoffEnabled = 'false'
        ExchangeSync__Binance__Enabled = 'true'
        AI__Signal__Enabled = 'false'
        BotExecutionPilot__Enabled = 'true'
        BotExecutionPilot__PilotActivationEnabled = if ($PilotActivationEnabled) { 'true' } else { 'false' }
        BotExecutionPilot__AllowedUserIds__0 = $UserId
        BotExecutionPilot__AllowedUserIds__1 = ''
        BotExecutionPilot__AllowedBotIds__0 = ([Guid]$BotId).ToString('N')
        BotExecutionPilot__AllowedBotIds__1 = ''
        BotExecutionPilot__AllowedSymbols__0 = $Symbol
        BotExecutionPilot__AllowedSymbols__1 = ''
        BotExecutionPilot__AllowedSymbols__2 = ''
        BotExecutionPilot__DefaultSymbol = $Symbol
        BotExecutionPilot__Timeframe = '1m'
        BotExecutionPilot__MaxOpenPositionsPerUser = '1'
        BotExecutionPilot__PerBotCooldownSeconds = '300'
        BotExecutionPilot__PerSymbolCooldownSeconds = '300'
        BotExecutionPilot__MaxOrderNotional = '110'
        BotExecutionPilot__MaxDailyLossPercentage = '1'
        Logging__LogLevel__Default = 'Information'
    }

    $originalEnv = @{}
    foreach ($entry in $envOverrides.GetEnumerator()) {
        $originalEnv[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }

    try {
        $process = Start-Process dotnet -ArgumentList 'run --project src\CoinBot.Worker\CoinBot.Worker.csproj --no-build' -WorkingDirectory $Root -PassThru -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
        Start-Sleep -Seconds $DurationSeconds
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force
            $process.WaitForExit()
        }
        return [pscustomobject]@{ Name = $Name; ExitCode = $process.ExitCode; StdOut = $stdoutPath; StdErr = $stderrPath }
    }
    finally {
        foreach ($entry in $originalEnv.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
        }
    }
}

Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'dotnet.exe' -and $_.CommandLine -like '*CoinBot.Worker*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Start-Sleep -Seconds 2

$baselineCount = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM ExecutionOrders WHERE OwnerUserId = '$UserId' AND BotId = '$BotId';")
$warmup = Invoke-WorkerRun -Name 'warmup' -PilotActivationEnabled:$false -DurationSeconds 75
$warmupCount = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM ExecutionOrders WHERE OwnerUserId = '$UserId' AND BotId = '$BotId';")
$submitStartedAtUtc = [DateTime]::UtcNow
$submit = Invoke-WorkerRun -Name 'submit' -PilotActivationEnabled:$true -DurationSeconds 60
$submitStartSql = $submitStartedAtUtc.ToString('yyyy-MM-ddTHH:mm:ss.fffffff')
$newOrders = Invoke-SqlRows @"
SELECT TOP (10)
    Id,
    State,
    FailureCode,
    SubmittedToBroker,
    ExternalOrderId,
    FilledQuantity,
    ReconciliationStatus,
    CreatedDate,
    FailureDetail
FROM ExecutionOrders
WHERE OwnerUserId = '$UserId'
  AND BotId = '$BotId'
  AND CreatedDate >= '$submitStartSql'
ORDER BY CreatedDate DESC;
"@
$latestOrderId = if ($newOrders.Count -gt 0) { $newOrders[0].Id } else { $null }
$latestTransitions = if ($latestOrderId) {
    Invoke-SqlRows "SELECT TOP (10) State, Detail, OccurredAtUtc FROM ExecutionOrderTransitions WHERE ExecutionOrderId = '$latestOrderId' ORDER BY OccurredAtUtc;"
} else { @() }

[pscustomobject]@{
    BaselineOrderCount = $baselineCount
    WarmupOrderCount = $warmupCount
    WarmupCreatedOrder = ($warmupCount -gt $baselineCount)
    WarmupStdOut = $warmup.StdOut
    SubmitStdOut = $submit.StdOut
    NewOrders = $newOrders
    LatestTransitions = $latestTransitions
} | ConvertTo-Json -Depth 6
