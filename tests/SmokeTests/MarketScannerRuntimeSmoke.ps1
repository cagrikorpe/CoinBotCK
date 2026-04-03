$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$diagRoot = Join-Path $repoRoot '.diag\market-scanner-runtime-smoke'
$summaryPath = Join-Path $diagRoot 'market-scanner-runtime-smoke-summary.json'
$pageHtmlPath = Join-Path $diagRoot 'admin-system-health.html'
$strategyPageHtmlPath = Join-Path $diagRoot 'admin-strategy-ai-monitoring.html'
$strategyBuilderHtmlPath = Join-Path $diagRoot 'strategy-builder.html'
$webStdOutPath = Join-Path $diagRoot 'web.stdout.log'
$webStdErrPath = Join-Path $diagRoot 'web.stderr.log'

function New-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return $listener.LocalEndpoint.Port } finally { $listener.Stop() }
}

function Wait-Until {
    param([string]$Name, [scriptblock]$Condition, [int]$TimeoutSeconds = 60, [int]$PollMilliseconds = 500)

    $startedAt = Get-Date
    $lastError = $null

    while (((Get-Date) - $startedAt).TotalSeconds -lt $TimeoutSeconds) {
        try {
            $result = & $Condition
            if ($result) { return $result }
        } catch {
            $lastError = $_
        }

        Start-Sleep -Milliseconds $PollMilliseconds
    }

    if ($lastError) {
        throw "Timed out while waiting for $Name. Last error: $($lastError.Exception.Message)"
    }

    throw "Timed out while waiting for $Name."
}

function New-SqlConnection {
    param([string]$ConnectionString)

    $connection = [System.Data.SqlClient.SqlConnection]::new($ConnectionString)
    $connection.Open()
    return $connection
}

function Invoke-SqlNonQuery {
    param([string]$ConnectionString, [string]$CommandText, [hashtable]$Parameters = @{})

    $connection = New-SqlConnection -ConnectionString $ConnectionString
    try {
        $command = $connection.CreateCommand()
        $command.CommandTimeout = 120
        $command.CommandText = $CommandText
        foreach ($entry in $Parameters.GetEnumerator()) {
            $null = $command.Parameters.AddWithValue("@$($entry.Key)", $entry.Value ?? [DBNull]::Value)
        }

        return $command.ExecuteNonQuery()
    }
    finally {
        $connection.Dispose()
    }
}

