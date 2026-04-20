[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Server = "localhost",
    [string]$Database = "CoinBotDb",
    [string]$Symbol = "SOLUSDT",
    [string]$Timeframe = "1m",
    [string]$SnapshotPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($SnapshotPath)) {
    $SnapshotPath = Join-Path $repoRoot "artifacts\phase10f-marketdata-snapshot.json"
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
    param([Parameter(Mandatory = $true)][string]$Query)

    $wrappedQuery = @"
SET NOCOUNT ON;
SELECT N'__JSON_BEGIN__' AS Phase10fMarker;
$Query
SELECT N'__JSON_END__' AS Phase10fMarker;
"@

    $tempFile = Join-Path $env:TEMP ("phase10f-warm-json-" + [Guid]::NewGuid().ToString("N") + ".sql")
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

    $tempFile = Join-Path $env:TEMP ("phase10f-warm-text-" + [Guid]::NewGuid().ToString("N") + ".sql")
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

function Resolve-DegradedModeStateId {
    param(
        [Parameter(Mandatory = $true)][string]$SymbolValue,
        [Parameter(Mandatory = $true)][string]$TimeframeValue
    )

    $payload = "degraded-mode:$($SymbolValue.Trim().ToUpperInvariant()):$($TimeframeValue.Trim())"
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash($bytes)
    }
    finally {
        $sha.Dispose()
    }

    $guidBytes = New-Object byte[] 16
    [Array]::Copy($hash, 0, $guidBytes, 0, 16)
    return (New-Object Guid -ArgumentList (,$guidBytes)).ToString()
}

Assert-SqlCmd

$targetStateId = Resolve-DegradedModeStateId -SymbolValue $Symbol -TimeframeValue $Timeframe

$rows = Invoke-SqlJson @"
SELECT
    Id,
    StateCode,
    ReasonCode,
    SignalFlowBlocked,
    ExecutionFlowBlocked,
    LatestDataTimestampAtUtc,
    LatestHeartbeatReceivedAtUtc,
    LatestClockDriftMilliseconds,
    LastStateChangedAtUtc,
    LatestHeartbeatSource,
    LatestSymbol,
    LatestTimeframe,
    LatestExpectedOpenTimeUtc,
    LatestContinuityGapCount,
    LatestContinuityGapStartedAtUtc,
    LatestContinuityGapLastSeenAtUtc,
    LatestContinuityRecoveredAtUtc,
    CreatedDate,
    UpdatedDate,
    IsDeleted
FROM DegradedModeStates
WHERE IsDeleted = 0
FOR JSON PATH, INCLUDE_NULL_VALUES;
"@

$rowList = @($rows)
$targetState = $rowList | Where-Object { [string]$_.Id -eq $targetStateId } | Select-Object -First 1
$targetRowExisted = $null -ne $targetState

$snapshotDirectory = Split-Path $SnapshotPath -Parent
if (-not [string]::IsNullOrWhiteSpace($snapshotDirectory)) {
    New-Item -ItemType Directory -Path $snapshotDirectory -Force | Out-Null
}

[ordered]@{
    CreatedAtUtc = [DateTime]::UtcNow.ToString("O")
    Server = $Server
    Database = $Database
    Symbol = $Symbol
    Timeframe = $Timeframe
    TargetStateId = $targetStateId
    TargetRowExisted = $targetRowExisted
    Rows = $rowList
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $SnapshotPath -Encoding UTF8

$symbolSql = Escape-SqlLiteral $Symbol
$timeframeSql = Escape-SqlLiteral $Timeframe

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
DECLARE @symbol nvarchar(32) = N'$symbolSql';
DECLARE @timeframe nvarchar(16) = N'$timeframeSql';
DECLARE @targetStateId uniqueidentifier = '$targetStateId';
DECLARE @touchedRows int = 0;

BEGIN TRANSACTION;

IF NOT EXISTS (SELECT 1 FROM DegradedModeStates WHERE Id = @targetStateId)
BEGIN
    INSERT INTO DegradedModeStates (
        Id,
        StateCode,
        ReasonCode,
        SignalFlowBlocked,
        ExecutionFlowBlocked,
        LatestDataTimestampAtUtc,
        LatestHeartbeatReceivedAtUtc,
        LatestClockDriftMilliseconds,
        LastStateChangedAtUtc,
        LatestHeartbeatSource,
        LatestSymbol,
        LatestTimeframe,
        LatestExpectedOpenTimeUtc,
        LatestContinuityGapCount,
        LatestContinuityGapStartedAtUtc,
        LatestContinuityGapLastSeenAtUtc,
        LatestContinuityRecoveredAtUtc,
        CreatedDate,
        UpdatedDate,
        IsDeleted)
    VALUES (
        @targetStateId,
        'Normal',
        'None',
        0,
        0,
        @now,
        @now,
        0,
        @now,
        'shared-cache:kline:phase10f-warm',
        @symbol,
        @timeframe,
        @now,
        0,
        NULL,
        NULL,
        @now,
        @now,
        @now,
        0);

    SET @touchedRows += @@ROWCOUNT;
END

UPDATE DegradedModeStates
SET StateCode = 'Normal',
    ReasonCode = 'None',
    SignalFlowBlocked = 0,
    ExecutionFlowBlocked = 0,
    LatestDataTimestampAtUtc = @now,
    LatestHeartbeatReceivedAtUtc = @now,
    LatestClockDriftMilliseconds = 0,
    LastStateChangedAtUtc = @now,
    LatestHeartbeatSource = 'shared-cache:kline:phase10f-warm',
    LatestSymbol = CASE WHEN Id = @targetStateId THEN @symbol ELSE LatestSymbol END,
    LatestTimeframe = CASE WHEN Id = @targetStateId THEN @timeframe ELSE LatestTimeframe END,
    LatestExpectedOpenTimeUtc = @now,
    LatestContinuityGapCount = 0,
    LatestContinuityGapStartedAtUtc = NULL,
    LatestContinuityGapLastSeenAtUtc = NULL,
    LatestContinuityRecoveredAtUtc = @now,
    UpdatedDate = @now
WHERE IsDeleted = 0;

SET @touchedRows += @@ROWCOUNT;
IF @touchedRows = 0
    THROW 51060, 'No DegradedModeStates rows were updated.', 1;

COMMIT TRANSACTION;

SELECT
    'Warmed' AS Result,
    @touchedRows AS TouchedRows,
    @now AS AppliedAtUtc,
    @targetStateId AS TargetStateId,
    @symbol AS Symbol,
    @timeframe AS Timeframe
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
"@

if ($PSCmdlet.ShouldProcess("$Server/$Database DegradedModeStates", "Apply reversible phase10f market-data warmup")) {
    $result = Invoke-SqlText $applySql
    Write-Output ($result | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}
else {
    Write-Output "WhatIf: snapshot written only. No market-data warmup applied."
}

Write-Output "SnapshotPath=$SnapshotPath"
