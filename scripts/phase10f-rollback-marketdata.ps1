[CmdletBinding(SupportsShouldProcess = $true)]
param(
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

function Escape-SqlLiteral {
    param([Parameter(Mandatory = $true)][string]$Value)
    return $Value.Replace("'", "''")
}

function Get-JsonProperty {
    param(
        [Parameter(Mandatory = $true)][object]$Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($Object.PSObject.Properties.Name -contains $Name) {
        return $Object.$Name
    }

    return $null
}

function Format-SqlString {
    param([object]$Value)

    if ($null -eq $Value) {
        return "NULL"
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return "NULL"
    }

    return "N'" + (Escape-SqlLiteral $text) + "'"
}

function Format-SqlDateTime {
    param([object]$Value)

    if ($null -eq $Value) {
        return "NULL"
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return "NULL"
    }

    $parsed = [DateTime]::Parse(
        $text,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal)

    return "'" + $parsed.ToString("yyyy-MM-dd HH:mm:ss.fffffff", [System.Globalization.CultureInfo]::InvariantCulture) + "'"
}

function Format-SqlBit {
    param([object]$Value)

    if ($null -eq $Value) {
        return "NULL"
    }

    if ($Value -is [bool]) {
        return $(if ($Value) { "1" } else { "0" })
    }

    $text = [string]$Value
    if ($text -eq "1" -or $text.Equals("true", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "1"
    }

    return "0"
}

function Format-SqlInt {
    param([object]$Value)

    if ($null -eq $Value) {
        return "NULL"
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return "NULL"
    }

    return ([int]$Value).ToString([System.Globalization.CultureInfo]::InvariantCulture)
}

function Invoke-SqlText {
    param(
        [Parameter(Mandatory = $true)][string]$Server,
        [Parameter(Mandatory = $true)][string]$Database,
        [Parameter(Mandatory = $true)][string]$Query
    )

    $tempFile = Join-Path $env:TEMP ("phase10f-rollback-marketdata-" + [Guid]::NewGuid().ToString("N") + ".sql")
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

if (-not (Test-Path -LiteralPath $SnapshotPath)) {
    throw "Snapshot was not found: $SnapshotPath"
}

$snapshot = Get-Content -LiteralPath $SnapshotPath -Raw | ConvertFrom-Json -ErrorAction Stop
if ($null -eq $snapshot -or $null -eq $snapshot.Server -or $null -eq $snapshot.Database -or $null -eq $snapshot.Rows) {
    throw "Snapshot is invalid."
}

$rows = @($snapshot.Rows)
$targetStateId = $null
if ($snapshot.PSObject.Properties.Name -contains "TargetStateId") {
    $targetStateId = [string]$snapshot.TargetStateId
}

$targetRowExisted = $true
if ($snapshot.PSObject.Properties.Name -contains "TargetRowExisted") {
    $targetRowExisted = [bool]$snapshot.TargetRowExisted
}

$updates = New-Object System.Text.StringBuilder
foreach ($row in $rows) {
    $id = [string](Get-JsonProperty -Object $row -Name "Id")
    if ([string]::IsNullOrWhiteSpace($id)) {
        throw "Snapshot row is missing Id."
    }

    [void]$updates.AppendLine("UPDATE DegradedModeStates")
    [void]$updates.AppendLine("SET StateCode = $(Format-SqlString (Get-JsonProperty -Object $row -Name "StateCode")),")
    [void]$updates.AppendLine("    ReasonCode = $(Format-SqlString (Get-JsonProperty -Object $row -Name "ReasonCode")),")
    [void]$updates.AppendLine("    SignalFlowBlocked = $(Format-SqlBit (Get-JsonProperty -Object $row -Name "SignalFlowBlocked")),")
    [void]$updates.AppendLine("    ExecutionFlowBlocked = $(Format-SqlBit (Get-JsonProperty -Object $row -Name "ExecutionFlowBlocked")),")
    [void]$updates.AppendLine("    LatestDataTimestampAtUtc = $(Format-SqlDateTime (Get-JsonProperty -Object $row -Name "LatestDataTimestampAtUtc")),")
    [void]$updates.AppendLine("    LatestHeartbeatReceivedAtUtc = $(Format-SqlDateTime (Get-JsonProperty -Object $row -Name "LatestHeartbeatReceivedAtUtc")),")
    [void]$updates.AppendLine("    LatestClockDriftMilliseconds = $(Format-SqlInt (Get-JsonProperty -Object $row -Name "LatestClockDriftMilliseconds")),")
    [void]$updates.AppendLine("    LastStateChangedAtUtc = $(Format-SqlDateTime (Get-JsonProperty -Object $row -Name "LastStateChangedAtUtc")),")
    [void]$updates.AppendLine("    LatestHeartbeatSource = $(Format-SqlString (Get-JsonProperty -Object $row -Name "LatestHeartbeatSource")),")
    [void]$updates.AppendLine("    LatestSymbol = $(Format-SqlString (Get-JsonProperty -Object $row -Name "LatestSymbol")),")
    [void]$updates.AppendLine("    LatestTimeframe = $(Format-SqlString (Get-JsonProperty -Object $row -Name "LatestTimeframe")),")
    [void]$updates.AppendLine("    LatestExpectedOpenTimeUtc = $(Format-SqlDateTime (Get-JsonProperty -Object $row -Name "LatestExpectedOpenTimeUtc")),")
    [void]$updates.AppendLine("    LatestContinuityGapCount = $(Format-SqlInt (Get-JsonProperty -Object $row -Name "LatestContinuityGapCount")),")
    [void]$updates.AppendLine("    LatestContinuityGapStartedAtUtc = $(Format-SqlDateTime (Get-JsonProperty -Object $row -Name "LatestContinuityGapStartedAtUtc")),")
    [void]$updates.AppendLine("    LatestContinuityGapLastSeenAtUtc = $(Format-SqlDateTime (Get-JsonProperty -Object $row -Name "LatestContinuityGapLastSeenAtUtc")),")
    [void]$updates.AppendLine("    LatestContinuityRecoveredAtUtc = $(Format-SqlDateTime (Get-JsonProperty -Object $row -Name "LatestContinuityRecoveredAtUtc")),")
    [void]$updates.AppendLine("    UpdatedDate = $(Format-SqlDateTime (Get-JsonProperty -Object $row -Name "UpdatedDate")),")
    [void]$updates.AppendLine("    IsDeleted = $(Format-SqlBit (Get-JsonProperty -Object $row -Name "IsDeleted"))")
    [void]$updates.AppendLine("WHERE Id = '$id';")
    [void]$updates.AppendLine("IF @@ROWCOUNT <> 1 THROW 51070, 'A DegradedModeStates snapshot row could not be restored.', 1;")
}

if (-not $targetRowExisted -and -not [string]::IsNullOrWhiteSpace($targetStateId)) {
    [void]$updates.AppendLine("DELETE FROM DegradedModeStates WHERE Id = '$targetStateId';")
}

$restoreSql = @"
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION;
$($updates.ToString())
COMMIT TRANSACTION;

SELECT 'RolledBack' AS Result, $($rows.Count) AS RestoredRows FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
"@

if ($PSCmdlet.ShouldProcess("$($snapshot.Server)/$($snapshot.Database) DegradedModeStates", "Rollback phase10f market-data warmup")) {
    $result = Invoke-SqlText -Server ([string]$snapshot.Server) -Database ([string]$snapshot.Database) -Query $restoreSql
    Write-Output ($result | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}
else {
    Write-Output "WhatIf: no rows restored."
}
