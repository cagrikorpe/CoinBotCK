[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Server = "localhost",
    [string]$Database = "CoinBotDb",
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

function Invoke-SqlText {
    param([Parameter(Mandatory = $true)][string]$Query)

    $tempFile = Join-Path $env:TEMP ("phase10d-rollback-" + [Guid]::NewGuid().ToString("N") + ".sql")
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

function ConvertTo-SqlStringLiteral {
    param($Value)
    if ($null -eq $Value) { return "NULL" }
    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) { return "NULL" }
    return "N'" + $text.Replace("'", "''") + "'"
}

function ConvertTo-SqlDateTimeLiteral {
    param($Value)
    if ($null -eq $Value) { return "NULL" }
    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) { return "NULL" }
    return "'" + ([DateTime]::Parse($text, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal).ToString("yyyy-MM-ddTHH:mm:ss.fffffff")) + "'"
}

function ConvertTo-SqlBitLiteral {
    param($Value)
    if ($null -eq $Value) { return "NULL" }
    return $(if ([bool]$Value) { "1" } else { "0" })
}

Assert-SqlCmd

if (-not (Test-Path -LiteralPath $SnapshotPath)) {
    throw "Snapshot file was not found: $SnapshotPath"
}

$snapshot = Get-Content -LiteralPath $SnapshotPath -Raw | ConvertFrom-Json -ErrorAction Stop

if ([string]::IsNullOrWhiteSpace($snapshot.UserId) -or
    [string]::IsNullOrWhiteSpace($snapshot.BotId) -or
    [string]::IsNullOrWhiteSpace($snapshot.StrategyId)) {
    throw "Snapshot is missing target identifiers."
}

$global = $snapshot.GlobalExecutionSwitch
$user = $snapshot.User
$bot = $snapshot.Bot
$strategy = $snapshot.Strategy

if ($null -eq $global -or $null -eq $user -or $null -eq $bot -or $null -eq $strategy) {
    throw "Snapshot does not contain all required state blocks."
}

$sql = @"
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @userId nvarchar(450) = N'$(([string]$snapshot.UserId).Replace("'", "''"))';
DECLARE @botId uniqueidentifier = '$($snapshot.BotId)';
DECLARE @strategyId uniqueidentifier = '$($snapshot.StrategyId)';
DECLARE @switchId uniqueidentifier = '$($global.Id)';
DECLARE @now datetime2(7) = SYSUTCDATETIME();

BEGIN TRANSACTION;

IF NOT EXISTS (SELECT 1 FROM GlobalExecutionSwitches WHERE Id = @switchId)
    THROW 51100, 'Snapshot global execution switch was not found.', 1;

IF NOT EXISTS (SELECT 1 FROM AspNetUsers WHERE Id = @userId)
    THROW 51101, 'Snapshot user was not found.', 1;

IF NOT EXISTS (SELECT 1 FROM TradingBots WHERE Id = @botId)
    THROW 51102, 'Snapshot bot was not found.', 1;

IF NOT EXISTS (SELECT 1 FROM TradingStrategies WHERE Id = @strategyId)
    THROW 51103, 'Snapshot strategy was not found.', 1;

UPDATE GlobalExecutionSwitches
SET TradeMasterState = $(ConvertTo-SqlStringLiteral $global.TradeMasterState),
    DemoModeEnabled = $(ConvertTo-SqlBitLiteral $global.DemoModeEnabled),
    LiveModeApprovedAtUtc = $(ConvertTo-SqlDateTimeLiteral $global.LiveModeApprovedAtUtc),
    LiveModeApprovalReference = $(ConvertTo-SqlStringLiteral $global.LiveModeApprovalReference),
    UpdatedDate = @now
WHERE Id = @switchId;

UPDATE AspNetUsers
SET TradingModeOverride = $(ConvertTo-SqlStringLiteral $user.TradingModeOverride),
    TradingModeApprovedAtUtc = $(ConvertTo-SqlDateTimeLiteral $user.TradingModeApprovedAtUtc),
    TradingModeApprovalReference = $(ConvertTo-SqlStringLiteral $user.TradingModeApprovalReference)
WHERE Id = @userId;

UPDATE TradingBots
SET TradingModeOverride = $(ConvertTo-SqlStringLiteral $bot.TradingModeOverride),
    TradingModeApprovedAtUtc = $(ConvertTo-SqlDateTimeLiteral $bot.TradingModeApprovedAtUtc),
    TradingModeApprovalReference = $(ConvertTo-SqlStringLiteral $bot.TradingModeApprovalReference),
    UpdatedDate = @now
WHERE Id = @botId;

UPDATE TradingStrategies
SET PromotionState = $(ConvertTo-SqlStringLiteral $strategy.PromotionState),
    PublishedMode = $(ConvertTo-SqlStringLiteral $strategy.PublishedMode),
    PublishedAtUtc = $(ConvertTo-SqlDateTimeLiteral $strategy.PublishedAtUtc),
    LivePromotionApprovedAtUtc = $(ConvertTo-SqlDateTimeLiteral $strategy.LivePromotionApprovedAtUtc),
    LivePromotionApprovalReference = $(ConvertTo-SqlStringLiteral $strategy.LivePromotionApprovalReference),
    ActiveTradingStrategyVersionId = $(ConvertTo-SqlStringLiteral $strategy.ActiveTradingStrategyVersionId),
    UpdatedDate = @now
WHERE Id = @strategyId;

COMMIT TRANSACTION;

SELECT
    'RolledBack' AS Result,
    @now AS RolledBackAtUtc,
    @botId AS BotId,
    @strategyId AS StrategyId,
    @userId AS UserId
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
"@

if ($PSCmdlet.ShouldProcess("$Server/$Database target runtime", "Rollback phase10d runtime state from snapshot")) {
    $result = Invoke-SqlText $sql
    Write-Output ($result | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}
else {
    Write-Output "WhatIf: no database rows changed."
}

Write-Output "SnapshotPath=$SnapshotPath"
