$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$diagRoot = Join-Path $repoRoot '.diag\clock-drift-runtime-smoke'
$webStdOutPath = Join-Path $diagRoot 'web.stdout.log'
$webStdErrPath = Join-Path $diagRoot 'web.stderr.log'
$workerStdOutPath = Join-Path $diagRoot 'worker.stdout.log'
$workerStdErrPath = Join-Path $diagRoot 'worker.stderr.log'
$summaryPath = Join-Path $diagRoot 'clock-drift-runtime-smoke-summary.json'

function New-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ($listener.LocalEndpoint).Port } finally { $listener.Stop() }
}

function Wait-Until {
    param(
        [string]$Name,
        [scriptblock]$Condition,
        [int]$TimeoutSeconds = 60,
        [int]$PollMilliseconds = 500
    )

    $startedAt = Get-Date
    $lastError = $null

    while (((Get-Date) - $startedAt).TotalSeconds -lt $TimeoutSeconds) {
        try {
            $result = & $Condition
            if ($result) { return $result }
        }
        catch {
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
    param(
        [string]$ConnectionString,
        [string]$CommandText,
        [hashtable]$Parameters = @{}
    )

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
    param(
        [string]$ConnectionString,
        [string]$CommandText,
        [hashtable]$Parameters = @{}
    )

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
    param(
        [string]$ConnectionString,
        [string]$CommandText,
        [hashtable]$Parameters = @{}
    )

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

        return $rows
    }
    finally {
        $connection.Dispose()
    }
}

function Start-ManagedProcess {
    param(
        [string]$FilePath,
        [string[]]$ArgumentList,
        [string]$WorkingDirectory,
        [string]$StandardOutputPath,
        [string]$StandardErrorPath,
        [hashtable]$EnvironmentVariables
    )

    $process = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -WorkingDirectory $WorkingDirectory -RedirectStandardOutput $StandardOutputPath -RedirectStandardError $StandardErrorPath -Environment $EnvironmentVariables -PassThru -WindowStyle Hidden

    return [pscustomobject]@{
        Process = $process
    }
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
    }
    catch {
    }
    finally {
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
        try {
            $aes.Encrypt($nonce, $plaintextBytes, $ciphertext, $tag)
        }
        finally {
            $aes.Dispose()
        }

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
    try {
        return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes))
    }
    finally {
        [System.Security.Cryptography.CryptographicOperations]::ZeroMemory($bytes)
    }
}

