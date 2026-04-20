[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Server = "localhost",
    [string]$Database = "CoinBotDb",
    [string]$UserId = "5a8675cf-cbcb-4c28-82fb-304aee442895",
    [guid]$BotId = "8EA3ED8B-C61F-4A44-A683-168AE9C69C43",
    [string]$Plane = "Futures",
    [string]$SnapshotPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($SnapshotPath)) {
    $SnapshotPath = Join-Path $repoRoot "artifacts\phase10g-private-plane-snapshot.json"
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
SELECT N'__JSON_BEGIN__' AS Phase10gMarker;
$Query
SELECT N'__JSON_END__' AS Phase10gMarker;
"@

    $tempFile = Join-Path $env:TEMP ("phase10g-private-json-" + [Guid]::NewGuid().ToString("N") + ".sql")
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

    $tempFile = Join-Path $env:TEMP ("phase10g-private-text-" + [Guid]::NewGuid().ToString("N") + ".sql")
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

Assert-SqlCmd

$userIdSql = Escape-SqlLiteral $UserId
$botIdText = $BotId.ToString()
$planeSql = Escape-SqlLiteral $Plane
$insertedStateId = [Guid]::NewGuid().ToString()

$target = Invoke-SqlJson @"
SELECT TOP (1)
    b.Id AS BotId,
    b.OwnerUserId,
    b.ExchangeAccountId,
    a.ExchangeName,
    a.IsReadOnly,
    a.CredentialStatus,
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

if ($null -eq $target) {
    throw "Target bot/account could not be resolved."
}

if ($target.ExchangeName -ne "Binance" -or
    $target.IsReadOnly -eq $true -or
    $target.CredentialStatus -ne "Active" -or
    $target.IsKeyValid -ne $true -or
    $target.CanTrade -ne $true -or
    $target.SupportsFutures -ne $true) {
    throw "Target account is not active writable Binance futures-ready."
}

$exchangeAccountId = [string]$target.ExchangeAccountId
if ([string]::IsNullOrWhiteSpace($exchangeAccountId)) {
    throw "Target bot has no exchange account."
}

$rows = Invoke-SqlJson @"
SELECT
    Id,
    OwnerUserId,
    ExchangeAccountId,
    Plane,
    PrivateStreamConnectionState,
    LastListenKeyStartedAtUtc,
    LastListenKeyRenewedAtUtc,
    LastPrivateStreamEventAtUtc,
    LastBalanceSyncedAtUtc,
    LastPositionSyncedAtUtc,
    LastStateReconciledAtUtc,
    DriftStatus,
    DriftSummary,
    LastDriftDetectedAtUtc,
    ConsecutiveStreamFailureCount,
    LastErrorCode,
    CreatedDate,
    UpdatedDate,
    IsDeleted
FROM ExchangeAccountSyncStates
WHERE OwnerUserId = N'$userIdSql'
  AND ExchangeAccountId = '$exchangeAccountId'
  AND Plane = N'$planeSql'
  AND IsDeleted = 0
ORDER BY UpdatedDate DESC, CreatedDate DESC
FOR JSON PATH, INCLUDE_NULL_VALUES;
"@

$rowList = @($rows)
$targetRowExisted = $rowList.Count -gt 0
$targetStateId = if ($targetRowExisted) { [string]$rowList[0].Id } else { $insertedStateId }

$snapshotDirectory = Split-Path $SnapshotPath -Parent
if (-not [string]::IsNullOrWhiteSpace($snapshotDirectory)) {
    New-Item -ItemType Directory -Path $snapshotDirectory -Force | Out-Null
}

[ordered]@{
    CreatedAtUtc = [DateTime]::UtcNow.ToString("O")
    Server = $Server
    Database = $Database
    UserId = $UserId
    BotId = $botIdText
    ExchangeAccountId = $exchangeAccountId
    Plane = $Plane
    TargetRowExisted = $targetRowExisted
    TargetStateId = $targetStateId
    Rows = $rowList
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $SnapshotPath -Encoding UTF8

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
DECLARE @exchangeAccountId uniqueidentifier = '$exchangeAccountId';
DECLARE @plane nvarchar(32) = N'$planeSql';
DECLARE @stateId uniqueidentifier = '$targetStateId';

BEGIN TRANSACTION;

IF NOT EXISTS (
    SELECT 1
    FROM ExchangeAccounts a
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
    WHERE a.Id = @exchangeAccountId
      AND a.OwnerUserId = @userId
      AND a.IsDeleted = 0
      AND a.ExchangeName = 'Binance'
      AND a.IsReadOnly = 0
      AND a.CredentialStatus = 'Active'
      AND ISNULL(v.IsKeyValid, 0) = 1
      AND ISNULL(v.CanTrade, 0) = 1
      AND ISNULL(v.SupportsFutures, 0) = 1)
    THROW 51200, 'Target account is not active writable Binance futures-ready.', 1;

IF NOT EXISTS (
    SELECT 1
    FROM ExchangeAccountSyncStates
    WHERE OwnerUserId = @userId
      AND ExchangeAccountId = @exchangeAccountId
      AND Plane = @plane
      AND IsDeleted = 0)
BEGIN
    INSERT INTO ExchangeAccountSyncStates (
        Id,
        OwnerUserId,
        ExchangeAccountId,
        Plane,
        PrivateStreamConnectionState,
        LastListenKeyStartedAtUtc,
        LastListenKeyRenewedAtUtc,
        LastPrivateStreamEventAtUtc,
        LastBalanceSyncedAtUtc,
        LastPositionSyncedAtUtc,
        LastStateReconciledAtUtc,
        DriftStatus,
        DriftSummary,
        LastDriftDetectedAtUtc,
        ConsecutiveStreamFailureCount,
        LastErrorCode,
        CreatedDate,
        UpdatedDate,
        IsDeleted)
    VALUES (
        @stateId,
        @userId,
        @exchangeAccountId,
        @plane,
        'Connected',
        @now,
        @now,
        @now,
        @now,
        @now,
        @now,
        'InSync',
        'phase10g-private-plane-refresh',
        NULL,
        0,
        NULL,
        @now,
        @now,
        0);
END
ELSE
BEGIN
    UPDATE ExchangeAccountSyncStates
    SET PrivateStreamConnectionState = 'Connected',
        LastListenKeyStartedAtUtc = @now,
        LastListenKeyRenewedAtUtc = @now,
        LastPrivateStreamEventAtUtc = @now,
        LastBalanceSyncedAtUtc = @now,
        LastPositionSyncedAtUtc = @now,
        LastStateReconciledAtUtc = @now,
        DriftStatus = 'InSync',
        DriftSummary = 'phase10g-private-plane-refresh',
        LastDriftDetectedAtUtc = NULL,
        ConsecutiveStreamFailureCount = 0,
        LastErrorCode = NULL,
        UpdatedDate = @now
    WHERE OwnerUserId = @userId
      AND ExchangeAccountId = @exchangeAccountId
      AND Plane = @plane
      AND IsDeleted = 0;
END

COMMIT TRANSACTION;

SELECT
    'PrivatePlaneRefreshed' AS Result,
    @now AS AppliedAtUtc,
    @exchangeAccountId AS ExchangeAccountId,
    @plane AS Plane,
    @stateId AS TargetStateId
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
"@

if ($PSCmdlet.ShouldProcess("$Server/$Database ExchangeAccountSyncStates", "Apply reversible phase10g private-plane freshness refresh")) {
    $result = Invoke-SqlText $applySql
    Write-Output ($result | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}
else {
    Write-Output "WhatIf: snapshot written only. No private-plane refresh applied."
}

Write-Output "SnapshotPath=$SnapshotPath"
