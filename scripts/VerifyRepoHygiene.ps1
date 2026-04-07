$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$globalJsonPath = Join-Path $repoRoot 'global.json'
$readmePath = Join-Path $repoRoot 'README.md'
$gitignorePath = Join-Path $repoRoot '.gitignore'
$workflowPath = Join-Path $repoRoot '.github\workflows\dotnet.yml'

function Assert-Contains {
    param(
        [string]$Content,
        [string]$Needle,
        [string]$Label)

    if (-not $Content.Contains($Needle)) {
        throw "$Label is missing required content: $Needle"
    }
}

if (-not (Test-Path $globalJsonPath)) {
    throw 'global.json is missing.'
}

$globalJson = Get-Content $globalJsonPath -Raw | ConvertFrom-Json
$sdkVersion = [string]$globalJson.sdk.version
if ([string]::IsNullOrWhiteSpace($sdkVersion)) {
    throw 'global.json does not define sdk.version.'
}

$readme = Get-Content $readmePath -Raw
$gitignore = Get-Content $gitignorePath -Raw
$workflow = Get-Content $workflowPath -Raw

Assert-Contains -Content $readme -Needle ('.NET SDK `' + $sdkVersion + '`') -Label 'README'
Assert-Contains -Content $readme -Needle 'dotnet restore CoinBot.sln' -Label 'README'
Assert-Contains -Content $readme -Needle 'dotnet build CoinBot.sln -c Debug -m:1 /nodeReuse:false -p:UseSharedCompilation=false' -Label 'README'
Assert-Contains -Content $readme -Needle 'dotnet test tests\CoinBot.UnitTests\CoinBot.UnitTests.csproj --no-build --no-restore -v:minimal' -Label 'README'
Assert-Contains -Content $readme -Needle 'dotnet test tests\CoinBot.IntegrationTests\CoinBot.IntegrationTests.csproj --no-build --no-restore -v:minimal' -Label 'README'
Assert-Contains -Content $readme -Needle 'tests\SmokeTests\PilotLifecycleRuntimeSmoke.ps1' -Label 'README'
Assert-Contains -Content $readme -Needle 'tests\SmokeTests\UiLiveBrowserSmoke.ps1' -Label 'README'

Assert-Contains -Content $gitignore -Needle '.diag/' -Label '.gitignore'
Assert-Contains -Content $gitignore -Needle 'artifacts/' -Label '.gitignore'
Assert-Contains -Content $gitignore -Needle 'build/' -Label '.gitignore'
Assert-Contains -Content $gitignore -Needle 'TestResults/' -Label '.gitignore'
Assert-Contains -Content $gitignore -Needle '*.log' -Label '.gitignore'
Assert-Contains -Content $gitignore -Needle '*.diag' -Label '.gitignore'
Assert-Contains -Content $gitignore -Needle '*.diag.*' -Label '.gitignore'
Assert-Contains -Content $gitignore -Needle '*.binlog' -Label '.gitignore'

Assert-Contains -Content $workflow -Needle 'global-json-file: global.json' -Label 'Workflow'
Assert-Contains -Content $workflow -Needle 'runs-on: windows-latest' -Label 'Workflow'
Assert-Contains -Content $workflow -Needle 'dotnet restore CoinBot.sln' -Label 'Workflow'
Assert-Contains -Content $workflow -Needle 'dotnet build CoinBot.sln -c Debug --no-restore -m:1 /nodeReuse:false -p:UseSharedCompilation=false' -Label 'Workflow'
Assert-Contains -Content $workflow -Needle 'dotnet test tests\CoinBot.UnitTests\CoinBot.UnitTests.csproj --no-build --no-restore -v:minimal' -Label 'Workflow'
Assert-Contains -Content $workflow -Needle 'scripts\VerifyRepoHygiene.ps1' -Label 'Workflow'

$artifactPattern = '(^|/)(artifacts|build|TestResults|\.diag|\.tmp|\.vs|\.appdata|\.dotnet|\.dotnet-home|\.dotnetcli|\.nuget|\.userprofile)(/|$)|\.(log|diag|binlog|etl)$|\.csproj\.user$|\.slnLaunch\.user$|(^|/)ck\.txt$'
$trackedArtifacts = @(git -C $repoRoot ls-files | Where-Object { $_ -match $artifactPattern })
if ($trackedArtifacts.Count -gt 0) {
    throw ('Tracked artifact files detected: ' + ($trackedArtifacts -join ', '))
}

Write-Host ('RepoHygiene=OK')
Write-Host ('PinnedSdk=' + $sdkVersion)
Write-Host ('TrackedArtifactCount=' + $trackedArtifacts.Count)