function Invoke-SqlRow {
    param([string]$ConnectionString, [string]$CommandText, [hashtable]$Parameters = @{})

    $connection = New-SqlConnection -ConnectionString $ConnectionString
    try {
        $command = $connection.CreateCommand()
        $command.CommandTimeout = 120
        $command.CommandText = $CommandText
        foreach ($entry in $Parameters.GetEnumerator()) {
            $null = $command.Parameters.AddWithValue("@$($entry.Key)", $entry.Value ?? [DBNull]::Value)
        }

        $reader = $command.ExecuteReader()
        try {
            if (-not $reader.Read()) { return $null }
            $row = [ordered]@{}
            for ($index = 0; $index -lt $reader.FieldCount; $index++) {
                $row[$reader.GetName($index)] = if ($reader.IsDBNull($index)) { $null } else { $reader.GetValue($index) }
            }
            return [pscustomobject]$row
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $connection.Dispose()
    }
}

function Invoke-SqlRows {
    param([string]$ConnectionString, [string]$CommandText, [hashtable]$Parameters = @{})

    $connection = New-SqlConnection -ConnectionString $ConnectionString
    try {
        $command = $connection.CreateCommand()
        $command.CommandTimeout = 120
        $command.CommandText = $CommandText
        foreach ($entry in $Parameters.GetEnumerator()) {
            $null = $command.Parameters.AddWithValue("@$($entry.Key)", $entry.Value ?? [DBNull]::Value)
        }

        $rows = New-Object System.Collections.Generic.List[object]
        $reader = $command.ExecuteReader()
        try {
            while ($reader.Read()) {
                $row = [ordered]@{}
                for ($index = 0; $index -lt $reader.FieldCount; $index++) {
                    $row[$reader.GetName($index)] = if ($reader.IsDBNull($index)) { $null } else { $reader.GetValue($index) }
                }
                $rows.Add([pscustomobject]$row)
            }
        }
        finally {
            $reader.Dispose()
        }

        return $rows
    }
    finally {
        $connection.Dispose()
    }
}

function Ensure-SqlDatabaseExists {
    param([string]$ConnectionString)

    $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($ConnectionString)
    $databaseName = $builder.InitialCatalog
    $masterConnectionString = $ConnectionString -replace 'Database=[^;]+', 'Database=master'
    $escapedDatabaseName = $databaseName.Replace(']', ']]')
    Invoke-SqlNonQuery -ConnectionString $masterConnectionString -CommandText "IF DB_ID(N'$databaseName') IS NULL BEGIN EXEC(N'CREATE DATABASE [$escapedDatabaseName]') END;" | Out-Null
}

function Start-ManagedProcess {
    param([string]$FilePath, [string[]]$ArgumentList, [string]$WorkingDirectory, [string]$StandardOutputPath, [string]$StandardErrorPath, [hashtable]$EnvironmentVariables)

    return Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -WorkingDirectory $WorkingDirectory -RedirectStandardOutput $StandardOutputPath -RedirectStandardError $StandardErrorPath -Environment $EnvironmentVariables -PassThru -WindowStyle Hidden
}

function Stop-ManagedProcess {
    param($Process)

    if ($null -eq $Process) { return }

    try {
        if (-not $Process.HasExited) {
            Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
            $null = $Process.WaitForExit(5000)
        }
    } catch {
    } finally {
        try { $Process.Dispose() } catch {}
    }
}

function Seed-MarketScannerCandles {
    param([string]$ConnectionString, [datetime]$UtcNow)

    $btcOpen = $UtcNow.AddMinutes(-2)
    $btcClose = $UtcNow.AddMinutes(-1)
    $solOpen = $UtcNow.AddMinutes(-2)
    $solClose = $UtcNow.AddMinutes(-1)

    Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText @"
INSERT INTO HistoricalMarketCandles (Id, Symbol, Interval, OpenTimeUtc, CloseTimeUtc, OpenPrice, HighPrice, LowPrice, ClosePrice, Volume, ReceivedAtUtc, Source, CreatedDate, UpdatedDate, IsDeleted)
VALUES
(NEWID(), 'BTCUSDT', '1m', @BtcOpen, @BtcClose, 100.0, 100.0, 100.0, 100.0, 2000.0, @BtcClose, 'market-scanner-smoke', @UtcNow, @UtcNow, 0),
(NEWID(), 'SOLUSDT', '1m', @SolOpen, @SolClose, 20.0, 20.0, 20.0, 20.0, 10.0, @SolClose, 'market-scanner-smoke', @UtcNow, @UtcNow, 0);
"@ -Parameters @{ BtcOpen = $btcOpen; BtcClose = $btcClose; SolOpen = $solOpen; SolClose = $solClose; UtcNow = $UtcNow } | Out-Null
}

function Seed-MarketScannerRuntimeGraph {
    param([string]$ConnectionString, [string]$AdminEmail, [datetime]$UtcNow)

    $adminRow = Wait-Until -Name 'admin user seed' -TimeoutSeconds 90 -Condition {
        return Invoke-SqlRow -ConnectionString $ConnectionString -CommandText "SELECT TOP (1) Id FROM AspNetUsers WHERE Email = @AdminEmail ORDER BY Id;" -Parameters @{ AdminEmail = $AdminEmail }
    }

    Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText @"
IF NOT EXISTS (SELECT 1 FROM GlobalExecutionSwitches)
BEGIN
    INSERT INTO GlobalExecutionSwitches (Id, TradeMasterState, DemoModeEnabled, LiveModeApprovedAtUtc, LiveModeApprovalReference, CreatedDate, UpdatedDate, IsDeleted)
    VALUES (NEWID(), 'Armed', 0, @UtcNow, 'scanner-handoff-smoke', @UtcNow, @UtcNow, 0);
END;

IF NOT EXISTS (SELECT 1 FROM RiskProfiles WHERE OwnerUserId = @OwnerUserId AND IsDeleted = 0)
BEGIN
    INSERT INTO RiskProfiles (Id, OwnerUserId, ProfileName, MaxDailyLossPercentage, MaxPositionSizePercentage, MaxLeverage, KillSwitchEnabled, CreatedDate, UpdatedDate, IsDeleted)
    VALUES (NEWID(), @OwnerUserId, 'Scanner Smoke Risk', 10.0, 100.0, 2.0, 0, @UtcNow, @UtcNow, 0);
END;

IF NOT EXISTS (SELECT 1 FROM TradingStrategies WHERE OwnerUserId = @OwnerUserId AND StrategyKey = 'scanner-handoff-smoke' AND IsDeleted = 0)
BEGIN
    DECLARE @StrategyId uniqueidentifier = NEWID();
    INSERT INTO TradingStrategies (Id, OwnerUserId, StrategyKey, DisplayName, PromotionState, PublishedMode, PublishedAtUtc, LivePromotionApprovedAtUtc, LivePromotionApprovalReference, CreatedDate, UpdatedDate, IsDeleted)
    VALUES (@StrategyId, @OwnerUserId, 'scanner-handoff-smoke', 'Scanner Handoff Smoke', 'LivePublished', 'Live', @UtcNow, @UtcNow, 'scanner-handoff-smoke', @UtcNow, @UtcNow, 0);

    INSERT INTO TradingStrategyVersions (Id, OwnerUserId, TradingStrategyId, SchemaVersion, VersionNumber, Status, DefinitionJson, PublishedAtUtc, ArchivedAtUtc, CreatedDate, UpdatedDate, IsDeleted)
    VALUES (NEWID(), @OwnerUserId, @StrategyId, 1, 1, 'Published', @DefinitionJson, @UtcNow, NULL, @UtcNow, @UtcNow, 0);
END;

IF NOT EXISTS (SELECT 1 FROM TradingBots WHERE OwnerUserId = @OwnerUserId AND Symbol = 'BTCUSDT' AND IsDeleted = 0)
BEGIN
    INSERT INTO TradingBots (Id, OwnerUserId, Name, StrategyKey, Symbol, Quantity, ExchangeAccountId, Leverage, MarginType, IsEnabled, TradingModeOverride, TradingModeApprovedAtUtc, TradingModeApprovalReference, OpenOrderCount, OpenPositionCount, CreatedDate, UpdatedDate, IsDeleted)
    VALUES (NEWID(), @OwnerUserId, 'Scanner Handoff Smoke Bot', 'scanner-handoff-smoke', 'BTCUSDT', NULL, NULL, NULL, NULL, 1, 'Live', @UtcNow, 'scanner-handoff-smoke', 0, 0, @UtcNow, @UtcNow, 0);
END;
"@ -Parameters @{
        OwnerUserId = $adminRow.Id
        UtcNow = $UtcNow
        DefinitionJson = '{ "schemaVersion": 2, "metadata": { "templateKey": "rsi-reversal", "templateName": "RSI Reversal" }, "entry": { "operator": "all", "ruleId": "entry-root", "ruleType": "group", "timeframe": "1m", "weight": 1, "enabled": true, "rules": [ { "ruleId": "entry-mode", "ruleType": "context", "path": "context.mode", "comparison": "equals", "value": "Live", "timeframe": "1m", "weight": 20, "enabled": true }, { "ruleId": "entry-rsi", "ruleType": "rsi", "path": "indicator.rsi.isReady", "comparison": "equals", "value": true, "timeframe": "1m", "weight": 80, "enabled": true } ] }, "risk": { "operator": "all", "ruleId": "risk-root", "ruleType": "group", "timeframe": "1m", "weight": 1, "enabled": true, "rules": [ { "ruleId": "risk-sample", "ruleType": "data-quality", "path": "indicator.sampleCount", "comparison": "greaterThanOrEqual", "value": 34, "timeframe": "1m", "weight": 20, "enabled": true } ] } }'
    } | Out-Null

    return $adminRow.Id
}

