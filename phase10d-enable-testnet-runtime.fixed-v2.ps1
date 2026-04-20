[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Server = "localhost",
    [string]$Database = "CoinBotDb",
    [string]$UserId = "5a8675cf-cbcb-4c28-82fb-304aee442895",
    [guid]$BotId = "8EA3ED8B-C61F-4A44-A683-168AE9C69C43",
    [guid]$StrategyId = "ECF74430-966C-49CA-8EC9-7F31E8F63350",
    [string]$ApprovalReference = "phase10d-testnet-window",
    [string]$SnapshotPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($SnapshotPath)) {
    $SnapshotPath = Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts\phase10d-runtime-snapshot.json"
}

function Assert-SqlCmd {
    if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
        throw "sqlcmd was not found. Install SQL Server command line tools or run this script where sqlcmd is available."
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
    param([Parameter(Mandatory = $true)][string]$Query)

    $wrappedQuery = @"
SET NOCOUNT ON;
SELECT N'__JSON_BEGIN__' AS Phase10dMarker;
$Query
SELECT N'__JSON_END__' AS Phase10dMarker;
"@

    $tempFile = Join-Path $env:TEMP ("phase10d-enable-" + [Guid]::NewGuid().ToString("N") + ".sql")
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

function Invoke-SqlText {
    param([Parameter(Mandatory = $true)][string]$Query)

    $tempFile = Join-Path $env:TEMP ("phase10d-enable-" + [Guid]::NewGuid().ToString("N") + ".sql")
    Set-Content -LiteralPath $tempFile -Value $Query -Encoding UTF8
    try {
        $output = & sqlcmd -S $Server -d $Database -E -b -r 1 -i $tempFile 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw ($output -join [Environment]::NewLine)
        }

        return $output
    }
    finally {
        Remove-Item -LiteralPath $tempFile -Force -ErrorAction SilentlyContinue
    }
}

function Escape-SqlLiteral {
    param([Parameter(Mandatory = $true)][string]$Value)
    return $Value.Replace("'", "''")
}

Assert-SqlCmd

$botIdText = $BotId.ToString()
$strategyIdText = $StrategyId.ToString()
$approvalSql = Escape-SqlLiteral $ApprovalReference
$userIdSql = Escape-SqlLiteral $UserId

$snapshot = [ordered]@{
    CreatedAtUtc = [DateTime]::UtcNow.ToString("O")
    Server = $Server
    Database = $Database
    UserId = $UserId
    BotId = $botIdText
    StrategyId = $strategyIdText
    ApprovalReference = $ApprovalReference
    GlobalExecutionSwitch = Invoke-SqlJson @"
SELECT TOP (1)
    Id,
    TradeMasterState,
    DemoModeEnabled,
    LiveModeApprovedAtUtc,
    LiveModeApprovalReference,
    UpdatedDate
FROM GlobalExecutionSwitches
WHERE IsDeleted = 0
ORDER BY UpdatedDate DESC
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES;
"@
    User = Invoke-SqlJson @"
SELECT
    Id,
    TradingModeOverride,
    TradingModeApprovedAtUtc,
    TradingModeApprovalReference
FROM AspNetUsers
WHERE Id = N'$userIdSql'
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES;
"@
    Bot = Invoke-SqlJson @"
SELECT
    Id,
    OwnerUserId,
    StrategyKey,
    Symbol,
    ExchangeAccountId,
    IsEnabled,
    TradingModeOverride,
    TradingModeApprovedAtUtc,
    TradingModeApprovalReference,
    UpdatedDate
FROM TradingBots
WHERE Id = '$botIdText' AND IsDeleted = 0
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES;
"@
    Strategy = Invoke-SqlJson @"
SELECT
    Id,
    OwnerUserId,
    StrategyKey,
    PromotionState,
    PublishedMode,
    PublishedAtUtc,
    LivePromotionApprovedAtUtc,
    LivePromotionApprovalReference,
    ActiveTradingStrategyVersionId,
    UpdatedDate
FROM TradingStrategies
WHERE Id = '$strategyIdText' AND IsDeleted = 0
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES;
"@
    ActiveVersion = Invoke-SqlJson @"
SELECT
    v.Id,
    v.TradingStrategyId,
    v.Status,
    v.PublishedAtUtc,
    v.UpdatedDate
FROM TradingStrategyVersions v
JOIN TradingStrategies s ON s.ActiveTradingStrategyVersionId = v.Id
WHERE s.Id = '$strategyIdText'
  AND s.IsDeleted = 0
  AND v.IsDeleted = 0
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES;
"@
    ExchangeAccount = Invoke-SqlJson @"
SELECT
    a.Id,
    a.OwnerUserId,
    a.ExchangeName,
    a.IsReadOnly,
    a.CredentialStatus,
    a.UpdatedDate,
    v.ValidationStatus,
    v.IsKeyValid,
    v.CanTrade,
    v.SupportsFutures,
    v.EnvironmentScope,
    v.IsEnvironmentMatch,
    v.ValidatedAtUtc
FROM TradingBots b
JOIN ExchangeAccounts a ON a.Id = b.ExchangeAccountId AND a.IsDeleted = 0
OUTER APPLY (
    SELECT TOP (1)
        ValidationStatus,
        IsKeyValid,
        CanTrade,
        SupportsFutures,
        EnvironmentScope,
        IsEnvironmentMatch,
        ValidatedAtUtc
    FROM ApiCredentialValidations
    WHERE ExchangeAccountId = a.Id
      AND OwnerUserId = a.OwnerUserId
      AND IsDeleted = 0
    ORDER BY ValidatedAtUtc DESC, CreatedDate DESC
) v
WHERE b.Id = '$botIdText'
  AND b.OwnerUserId = N'$userIdSql'
  AND b.IsDeleted = 0
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES;
"@
}

