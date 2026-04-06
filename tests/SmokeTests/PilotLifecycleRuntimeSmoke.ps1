$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
# Local-only operational evidence. `.diag/` is git-ignored and must stay out of commits.
$diagRoot = Join-Path $repoRoot '.diag\pilot-lifecycle-runtime-smoke'
$runStamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N')
$runRoot = Join-Path $diagRoot $runStamp
$webStdOutPath = Join-Path $runRoot 'web.stdout.log'
$webStdErrPath = Join-Path $runRoot 'web.stderr.log'
$warmupStdOutPath = Join-Path $runRoot 'worker-warmup.stdout.log'
$warmupStdErrPath = Join-Path $runRoot 'worker-warmup.stderr.log'
$submitStdOutPath = Join-Path $runRoot 'worker-submit.stdout.log'
$submitStdErrPath = Join-Path $runRoot 'worker-submit.stderr.log'
$summaryPath = Join-Path $runRoot 'pilot-lifecycle-runtime-smoke-summary.json'

function New-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ($listener.LocalEndpoint).Port } finally { $listener.Stop() }
}

function Wait-Until {
    param([string]$Name, [scriptblock]$Condition, [int]$TimeoutSeconds = 120, [int]$PollMilliseconds = 1000)
    $startedAt = Get-Date
    $lastError = $null
    while (((Get-Date) - $startedAt).TotalSeconds -lt $TimeoutSeconds) {
        try {
            $result = & $Condition
            if ($null -ne $result -and $false -ne $result) { return $result }
        }
        catch {
            $lastError = $_
        }
        Start-Sleep -Milliseconds $PollMilliseconds
    }
    if ($lastError) { throw "Timed out while waiting for $Name. Last error: $($lastError.Exception.Message)" }
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
        $command.CommandTimeout = 180
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
        $command.CommandTimeout = 180
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
        $command.CommandTimeout = 180
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
    }
    catch {
    }
    finally {
        try { $process.Dispose() } catch {}
    }
}

function Get-UserSecretsConnectionString {
    $userSecretsId = '016c8a65-b0e7-404b-a04c-0a51f7bea920'
    $path = Join-Path $env:APPDATA ("Microsoft\\UserSecrets\\$userSecretsId\\secrets.json")
    if (-not (Test-Path $path)) { throw 'User secrets file is missing.' }
    $json = Get-Content $path -Raw | ConvertFrom-Json
    $connectionString = $json.'ConnectionStrings:DefaultConnection'
    if ([string]::IsNullOrWhiteSpace($connectionString)) { throw 'ConnectionStrings:DefaultConnection is missing from user-secrets.' }
    return $connectionString
}

function Ensure-SqlDatabaseExists {
    param([string]$ConnectionString)
    $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($ConnectionString)
    $databaseName = $builder.InitialCatalog
    if ([string]::IsNullOrWhiteSpace($databaseName)) { throw 'Database name is required for smoke bootstrap.' }
    $masterConnectionString = $ConnectionString -replace 'Database=[^;]+', 'Database=master'
    $escapedDatabaseName = $databaseName.Replace(']', ']]')
    Invoke-SqlNonQuery -ConnectionString $masterConnectionString -CommandText "IF DB_ID(N'$databaseName') IS NULL BEGIN EXEC(N'CREATE DATABASE [$escapedDatabaseName]') END;" | Out-Null
}

function Get-LatestLogLines {
    param([string]$Path, [string]$Pattern, [int]$Take = 10)
    if (-not (Test-Path $Path)) { return @() }
    return @(Select-String -Path $Path -Pattern $Pattern | Select-Object -Last $Take | ForEach-Object { $_.Line.Trim() })
}
function Get-SourceBootstrap {
    param([string]$ConnectionString)

    $preflight = Invoke-SqlRow -ConnectionString $ConnectionString -CommandText @"
SELECT
    (SELECT COUNT(*) FROM ExchangeAccounts WHERE IsDeleted = 0 AND CredentialStatus = 'Active' AND ApiKeyCiphertext IS NOT NULL AND ApiSecretCiphertext IS NOT NULL) AS ActiveAccounts,
    (SELECT COUNT(*) FROM TradingBots WHERE IsDeleted = 0 AND IsEnabled = 1) AS EnabledBots,
    (SELECT COUNT(*) FROM ExecutionOrders WHERE IsDeleted = 0 AND State IN ('Received','GatePassed','Dispatching','Submitted','PartiallyFilled')) AS OpenExecutionOrders,
    (SELECT COUNT(*) FROM ExchangePositions WHERE IsDeleted = 0 AND ABS(Quantity) > 0) AS OpenPositions;
"@

    if ($preflight.ActiveAccounts -ne 1) { throw "Pilot smoke requires exactly one active exchange account. Found=$($preflight.ActiveAccounts)." }
    if ($preflight.EnabledBots -ne 1) { throw "Pilot smoke requires exactly one enabled bot. Found=$($preflight.EnabledBots)." }
    if ($preflight.OpenExecutionOrders -ne 0) { throw "Pilot smoke requires zero open execution orders. Found=$($preflight.OpenExecutionOrders)." }
    if ($preflight.OpenPositions -ne 0) { throw "Pilot smoke requires zero open positions. Found=$($preflight.OpenPositions)." }

    $row = Invoke-SqlRow -ConnectionString $ConnectionString -CommandText @"
SELECT TOP (1)
    ea.ApiKeyCiphertext,
    ea.ApiSecretCiphertext,
    ea.CredentialFingerprint,
    ea.CredentialKeyVersion,
    b.Symbol,
    v.ValidationStatus,
    v.PermissionSummary,
    v.EnvironmentScope,
    v.IsEnvironmentMatch,
    v.IsKeyValid,
    v.CanTrade,
    v.CanWithdraw,
    v.SupportsSpot,
    v.SupportsFutures,
    v.HasTimestampSkew,
    v.HasIpRestrictionIssue,
    v.FailureReason,
    v.CorrelationId,
    v.ValidatedAtUtc,
    s.PrivateStreamConnectionState,
    s.DriftStatus,
    COALESCE(s.LastPrivateStreamEventAtUtc, s.LastBalanceSyncedAtUtc, s.LastPositionSyncedAtUtc, s.LastStateReconciledAtUtc) AS LastPrivateSyncAtUtc
FROM ExchangeAccounts ea
INNER JOIN TradingBots b ON b.ExchangeAccountId = ea.Id AND b.IsDeleted = 0 AND b.IsEnabled = 1
OUTER APPLY (
    SELECT TOP (1) *
    FROM ApiCredentialValidations
    WHERE ExchangeAccountId = ea.Id AND IsDeleted = 0
    ORDER BY ValidatedAtUtc DESC, CreatedDate DESC
) v
OUTER APPLY (
    SELECT TOP (1) *
    FROM ExchangeAccountSyncStates
    WHERE ExchangeAccountId = ea.Id AND Plane = 'Futures' AND IsDeleted = 0
    ORDER BY UpdatedDate DESC, CreatedDate DESC
) s
WHERE ea.IsDeleted = 0 AND ea.CredentialStatus = 'Active'
ORDER BY b.CreatedDate DESC;
"@

    if ($null -eq $row) { throw 'Source bootstrap row is missing.' }
    if ($row.ValidationStatus -ne 'Valid' -or -not $row.IsKeyValid -or -not $row.CanTrade -or -not $row.SupportsFutures -or -not $row.IsEnvironmentMatch -or $row.EnvironmentScope -ne 'Demo') {
        throw 'Source credential validation is not testnet-trade ready.'
    }

    return [pscustomobject]@{ Preflight = $preflight; Source = $row }
}