function Ensure-SqlDatabaseExists {
    param([string]$ConnectionString)

    $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($ConnectionString)
    $databaseName = $builder.InitialCatalog

    if ([string]::IsNullOrWhiteSpace($databaseName)) {
        throw 'Database name is required for smoke bootstrap.'
    }

    $masterConnectionString = $ConnectionString -replace 'Database=[^;]+', 'Database=master'
    $escapedDatabaseName = $databaseName.Replace(']', ']]')
    $createDatabaseSql = "IF DB_ID(N'$databaseName') IS NULL BEGIN EXEC(N'CREATE DATABASE [$escapedDatabaseName]') END;"

    Invoke-SqlNonQuery -ConnectionString $masterConnectionString -CommandText $createDatabaseSql | Out-Null
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

function Get-SmokeOrders {
    param([string]$ConnectionString, [guid]$BotId)
    return Invoke-SqlRows -ConnectionString $ConnectionString -CommandText "SELECT TOP (10) Id, State, FailureCode, FailureDetail, SubmittedAtUtc, LastStateChangedAtUtc, CreatedDate, ExternalOrderId FROM ExecutionOrders WHERE BotId = @BotId ORDER BY CreatedDate ASC;" -Parameters @{ BotId = $BotId }
}

function Seed-SmokeGraph {
    param(
        [string]$ConnectionString,
        [string]$UserId,
        [string]$Email,
        [guid]$StrategyId,
        [guid]$StrategyVersionId,
        [guid]$ExchangeAccountId,
        [guid]$BotId,
        [string]$ApiKeyCiphertext,
        [string]$ApiSecretCiphertext,
        [string]$CredentialFingerprint,
        [datetime]$UtcNow
    )

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

    Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText "INSERT INTO AspNetUsers (Id, FullName, UserName, NormalizedUserName, Email, NormalizedEmail, EmailConfirmed, PasswordHash, SecurityStamp, ConcurrencyStamp, PhoneNumber, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnd, LockoutEnabled, AccessFailedCount, MfaEnabled, TotpEnabled, EmailOtpEnabled, PreferredMfaProvider, TotpSecretCiphertext, MfaUpdatedAtUtc, TradingModeOverride, TradingModeApprovedAtUtc, TradingModeApprovalReference, PreferredTimeZoneId) VALUES (@UserId, 'Clock Drift Smoke', @Email, UPPER(@Email), @Email, UPPER(@Email), 1, NULL, NEWID(), NEWID(), NULL, 0, 0, NULL, 1, 0, 0, 0, 0, NULL, NULL, NULL, NULL, NULL, NULL, 'UTC');" -Parameters @{ UserId = $UserId; Email = $Email } | Out-Null

    Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText "INSERT INTO GlobalExecutionSwitches (Id, TradeMasterState, DemoModeEnabled, LiveModeApprovedAtUtc, LiveModeApprovalReference, CreatedDate, UpdatedDate, IsDeleted) VALUES ('0F4D61F5-595D-4C35-9B21-3D87A0F1D001', 'Armed', 0, @UtcNow, 'clock-drift-smoke', @UtcNow, @UtcNow, 0);" -Parameters @{ UtcNow = $UtcNow } | Out-Null

    Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText "INSERT INTO TradingStrategies (Id, StrategyKey, DisplayName, PromotionState, PublishedMode, PublishedAtUtc, LivePromotionApprovedAtUtc, LivePromotionApprovalReference, CreatedDate, UpdatedDate, IsDeleted, OwnerUserId) VALUES (@StrategyId, 'clock-drift-smoke-core', 'Clock Drift Smoke Core', 'LivePublished', 'Live', @UtcNow, @UtcNow, 'clock-drift-smoke', @UtcNow, @UtcNow, 0, @UserId);" -Parameters @{ StrategyId = $StrategyId; UtcNow = $UtcNow; UserId = $UserId } | Out-Null

    Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText "INSERT INTO TradingStrategyVersions (Id, TradingStrategyId, SchemaVersion, VersionNumber, Status, DefinitionJson, PublishedAtUtc, ArchivedAtUtc, CreatedDate, UpdatedDate, IsDeleted, OwnerUserId) VALUES (@StrategyVersionId, @StrategyId, 1, 1, 'Published', @DefinitionJson, @UtcNow, NULL, @UtcNow, @UtcNow, 0, @UserId);" -Parameters @{ StrategyVersionId = $StrategyVersionId; StrategyId = $StrategyId; DefinitionJson = $definitionJson; UtcNow = $UtcNow; UserId = $UserId } | Out-Null

    Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText "INSERT INTO RiskProfiles (Id, ProfileName, MaxDailyLossPercentage, MaxPositionSizePercentage, MaxLeverage, KillSwitchEnabled, CreatedDate, UpdatedDate, IsDeleted, OwnerUserId) VALUES (NEWID(), 'Clock Drift Smoke', 10.0000, 100.0000, 2.0000, 0, @UtcNow, @UtcNow, 0, @UserId);" -Parameters @{ UtcNow = $UtcNow; UserId = $UserId } | Out-Null

    Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText "INSERT INTO ExchangeAccounts (Id, ExchangeName, DisplayName, IsReadOnly, LastValidatedAt, ApiKeyCiphertext, ApiSecretCiphertext, CredentialFingerprint, CredentialKeyVersion, CredentialStatus, CredentialStoredAtUtc, CredentialLastAccessedAtUtc, CredentialLastRotatedAtUtc, CredentialRevalidateAfterUtc, CredentialRotateAfterUtc, CreatedDate, UpdatedDate, IsDeleted, OwnerUserId) VALUES (@ExchangeAccountId, 'Binance', 'Clock Drift Smoke Binance', 0, @UtcNow, @ApiKeyCiphertext, @ApiSecretCiphertext, @CredentialFingerprint, 'credential-v1', 'Active', @UtcNow, NULL, @UtcNow, DATEADD(DAY, 30, @UtcNow), DATEADD(DAY, 90, @UtcNow), @UtcNow, @UtcNow, 0, @UserId);" -Parameters @{ ExchangeAccountId = $ExchangeAccountId; ApiKeyCiphertext = $ApiKeyCiphertext; ApiSecretCiphertext = $ApiSecretCiphertext; CredentialFingerprint = $CredentialFingerprint; UtcNow = $UtcNow; UserId = $UserId } | Out-Null

    Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText "INSERT INTO ExchangeBalances (Id, ExchangeAccountId, Asset, WalletBalance, CrossWalletBalance, AvailableBalance, MaxWithdrawAmount, ExchangeUpdatedAtUtc, SyncedAtUtc, CreatedDate, UpdatedDate, IsDeleted, OwnerUserId) VALUES (NEWID(), @ExchangeAccountId, 'USDT', 1000, 1000, 1000, 1000, @UtcNow, @UtcNow, @UtcNow, @UtcNow, 0, @UserId);" -Parameters @{ ExchangeAccountId = $ExchangeAccountId; UtcNow = $UtcNow; UserId = $UserId } | Out-Null

    Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText "INSERT INTO TradingBots (Id, Name, StrategyKey, Symbol, Quantity, ExchangeAccountId, Leverage, MarginType, IsEnabled, TradingModeOverride, TradingModeApprovedAtUtc, TradingModeApprovalReference, OpenOrderCount, OpenPositionCount, CreatedDate, UpdatedDate, IsDeleted, OwnerUserId) VALUES (@BotId, 'Clock Drift Smoke Bot', 'clock-drift-smoke-core', 'BTCUSDT', 0.002, @ExchangeAccountId, 1, 'ISOLATED', 1, NULL, NULL, NULL, 0, 0, @UtcNow, @UtcNow, 0, @UserId);" -Parameters @{ BotId = $BotId; ExchangeAccountId = $ExchangeAccountId; UtcNow = $UtcNow; UserId = $UserId } | Out-Null
}

if (Test-Path $diagRoot) {
    Remove-Item $diagRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $diagRoot | Out-Null

$smokeDatabaseName = 'CoinBotClockDriftSmoke_' + [Guid]::NewGuid().ToString('N')
$webPort = New-FreeTcpPort
$baseUrl = 'http://127.0.0.1:' + $webPort
$connectionString = 'Server=(localdb)\MSSQLLocalDB;Database=' + $smokeDatabaseName + ';Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True'
Ensure-SqlDatabaseExists -ConnectionString $connectionString
$randomKeyBytes = New-Object byte[] 32
[System.Security.Cryptography.RandomNumberGenerator]::Fill($randomKeyBytes)
$credentialKeyBase64 = [Convert]::ToBase64String($randomKeyBytes)

$userId = 'clockdrift-smoke-user'
$email = 'clockdrift.smoke@coinbot.test'
$strategyId = [Guid]'11111111-1111-1111-1111-111111111111'
$strategyVersionId = [Guid]'22222222-2222-2222-2222-222222222222'
$exchangeAccountId = [Guid]'33333333-3333-3333-3333-333333333333'
$botId = [Guid]'44444444-4444-4444-4444-444444444444'

$environmentVariables = @{
    DOTNET_CLI_HOME = (Join-Path $repoRoot '.dotnet')
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_NOLOGO = '1'
    ASPNETCORE_ENVIRONMENT = 'Development'
    DOTNET_ENVIRONMENT = 'Development'
    ASPNETCORE_URLS = $baseUrl
    ConnectionStrings__DefaultConnection = $connectionString
    COINBOT_CREDENTIAL_ENCRYPTION_KEY_BASE64 = $credentialKeyBase64
    BotExecutionPilot__PerBotCooldownSeconds = '0'
    BotExecutionPilot__PerSymbolCooldownSeconds = '0'
    BotExecutionPilot__PrimeHistoricalCandleCount = '50'
    JobOrchestration__Enabled = 'true'
    JobOrchestration__SchedulerPollIntervalSeconds = '1'
    JobOrchestration__BotExecutionIntervalSeconds = '5'
    JobOrchestration__InitialRetryDelaySeconds = '1'
    JobOrchestration__MaxRetryDelaySeconds = '5'
    JobOrchestration__MaxRetryAttempts = '10'
    JobOrchestration__WorkerInstanceId = 'clock-drift-smoke'
    'Logging__LogLevel__System.Net.Http.HttpClient' = 'Information'
}

$summary = [ordered]@{
    SmokeDatabaseName = $smokeDatabaseName
    BaseUrl = $baseUrl
    HistoricalBaselineReason = 'ClockDriftExceeded'
    ClockDriftThresholdMs = 2000
    TimezoneTechnicalExplanation = 'PreferredTimeZoneId only affects UI rendering. Runtime drift compares the machine UTC clock to Binance server UTC from fapi/v1/time.'
}

$webHandle = $null
$workerHandle = $null

try {
    $webHandle = Start-ManagedProcess -FilePath 'dotnet' -ArgumentList @('run', '--no-build', '--no-launch-profile') -WorkingDirectory (Join-Path $repoRoot 'src\CoinBot.Web') -StandardOutputPath $webStdOutPath -StandardErrorPath $webStdErrPath -EnvironmentVariables $environmentVariables

    Wait-Until -Name 'web startup' -TimeoutSeconds 90 -Condition {
        try {
            $response = Invoke-WebRequest -Uri ($baseUrl + '/Auth/Login') -MaximumRedirection 0 -SkipHttpErrorCheck
            return $response.StatusCode -in 200, 302
        }
        catch {
            return $false
        }
    } | Out-Null

    $nowUtc = [DateTime]::UtcNow
    $encryptedApiKey = Protect-CredentialValue -Plaintext 'clock-drift-smoke-api-key' -KeyMaterial $randomKeyBytes
    $encryptedApiSecret = Protect-CredentialValue -Plaintext 'clock-drift-smoke-api-secret' -KeyMaterial $randomKeyBytes
    $fingerprint = Get-Sha256Hex -Value 'clock-drift-smoke-api-key'

    Seed-SmokeGraph -ConnectionString $connectionString -UserId $userId -Email $email -StrategyId $strategyId -StrategyVersionId $strategyVersionId -ExchangeAccountId $exchangeAccountId -BotId $botId -ApiKeyCiphertext $encryptedApiKey -ApiSecretCiphertext $encryptedApiSecret -CredentialFingerprint $fingerprint -UtcNow $nowUtc

    $driftSnapshot = Wait-Until -Name 'clock drift snapshot' -TimeoutSeconds 120 -Condition {
        $snapshot = Get-ClockDriftSnapshot -ConnectionString $connectionString
        if ($null -eq $snapshot) { return $null }
        if ([string]::IsNullOrWhiteSpace([string]$snapshot.Detail)) { return $null }
        return $snapshot
    }
    $summary.DriftSnapshotBeforeWorker = $driftSnapshot

    $normalSnapshot = Wait-Until -Name 'non-drift degraded mode state' -TimeoutSeconds 120 -Condition {
        $snapshot = Get-ClockDriftSnapshot -ConnectionString $connectionString
        if ($null -eq $snapshot) { return $null }
        if ($snapshot.ReasonCode -eq 'ClockDriftExceeded') { return $null }
        if ($snapshot.StateCode -eq 'Stopped' -and $snapshot.ReasonCode -eq 'MarketDataUnavailable') { return $null }
        return $snapshot
    }
    $summary.DriftSnapshotAtExecutionStart = $normalSnapshot

    $workerHandle = Start-ManagedProcess -FilePath 'dotnet' -ArgumentList @('run', '--no-build', '--no-launch-profile') -WorkingDirectory (Join-Path $repoRoot 'src\CoinBot.Worker') -StandardOutputPath $workerStdOutPath -StandardErrorPath $workerStdErrPath -EnvironmentVariables $environmentVariables

    Wait-Until -Name 'worker readiness summary' -TimeoutSeconds 120 -Condition {
        $lines = Get-LatestLogLines -Path $workerStdOutPath -Pattern 'EnabledBotCount=1'
        if ($lines.Count -gt 0) { return $lines }
        return $null
    } | Out-Null

    Wait-Until -Name 'first bot execution order' -TimeoutSeconds 180 -Condition {
        $orders = Get-SmokeOrders -ConnectionString $connectionString -BotId $botId
        if ($orders.Count -ge 1) { return $orders }
        return $null
    } | Out-Null

    $orders = Wait-Until -Name 'second bot execution order' -TimeoutSeconds 180 -Condition {
        $currentOrders = Get-SmokeOrders -ConnectionString $connectionString -BotId $botId
        if ($currentOrders.Count -ge 2) { return $currentOrders }
        return $null
    }

    $summary.Orders = $orders
    $summary.AttemptCount = $orders.Count
    $summary.Attempt1 = if ($orders.Count -ge 1) { $orders[0] } else { $null }
    $summary.Attempt2 = if ($orders.Count -ge 2) { $orders[1] } else { $null }
    $summary.WorkerReadinessLines = Get-LatestLogLines -Path $workerStdOutPath -Pattern 'Bot execution pilot readiness'
    $summary.WorkerTriggeredLines = Get-LatestLogLines -Path $workerStdOutPath -Pattern 'TriggeredJobs=' -Take 10
    $summary.CurrentClockDriftExceededLogLines = Get-LatestLogLines -Path $workerStdOutPath -Pattern 'ClockDriftExceeded|clock drift exceeded'
    $summary.PrivateRestAdvanceLogLines = Get-LatestLogLines -Path $workerStdOutPath -Pattern '/fapi/v1/marginType|/fapi/v1/leverage|/fapi/v1/order|Binance executor submitted order' -Take 20
    $summary.AdvancedBeyondClockDrift = @($orders | Where-Object { $_.FailureCode -ne 'ClockDriftExceeded' }).Count -gt 0
    $summary.ClockDriftStillBlocking = @($orders | Where-Object { $_.FailureCode -eq 'ClockDriftExceeded' }).Count -gt 0
    $summary.Comparison = if ($summary.AdvancedBeyondClockDrift) { 'Historical blocker was ClockDriftExceeded before order attempt. Current runtime measured healthy drift and advanced the same bot to later execution/private-rest failure states.' } elseif ($summary.ClockDriftStillBlocking) { 'Current runtime still blocked on ClockDriftExceeded, but drift snapshot now records measured milliseconds, local UTC, exchange UTC and degraded-mode state explicitly.' } else { 'Current runtime changed behavior away from the historical drift blocker, but review order failure details for the exact downstream reason.' }
    $summary.DriftSnapshotAfterAttempts = Get-ClockDriftSnapshot -ConnectionString $connectionString

    $summary | ConvertTo-Json -Depth 8 | Set-Content -Path $summaryPath -Encoding UTF8

    Write-Host ('SmokeDatabaseName=' + $smokeDatabaseName)
    Write-Host ('DriftDetail=' + $summary.DriftSnapshotAfterAttempts.Detail)
    Write-Host ('DegradedReason=' + $summary.DriftSnapshotAfterAttempts.ReasonCode)
    Write-Host ('AttemptCount=' + $orders.Count)
    if ($summary.Attempt1) {
        Write-Host ('Attempt1FailureCode=' + $summary.Attempt1.FailureCode)
        Write-Host ('Attempt1FailureDetail=' + $summary.Attempt1.FailureDetail)
    }
    if ($summary.Attempt2) {
        Write-Host ('Attempt2FailureCode=' + $summary.Attempt2.FailureCode)
        Write-Host ('Attempt2FailureDetail=' + $summary.Attempt2.FailureDetail)
    }
    Write-Host ('AdvancedBeyondClockDrift=' + $summary.AdvancedBeyondClockDrift)
    Write-Host ('SummaryPath=' + $summaryPath)
}
finally {
    Stop-ManagedProcess -Handle $workerHandle
    Stop-ManagedProcess -Handle $webHandle
    if ($randomKeyBytes) { [System.Security.Cryptography.CryptographicOperations]::ZeroMemory($randomKeyBytes) }
}