if ($null -eq $snapshot.GlobalExecutionSwitch) { throw "GlobalExecutionSwitch snapshot is empty." }
if ($null -eq $snapshot.User) { throw "Target user snapshot is empty." }
if ($null -eq $snapshot.Bot) { throw "Target bot snapshot is empty." }
if ($null -eq $snapshot.Strategy) { throw "Target strategy snapshot is empty." }
if ($null -eq $snapshot.ActiveVersion) { throw "Active strategy version snapshot is empty." }
if ($null -eq $snapshot.ExchangeAccount) { throw "Target exchange account snapshot is empty." }

if ($snapshot.GlobalExecutionSwitch.TradeMasterState -ne "Armed") {
    throw "TradeMasterState is '$($snapshot.GlobalExecutionSwitch.TradeMasterState)'. Testnet window will not arm it automatically."
}

if ($snapshot.ActiveVersion.Status -ne "Published") {
    throw "Active strategy version is '$($snapshot.ActiveVersion.Status)', expected Published."
}

if ($snapshot.ExchangeAccount.ExchangeName -ne "Binance" -or
    $snapshot.ExchangeAccount.IsReadOnly -eq $true -or
    $snapshot.ExchangeAccount.CredentialStatus -ne "Active" -or
    $snapshot.ExchangeAccount.IsKeyValid -ne $true -or
    $snapshot.ExchangeAccount.CanTrade -ne $true -or
    $snapshot.ExchangeAccount.SupportsFutures -ne $true) {
    throw "Exchange account is not a writable, active Binance futures-capable account."
}

$snapshotDirectory = Split-Path $SnapshotPath -Parent
if (-not [string]::IsNullOrWhiteSpace($snapshotDirectory)) {
    New-Item -ItemType Directory -Path $snapshotDirectory -Force | Out-Null
}

$snapshot | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $SnapshotPath -Encoding UTF8

$applySql = @"
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @now datetime2(7) = SYSUTCDATETIME();
DECLARE @userId nvarchar(450) = N'$userIdSql';
DECLARE @botId uniqueidentifier = '$botIdText';
DECLARE @strategyId uniqueidentifier = '$strategyIdText';
DECLARE @approval nvarchar(256) = N'$approvalSql';

BEGIN TRANSACTION;