function Seed-SmokeGraph {
    param([string]$ConnectionString, [pscustomobject]$Bootstrap, [string]$UserId, [string]$Email, [guid]$StrategyId, [guid]$StrategyVersionId, [guid]$ExchangeAccountId, [guid]$ApiCredentialId, [guid]$BotId, [datetime]$UtcNow)

    $definitionJson = [string]::Join([Environment]::NewLine, @(
        '{', '  "schemaVersion": 1,', '  "entry": {', '    "operator": "all",', '    "rules": [', '      {', '        "path": "context.mode",', '        "comparison": "equals",', '        "value": "Live"', '      }', '    ]', '  },', '  "risk": {', '    "operator": "all",', '    "rules": [', '      {', '        "path": "indicator.sampleCount",', '        "comparison": "greaterThanOrEqual",', '        "value": 100', '      }', '    ]', '  }', '}'
    ))

    Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText "INSERT INTO AspNetUsers (Id, FullName, UserName, NormalizedUserName, Email, NormalizedEmail, EmailConfirmed, PasswordHash, SecurityStamp, ConcurrencyStamp, PhoneNumber, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnd, LockoutEnabled, AccessFailedCount, MfaEnabled, TotpEnabled, EmailOtpEnabled, PreferredMfaProvider, TotpSecretCiphertext, MfaUpdatedAtUtc, TradingModeOverride, TradingModeApprovedAtUtc, TradingModeApprovalReference, PreferredTimeZoneId) VALUES (@UserId, 'Pilot Lifecycle Smoke', @Email, UPPER(@Email), @Email, UPPER(@Email), 1, NULL, NEWID(), NEWID(), NULL, 0, 0, NULL, 1, 0, 0, 0, 0, NULL, NULL, NULL, NULL, NULL, NULL, 'UTC');" -Parameters @{ UserId = $UserId; Email = $Email } | Out-Null
    Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText @"
IF EXISTS (SELECT 1 FROM GlobalExecutionSwitches)
BEGIN
    UPDATE GlobalExecutionSwitches SET TradeMasterState = 'Armed', DemoModeEnabled = 1, UpdatedDate = @UtcNow, IsDeleted = 0;
END
ELSE
BEGIN
    INSERT INTO GlobalExecutionSwitches (Id, TradeMasterState, DemoModeEnabled, LiveModeApprovedAtUtc, LiveModeApprovalReference, CreatedDate, UpdatedDate, IsDeleted)
    VALUES ('0F4D61F5-595D-4C35-9B21-3D87A0F1D001', 'Armed', 1, NULL, NULL, @UtcNow, @UtcNow, 0);
END
"@ -Parameters @{ UtcNow = $UtcNow } | Out-Null
    Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText "INSERT INTO TradingStrategies (Id, StrategyKey, DisplayName, PromotionState, PublishedMode, PublishedAtUtc, LivePromotionApprovedAtUtc, LivePromotionApprovalReference, CreatedDate, UpdatedDate, IsDeleted, OwnerUserId) VALUES (@StrategyId, 'pilot-lifecycle-smoke-core', 'Pilot Lifecycle Smoke Core', 'LivePublished', 'Live', @UtcNow, @UtcNow, 'pilot-lifecycle-smoke', @UtcNow, @UtcNow, 0, @UserId);" -Parameters @{ StrategyId = $StrategyId; UtcNow = $UtcNow; UserId = $UserId } | Out-Null
    Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText "INSERT INTO TradingStrategyVersions (Id, TradingStrategyId, SchemaVersion, VersionNumber, Status, DefinitionJson, PublishedAtUtc, ArchivedAtUtc, CreatedDate, UpdatedDate, IsDeleted, OwnerUserId) VALUES (@StrategyVersionId, @StrategyId, 1, 1, 'Published', @DefinitionJson, @UtcNow, NULL, @UtcNow, @UtcNow, 0, @UserId);" -Parameters @{ StrategyVersionId = $StrategyVersionId; StrategyId = $StrategyId; DefinitionJson = $definitionJson; UtcNow = $UtcNow; UserId = $UserId } | Out-Null
    Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText "INSERT INTO RiskProfiles (Id, ProfileName, MaxDailyLossPercentage, MaxPositionSizePercentage, MaxLeverage, KillSwitchEnabled, CreatedDate, UpdatedDate, IsDeleted, OwnerUserId) VALUES (NEWID(), 'Pilot Lifecycle Smoke', 10.0000, 100.0000, 2.0000, 0, @UtcNow, @UtcNow, 0, @UserId);" -Parameters @{ UtcNow = $UtcNow; UserId = $UserId } | Out-Null
    Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText "INSERT INTO ExchangeAccounts (Id, ExchangeName, DisplayName, IsReadOnly, LastValidatedAt, ApiKeyCiphertext, ApiSecretCiphertext, CredentialFingerprint, CredentialKeyVersion, CredentialStatus, CredentialStoredAtUtc, CredentialLastAccessedAtUtc, CredentialLastRotatedAtUtc, CredentialRevalidateAfterUtc, CredentialRotateAfterUtc, CreatedDate, UpdatedDate, IsDeleted, OwnerUserId) VALUES (@ExchangeAccountId, 'Binance', 'Pilot Lifecycle Smoke Binance', 0, @UtcNow, @ApiKeyCiphertext, @ApiSecretCiphertext, @CredentialFingerprint, @CredentialKeyVersion, 'Active', @UtcNow, NULL, @UtcNow, DATEADD(DAY, 30, @UtcNow), DATEADD(DAY, 90, @UtcNow), @UtcNow, @UtcNow, 0, @UserId);" -Parameters @{ ExchangeAccountId = $ExchangeAccountId; UtcNow = $UtcNow; UserId = $UserId; ApiKeyCiphertext = $Bootstrap.Source.ApiKeyCiphertext; ApiSecretCiphertext = $Bootstrap.Source.ApiSecretCiphertext; CredentialFingerprint = $Bootstrap.Source.CredentialFingerprint; CredentialKeyVersion = if ([string]::IsNullOrWhiteSpace([string]$Bootstrap.Source.CredentialKeyVersion)) { 'credential-v1' } else { $Bootstrap.Source.CredentialKeyVersion } } | Out-Null
    Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText "INSERT INTO ApiCredentials (Id, ExchangeAccountId, OwnerUserId, ApiKeyCiphertext, ApiSecretCiphertext, CredentialFingerprint, KeyVersion, EncryptedBlobVersion, ValidationStatus, PermissionSummary, StoredAtUtc, LastValidatedAtUtc, LastFailureReason, CreatedDate, UpdatedDate, IsDeleted) VALUES (@ApiCredentialId, @ExchangeAccountId, @UserId, @ApiKeyCiphertext, @ApiSecretCiphertext, @CredentialFingerprint, @CredentialKeyVersion, 1, @ValidationStatus, @PermissionSummary, @UtcNow, @ValidatedAtUtc, @FailureReason, @UtcNow, @UtcNow, 0);" -Parameters @{ ApiCredentialId = $ApiCredentialId; ExchangeAccountId = $ExchangeAccountId; UserId = $UserId; ApiKeyCiphertext = $Bootstrap.Source.ApiKeyCiphertext; ApiSecretCiphertext = $Bootstrap.Source.ApiSecretCiphertext; CredentialFingerprint = $Bootstrap.Source.CredentialFingerprint; CredentialKeyVersion = if ([string]::IsNullOrWhiteSpace([string]$Bootstrap.Source.CredentialKeyVersion)) { 'credential-v1' } else { $Bootstrap.Source.CredentialKeyVersion }; ValidationStatus = $Bootstrap.Source.ValidationStatus; PermissionSummary = $Bootstrap.Source.PermissionSummary; UtcNow = $UtcNow; ValidatedAtUtc = $Bootstrap.Source.ValidatedAtUtc; FailureReason = $Bootstrap.Source.FailureReason } | Out-Null
    Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText "INSERT INTO ApiCredentialValidations (Id, ApiCredentialId, ExchangeAccountId, OwnerUserId, IsKeyValid, CanTrade, CanWithdraw, SupportsSpot, SupportsFutures, EnvironmentScope, IsEnvironmentMatch, HasTimestampSkew, HasIpRestrictionIssue, ValidationStatus, PermissionSummary, FailureReason, CorrelationId, ValidatedAtUtc, CreatedDate, UpdatedDate, IsDeleted) VALUES (NEWID(), @ApiCredentialId, @ExchangeAccountId, @UserId, @IsKeyValid, @CanTrade, @CanWithdraw, @SupportsSpot, @SupportsFutures, @EnvironmentScope, @IsEnvironmentMatch, @HasTimestampSkew, @HasIpRestrictionIssue, @ValidationStatus, @PermissionSummary, @FailureReason, @CorrelationId, @ValidatedAtUtc, @UtcNow, @UtcNow, 0);" -Parameters @{ ApiCredentialId = $ApiCredentialId; ExchangeAccountId = $ExchangeAccountId; UserId = $UserId; IsKeyValid = $Bootstrap.Source.IsKeyValid; CanTrade = $Bootstrap.Source.CanTrade; CanWithdraw = if ($null -eq $Bootstrap.Source.CanWithdraw) { $false } else { $Bootstrap.Source.CanWithdraw }; SupportsSpot = if ($null -eq $Bootstrap.Source.SupportsSpot) { $false } else { $Bootstrap.Source.SupportsSpot }; SupportsFutures = $Bootstrap.Source.SupportsFutures; EnvironmentScope = $Bootstrap.Source.EnvironmentScope; IsEnvironmentMatch = $Bootstrap.Source.IsEnvironmentMatch; HasTimestampSkew = if ($null -eq $Bootstrap.Source.HasTimestampSkew) { $false } else { $Bootstrap.Source.HasTimestampSkew }; HasIpRestrictionIssue = if ($null -eq $Bootstrap.Source.HasIpRestrictionIssue) { $false } else { $Bootstrap.Source.HasIpRestrictionIssue }; ValidationStatus = $Bootstrap.Source.ValidationStatus; PermissionSummary = $Bootstrap.Source.PermissionSummary; FailureReason = $Bootstrap.Source.FailureReason; CorrelationId = $Bootstrap.Source.CorrelationId; ValidatedAtUtc = $Bootstrap.Source.ValidatedAtUtc; UtcNow = $UtcNow } | Out-Null
    Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText "INSERT INTO TradingBots (Id, Name, StrategyKey, Symbol, Quantity, ExchangeAccountId, Leverage, MarginType, IsEnabled, TradingModeOverride, TradingModeApprovedAtUtc, TradingModeApprovalReference, OpenOrderCount, OpenPositionCount, CreatedDate, UpdatedDate, IsDeleted, OwnerUserId) VALUES (@BotId, 'Pilot Lifecycle Smoke Bot', 'pilot-lifecycle-smoke-core', @Symbol, 0.002, @ExchangeAccountId, 1, 'ISOLATED', 1, NULL, NULL, NULL, 0, 0, @UtcNow, @UtcNow, 0, @UserId);" -Parameters @{ BotId = $BotId; Symbol = $Bootstrap.Source.Symbol; ExchangeAccountId = $ExchangeAccountId; UtcNow = $UtcNow; UserId = $UserId } | Out-Null
    Seed-SmokeGlobalPolicy -ConnectionString $ConnectionString -UtcNow $UtcNow
}

