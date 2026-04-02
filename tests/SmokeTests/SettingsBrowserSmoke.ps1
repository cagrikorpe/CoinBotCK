$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
Set-Location $repoRoot
& node (Join-Path $PSScriptRoot 'SettingsBrowserSmoke.mjs')
exit $LASTEXITCODE
