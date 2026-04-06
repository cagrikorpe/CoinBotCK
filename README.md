# CoinBot

CoinBot targets .NET 10 and uses a Windows-first local development workflow. The checked-in CI pipeline restores, builds, and runs the unit test suite on Windows. Integration tests and runtime smoke scripts stay manual because they require LocalDB, user-secrets, environment-specific credentials, or live testnet connectivity.

## SDK

The repo is pinned with [global.json](global.json) to .NET SDK `10.0.100`.

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

Runtime smoke scripts live under [tests/SmokeTests](tests/SmokeTests). They are intentionally manual and should only be run with explicit testnet scope, valid secrets, and a clean post-run review of logs and DB state.

## Operations notes

Operational build, release, rollback, backup, and secret-handling guidance lives in [docs/OPERATIONS.md](docs/OPERATIONS.md).
