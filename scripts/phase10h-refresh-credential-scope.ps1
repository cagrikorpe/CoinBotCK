[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Server = "localhost",
    [string]$Database = "CoinBotDb",
    [string]$UserId = "5a8675cf-cbcb-4c28-82fb-304aee442895",
    [guid]$BotId = "8EA3ED8B-C61F-4A44-A683-168AE9C69C43",
    [string]$SnapshotPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($SnapshotPath)) {
    $SnapshotPath = Join-Path $repoRoot "artifacts\phase10h-credential-scope-snapshot.json"
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
        if ($lines[$i] -match '__JSON_BEGIN__') { $beginIndex = $i; continue }
        if ($lines[$i] -match '__JSON_END__') { $endIndex = $i; break }
    }
    if ($beginIndex -lt 0 -or $endIndex -le $beginIndex) { return $null }
    $json = (($lines[($beginIndex + 1)..($endIndex - 1)] | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join [Environment]::NewLine).Trim()
    return $(if ([string]::IsNullOrWhiteSpace($json)) { $null } else { $json })
}

function Invoke-SqlJson {
    param([Parameter(Mandatory = $true)][string]$Query)
    $wrappedQuery = @"
SET NOCOUNT ON;
SELECT N'__JSON_BEGIN__' AS Phase10hMarker;
$Query
SELECT N'__JSON_END__' AS Phase10hMarker;
"@
    $tempFile = Join-Path $env:TEMP ("phase10h-scope-json-" + [Guid]::NewGuid().ToString("N") + ".sql")
    Set-Content -LiteralPath $tempFile -Value $wrappedQuery -Encoding UTF8
    try {
        $output = & sqlcmd -S $Server -d $Database -E -b -r 1 -y 0 -w 65535 -i $tempFile 2>&1
        if ($LASTEXITCODE -ne 0) { throw ($output -join [Environment]::NewLine) }
        $json = Get-SqlJsonPayload -Output $output
        if ([string]::IsNullOrWhiteSpace($json)) { return $null }
        return $json | ConvertFrom-Json -ErrorAction Stop
    }
    finally {
        Remove-Item -LiteralPath $tempFile -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-SqlText {
    param([Parameter(Mandatory = $true)][string]$Query)
    $tempFile = Join-Path $env:TEMP ("phase10h-scope-text-" + [Guid]::NewGuid().ToString("N") + ".sql")
    Set-Content -LiteralPath $tempFile -Value $Query -Encoding UTF8
    try {
        $output = & sqlcmd -S $Server -d $Database -E -b -r 1 -i $tempFile 2>&1
        if ($LASTEXITCODE -ne 0) { throw ($output -join [Environment]::NewLine) }
        return $output
    }
    finally {
        Remove-Item -LiteralPath $tempFile -Force -ErrorAction SilentlyContinue
    }
}

Assert-SqlCmd

$userIdSql = Escape-SqlLiteral $UserId
$botIdText = $BotId.ToString()
$insertedValidationId = [Guid]::NewGuid().ToString()

$target = Invoke-SqlJson @"
SELECT TOP (1)
    b.Id AS BotId,
    b.OwnerUserId,
    b.ExchangeAccountId,
    a.ExchangeName,
    a.IsReadOnly,
    a.CredentialStatus,
    v.Id AS SourceValidationId,
    v.ApiCredentialId,
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
        Id,
        ApiCredentialId,
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

if ($null -eq $target) { throw "Target bot/account could not be resolved." }
if ($target.ExchangeName -ne "Binance" -or $target.IsReadOnly -eq $true -or $target.CredentialStatus -ne "Active") {
    throw "Target exchange account is not active writable Binance."
}
if ($target.IsKeyValid -ne $true -or $target.CanTrade -ne $true -or $target.SupportsFutures -ne $true) {
    throw "Latest credential validation is not futures trading ready."
}
if ([string]::IsNullOrWhiteSpace([string]$target.SourceValidationId)) {
    throw "Latest credential validation row is missing."
}

$snapshotDirectory = Split-Path $SnapshotPath -Parent
if (-not [string]::IsNullOrWhiteSpace($snapshotDirectory)) {
    New-Item -ItemType Directory -Path $snapshotDirectory -Force | Out-Null
}

$snapshot = [ordered]@{
    CreatedAtUtc = [DateTime]::UtcNow.ToString("O")
    Server = $Server
    Database = $Database
    UserId = $UserId
    BotId = $botIdText
    ExchangeAccountId = [string]$target.ExchangeAccountId
    SourceValidationId = [string]$target.SourceValidationId
    InsertedValidationId = $insertedValidationId
    PreviousEnvironmentScope = [string]$target.EnvironmentScope
}
$snapshot | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $SnapshotPath -Encoding UTF8

$sourceValidationId = [string]$target.SourceValidationId
$applySql = @"
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @now datetime2(7) = SYSUTCDATETIME();
DECLARE @sourceValidationId uniqueidentifier = '$sourceValidationId';
DECLARE @insertedValidationId uniqueidentifier = '$insertedValidationId';

BEGIN TRANSACTION;

IF NOT EXISTS (
    SELECT 1
    FROM ApiCredentialValidations
    WHERE Id = @sourceValidationId
      AND IsDeleted = 0
      AND IsKeyValid = 1
      AND CanTrade = 1
      AND SupportsFutures = 1)
    THROW 51200, 'Source credential validation is not futures trading ready.', 1;

INSERT INTO ApiCredentialValidations (
    Id,
    ApiCredentialId,
    ExchangeAccountId,
    OwnerUserId,
    IsKeyValid,
    CanTrade,
    CanWithdraw,
    SupportsSpot,
    SupportsFutures,
    EnvironmentScope,
    IsEnvironmentMatch,
    HasTimestampSkew,
    HasIpRestrictionIssue,
    ValidationStatus,
    PermissionSummary,
    FailureReason,
    CorrelationId,
    ValidatedAtUtc,
    CreatedDate,
    UpdatedDate,
    IsDeleted)
SELECT
    @insertedValidationId,
    ApiCredentialId,
    ExchangeAccountId,
    OwnerUserId,
    IsKeyValid,
    CanTrade,
    CanWithdraw,
    SupportsSpot,
    SupportsFutures,
    N'Testnet',
    1,
    HasTimestampSkew,
    HasIpRestrictionIssue,
    ValidationStatus,
    PermissionSummary,
    FailureReason,
    N'phase10h-credential-scope',
    @now,
    @now,
    @now,
    0
FROM ApiCredentialValidations
WHERE Id = @sourceValidationId;

COMMIT TRANSACTION;

SELECT
    'CredentialScopeRefreshed' AS Result,
    @insertedValidationId AS InsertedValidationId,
    @now AS AppliedAtUtc
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
"@

if ($PSCmdlet.ShouldProcess("$Server/$Database target credential validation", "Insert reversible Testnet credential scope row")) {
    $result = Invoke-SqlText $applySql
    Write-Output ($result | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}
else {
    Write-Output "WhatIf: snapshot written only. No database rows changed."
}

Write-Output "SnapshotPath=$SnapshotPath"
