# CoinBot

CoinBot targets .NET 10 and uses a Windows-first local development workflow. The checked-in CI pipeline restores, builds, runs the repo hygiene guard, and executes the unit test suite on Windows. Integration tests and runtime smoke scripts stay manual because they require LocalDB, user-secrets, environment-specific credentials, live testnet connectivity, or a local browser session.

## SDK

The repo is pinned with [global.json](global.json) to .NET SDK `10.0.100`.

Verify the active SDK locally:

```powershell
dotnet --version
```

## Build and test

```powershell
dotnet restore CoinBot.sln
dotnet build CoinBot.sln -c Debug -m:1 /nodeReuse:false -p:UseSharedCompilation=false
dotnet test tests\CoinBot.UnitTests\CoinBot.UnitTests.csproj --no-build --no-restore -v:minimal
```

## Environment-dependent validation

Run integration tests only when LocalDB and the required configuration are available:

```powershell
dotnet test tests\CoinBot.IntegrationTests\CoinBot.IntegrationTests.csproj --no-build --no-restore -v:minimal
```

## Manual smoke entry points

Runtime smoke scripts live under [tests/SmokeTests](tests/SmokeTests). They are intentionally manual and should only be run with explicit scope, valid secrets, and a clean post-run review of logs and DB state.

Relevant entry points:

```powershell
pwsh -File tests\SmokeTests\PilotLifecycleRuntimeSmoke.ps1
pwsh -File tests\SmokeTests\UiLiveBrowserSmoke.ps1
```

These smokes are not part of CI because they depend on LocalDB, deterministic seed/setup, live testnet or local browser capabilities, and operator review of runtime evidence.

## CI scope

The checked-in workflow is [`.github/workflows/dotnet.yml`](.github/workflows/dotnet.yml). It uses `global.json`, runs on Windows, executes the repo hygiene guard, restores the solution, builds the solution, and runs the unit test project.

## Operations notes

Operational release, rollback, secret-handling, backup, and smoke guidance lives in [docs/OPERATIONS.md](docs/OPERATIONS.md) and [docs/PILOT_TESTNET_RUNBOOK.md](docs/PILOT_TESTNET_RUNBOOK.md).
