$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$diagRoot = Join-Path $repoRoot '.diag\bot-ui-runtime-smoke'
$webStdOutPath = Join-Path $diagRoot 'web.stdout.log'
$webStdErrPath = Join-Path $diagRoot 'web.stderr.log'
$workerStdOutPath = Join-Path $diagRoot 'worker.stdout.log'
$workerStdErrPath = Join-Path $diagRoot 'worker.stderr.log'
$browserSummaryPath = Join-Path $diagRoot 'bot-ui-browser-summary.json'
$summaryPath = Join-Path $diagRoot 'bot-ui-runtime-smoke-summary.json'

function New-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ($listener.LocalEndpoint).Port } finally { $listener.Stop() }
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
                $value = if ($reader.IsDBNull($index)) { $null } else { $reader.GetValue($index) }
                $row[$reader.GetName($index)] = $value
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

        $reader = $command.ExecuteReader()
        $rows = New-Object System.Collections.Generic.List[object]
        try {
            while ($reader.Read()) {
                $row = [ordered]@{}
                for ($index = 0; $index -lt $reader.FieldCount; $index++) {
                    $value = if ($reader.IsDBNull($index)) { $null } else { $reader.GetValue($index) }
                    $row[$reader.GetName($index)] = $value
                }

                $rows.Add([pscustomobject]$row)
            }
        }
        finally {
            $reader.Dispose()
        }

        Write-Output -NoEnumerate $rows
    }
    finally {
        $connection.Dispose()
    }
}

function Start-ManagedProcess {
    param([string]$FilePath, [string[]]$ArgumentList, [string]$WorkingDirectory, [string]$StandardOutputPath, [string]$StandardErrorPath, [hashtable]$EnvironmentVariables)

    $process = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -WorkingDirectory $WorkingDirectory -RedirectStandardOutput $StandardOutputPath -RedirectStandardError $StandardErrorPath -Environment $EnvironmentVariables -PassThru -WindowStyle Hidden
    return [pscustomobject]@{ Process = $process }
}

function Stop-ManagedProcess {
    param($Handle)
    if ($null -eq $Handle) { return }

    $process = $Handle.Process
    try {
        if ($process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $null = $process.WaitForExit(5000)
        }
    } catch {
    } finally {
        try { $process.Dispose() } catch {}
    }
}

function Protect-CredentialValue {
    param([string]$Plaintext, [byte[]]$KeyMaterial)

    $plaintextBytes = [System.Text.Encoding]::UTF8.GetBytes($Plaintext)
    $nonce = New-Object byte[] 12
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($nonce)
    $ciphertext = New-Object byte[] $plaintextBytes.Length
    $tag = New-Object byte[] 16

    try {
        $aes = [System.Security.Cryptography.AesGcm]::new($KeyMaterial, 16)
        try { $aes.Encrypt($nonce, $plaintextBytes, $ciphertext, $tag) } finally { $aes.Dispose() }
        $payload = New-Object byte[] (1 + $nonce.Length + $tag.Length + $ciphertext.Length)
        $payload[0] = 1
        [System.Buffer]::BlockCopy($nonce, 0, $payload, 1, $nonce.Length)
        [System.Buffer]::BlockCopy($tag, 0, $payload, 1 + $nonce.Length, $tag.Length)
        [System.Buffer]::BlockCopy($ciphertext, 0, $payload, 1 + $nonce.Length + $tag.Length, $ciphertext.Length)
        return [Convert]::ToBase64String($payload)
    }
    finally {
        if ($plaintextBytes) { [System.Security.Cryptography.CryptographicOperations]::ZeroMemory($plaintextBytes) }
        if ($nonce) { [System.Security.Cryptography.CryptographicOperations]::ZeroMemory($nonce) }
        if ($ciphertext) { [System.Security.Cryptography.CryptographicOperations]::ZeroMemory($ciphertext) }
        if ($tag) { [System.Security.Cryptography.CryptographicOperations]::ZeroMemory($tag) }
    }
}

function Get-Sha256Hex {
    param([string]$Value)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
    try { return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)) }
    finally { [System.Security.Cryptography.CryptographicOperations]::ZeroMemory($bytes) }
}