function Seed-SmokeGlobalPolicy {
    param([string]$ConnectionString, [datetime]$UtcNow)

    $policyJson = [ordered]@{
        PolicyKey = 'GlobalRiskPolicy'
        ExecutionGuardPolicy = [ordered]@{
            MaxOrderNotional = 1000000
            MaxPositionNotional = $null
            MaxDailyTrades = $null
            CloseOnlyBlocksNewPositions = $true
        }
        AutonomyPolicy = [ordered]@{
            Mode = 1
            RequireManualApprovalForLive = $false
        }
        SymbolRestrictions = @()
    } | ConvertTo-Json -Compress -Depth 8

    $policyHashBytes = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($policyJson))
    $policyHash = ([System.BitConverter]::ToString($policyHashBytes)).Replace('-', '').ToLowerInvariant()
    $policyId = '8A8A6C2B-7B4D-4B1C-9136-4AF1D06F2C21'

    Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText @"
DELETE FROM RiskPolicyVersions;
DELETE FROM RiskPolicies;
INSERT INTO RiskPolicies (Id, PolicyKey, CurrentVersion, PolicyJson, PolicyHash, LastUpdatedAtUtc, LastUpdatedByUserId, LastChangeSummary)
VALUES (@PolicyId, 'GlobalRiskPolicy', 1, @PolicyJson, @PolicyHash, @UtcNow, 'system', 'Pilot runtime smoke policy bootstrap');
INSERT INTO RiskPolicyVersions (Id, RiskPolicyId, Version, CreatedAtUtc, CreatedByUserId, Source, CorrelationId, ChangeSummary, PolicyJson, DiffJson, RolledBackFromVersion)
VALUES (NEWID(), @PolicyId, 1, @UtcNow, 'system', 'PilotLifecycleRuntimeSmoke', 'pilot-lifecycle-runtime-smoke', 'Pilot runtime smoke policy bootstrap', @PolicyJson, '[]', NULL);
"@ -Parameters @{ PolicyId = $policyId; PolicyJson = $policyJson; PolicyHash = $policyHash; UtcNow = $UtcNow } | Out-Null
}