function Seed-MarketScannerHandoffCandles {
    param([string]$ConnectionString, [datetime]$UtcNow)

    Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText @"
DELETE FROM HistoricalMarketCandles
WHERE Symbol IN ('BTCUSDT', 'SOLUSDT')
  AND Interval = '1m';
"@ | Out-Null

    for ($index = 59; $index -ge 0; $index--) {
        $btcClose = $UtcNow.AddMinutes(-$index)
        $btcOpen = $btcClose.AddMinutes(-1)
        $solClose = $UtcNow.AddMinutes(-$index)
        $solOpen = $solClose.AddMinutes(-1)

        Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText @"
INSERT INTO HistoricalMarketCandles (Id, Symbol, Interval, OpenTimeUtc, CloseTimeUtc, OpenPrice, HighPrice, LowPrice, ClosePrice, Volume, ReceivedAtUtc, Source, CreatedDate, UpdatedDate, IsDeleted)
VALUES
(NEWID(), 'BTCUSDT', '1m', @BtcOpen, @BtcClose, 100.0, 101.0, 99.0, 100.0, 2000.0, @BtcClose, 'market-scanner-handoff-smoke', @UtcNow, @UtcNow, 0),
(NEWID(), 'SOLUSDT', '1m', @SolOpen, @SolClose, 20.0, 20.5, 19.5, 20.0, 0.1, @SolClose, 'market-scanner-handoff-smoke', @UtcNow, @UtcNow, 0);
"@ -Parameters @{ BtcOpen = $btcOpen; BtcClose = $btcClose; SolOpen = $solOpen; SolClose = $solClose; UtcNow = $UtcNow } | Out-Null
    }
}