function Ensure-SqlDatabaseExists {
    param([string]$ConnectionString)

    $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($ConnectionString)
    $databaseName = $builder.InitialCatalog
    if ([string]::IsNullOrWhiteSpace($databaseName)) { throw 'Database name is required for UI smoke bootstrap.' }

    $masterConnectionString = $ConnectionString -replace 'Database=[^;]+', 'Database=master'
    $escapedDatabaseName = $databaseName.Replace(']', ']]')
    Invoke-SqlNonQuery -ConnectionString $masterConnectionString -CommandText "IF DB_ID(N'$databaseName') IS NULL BEGIN EXEC(N'CREATE DATABASE [$escapedDatabaseName]') END;" | Out-Null
}

function Get-LatestLogLines {
    param([string]$Path, [string]$Pattern, [int]$Take = 5)
    if (-not (Test-Path $Path)) { return @() }
    return @(Select-String -Path $Path -Pattern $Pattern | Select-Object -Last $Take | ForEach-Object { $_.Line.Trim() })
}

function Get-ClockDriftSnapshot {
    param([string]$ConnectionString)
    return Invoke-SqlRow -ConnectionString $ConnectionString -CommandText "SELECT TOP (1) hs.HealthState, hs.CircuitBreakerState, hs.LastUpdatedAtUtc, hs.Detail, dms.StateCode, dms.ReasonCode, dms.LatestClockDriftMilliseconds, dms.LastStateChangedAtUtc FROM HealthSnapshots hs LEFT JOIN DegradedModeStates dms ON dms.Id = '3E17E8EF-3A73-45CC-8C32-A11FA55178D7' WHERE hs.SnapshotKey = 'clock-drift-monitor' ORDER BY hs.LastUpdatedAtUtc DESC;"
}

function Get-SmokeUser {
    param([string]$ConnectionString, [string]$Email)
    return Invoke-SqlRow -ConnectionString $ConnectionString -CommandText "SELECT TOP (1) Id, Email FROM AspNetUsers WHERE NormalizedEmail = UPPER(@Email);" -Parameters @{ Email = $Email }
}

function Get-SmokeOrders {
    param([string]$ConnectionString, [guid]$BotId)
    return Invoke-SqlRows -ConnectionString $ConnectionString -CommandText "SELECT TOP (10) Id, BotId, StrategySignalId, State, FailureCode, FailureDetail, RejectionStage, SubmittedToBroker, RetryEligible, CooldownApplied, ReduceOnly, StopLossPrice, TakeProfitPrice, DuplicateSuppressed, SubmittedAtUtc, LastStateChangedAtUtc, CreatedDate, ExternalOrderId FROM ExecutionOrders WHERE BotId = @BotId ORDER BY CreatedDate ASC;" -Parameters @{ BotId = $BotId }
}

function Get-SmokeBotSnapshot {
    param([string]$ConnectionString, [guid]$BotId)
    return Invoke-SqlRow -ConnectionString $ConnectionString -CommandText @"
SELECT TOP (1)
    b.Id AS BotId,
    b.Name AS BotName,
    b.IsEnabled,
    b.Symbol,
    b.OwnerUserId,
    eo.State AS LastExecutionState,
    eo.FailureCode AS LastExecutionFailureCode,
    eo.FailureDetail AS LastExecutionFailureDetail
FROM TradingBots b
OUTER APPLY (
    SELECT TOP (1) State, FailureCode, FailureDetail
    FROM ExecutionOrders
    WHERE BotId = b.Id
    ORDER BY CreatedDate DESC
) eo
WHERE b.Id = @BotId;
"@ -Parameters @{ BotId = $BotId }
}