function Get-ReadinessSnapshot {
    param([string]$ConnectionString, [guid]$ExchangeAccountId, [string]$Symbol, [string]$Timeframe)
    Invoke-SqlRow -ConnectionString $ConnectionString -CommandText @"
SELECT TOP (1)
    d.StateCode, d.ReasonCode, d.LatestHeartbeatSource, d.LatestDataTimestampAtUtc, d.LatestHeartbeatReceivedAtUtc,
    s.PrivateStreamConnectionState, s.DriftStatus,
    COALESCE(s.LastPrivateStreamEventAtUtc, s.LastBalanceSyncedAtUtc, s.LastPositionSyncedAtUtc, s.LastStateReconciledAtUtc) AS LastPrivateSyncAtUtc
FROM ExchangeAccounts ea
OUTER APPLY (SELECT TOP (1) * FROM DegradedModeStates WHERE LatestSymbol = @Symbol AND LatestTimeframe = @Timeframe AND IsDeleted = 0 ORDER BY UpdatedDate DESC, CreatedDate DESC) d
OUTER APPLY (SELECT TOP (1) * FROM ExchangeAccountSyncStates WHERE ExchangeAccountId = @ExchangeAccountId AND Plane = 'Futures' AND IsDeleted = 0 ORDER BY UpdatedDate DESC, CreatedDate DESC) s
WHERE ea.Id = @ExchangeAccountId;
"@ -Parameters @{ ExchangeAccountId = $ExchangeAccountId; Symbol = $Symbol; Timeframe = $Timeframe }
}

