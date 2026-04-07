# Operations Runbook

## Build and test standard

Use the repo root solution and the SDK pinned in `global.json`.

```powershell
dotnet restore CoinBot.sln
dotnet build CoinBot.sln -c Debug -m:1 /nodeReuse:false -p:UseSharedCompilation=false
dotnet test tests\CoinBot.UnitTests\CoinBot.UnitTests.csproj --no-build --no-restore -v:minimal
```

Integration tests are environment-dependent because they rely on SQL Server LocalDB and runtime configuration:

```powershell
dotnet test tests\CoinBot.IntegrationTests\CoinBot.IntegrationTests.csproj --no-build --no-restore -v:minimal
```

Smoke tests under `tests/SmokeTests` are manual-only. Do not wire them into CI.

Pilot testnet lifecycle enable, smoke, rollback ve incident akisi icin [PILOT_TESTNET_RUNBOOK.md](PILOT_TESTNET_RUNBOOK.md) dosyasini kullan.

## CI scope

The GitHub Actions workflow is intentionally limited to:
- restore
- solution build
- unit tests on Windows

Integration tests and smoke scripts remain outside CI because they need local infrastructure, user-secrets, or live testnet access.

## Release and publish checklist

Before publish:
- confirm `git status --short` contains only intentional source and doc changes
- run restore, build, unit tests, and the relevant integration slice
- verify pending EF migrations are understood and applied intentionally
- confirm pilot activation is off unless the release explicitly targets controlled testnet activation
- confirm no runtime artifacts, logs, or local cache files are staged

Publish sequence:
- publish the intended app project with the same SDK pinned in `global.json`
- apply EF migrations intentionally and verify `__EFMigrationsHistory`
- restart the target services
- verify startup health, auth, dashboard load, and expected background workers

After publish:
- verify recent logs contain no credential or payload leakage
- verify dashboard/control state surfaces load with real data or honest empty states
- if the release touches execution, run the narrowest safe testnet smoke needed for that change

## Rollback checklist

If a release must be rolled back:
- stop the affected web and worker services
- redeploy the previous application build
- only roll back database changes if the migration set was designed for reversal and data impact is understood
- restart services and verify auth, dashboard load, and worker startup
- re-run the smallest relevant smoke or integration check
- record the failure reason, affected migration/build, and any required follow-up cleanup

## Secrets and rotation

Do not commit secrets, tokens, or local secret stores.

Current secret/config sources:
- environment variables such as `COINBOT_CREDENTIAL_ENCRYPTION_KEY_BASE64`
- ASP.NET Core user-secrets for local development
- deployment environment configuration for connection strings and exchange credentials

Rotation guidance:
- rotate credential encryption keys, exchange API keys, database connection secrets, and any publish/deploy secrets together with a dated change record
- after rotation, verify local development, unit tests, and the narrowest environment-specific smoke needed for the changed secret
- remove obsolete secret material from local shell history, temp files, and shared notes

## Backup and restore

Minimum operational stance:
- ensure database backups exist before EF migration or publish steps that affect persistence
- keep a dated copy of the pre-release database backup reference in the release notes
- verify restore instructions for the target SQL instance before publish day
- after restore, run application startup verification and a small read-only smoke to confirm schema and auth state

## Artifact and log hygiene

The following must stay out of git:
- `artifacts/`
- `build/`
- `TestResults/`
- local `.dotnet`, `.nuget`, `.tmp`, `.vs`, `.appdata`, `.userprofile` state
- `*.log`, `*.diag*`, `*.binlog`
- `*.user`, `*.csproj.user`, `*.slnLaunch.user`

Runtime smoke output under `.diag/` should be reviewed locally and then discarded or archived outside the repo; it is local operational evidence, not a tracked artifact.

For the pilot runtime smoke, `ReconciliationStatus=Unknown` is no longer a passing end state. A successful broker-submitted smoke run must leave `LastReconciledAtUtc` populated, `ReconciliationStatus` outside `Unknown`, and smoke-scoped open orders/positions at zero after smoke-local cleanup.

## Warning hygiene

This phase keeps warning handling honest instead of hiding it.

Resolved here:
- aligned EF Core package versions in `CoinBot.Infrastructure.csproj`
- removed redundant `Microsoft.Extensions.Configuration.EnvironmentVariables` and `Microsoft.Extensions.Configuration.Json` package references that triggered `NU1510`
- fixed the `xUnit2031` test warning in `StrategyLifecycleIntegrationTests`

Any warnings that remain after validation should be treated as explicit backlog, not ignored by broad suppression.