function Get-SmokeSignalSnapshot {
    param([string]$ConnectionString, [guid]$StrategyId)
    return Invoke-SqlRow -ConnectionString $ConnectionString -CommandText @"
SELECT TOP (1)
    s.Id AS SignalId,
    s.SignalType,
    s.GeneratedAtUtc,
    d.DecisionOutcome
FROM TradingStrategySignals s
OUTER APPLY (
    SELECT TOP (1) DecisionOutcome
    FROM DecisionTraces
    WHERE StrategySignalId = s.Id
    ORDER BY CreatedDate ASC
) d
WHERE s.TradingStrategyId = @StrategyId
ORDER BY s.GeneratedAtUtc DESC;
"@ -Parameters @{ StrategyId = $StrategyId }
}
function Seed-SmokeGraph {
    param([string]$ConnectionString, [string]$UserId, [guid]$StrategyId, [guid]$StrategyVersionId, [guid]$ExchangeAccountId, [guid]$BotId, [string]$ApiKeyCiphertext, [string]$ApiSecretCiphertext, [string]$CredentialFingerprint, [datetime]$UtcNow)

    $definitionJson = [string]::Join([Environment]::NewLine, @(
        '{',
        '  "schemaVersion": 1,',
        '  "entry": {',
        '    "operator": "all",',
        '    "rules": [',
        '      {',
        '        "path": "context.mode",',
        '        "comparison": "equals",',
        '        "value": "Live"',
        '      }',
        '    ]',
        '  },',
        '  "risk": {',
        '    "operator": "all",',
        '    "rules": [',
        '      {',
        '        "path": "indicator.sampleCount",',
        '        "comparison": "greaterThanOrEqual",',
        '        "value": 50',
        '      }',
        '    ]',
        '  }',
        '}'
    ))

    Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText "IF NOT EXISTS (SELECT 1 FROM GlobalExecutionSwitches WHERE Id = '0F4D61F5-595D-4C35-9B21-3D87A0F1D001') INSERT INTO GlobalExecutionSwitches (Id, TradeMasterState, DemoModeEnabled, LiveModeApprovedAtUtc, LiveModeApprovalReference, CreatedDate, UpdatedDate, IsDeleted) VALUES ('0F4D61F5-595D-4C35-9B21-3D87A0F1D001', 'Armed', 0, @UtcNow, 'bot-ui-smoke', @UtcNow, @UtcNow, 0);" -Parameters @{ UtcNow = $UtcNow } | Out-Null
    Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText "INSERT INTO TradingStrategies (Id, StrategyKey, DisplayName, PromotionState, PublishedMode, PublishedAtUtc, LivePromotionApprovedAtUtc, LivePromotionApprovalReference, CreatedDate, UpdatedDate, IsDeleted, OwnerUserId) VALUES (@StrategyId, 'bot-ui-smoke-core', 'Bot UI Smoke Core', 'LivePublished', 'Live', @UtcNow, @UtcNow, 'bot-ui-smoke', @UtcNow, @UtcNow, 0, @UserId);" -Parameters @{ StrategyId = $StrategyId; UtcNow = $UtcNow; UserId = $UserId } | Out-Null
    Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText "INSERT INTO TradingStrategyVersions (Id, TradingStrategyId, SchemaVersion, VersionNumber, Status, DefinitionJson, PublishedAtUtc, ArchivedAtUtc, CreatedDate, UpdatedDate, IsDeleted, OwnerUserId) VALUES (@StrategyVersionId, @StrategyId, 1, 1, 'Published', @DefinitionJson, @UtcNow, NULL, @UtcNow, @UtcNow, 0, @UserId);" -Parameters @{ StrategyVersionId = $StrategyVersionId; StrategyId = $StrategyId; DefinitionJson = $definitionJson; UtcNow = $UtcNow; UserId = $UserId } | Out-Null
    Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText "INSERT INTO RiskProfiles (Id, ProfileName, MaxDailyLossPercentage, MaxPositionSizePercentage, MaxLeverage, KillSwitchEnabled, CreatedDate, UpdatedDate, IsDeleted, OwnerUserId) VALUES (NEWID(), 'Bot UI Smoke', 10.0000, 100.0000, 2.0000, 0, @UtcNow, @UtcNow, 0, @UserId);" -Parameters @{ UtcNow = $UtcNow; UserId = $UserId } | Out-Null
    Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText "INSERT INTO ExchangeAccounts (Id, ExchangeName, DisplayName, IsReadOnly, LastValidatedAt, ApiKeyCiphertext, ApiSecretCiphertext, CredentialFingerprint, CredentialKeyVersion, CredentialStatus, CredentialStoredAtUtc, CredentialLastAccessedAtUtc, CredentialLastRotatedAtUtc, CredentialRevalidateAfterUtc, CredentialRotateAfterUtc, CreatedDate, UpdatedDate, IsDeleted, OwnerUserId) VALUES (@ExchangeAccountId, 'Binance', 'Bot UI Smoke Binance', 0, @UtcNow, @ApiKeyCiphertext, @ApiSecretCiphertext, @CredentialFingerprint, 'credential-v1', 'Active', @UtcNow, NULL, @UtcNow, DATEADD(DAY, 30, @UtcNow), DATEADD(DAY, 90, @UtcNow), @UtcNow, @UtcNow, 0, @UserId);" -Parameters @{ ExchangeAccountId = $ExchangeAccountId; ApiKeyCiphertext = $ApiKeyCiphertext; ApiSecretCiphertext = $ApiSecretCiphertext; CredentialFingerprint = $CredentialFingerprint; UtcNow = $UtcNow; UserId = $UserId } | Out-Null
    Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText "INSERT INTO ExchangeBalances (Id, ExchangeAccountId, Asset, WalletBalance, CrossWalletBalance, AvailableBalance, MaxWithdrawAmount, ExchangeUpdatedAtUtc, SyncedAtUtc, CreatedDate, UpdatedDate, IsDeleted, OwnerUserId) VALUES (NEWID(), @ExchangeAccountId, 'USDT', 1000, 1000, 1000, 1000, @UtcNow, @UtcNow, @UtcNow, @UtcNow, 0, @UserId);" -Parameters @{ ExchangeAccountId = $ExchangeAccountId; UtcNow = $UtcNow; UserId = $UserId } | Out-Null
    Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText "INSERT INTO TradingBots (Id, Name, StrategyKey, Symbol, Quantity, ExchangeAccountId, Leverage, MarginType, IsEnabled, TradingModeOverride, TradingModeApprovedAtUtc, TradingModeApprovalReference, OpenOrderCount, OpenPositionCount, CreatedDate, UpdatedDate, IsDeleted, OwnerUserId) VALUES (@BotId, 'Bot UI Smoke Bot', 'bot-ui-smoke-core', 'BTCUSDT', 0.002, @ExchangeAccountId, 1, 'ISOLATED', 1, NULL, NULL, NULL, 0, 0, @UtcNow, @UtcNow, 0, @UserId);" -Parameters @{ BotId = $BotId; ExchangeAccountId = $ExchangeAccountId; UtcNow = $UtcNow; UserId = $UserId } | Out-Null
}