function Get-SmokeOrders { param([string]$ConnectionString, [guid]$BotId) Invoke-SqlRows -ConnectionString $ConnectionString -CommandText "SELECT TOP (10) Id, StrategySignalId, Plane, State, FailureCode, FailureDetail, ReconciliationStatus, ReconciliationSummary, SubmittedToBroker, ExternalOrderId, FilledQuantity, AverageFillPrice, SubmittedAtUtc, LastStateChangedAtUtc, CreatedDate FROM ExecutionOrders WHERE BotId = @BotId AND IsDeleted = 0 ORDER BY CreatedDate DESC;" -Parameters @{ BotId = $BotId } }
function Get-SmokeTransitions { param([string]$ConnectionString, [guid]$ExecutionOrderId) Invoke-SqlRows -ConnectionString $ConnectionString -CommandText "SELECT TOP (20) SequenceNumber, State, EventCode, Detail, CorrelationId, ParentCorrelationId, OccurredAtUtc FROM ExecutionOrderTransitions WHERE ExecutionOrderId = @ExecutionOrderId AND IsDeleted = 0 ORDER BY SequenceNumber ASC;" -Parameters @{ ExecutionOrderId = $ExecutionOrderId } }
function Get-SmokeExecutionTraces { param([string]$ConnectionString, [guid]$ExecutionOrderId) Invoke-SqlRows -ConnectionString $ConnectionString -CommandText "SELECT TOP (20) Provider, Endpoint, HttpStatusCode, ExchangeCode, LatencyMs, CreatedAtUtc FROM ExecutionTraces WHERE ExecutionOrderId = @ExecutionOrderId AND IsDeleted = 0 ORDER BY CreatedAtUtc ASC;" -Parameters @{ ExecutionOrderId = $ExecutionOrderId } }
function Get-SmokePositions { param([string]$ConnectionString, [guid]$ExchangeAccountId) Invoke-SqlRows -ConnectionString $ConnectionString -CommandText "SELECT TOP (10) Symbol, PositionSide, Quantity, EntryPrice, BreakEvenPrice, UnrealizedProfit, MarginType, ExchangeUpdatedAtUtc FROM ExchangePositions WHERE ExchangeAccountId = @ExchangeAccountId AND IsDeleted = 0 ORDER BY UpdatedDate DESC;" -Parameters @{ ExchangeAccountId = $ExchangeAccountId } }
function Get-SmokeBalances { param([string]$ConnectionString, [guid]$ExchangeAccountId) Invoke-SqlRows -ConnectionString $ConnectionString -CommandText "SELECT TOP (10) Asset, WalletBalance, CrossWalletBalance, AvailableBalance, MaxWithdrawAmount, ExchangeUpdatedAtUtc FROM ExchangeBalances WHERE ExchangeAccountId = @ExchangeAccountId AND IsDeleted = 0 ORDER BY UpdatedDate DESC;" -Parameters @{ ExchangeAccountId = $ExchangeAccountId } }
function Set-SmokeBotEnabled {
    param([string]$ConnectionString, [guid]$BotId, [bool]$IsEnabled)
    Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText "UPDATE TradingBots SET IsEnabled = @IsEnabled, UpdatedDate = SYSUTCDATETIME() WHERE Id = @BotId AND IsDeleted = 0;" -Parameters @{ BotId = $BotId; IsEnabled = $IsEnabled } | Out-Null
}

function Build-EnvironmentVariables {
    param([string]$ConnectionString, [string]$BaseUrl, [string]$UserId, [guid]$BotId, [string]$Symbol, [bool]$PilotActivationEnabled, [string]$WorkerInstanceId)
    return @{
        DOTNET_CLI_HOME = (Join-Path $repoRoot '.dotnet')
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
        DOTNET_NOLOGO = '1'
        ASPNETCORE_ENVIRONMENT = 'Development'
        DOTNET_ENVIRONMENT = 'Development'
        ASPNETCORE_URLS = $BaseUrl
        ConnectionStrings__DefaultConnection = $ConnectionString
        JobOrchestration__Enabled = 'true'
        JobOrchestration__SchedulerPollIntervalSeconds = '1'
        JobOrchestration__BotExecutionIntervalSeconds = '1'
        JobOrchestration__InitialRetryDelaySeconds = '1'
        JobOrchestration__MaxRetryDelaySeconds = '5'
        JobOrchestration__MaxRetryAttempts = '5'
        JobOrchestration__WorkerInstanceId = $WorkerInstanceId
        MarketData__Binance__Enabled = 'true'
        MarketData__Binance__RestBaseUrl = 'https://testnet.binancefuture.com'
        MarketData__Binance__WebSocketBaseUrl = 'wss://fstream.binancefuture.com'
        MarketData__Binance__SeedSymbols__0 = $Symbol
        MarketData__HistoricalGapFiller__Enabled = 'false'
        MarketData__Scanner__Enabled = 'false'
        MarketData__Scanner__HandoffEnabled = 'false'
        ExchangeSync__Binance__Enabled = 'true'
        ExchangeSync__Binance__RestBaseUrl = 'https://testnet.binancefuture.com'
        ExchangeSync__Binance__WebSocketBaseUrl = 'wss://fstream.binancefuture.com'
        ExchangeSync__Binance__SessionScanIntervalSeconds = '5'
        ExchangeSync__Binance__ReconnectDelaySeconds = '5'
        ExchangeSync__Binance__ReconciliationIntervalMinutes = '1'
        BotExecutionPilot__Enabled = 'true'
        BotExecutionPilot__PilotActivationEnabled = if ($PilotActivationEnabled) { 'true' } else { 'false' }
        BotExecutionPilot__SignalEvaluationMode = 'Live'
        BotExecutionPilot__DefaultSymbol = $Symbol
        BotExecutionPilot__Timeframe = '1m'
        BotExecutionPilot__DefaultLeverage = '1'
        BotExecutionPilot__DefaultMarginType = 'ISOLATED'
        BotExecutionPilot__AllowedUserIds__0 = $UserId
        BotExecutionPilot__AllowedBotIds__0 = $BotId.ToString('N')
        BotExecutionPilot__AllowedSymbols__0 = $Symbol
        BotExecutionPilot__AllowedSymbols__1 = ''
        BotExecutionPilot__AllowedSymbols__2 = ''
        BotExecutionPilot__MaxPilotOrderNotional = '250'
        BotExecutionPilot__MaxOpenPositionsPerUser = '1'
        BotExecutionPilot__PerBotCooldownSeconds = '120'
        BotExecutionPilot__PerSymbolCooldownSeconds = '60'
        BotExecutionPilot__MaxDailyLossPercentage = '1'
        BotExecutionPilot__PrimeHistoricalCandleCount = '120'
    }
}
if (Test-Path $runRoot) { Remove-Item $runRoot -Recurse -Force }
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