function Get-LatestScannerHandoff {
    param([string]$ConnectionString)

    return Invoke-SqlRow -ConnectionString $ConnectionString -CommandText @"
SELECT TOP (1)
    Id,
    ScanCycleId,
    SelectedSymbol,
    SelectedTimeframe,
    CandidateRank,
    CandidateScore,
    SelectionReason,
    OwnerUserId,
    BotId,
    StrategyKey,
    StrategyDecisionOutcome,
    StrategyVetoReasonCode,
    StrategyScore,
    ExecutionRequestStatus,
    BlockerCode,
    BlockerDetail,
    GuardSummary,
    CorrelationId,
    CompletedAtUtc
FROM MarketScannerHandoffAttempts
ORDER BY CompletedAtUtc DESC, CreatedDate DESC;
"@
}
function Get-AntiforgeryToken {
    param([string]$Html)

    $match = [regex]::Match($Html, 'name="__RequestVerificationToken"[^>]*value="(?<token>[^"]+)"', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $match.Success) {
        $match = [regex]::Match($Html, 'value="(?<token>[^"]+)"[^>]*name="__RequestVerificationToken"', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    }
    if (-not $match.Success) {
        throw 'Antiforgery token was not found on the login page.'
    }

    return [System.Net.WebUtility]::HtmlDecode($match.Groups['token'].Value)
}

function Read-HtmlText {
    param([string]$Html, [string]$DataAttribute)

    $pattern = '<[^>]*' + [regex]::Escape($DataAttribute) + '[^>]*>(?<value>.*?)</[^>]+>'
    $match = [regex]::Match($Html, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $match.Success) { return $null }

    $value = [regex]::Replace($match.Groups['value'].Value, '<[^>]+>', ' ')
    $value = [System.Net.WebUtility]::HtmlDecode($value)
    return [regex]::Replace($value, '\s+', ' ').Trim()
}

function Read-HtmlAttribute {
    param([string]$Html, [string]$SelectorAttribute, [string]$ValueAttribute)

    $pattern = '<[^>]*' + [regex]::Escape($SelectorAttribute) + '[^>]*' + [regex]::Escape($ValueAttribute) + '="(?<value>[^"]*)"[^>]*>'
    $match = [regex]::Match($Html, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $match.Success) {
        $pattern = '<[^>]*' + [regex]::Escape($ValueAttribute) + '="(?<value>[^"]*)"[^>]*' + [regex]::Escape($SelectorAttribute) + '[^>]*>'
        $match = [regex]::Match($Html, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Singleline)
    }

    if (-not $match.Success) { return $null }
    return [System.Net.WebUtility]::HtmlDecode($match.Groups['value'].Value).Trim()
}

function Get-LatestScannerCycle {
    param([string]$ConnectionString)

    return Invoke-SqlRow -ConnectionString $ConnectionString -CommandText @"
SELECT TOP (1)
    Id,
    StartedAtUtc,
    CompletedAtUtc,
    UniverseSource,
    ScannedSymbolCount,
    EligibleCandidateCount,
    TopCandidateCount,
    BestCandidateSymbol,
    BestCandidateScore,
    Summary
FROM MarketScannerCycles
WHERE ScannedSymbolCount >= 2
ORDER BY CompletedAtUtc DESC, CreatedDate DESC;
"@
}

function Get-ScannerCandidates {
    param([string]$ConnectionString, [guid]$ScanCycleId)

    return Invoke-SqlRows -ConnectionString $ConnectionString -CommandText @"
SELECT Symbol, UniverseSource, LastCandleAtUtc, LastPrice, QuoteVolume24h, IsEligible, RejectionReason, Score, Rank, IsTopCandidate
FROM MarketScannerCandidates
WHERE ScanCycleId = @ScanCycleId
ORDER BY Symbol;
"@ -Parameters @{ ScanCycleId = $ScanCycleId }
}

if (Test-Path $diagRoot) {
    Remove-Item $diagRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $diagRoot | Out-Null

$smokeDatabaseName = 'CoinBotMarketScannerSmoke_' + [Guid]::NewGuid().ToString('N')
$baseUrl = 'http://127.0.0.1:' + (New-FreeTcpPort)
$connectionString = 'Server=(localdb)\MSSQLLocalDB;Database=' + $smokeDatabaseName + ';Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True'
$adminEmail = 'scanner.admin.' + [Guid]::NewGuid().ToString('N') + '@coinbot.test'
$adminPassword = 'Passw0rd!Scanner1'
$webProcess = $null

Ensure-SqlDatabaseExists -ConnectionString $connectionString

$environmentVariables = @{
    DOTNET_CLI_HOME = (Join-Path $repoRoot '.dotnet')
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_NOLOGO = '1'
    ASPNETCORE_ENVIRONMENT = 'Development'
    DOTNET_ENVIRONMENT = 'Development'
    ASPNETCORE_URLS = $baseUrl
    ConnectionStrings__DefaultConnection = $connectionString
    IdentitySeed__SuperAdminEmail = $adminEmail
    IdentitySeed__SuperAdminPassword = $adminPassword
    IdentitySeed__SuperAdminFullName = 'Scanner Smoke Admin'
    ExchangeSync__Binance__Enabled = 'false'
    MarketData__Binance__Enabled = 'false'
    MarketData__Scanner__Enabled = 'true'
    MarketData__Scanner__HandoffEnabled = 'true'
    MarketData__Scanner__ScanIntervalSeconds = '5'
    MarketData__Scanner__MaxUniverseSymbols = '20'
    MarketData__Scanner__TopCandidateCount = '2'
    MarketData__Scanner__Min24hQuoteVolume = '1000'
    MarketData__Scanner__MaxDataAgeSeconds = '900'
    MarketData__Binance__SeedSymbols__0 = 'BTCUSDT'
    MarketData__Binance__SeedSymbols__1 = 'SOLUSDT'
    BotExecutionPilot__SignalEvaluationMode = 'Live'
    BotExecutionPilot__PerBotCooldownSeconds = '0'
    BotExecutionPilot__PerSymbolCooldownSeconds = '0'
    BotExecutionPilot__PrimeHistoricalCandleCount = '50'
}

try {
    $webProcess = Start-ManagedProcess -FilePath 'dotnet' -ArgumentList @('run', '--project', 'src\CoinBot.Web\CoinBot.Web.csproj', '--no-build', '--no-launch-profile') -WorkingDirectory $repoRoot -StandardOutputPath $webStdOutPath -StandardErrorPath $webStdErrPath -EnvironmentVariables $environmentVariables

    Wait-Until -Name 'web startup' -TimeoutSeconds 90 -Condition {
        try {
            $response = Invoke-WebRequest -Uri ($baseUrl + '/Auth/Login') -MaximumRedirection 0 -SkipHttpErrorCheck
            return $response.StatusCode -in 200, 302
        } catch {
            return $false
        }
    } | Out-Null

    Wait-Until -Name 'MarketScannerCycles table' -TimeoutSeconds 90 -Condition {
        $row = Invoke-SqlRow -ConnectionString $connectionString -CommandText "SELECT COUNT(*) AS TableCount FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'MarketScannerCycles';"
        return $row -and $row.TableCount -eq 1
    } | Out-Null

    $seededAtUtc = [DateTime]::UtcNow
    $adminUserId = Seed-MarketScannerRuntimeGraph -ConnectionString $connectionString -AdminEmail $adminEmail -UtcNow $seededAtUtc
    Seed-MarketScannerHandoffCandles -ConnectionString $connectionString -UtcNow $seededAtUtc

    $latestCycle = Wait-Until -Name 'market scanner cycle' -TimeoutSeconds 120 -Condition {
        Seed-MarketScannerHandoffCandles -ConnectionString $connectionString -UtcNow ([DateTime]::UtcNow)
        $cycleRow = Get-LatestScannerCycle -ConnectionString $connectionString
        if ($null -eq $cycleRow) { return $null }
        if ($cycleRow.BestCandidateSymbol -ne 'BTCUSDT') { return $null }
        return $cycleRow
    }

    $latestHandoff = Wait-Until -Name 'market scanner handoff' -TimeoutSeconds 120 -Condition {
        Seed-MarketScannerHandoffCandles -ConnectionString $connectionString -UtcNow ([DateTime]::UtcNow)
        $handoffRow = Get-LatestScannerHandoff -ConnectionString $connectionString
        if ($null -eq $handoffRow) { return $null }
        if ($handoffRow.SelectedSymbol -ne 'BTCUSDT') { return $null }
        if ($handoffRow.ExecutionRequestStatus -notin 'Prepared', 'Blocked') { return $null }
        if ($handoffRow.ExecutionRequestStatus -eq 'Blocked' -and [string]::IsNullOrWhiteSpace($handoffRow.BlockerCode)) { return $null }
        return $handoffRow
    }

    $candidateRows = Get-ScannerCandidates -ConnectionString $connectionString -ScanCycleId $latestCycle.Id
    $heartbeatRow = Invoke-SqlRow -ConnectionString $connectionString -CommandText "SELECT TOP (1) WorkerKey, HealthState, CircuitBreakerState, LastErrorCode, Detail FROM WorkerHeartbeats WHERE WorkerKey = 'market-scanner';"

    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $loginPage = Invoke-WebRequest -Uri ($baseUrl + '/Auth/Login') -WebSession $session
    $token = Get-AntiforgeryToken -Html $loginPage.Content
    $loginBody = @{ EmailOrUserName = $adminEmail; Password = $adminPassword; __RequestVerificationToken = $token }
    $loginResponse = Invoke-WebRequest -Uri ($baseUrl + '/Auth/Login') -Method Post -WebSession $session -Body $loginBody
    if ($loginResponse.StatusCode -notin 200, 302) {
        throw "Admin login failed with HTTP $($loginResponse.StatusCode)."
    }

    $adminPage = Invoke-WebRequest -Uri ($baseUrl + '/admin/SystemHealth') -WebSession $session
    Set-Content -Path $pageHtmlPath -Value $adminPage.Content -Encoding UTF8

    $strategyPage = Invoke-WebRequest -Uri ($baseUrl + '/admin/StrategyAiMonitoring') -WebSession $session
    Set-Content -Path $strategyPageHtmlPath -Value $strategyPage.Content -Encoding UTF8

    $strategyBuilderPage = Invoke-WebRequest -Uri ($baseUrl + '/StrategyBuilder') -WebSession $session
    Set-Content -Path $strategyBuilderHtmlPath -Value $strategyBuilderPage.Content -Encoding UTF8

    $summary = [ordered]@{
        SmokeDatabaseName = $smokeDatabaseName
        BaseUrl = $baseUrl
        ScanCycleId = $latestCycle.Id
        ScannedSymbolCount = $latestCycle.ScannedSymbolCount
        EligibleCandidateCount = $latestCycle.EligibleCandidateCount
        UniverseSourceDb = $latestCycle.UniverseSource
        BestCandidateSymbolDb = $latestCycle.BestCandidateSymbol
        BestCandidateScoreDb = $latestCycle.BestCandidateScore
        CandidatesDb = $candidateRows
        WorkerHeartbeatDb = $heartbeatRow
        AdminUserId = $adminUserId
        LatestHandoffDb = $latestHandoff
        UiLastScan = Read-HtmlText -Html $adminPage.Content -DataAttribute 'data-cb-scanner-last-scan'
        UiScanCycle = Read-HtmlText -Html $adminPage.Content -DataAttribute 'data-cb-scanner-cycle'
        UiUniverseSource = Read-HtmlText -Html $adminPage.Content -DataAttribute 'data-cb-scanner-universe'
        UiCounts = Read-HtmlText -Html $adminPage.Content -DataAttribute 'data-cb-scanner-counts'
        UiBestSymbol = Read-HtmlText -Html $adminPage.Content -DataAttribute 'data-cb-scanner-best-symbol'
        UiBestScore = Read-HtmlText -Html $adminPage.Content -DataAttribute 'data-cb-scanner-best-score'
        UiRejectedSummary = Read-HtmlText -Html $adminPage.Content -DataAttribute 'data-cb-scanner-rejected-summary'
        UiHandoffStatus = Read-HtmlText -Html $adminPage.Content -DataAttribute 'data-cb-scanner-handoff-status'
        UiHandoffSymbol = Read-HtmlText -Html $adminPage.Content -DataAttribute 'data-cb-scanner-handoff-symbol'
        UiHandoffSelection = Read-HtmlText -Html $adminPage.Content -DataAttribute 'data-cb-scanner-handoff-selection'
        UiHandoffStrategy = Read-HtmlText -Html $adminPage.Content -DataAttribute 'data-cb-scanner-handoff-strategy'
        UiHandoffBlocker = Read-HtmlText -Html $adminPage.Content -DataAttribute 'data-cb-scanner-handoff-blocker'
        UiHandoffGuard = Read-HtmlText -Html $adminPage.Content -DataAttribute 'data-cb-scanner-handoff-guard'
        UiHandoffAt = Read-HtmlText -Html $adminPage.Content -DataAttribute 'data-cb-scanner-handoff-at'
        UiHandoffScore = Read-HtmlText -Html $adminPage.Content -DataAttribute 'data-cb-scanner-handoff-score'
        UiStrategyUsageValidation = Read-HtmlText -Html $strategyPage.Content -DataAttribute 'data-cb-strategy-usage-validation'
        UiStrategyUsageScore = Read-HtmlText -Html $strategyPage.Content -DataAttribute 'data-cb-strategy-usage-score'
        UiStrategyUsageExplainability = Read-HtmlText -Html $strategyPage.Content -DataAttribute 'data-cb-strategy-usage-explainability'
        UiStrategyUsageRuleSummary = Read-HtmlText -Html $strategyPage.Content -DataAttribute 'data-cb-strategy-usage-rule-summary'
        UiStrategyExplainabilitySummary = Read-HtmlText -Html $strategyPage.Content -DataAttribute 'data-cb-strategy-explainability-summary'
        UiStrategyExplainabilityTemplate = Read-HtmlText -Html $strategyPage.Content -DataAttribute 'data-cb-strategy-explainability-template'
        UiStrategyExplainabilityReason = Read-HtmlText -Html $strategyPage.Content -DataAttribute 'data-cb-strategy-explainability-reason'
        UiStrategyExplainabilityRules = Read-HtmlText -Html $strategyPage.Content -DataAttribute 'data-cb-strategy-explainability-rules'
        UiTemplateCardKey = Read-HtmlAttribute -Html $strategyBuilderPage.Content -SelectorAttribute 'data-cb-template-card' -ValueAttribute 'data-cb-template-key'
        UiTemplateCardName = Read-HtmlAttribute -Html $strategyBuilderPage.Content -SelectorAttribute 'data-cb-template-card' -ValueAttribute 'data-cb-template-name'
        UiTemplateCardValidation = Read-HtmlAttribute -Html $strategyBuilderPage.Content -SelectorAttribute 'data-cb-template-card' -ValueAttribute 'data-cb-template-validation'
        UiTemplateCardSchema = Read-HtmlAttribute -Html $strategyBuilderPage.Content -SelectorAttribute 'data-cb-template-card' -ValueAttribute 'data-cb-template-schema'
        PageHtmlPath = $pageHtmlPath
        StrategyPageHtmlPath = $strategyPageHtmlPath
        StrategyBuilderHtmlPath = $strategyBuilderHtmlPath
        WebStdOutPath = $webStdOutPath
        WebStdErrPath = $webStdErrPath
    }

    $summary | ConvertTo-Json -Depth 8 | Set-Content -Path $summaryPath -Encoding UTF8

    Write-Host ('SmokeDatabaseName=' + $summary.SmokeDatabaseName)
    Write-Host ('ScanCycleId=' + $summary.ScanCycleId)
    Write-Host ('ScannedSymbolCount=' + $summary.ScannedSymbolCount)
    Write-Host ('EligibleCandidateCount=' + $summary.EligibleCandidateCount)
    Write-Host ('BestCandidateSymbolDb=' + $summary.BestCandidateSymbolDb)
    Write-Host ('UiBestSymbol=' + $summary.UiBestSymbol)
    Write-Host ('UiCounts=' + $summary.UiCounts)
    Write-Host ('UiRejectedSummary=' + $summary.UiRejectedSummary)
    Write-Host ('HandoffStatusDb=' + $summary.LatestHandoffDb.ExecutionRequestStatus)
    Write-Host ('HandoffSymbolDb=' + $summary.LatestHandoffDb.SelectedSymbol)
    Write-Host ('HandoffBlockerDb=' + ($summary.LatestHandoffDb.BlockerCode ?? 'none'))
    Write-Host ('UiHandoffStatus=' + $summary.UiHandoffStatus)
    Write-Host ('UiHandoffSymbol=' + $summary.UiHandoffSymbol)
    Write-Host ('UiHandoffBlocker=' + $summary.UiHandoffBlocker)
    Write-Host ('UiStrategyUsageValidation=' + $summary.UiStrategyUsageValidation)
    Write-Host ('UiStrategyExplainabilitySummary=' + $summary.UiStrategyExplainabilitySummary)
    Write-Host ('UiTemplateCard=' + $summary.UiTemplateCardKey + '/' + $summary.UiTemplateCardValidation)
    Write-Host ('SummaryPath=' + $summaryPath)
}
finally {
    Stop-ManagedProcess -Process $webProcess
}


