if (Test-Path $diagRoot) {
    Remove-Item $diagRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $diagRoot | Out-Null

$smokeDatabaseName = 'CoinBotBotUiSmoke_' + [Guid]::NewGuid().ToString('N')
$webPort = New-FreeTcpPort
$baseUrl = 'http://127.0.0.1:' + $webPort
$registrationEmail = 'bot.ui.smoke.' + [Guid]::NewGuid().ToString('N') + '@coinbot.test'
$registrationPassword = 'Passw0rd!Smoke1'
$connectionString = 'Server=(localdb)\MSSQLLocalDB;Database=' + $smokeDatabaseName + ';Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True'
Ensure-SqlDatabaseExists -ConnectionString $connectionString

$randomKeyBytes = New-Object byte[] 32
[System.Security.Cryptography.RandomNumberGenerator]::Fill($randomKeyBytes)
$credentialKeyBase64 = [Convert]::ToBase64String($randomKeyBytes)

$strategyId = [Guid]'55555555-5555-5555-5555-555555555555'
$strategyVersionId = [Guid]'66666666-6666-6666-6666-666666666666'
$exchangeAccountId = [Guid]'77777777-7777-7777-7777-777777777777'
$botId = [Guid]'88888888-8888-8888-8888-888888888888'

$environmentVariables = @{
    DOTNET_CLI_HOME = (Join-Path $repoRoot '.dotnet')
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_NOLOGO = '1'
    ASPNETCORE_ENVIRONMENT = 'Development'
    DOTNET_ENVIRONMENT = 'Development'
    ASPNETCORE_URLS = $baseUrl
    ConnectionStrings__DefaultConnection = $connectionString
    COINBOT_CREDENTIAL_ENCRYPTION_KEY_BASE64 = $credentialKeyBase64
    BotExecutionPilot__PerBotCooldownSeconds = '120'
    BotExecutionPilot__PerSymbolCooldownSeconds = '0'
    BotExecutionPilot__PrimeHistoricalCandleCount = '50'
    JobOrchestration__Enabled = 'true'
    JobOrchestration__SchedulerPollIntervalSeconds = '1'
    JobOrchestration__BotExecutionIntervalSeconds = '5'
    JobOrchestration__InitialRetryDelaySeconds = '1'
    JobOrchestration__MaxRetryDelaySeconds = '5'
    JobOrchestration__MaxRetryAttempts = '10'
    JobOrchestration__WorkerInstanceId = 'bot-ui-smoke'
    'Logging__LogLevel__System.Net.Http.HttpClient' = 'Information'
}

$summary = [ordered]@{
    SmokeDatabaseName = $smokeDatabaseName
    BaseUrl = $baseUrl
    RegistrationEmail = $registrationEmail
    BotId = $botId
    BrowserSummaryPath = $browserSummaryPath
    HistoricalBaselineReason = 'ClockDriftExceeded'
    Ui = $null
}

$webHandle = $null
$workerHandle = $null

try {
    $webHandle = Start-ManagedProcess -FilePath 'dotnet' -ArgumentList @('run', '--no-build', '--no-launch-profile') -WorkingDirectory (Join-Path $repoRoot 'src\CoinBot.Web') -StandardOutputPath $webStdOutPath -StandardErrorPath $webStdErrPath -EnvironmentVariables $environmentVariables

    Wait-Until -Name 'web startup' -TimeoutSeconds 90 -Condition {
        try {
            $response = Invoke-WebRequest -Uri ($baseUrl + '/Auth/Login') -MaximumRedirection 0 -SkipHttpErrorCheck
            return $response.StatusCode -in 200, 302
        } catch {
            return $false
        }
    } | Out-Null

    & node (Join-Path $PSScriptRoot 'BotUiRuntimeSmoke.mjs') register $baseUrl $registrationEmail $registrationPassword $diagRoot

    $userRow = Wait-Until -Name 'registered smoke user' -TimeoutSeconds 30 -Condition {
        return Get-SmokeUser -ConnectionString $connectionString -Email $registrationEmail
    }
    $summary.RegisteredUserId = $userRow.Id

    $nowUtc = [DateTime]::UtcNow
    $encryptedApiKey = Protect-CredentialValue -Plaintext 'bot-ui-smoke-api-key' -KeyMaterial $randomKeyBytes
    $encryptedApiSecret = Protect-CredentialValue -Plaintext 'bot-ui-smoke-api-secret' -KeyMaterial $randomKeyBytes
    $fingerprint = Get-Sha256Hex -Value 'bot-ui-smoke-api-key'

    Seed-SmokeGraph -ConnectionString $connectionString -UserId $userRow.Id -StrategyId $strategyId -StrategyVersionId $strategyVersionId -ExchangeAccountId $exchangeAccountId -BotId $botId -ApiKeyCiphertext $encryptedApiKey -ApiSecretCiphertext $encryptedApiSecret -CredentialFingerprint $fingerprint -UtcNow $nowUtc

    $summary.DriftSnapshotBeforeWorker = Wait-Until -Name 'clock drift snapshot' -TimeoutSeconds 120 -Condition {
        $snapshot = Get-ClockDriftSnapshot -ConnectionString $connectionString
        if ($null -eq $snapshot) { return $null }
        if ([string]::IsNullOrWhiteSpace([string]$snapshot.Detail)) { return $null }
        return $snapshot
    }

    $summary.DriftSnapshotAtExecutionStart = Wait-Until -Name 'non-drift degraded mode state' -TimeoutSeconds 120 -Condition {
        $snapshot = Get-ClockDriftSnapshot -ConnectionString $connectionString
        if ($null -eq $snapshot) { return $null }
        if ($snapshot.ReasonCode -eq 'ClockDriftExceeded') { return $null }
        if ($snapshot.StateCode -eq 'Stopped' -and $snapshot.ReasonCode -eq 'MarketDataUnavailable') { return $null }
        return $snapshot
    }

    $workerHandle = Start-ManagedProcess -FilePath 'dotnet' -ArgumentList @('run', '--no-build', '--no-launch-profile') -WorkingDirectory (Join-Path $repoRoot 'src\CoinBot.Worker') -StandardOutputPath $workerStdOutPath -StandardErrorPath $workerStdErrPath -EnvironmentVariables $environmentVariables

    Wait-Until -Name 'worker readiness summary' -TimeoutSeconds 120 -Condition {
        $lines = Get-LatestLogLines -Path $workerStdOutPath -Pattern 'EnabledBotCount=1'
        if ($lines.Count -gt 0) { return $lines }
        return $null
    } | Out-Null

    $firstObservedOrders = $null
    try {
        $firstObservedOrders = Wait-Until -Name 'first bot execution order' -TimeoutSeconds 180 -Condition {
            $orders = @(Get-SmokeOrders -ConnectionString $connectionString -BotId $botId)
            if ($orders.Count -ge 1) { return $orders }
            return $null
        }
    }
    catch {
        $firstObservedOrders = @(Get-SmokeOrders -ConnectionString $connectionString -BotId $botId)
    }

    $firstObservedOrders = @($firstObservedOrders | ForEach-Object { $_ })
    if (@($firstObservedOrders).Count -ge 1) {
        try {
            $summary.Orders = Wait-Until -Name 'second bot execution order' -TimeoutSeconds 180 -Condition {
                $currentOrders = @(Get-SmokeOrders -ConnectionString $connectionString -BotId $botId)
                if ($currentOrders.Count -ge 2) { return $currentOrders }
                return $null
            }
        }
        catch {
            $summary.Orders = @($firstObservedOrders)
        }
    }
    else {
        $summary.Orders = @()
    }
    $summary.Orders = @($summary.Orders | ForEach-Object { $_ })

    $summary.AttemptCount = @($summary.Orders).Count
    $summary.Attempt1 = if (@($summary.Orders).Count -ge 1) { $summary.Orders[0] } else { $null }
    $summary.Attempt2 = if (@($summary.Orders).Count -ge 2) { $summary.Orders[1] } else { $null }
    $summary.OrderCaptureState = if ($summary.AttemptCount -ge 1) { 'OrdersObserved' } else { 'NoOrderObserved' }
    $summary.BotRuntimeSnapshot = Get-SmokeBotSnapshot -ConnectionString $connectionString -BotId $botId
    $summary.SignalRuntimeSnapshot = Get-SmokeSignalSnapshot -ConnectionString $connectionString -StrategyId $strategyId
    $summary.DriftSnapshotAfterAttempts = Get-ClockDriftSnapshot -ConnectionString $connectionString
    $summary.WorkerReadinessLines = Get-LatestLogLines -Path $workerStdOutPath -Pattern 'Bot execution pilot readiness'
    $summary.WorkerTriggeredLines = Get-LatestLogLines -Path $workerStdOutPath -Pattern 'TriggeredJobs=' -Take 10
    $summary.ExecutionDecisionLines = Get-LatestLogLines -Path $workerStdOutPath -Pattern 'Signal stage accepted an execution decision request|Execution stage allowed the request' -Take 20
    $summary.PrivateRestAdvanceLogLines = Get-LatestLogLines -Path $workerStdOutPath -Pattern '/fapi/v1/marginType|/fapi/v1/leverage|/fapi/v1/order|Binance executor submitted order' -Take 20
    $summary.CurrentClockDriftExceededLogLines = Get-LatestLogLines -Path $workerStdOutPath -Pattern 'ClockDriftExceeded|clock drift exceeded'
    $summary.AdvancedBeyondClockDrift = @($summary.Orders | Where-Object { $_.FailureCode -ne 'ClockDriftExceeded' }).Count -gt 0

    & node (Join-Path $PSScriptRoot 'BotUiRuntimeSmoke.mjs') inspect $baseUrl $registrationEmail $registrationPassword $diagRoot $botId.ToString()

    if (Test-Path $browserSummaryPath) {
        $summary.Ui = Get-Content -Path $browserSummaryPath -Raw | ConvertFrom-Json
    }

    $summary | ConvertTo-Json -Depth 8 | Set-Content -Path $summaryPath -Encoding UTF8

    Write-Host ('SmokeDatabaseName=' + $smokeDatabaseName)
    Write-Host ('RegisteredUserId=' + $summary.RegisteredUserId)
    Write-Host ('AttemptCount=' + $summary.AttemptCount)
    Write-Host ('OrderCaptureState=' + $summary.OrderCaptureState)
    if ($summary.Attempt1) {
        Write-Host ('Attempt1FailureCode=' + $summary.Attempt1.FailureCode)
        Write-Host ('Attempt1FailureDetail=' + $summary.Attempt1.FailureDetail)
        Write-Host ('Attempt1RejectionStage=' + $summary.Attempt1.RejectionStage)
        Write-Host ('Attempt1SubmittedToBroker=' + $summary.Attempt1.SubmittedToBroker)
        Write-Host ('Attempt1RetryEligible=' + $summary.Attempt1.RetryEligible)
        Write-Host ('Attempt1CooldownApplied=' + $summary.Attempt1.CooldownApplied)
        Write-Host ('Attempt1ReduceOnly=' + $summary.Attempt1.ReduceOnly)
        Write-Host ('Attempt1StopLossPrice=' + $summary.Attempt1.StopLossPrice)
        Write-Host ('Attempt1TakeProfitPrice=' + $summary.Attempt1.TakeProfitPrice)
        Write-Host ('Attempt1DuplicateSuppressed=' + $summary.Attempt1.DuplicateSuppressed)
    }
    if ($summary.Ui) {
        Write-Host ('UiBotName=' + $summary.Ui.bots.botNameText)
        Write-Host ('UiBotEnabled=' + $summary.Ui.bots.enabledBadgeText)
        Write-Host ('UiBotExecutionState=' + $summary.Ui.bots.postToggleExecutionStateText)
        Write-Host ('UiBotExecutionError=' + $summary.Ui.bots.postToggleExecutionFailureText)
        Write-Host ('UiBotExecutionBlockDetail=' + $summary.Ui.bots.postToggleExecutionBlockDetailText)
        Write-Host ('UiBotExecutionSubmit=' + $summary.Ui.bots.postToggleExecutionSubmitText)
        Write-Host ('UiBotExecutionRetry=' + $summary.Ui.bots.postToggleExecutionRetryText)
        Write-Host ('UiBotExecutionProtection=' + $summary.Ui.bots.postToggleExecutionProtectionText)
        Write-Host ('UiBotExecutionStage=' + $summary.Ui.bots.postToggleExecutionStageText)
        Write-Host ('UiBotExecutionTransition=' + $summary.Ui.bots.postToggleExecutionTransitionText)
        Write-Host ('UiBotExecutionCorrelation=' + $summary.Ui.bots.postToggleExecutionCorrelationText)
        Write-Host ('UiBotExecutionClientOrder=' + $summary.Ui.bots.postToggleExecutionClientOrderText)
        Write-Host ('UiBotExecutionDuplicate=' + $summary.Ui.bots.postToggleExecutionDuplicateText)
        Write-Host ('UiBotCooldownBadge=' + $summary.Ui.bots.postToggleCooldownBadgeText)
        Write-Host ('UiBotCooldownRemaining=' + $summary.Ui.bots.postToggleCooldownRemainingText)
        Write-Host ('UiDashboardDriftSummary=' + $summary.Ui.dashboard.driftSummaryText)
        Write-Host ('UiPositionsPnlConsistency=' + $summary.Ui.positions.pnlConsistencyText)
        Write-Host ('UiPositionsSummaryUnrealized=' + $summary.Ui.positions.summaryUnrealizedText)
        Write-Host ('UiPositionsSummaryRealized=' + $summary.Ui.positions.summaryRealizedText)
        Write-Host ('UiHistorySymbol=' + $summary.Ui.positions.historySymbolText)
        Write-Host ('UiHistoryResultCode=' + $summary.Ui.positions.historyResultCodeText)
        Write-Host ('UiHistoryReasonChain=' + $summary.Ui.positions.historyReasonChainText)
        Write-Host ('UiHistoryPnl=' + $summary.Ui.positions.historyPnlText)
        Write-Host ('UiHistoryAiLabel=' + $summary.Ui.positions.historyAiLabelText)
        Write-Host ('UiHistoryAiSummary=' + $summary.Ui.positions.historyAiSummaryText)
        Write-Host ('UiHistoryAiSource=' + $summary.Ui.positions.historyAiSourceText)
        Write-Host ('UiHistoryStage=' + $summary.Ui.positions.historyStageText)
        Write-Host ('UiHistorySubmitted=' + $summary.Ui.positions.historySubmittedText)
        Write-Host ('UiHistoryRetry=' + $summary.Ui.positions.historyRetryText)
        Write-Host ('UiHistoryCorrelation=' + $summary.Ui.positions.historyCorrelationText)
        Write-Host ('UiHistoryClientOrder=' + $summary.Ui.positions.historyClientOrderText)
        Write-Host ('UiExchangeBannerDetail=' + $summary.Ui.exchanges.bannerDetailText)
        Write-Host ('UiToggleCycle=' + $summary.Ui.bots.disableThenEnableCycle)
    }
    Write-Host ('SummaryPath=' + $summaryPath)
}
finally {
    Stop-ManagedProcess -Handle $workerHandle
    Stop-ManagedProcess -Handle $webHandle
    if ($randomKeyBytes) {
        [System.Security.Cryptography.CryptographicOperations]::ZeroMemory($randomKeyBytes)
    }
}











