$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
Set-Location $repoRoot
& node (Join-Path $PSScriptRoot 'StrategyBuilderBrowserSmoke.mjs')
exit $LASTEXITCODE
