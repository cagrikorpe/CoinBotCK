$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$diagRoot = Join-Path $repoRoot '.diag\faz-c-test-rerun'
$summaryPath = Join-Path $diagRoot 'summary.json'

New-Item -ItemType Directory -Force -Path $diagRoot | Out-Null

function Write-StepLog {
    param(
        [AllowNull()][AllowEmptyString()][object]$Message,
        [Parameter(Mandatory = $true)][string]$LogPath,
        [switch]$Append)

    $teeParams = @{ FilePath = $LogPath }
    if ($Append) {
        $teeParams.Append = $true
    }

    $text = if ($null -eq $Message) { '' } else { [string]$Message }
    $text | Tee-Object @teeParams | Out-Host
}

function Invoke-Step {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [Parameter(Mandatory = $true)][string]$LogPath,
        [switch]$SkipWhenBuildFailed,
        [bool]$BuildSucceeded = $true)

    $commandText = ($ArgumentList | ForEach-Object {
        if ($_ -match '\s') { '"{0}"' -f $_ } else { $_ }
    }) -join ' '

    if ($SkipWhenBuildFailed -and -not $BuildSucceeded) {
        $skipResult = [ordered]@{
            Name = $Name
            Command = "$FilePath $commandText"
            Succeeded = $false
            Skipped = $true
            ExitCode = $null
            StartedAtUtc = [DateTime]::UtcNow
            CompletedAtUtc = [DateTime]::UtcNow
            DurationSeconds = 0
            LogPath = $LogPath
            Summary = 'Build failed earlier; no-build rerun step was skipped.'
        }

        Write-StepLog -Message "[$Name] SKIP (build failed)" -LogPath $LogPath
        return [pscustomobject]$skipResult
    }

    $startedAtUtc = [DateTime]::UtcNow
    Write-StepLog -Message "[$Name] START" -LogPath $LogPath
    Write-StepLog -Message ("COMMAND: {0} {1}" -f $FilePath, $commandText) -LogPath $LogPath -Append

    $previousNativePreference = $null
    if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
        $previousNativePreference = $PSNativeCommandUseErrorActionPreference
        $PSNativeCommandUseErrorActionPreference = $false
    }

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'

    try {
        & $FilePath @ArgumentList *>&1 | ForEach-Object {
            Write-StepLog -Message ([string]$_) -LogPath $LogPath -Append
        }
        $exitCode = if ($null -ne $LASTEXITCODE) { [int]$LASTEXITCODE } else { 0 }
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
        if ($null -ne $previousNativePreference) {
            $PSNativeCommandUseErrorActionPreference = $previousNativePreference
        }
    }

    $completedAtUtc = [DateTime]::UtcNow
    $durationSeconds = [Math]::Round(($completedAtUtc - $startedAtUtc).TotalSeconds, 3)
    $succeeded = ($exitCode -eq 0)
    $summary = if ($succeeded) { 'Succeeded.' } else { "Failed with exit code $exitCode." }

    Write-StepLog -Message ("[{0}] END {1:O}" -f $Name, $completedAtUtc) -LogPath $LogPath -Append
    Write-StepLog -Message ("EXIT_CODE: {0}" -f $exitCode) -LogPath $LogPath -Append
    Write-StepLog -Message ("SUMMARY: {0}" -f $summary) -LogPath $LogPath -Append

    if ($succeeded) {
        Write-StepLog -Message "[$Name] PASS" -LogPath $LogPath -Append
    }
    else {
        Write-StepLog -Message "[$Name] FAIL (exit=$exitCode)" -LogPath $LogPath -Append
    }

    return [pscustomobject][ordered]@{
        Name = $Name
        Command = "$FilePath $commandText"
        Succeeded = $succeeded
        Skipped = $false
        ExitCode = $exitCode
        StartedAtUtc = $startedAtUtc
        CompletedAtUtc = $completedAtUtc
        DurationSeconds = $durationSeconds
        LogPath = $LogPath
        Summary = $summary
    }
}

