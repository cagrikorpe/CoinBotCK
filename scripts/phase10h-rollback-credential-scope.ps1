[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Server = "localhost",
    [string]$Database = "CoinBotDb",
    [string]$SnapshotPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($SnapshotPath)) {
    $SnapshotPath = Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts\phase10h-credential-scope-snapshot.json"
}

function Assert-SqlCmd {
    if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
        throw "sqlcmd was not found."
    }
}

function Invoke-SqlText {
    param([Parameter(Mandatory = $true)][string]$Query)
    $tempFile = Join-Path $env:TEMP ("phase10h-scope-rollback-" + [Guid]::NewGuid().ToString("N") + ".sql")
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

if (-not (Test-Path -LiteralPath $SnapshotPath)) {
    throw "Snapshot file was not found: $SnapshotPath"
}

$snapshot = Get-Content -LiteralPath $SnapshotPath -Raw | ConvertFrom-Json -ErrorAction Stop
if ([string]::IsNullOrWhiteSpace([string]$snapshot.InsertedValidationId)) {
    throw "Snapshot is missing inserted validation id."
}

$insertedValidationId = [string]$snapshot.InsertedValidationId
$sql = @"
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @insertedValidationId uniqueidentifier = '$insertedValidationId';
DECLARE @now datetime2(7) = SYSUTCDATETIME();

BEGIN TRANSACTION;

UPDATE ApiCredentialValidations
SET IsDeleted = 1,
    UpdatedDate = @now
WHERE Id = @insertedValidationId
  AND CorrelationId = N'phase10h-credential-scope';

COMMIT TRANSACTION;

SELECT
    'CredentialScopeRolledBack' AS Result,
    @insertedValidationId AS InsertedValidationId,
    @now AS RolledBackAtUtc
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
"@

if ($PSCmdlet.ShouldProcess("$Server/$Database target credential validation", "Rollback phase10h Testnet credential scope row")) {
    $result = Invoke-SqlText $sql
    Write-Output ($result | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}
else {
    Write-Output "WhatIf: no database rows changed."
}

Write-Output "SnapshotPath=$SnapshotPath"