IF NOT EXISTS (SELECT 1 FROM AspNetUsers WHERE Id = @userId)
    THROW 51000, 'Target user was not found.', 1;

IF NOT EXISTS (SELECT 1 FROM TradingBots WHERE Id = @botId AND OwnerUserId = @userId AND IsDeleted = 0)
    THROW 51001, 'Target bot was not found for the target user.', 1;

IF NOT EXISTS (SELECT 1 FROM TradingStrategies WHERE Id = @strategyId AND OwnerUserId = @userId AND IsDeleted = 0)
    THROW 51002, 'Target strategy was not found for the target user.', 1;

IF NOT EXISTS (
    SELECT 1
    FROM TradingStrategies s
    JOIN TradingStrategyVersions v ON v.Id = s.ActiveTradingStrategyVersionId
    WHERE s.Id = @strategyId
      AND s.IsDeleted = 0
      AND v.IsDeleted = 0
      AND v.Status = 'Published')
    THROW 51003, 'Active strategy version is not Published.', 1;

IF NOT EXISTS (
    SELECT 1
    FROM TradingBots b
    JOIN ExchangeAccounts a ON a.Id = b.ExchangeAccountId AND a.IsDeleted = 0
    OUTER APPLY (
        SELECT TOP (1)
            IsKeyValid,
            CanTrade,
            SupportsFutures
        FROM ApiCredentialValidations
        WHERE ExchangeAccountId = a.Id
          AND OwnerUserId = a.OwnerUserId
          AND IsDeleted = 0
        ORDER BY ValidatedAtUtc DESC, CreatedDate DESC
    ) v
    WHERE b.Id = @botId
      AND b.OwnerUserId = @userId
      AND b.IsDeleted = 0
      AND a.ExchangeName = 'Binance'
      AND a.IsReadOnly = 0
      AND a.CredentialStatus = 'Active'
      AND ISNULL(v.IsKeyValid, 0) = 1
      AND ISNULL(v.CanTrade, 0) = 1
      AND ISNULL(v.SupportsFutures, 0) = 1)
    THROW 51004, 'Target account is not writable Binance futures-ready.', 1;

IF NOT EXISTS (SELECT 1 FROM GlobalExecutionSwitches WHERE IsDeleted = 0 AND TradeMasterState = 'Armed')
    THROW 51005, 'Trade master is not armed.', 1;

UPDATE GlobalExecutionSwitches
SET DemoModeEnabled = 0,
    LiveModeApprovedAtUtc = @now,
    LiveModeApprovalReference = @approval,
    UpdatedDate = @now
WHERE IsDeleted = 0;

UPDATE AspNetUsers
SET TradingModeOverride = 'Live',
    TradingModeApprovedAtUtc = @now,
    TradingModeApprovalReference = @approval
WHERE Id = @userId;

UPDATE TradingBots
SET TradingModeOverride = 'Live',
    TradingModeApprovedAtUtc = @now,
    TradingModeApprovalReference = @approval,
    UpdatedDate = @now
WHERE Id = @botId
  AND OwnerUserId = @userId
  AND IsDeleted = 0;

UPDATE TradingStrategies
SET PromotionState = 'LivePublished',
    PublishedMode = 'Live',
    PublishedAtUtc = COALESCE(PublishedAtUtc, @now),
    LivePromotionApprovedAtUtc = @now,
    LivePromotionApprovalReference = @approval,
    UpdatedDate = @now
WHERE Id = @strategyId
  AND OwnerUserId = @userId
  AND IsDeleted = 0;

COMMIT TRANSACTION;

SELECT
    'Enabled' AS Result,
    @now AS AppliedAtUtc,
    @botId AS BotId,
    @strategyId AS StrategyId,
    @userId AS UserId
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
"@

if ($PSCmdlet.ShouldProcess("$Server/$Database target runtime", "Enable reversible testnet-only runtime state")) {
    $result = Invoke-SqlText $applySql
    Write-Output ($result | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}
else {
    Write-Output "WhatIf: snapshot written only. No database rows changed."
}

Write-Output "SnapshotPath=$SnapshotPath"