Push-Location $repoRoot
try {
    $startedAtUtc = [DateTime]::UtcNow
    $dotnet = 'dotnet'

    $steps = New-Object System.Collections.Generic.List[object]

    $buildStep = Invoke-Step -Name 'build' -FilePath $dotnet -ArgumentList @(
        'build',
        'CoinBot.sln',
        '-c', 'Debug',
        '-m:1',
        '/nodeReuse:false',
        '-p:UseSharedCompilation=false'
    ) -LogPath (Join-Path $diagRoot '01-build.log')
    $steps.Add($buildStep) | Out-Null

    $buildSucceeded = [bool]$buildStep.Succeeded

    $steps.Add((Invoke-Step -Name 'MarketScannerServiceTests' -FilePath $dotnet -ArgumentList @(
        'test',
        'tests/CoinBot.UnitTests/CoinBot.UnitTests.csproj',
        '--no-build',
        '--no-restore',
        '-v:minimal',
        '--filter', 'FullyQualifiedName~MarketScannerServiceTests'
    ) -LogPath (Join-Path $diagRoot '02-market-scanner-service-tests.log') -SkipWhenBuildFailed -BuildSucceeded:$buildSucceeded)) | Out-Null

    $steps.Add((Invoke-Step -Name 'MarketScannerIntegrationTests' -FilePath $dotnet -ArgumentList @(
        'test',
        'tests/CoinBot.IntegrationTests/CoinBot.IntegrationTests.csproj',
        '--no-build',
        '--no-restore',
        '-v:minimal',
        '--filter', 'FullyQualifiedName~MarketScannerIntegrationTests'
    ) -LogPath (Join-Path $diagRoot '03-market-scanner-integration-tests.log') -SkipWhenBuildFailed -BuildSucceeded:$buildSucceeded)) | Out-Null

    $steps.Add((Invoke-Step -Name 'AdminOperationsCenterComposerTests' -FilePath $dotnet -ArgumentList @(
        'test',
        'tests/CoinBot.UnitTests/CoinBot.UnitTests.csproj',
        '--no-build',
        '--no-restore',
        '-v:minimal',
        '--filter', 'FullyQualifiedName~AdminOperationsCenterComposerTests'
    ) -LogPath (Join-Path $diagRoot '04-admin-operations-center-composer-tests.log') -SkipWhenBuildFailed -BuildSucceeded:$buildSucceeded)) | Out-Null

    $steps.Add((Invoke-Step -Name 'AdminOperationsCenterIntegrationTests' -FilePath $dotnet -ArgumentList @(
        'test',
        'tests/CoinBot.IntegrationTests/CoinBot.IntegrationTests.csproj',
        '--no-build',
        '--no-restore',
        '-v:minimal',
        '--filter', 'FullyQualifiedName~AdminOperationsCenterIntegrationTests'
    ) -LogPath (Join-Path $diagRoot '05-admin-operations-center-integration-tests.log') -SkipWhenBuildFailed -BuildSucceeded:$buildSucceeded)) | Out-Null

    $failedStep = $steps | Where-Object { -not $_.Succeeded -and -not $_.Skipped } | Select-Object -First 1
    $skippedCount = @($steps | Where-Object { $_.Skipped }).Count
    $completedAtUtc = [DateTime]::UtcNow
    $succeeded = -not $failedStep -and ($skippedCount -eq 0)

    $summary = if ($succeeded) {
        'Faz-C rerun tamamlandi. Build ve hedef test zinciri temiz gecti.'
    }
    elseif (-not $buildSucceeded) {
        'Build asamasinda kirik var. no-build test rerun adimlari bu nedenle skip edildi.'
    }
    else {
        "Hedef rerun zincirinde kirik var: $($failedStep.Name)."
    }

    $payload = [ordered]@{
        StartedAtUtc = $startedAtUtc
        CompletedAtUtc = $completedAtUtc
        Succeeded = $succeeded
        FailedStep = if ($null -ne $failedStep) { $failedStep.Name } else { $null }
        SkippedStepCount = $skippedCount
        Summary = $summary
        Steps = $steps
    }

    $payload | ConvertTo-Json -Depth 6 | Set-Content -Path $summaryPath -Encoding UTF8

    Write-Host ''
    Write-Host ('SummaryPath=' + $summaryPath)
    Write-Host ('Succeeded=' + $succeeded)
    $failedStepName = if ($null -ne $failedStep) { $failedStep.Name } else { 'none' }
    Write-Host ('FailedStep=' + $failedStepName)
    Write-Host ('SkippedStepCount=' + $skippedCount)

    if (-not $succeeded) {
        exit 1
    }
}
finally {
    Pop-Location
}