$sourceConnectionString = Get-UserSecretsConnectionString
$bootstrap = Get-SourceBootstrap -ConnectionString $sourceConnectionString
$smokeDatabaseName = 'CoinBotPilotLifecycleSmoke_' + [Guid]::NewGuid().ToString('N')
$connectionString = 'Server=(localdb)\MSSQLLocalDB;Database=' + $smokeDatabaseName + ';Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True'
Ensure-SqlDatabaseExists -ConnectionString $connectionString

$webPort = New-FreeTcpPort
$baseUrl = 'http://127.0.0.1:' + $webPort
$smokeUserId = 'pilot-lifecycle-smoke-user'
$smokeEmail = 'pilot.lifecycle.smoke@coinbot.test'
$strategyId = [Guid]'71111111-1111-1111-1111-111111111111'
$strategyVersionId = [Guid]'72222222-2222-2222-2222-222222222222'
$exchangeAccountId = [Guid]'73333333-3333-3333-3333-333333333333'
$apiCredentialId = [Guid]'74444444-4444-4444-4444-444444444444'
$botId = [Guid]'75555555-5555-5555-5555-555555555555'
$symbol = [string]$bootstrap.Source.Symbol
$summary = [ordered]@{
    SelectedPlane = 'Futures'
    SmokeDatabaseName = $smokeDatabaseName
    SummaryPath = $summaryPath
    SummaryStoragePolicy = 'LocalGitIgnoredEvidence'
    SourcePreflight = [ordered]@{
        SelectedPlane = 'Futures'
        ActiveAccounts = $bootstrap.Preflight.ActiveAccounts
        EnabledBots = $bootstrap.Preflight.EnabledBots
        OpenExecutionOrders = $bootstrap.Preflight.OpenExecutionOrders
        OpenPositions = $bootstrap.Preflight.OpenPositions
        Symbol = $symbol
        ValidationStatus = $bootstrap.Source.ValidationStatus
        EnvironmentScope = $bootstrap.Source.EnvironmentScope
        SupportsSpot = $bootstrap.Source.SupportsSpot
        SupportsFutures = $bootstrap.Source.SupportsFutures
        SmokePolicyMode = 'RecommendOnly'
        PrivateStreamConnectionState = $bootstrap.Source.PrivateStreamConnectionState
        DriftStatus = $bootstrap.Source.DriftStatus
    }
}

$webHandle = $null
$warmupWorkerHandle = $null
$submitWorkerHandle = $null

try {
    $webEnvironment = Build-EnvironmentVariables -ConnectionString $connectionString -BaseUrl $baseUrl -UserId $smokeUserId -BotId $botId -Symbol $symbol -PilotActivationEnabled:$false -WorkerInstanceId 'pilot-lifecycle-web'
    $webEnvironment['JobOrchestration__Enabled'] = 'false'
    $webHandle = Start-ManagedProcess -FilePath 'dotnet' -ArgumentList @('run', '--project', 'src\CoinBot.Web\CoinBot.Web.csproj', '--no-build', '--no-launch-profile') -WorkingDirectory $repoRoot -StandardOutputPath $webStdOutPath -StandardErrorPath $webStdErrPath -EnvironmentVariables $webEnvironment

    Wait-Until -Name 'web startup' -TimeoutSeconds 120 -Condition {
        try {
            $response = Invoke-WebRequest -Uri ($baseUrl + '/health/live') -MaximumRedirection 0 -SkipHttpErrorCheck
            return $response.StatusCode -eq 200
        }
        catch {
            return $false
        }
    } | Out-Null

    Seed-SmokeGraph -ConnectionString $connectionString -Bootstrap $bootstrap -UserId $smokeUserId -Email $smokeEmail -StrategyId $strategyId -StrategyVersionId $strategyVersionId -ExchangeAccountId $exchangeAccountId -ApiCredentialId $apiCredentialId -BotId $botId -UtcNow ([DateTime]::UtcNow)

    $warmupEnvironment = Build-EnvironmentVariables -ConnectionString $connectionString -BaseUrl $baseUrl -UserId $smokeUserId -BotId $botId -Symbol $symbol -PilotActivationEnabled:$false -WorkerInstanceId 'pilot-lifecycle-warmup'
    $warmupWorkerHandle = Start-ManagedProcess -FilePath 'dotnet' -ArgumentList @('run', '--project', 'src\CoinBot.Worker\CoinBot.Worker.csproj', '--no-build', '--no-launch-profile') -WorkingDirectory $repoRoot -StandardOutputPath $warmupStdOutPath -StandardErrorPath $warmupStdErrPath -EnvironmentVariables $warmupEnvironment

    $summary.WarmupReadiness = Wait-Until -Name 'market/private plane readiness' -TimeoutSeconds 240 -Condition {
        $snapshot = Get-ReadinessSnapshot -ConnectionString $connectionString -ExchangeAccountId $exchangeAccountId -Symbol $symbol -Timeframe '1m'
        if ($null -eq $snapshot) { return $null }
        if ($snapshot.StateCode -ne 'Normal' -or $snapshot.ReasonCode -ne 'None') { return $null }
        if ($snapshot.PrivateStreamConnectionState -ne 'Connected' -or $snapshot.DriftStatus -ne 'InSync') { return $null }
        if ($null -eq $snapshot.LastPrivateSyncAtUtc) { return $null }
        return $snapshot
    }

    $baselineDataTimestampUtc = [DateTime]$summary.WarmupReadiness.LatestDataTimestampAtUtc
    $summary.SubmitReadiness = Wait-Until -Name 'fresh closed candle after warmup' -TimeoutSeconds 120 -Condition {
        $snapshot = Get-ReadinessSnapshot -ConnectionString $connectionString -ExchangeAccountId $exchangeAccountId -Symbol $symbol -Timeframe '1m'
        if ($null -eq $snapshot) { return $null }
        if ($snapshot.StateCode -ne 'Normal' -or $snapshot.ReasonCode -ne 'None') { return $null }
        if ($snapshot.PrivateStreamConnectionState -ne 'Connected' -or $snapshot.DriftStatus -ne 'InSync') { return $null }
        if ($null -eq $snapshot.LastPrivateSyncAtUtc -or $null -eq $snapshot.LatestDataTimestampAtUtc) { return $null }
        if ([DateTime]$snapshot.LatestDataTimestampAtUtc -le $baselineDataTimestampUtc) { return $null }
        return $snapshot
    }

    $warmupOrders = @(Get-SmokeOrders -ConnectionString $connectionString -BotId $botId)
    if ($warmupOrders.Count -ne 0) { throw "Warm-up phase created execution orders while PilotActivationEnabled=false. Count=$($warmupOrders.Count)." }
    $summary.WarmupSubmitBlocked = $true
    $summary.WarmupWorkerLines = Get-LatestLogLines -Path $warmupStdOutPath -Pattern 'PilotActivationEnabled is false|readiness|skipped submit' -Take 20
    Set-SmokeBotEnabled -ConnectionString $connectionString -BotId $botId -IsEnabled:$false

    Stop-ManagedProcess -Handle $warmupWorkerHandle
    $warmupWorkerHandle = $null

    $submitEnvironment = Build-EnvironmentVariables -ConnectionString $connectionString -BaseUrl $baseUrl -UserId $smokeUserId -BotId $botId -Symbol $symbol -PilotActivationEnabled:$true -WorkerInstanceId 'pilot-lifecycle-submit'
    $submitWorkerHandle = Start-ManagedProcess -FilePath 'dotnet' -ArgumentList @('run', '--project', 'src\CoinBot.Worker\CoinBot.Worker.csproj', '--no-build', '--no-launch-profile') -WorkingDirectory $repoRoot -StandardOutputPath $submitStdOutPath -StandardErrorPath $submitStdErrPath -EnvironmentVariables $submitEnvironment

    $submitWorkerReadiness = Wait-Until -Name 'submit worker market readiness' -TimeoutSeconds 180 -Condition {
        $snapshot = Get-ReadinessSnapshot -ConnectionString $connectionString -ExchangeAccountId $exchangeAccountId -Symbol $symbol -Timeframe '1m'
        if ($null -eq $snapshot) { return $null }
        if ($snapshot.StateCode -ne 'Normal' -or $snapshot.ReasonCode -ne 'None') { return $null }
        if ($snapshot.PrivateStreamConnectionState -ne 'Connected' -or $snapshot.DriftStatus -ne 'InSync') { return $null }
        if ($null -eq $snapshot.LastPrivateSyncAtUtc -or $null -eq $snapshot.LatestHeartbeatReceivedAtUtc) { return $null }

        $heartbeatAdvanced = [DateTime]$snapshot.LatestHeartbeatReceivedAtUtc -gt ([DateTime]$summary.SubmitReadiness.LatestHeartbeatReceivedAtUtc)
        $dataAdvanced = $null -ne $snapshot.LatestDataTimestampAtUtc -and [DateTime]$snapshot.LatestDataTimestampAtUtc -gt $baselineDataTimestampUtc
        if (-not $heartbeatAdvanced -and -not $dataAdvanced) { return $null }

        return $snapshot
    }

    $summary.SubmitWorkerReadiness = $submitWorkerReadiness
    Set-SmokeBotEnabled -ConnectionString $connectionString -BotId $botId -IsEnabled:$true

    $firstOrder = Wait-Until -Name 'first pilot execution order' -TimeoutSeconds 240 -Condition {
        $rows = @(Get-SmokeOrders -ConnectionString $connectionString -BotId $botId)
        if ($rows.Count -eq 0) { return $null }
        return $rows[0]
    }

    # Freeze the bot as soon as the first order exists so the smoke run cannot open a second pilot order.
    Set-SmokeBotEnabled -ConnectionString $connectionString -BotId $botId -IsEnabled:$false

    $submittedOrBlockedOrder = Wait-Until -Name 'broker submit or terminal pre-submit rejection' -TimeoutSeconds 240 -Condition {
        $row = (Get-SmokeOrders -ConnectionString $connectionString -BotId $botId | Where-Object { $_.Id -eq $firstOrder.Id } | Select-Object -First 1)
        if ($null -eq $row) { return $null }
        if (-not [string]::IsNullOrWhiteSpace([string]$row.FailureCode)) { return $row }
        if ($row.SubmittedToBroker -and $null -ne $row.SubmittedAtUtc) { return $row }
        return $null
    }

    Set-SmokeBotEnabled -ConnectionString $connectionString -BotId $botId -IsEnabled:$false
    Stop-ManagedProcess -Handle $submitWorkerHandle
    $submitWorkerHandle = $null

    if (-not $submittedOrBlockedOrder.SubmittedToBroker -and -not [string]::IsNullOrWhiteSpace([string]$submittedOrBlockedOrder.FailureCode)) {
        throw "Pilot submit stopped before broker dispatch. FailureCode=$($submittedOrBlockedOrder.FailureCode)."
    }

    $finalOrder = Wait-Until -Name 'broker response or terminal lifecycle state' -TimeoutSeconds 240 -Condition {
        $row = (Get-SmokeOrders -ConnectionString $connectionString -BotId $botId | Where-Object { $_.Id -eq $firstOrder.Id } | Select-Object -First 1)
        if ($null -eq $row) { return $null }
        if ($row.State -in @('Rejected','Cancelled','Filled','PartiallyFilled') -and ($row.SubmittedToBroker -or -not [string]::IsNullOrWhiteSpace([string]$row.FailureCode))) { return $row }
        if ($row.SubmittedToBroker -and $null -ne $row.SubmittedAtUtc -and -not [string]::IsNullOrWhiteSpace([string]$row.ExternalOrderId) -and $row.State -eq 'Submitted') { return $row }
        return $null
    }

    if (-not $finalOrder.SubmittedToBroker) {
        throw "Pilot order never reached broker submit. State=$($finalOrder.State); FailureCode=$($finalOrder.FailureCode)."
    }

    if ($finalOrder.Plane -ne 'Futures') {
        throw "Pilot smoke resolved unexpected execution plane. Plane=$($finalOrder.Plane)."
    }

    $transitions = Wait-Until -Name 'execution transitions' -TimeoutSeconds 180 -Condition {
        $rows = @(Get-SmokeTransitions -ConnectionString $connectionString -ExecutionOrderId ([Guid]$finalOrder.Id))
        if ($rows.Count -lt 4) { return $null }
        if (-not ($rows | Where-Object { $_.State -in @('Submitted','PartiallyFilled','Filled','Rejected','Cancelled') })) { return $null }
        return $rows
    }

    $orders = @(Get-SmokeOrders -ConnectionString $connectionString -BotId $botId)
    if ($orders.Count -ne 1) { throw "Duplicate or ghost execution order detected. OrderCount=$($orders.Count)." }

    $summary.SubmitWorkerLines = Get-LatestLogLines -Path $submitStdOutPath -Pattern 'Binance order placed|submitted order|rejected order|filled|Execution engine rejected' -Take 30
    $summary.FinalOrder = (Get-SmokeOrders -ConnectionString $connectionString -BotId $botId | Select-Object -First 1)
    $summary.Transitions = $transitions
    $summary.ExecutionTraces = @(Get-SmokeExecutionTraces -ConnectionString $connectionString -ExecutionOrderId ([Guid]$summary.FinalOrder.Id))
    $summary.Positions = @(Get-SmokePositions -ConnectionString $connectionString -ExchangeAccountId $exchangeAccountId)
    $summary.Balances = @(Get-SmokeBalances -ConnectionString $connectionString -ExchangeAccountId $exchangeAccountId)
    $summary.BrokerSubmitReached = [bool]$summary.FinalOrder.SubmittedToBroker
    $summary.ExternalOrderIdPresent = -not [string]::IsNullOrWhiteSpace([string]$summary.FinalOrder.ExternalOrderId)
    $summary.OrderCount = $orders.Count
    $summary.PositionCount = $summary.Positions.Count
    $summary.BalanceCount = $summary.Balances.Count
    $summary.ReconciliationExpectation = if ($summary.FinalOrder.ReconciliationStatus -eq 'Unknown') { 'AcceptedInterimStateUntilAsyncReconciliationRuns' } else { 'Observed' }
    $summary.ReconciliationNote = 'Pilot runtime smoke uses futures private-stream and order telemetry for immediate closure. ReconciliationStatus=Unknown is accepted at smoke completion until asynchronous reconciliation advances in a later cycle.'
    $summary.ReconciliationExpectationDetail = 'Async reconciliation should later populate LastReconciledAtUtc and move reconciliation status away from Unknown. If it does not, investigate reconciliation separately from smoke closure.'
    $summary.SmokePolicyMode = 'RecommendOnly'

    $summary | ConvertTo-Json -Depth 8 | Set-Content -Path $summaryPath -Encoding UTF8

    Write-Host ('SelectedPlane=' + $summary.SelectedPlane)
    Write-Host ('SmokePolicyMode=' + $summary.SmokePolicyMode)
    Write-Host ('SmokeDatabaseName=' + $smokeDatabaseName)
    Write-Host ('WarmupSubmitBlocked=' + $summary.WarmupSubmitBlocked)
    Write-Host ('BrokerSubmitReached=' + $summary.BrokerSubmitReached)
    Write-Host ('OrderPlane=' + $summary.FinalOrder.Plane)
    Write-Host ('OrderState=' + $summary.FinalOrder.State)
    Write-Host ('FailureCode=' + ($summary.FinalOrder.FailureCode ?? 'none'))
    Write-Host ('ExternalOrderIdPresent=' + $summary.ExternalOrderIdPresent)
    Write-Host ('ReconciliationStatus=' + ($summary.FinalOrder.ReconciliationStatus ?? 'none'))
    Write-Host ('PositionCount=' + $summary.PositionCount)
    Write-Host ('BalanceCount=' + $summary.BalanceCount)
    Write-Host ('SummaryPath=' + $summaryPath)
}
finally {
    Stop-ManagedProcess -Handle $submitWorkerHandle
    Stop-ManagedProcess -Handle $warmupWorkerHandle
    Stop-ManagedProcess -Handle $webHandle
}















