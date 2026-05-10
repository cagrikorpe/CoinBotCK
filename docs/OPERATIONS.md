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

Relevant manual smoke entry points:
- `tests\SmokeTests\PilotLifecycleRuntimeSmoke.ps1` for Paket-1 pilot lifecycle closure
- `tests\SmokeTests\UiLiveBrowserSmoke.ps1` for Paket-4 browser-level Home/AiRobot render closure

Pilot testnet lifecycle enable, smoke, rollback ve incident akisi icin [PILOT_TESTNET_RUNBOOK.md](PILOT_TESTNET_RUNBOOK.md) dosyasini kullan.

## CI scope

The GitHub Actions workflow is intentionally limited to:
- repo hygiene guard
- restore
- solution build
- unit tests on Windows

Integration tests and smoke scripts remain outside CI because they need local infrastructure, user-secrets, a deterministic LocalDB graph, explicit browser availability, or live testnet access.

## Versioning and release tags

Use a single release story:
- create a release tag only from a commit that already passed build, unit tests, the relevant integration slice, and any explicitly required manual smoke
- use annotated release tags in `vX.Y.Z` format
- keep web and worker publish outputs tied to the same tagged commit
- treat `dotnet publish` output as the deployable artifact; do not publish ad-hoc local evidence or runtime smoke output

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
- confirm the target rollback build/tag and the exact migration state before changing anything
- redeploy the previous application build
- only roll back database changes if the migration set was designed for reversal and data impact is understood
- restart services and verify auth, dashboard load, and worker startup
- re-run the smallest relevant smoke or integration check
- record the failure reason, affected migration/build, and any required follow-up cleanup

## Secrets and config hygiene

Do not commit secrets, tokens, or local secret stores.

Current secret/config sources:
- environment variables such as `COINBOT_CREDENTIAL_ENCRYPTION_KEY_BASE64`
- ASP.NET Core user-secrets for local development
- deployment environment configuration for connection strings and exchange credentials

Rules:
- keep local development secrets in user-secrets or local environment variables, never in tracked `appsettings*.json`
- keep deployed secrets in environment-specific configuration outside the repo
- use only obvious placeholders in docs and scripts; never write values that look like real secrets

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

Runtime evidence under `.diag/` is local-only:
- `.diag\pilot-lifecycle-runtime-smoke\...` for pilot runtime evidence
- `.diag\ui-live-browser-smoke\...` for browser render evidence
- screenshots, summary json, stdout/stderr logs, and browser profiles stay local and must not be committed

For the pilot runtime smoke, `ReconciliationStatus=Unknown` is no longer a passing end state. A successful broker-submitted smoke run must leave `LastReconciledAtUtc` populated, `ReconciliationStatus` outside `Unknown`, and smoke-scoped open orders/positions at zero after smoke-local cleanup.

## Warning hygiene

This phase keeps warning handling honest instead of hiding it.

Resolved here:
- aligned EF Core package versions in `CoinBot.Infrastructure.csproj`
- removed redundant `Microsoft.Extensions.Configuration.EnvironmentVariables` and `Microsoft.Extensions.Configuration.Json` package references that triggered `NU1510`
- fixed the `xUnit2031` test warning in `StrategyLifecycleIntegrationTests`

Any warnings that remain after validation should be tracked explicitly and resolved intentionally; do not hide them behind broad suppression.

## ML dataset workflow

Current ML support is offline-only and must remain execution-neutral.

Status checkpoints:
- `FAZ-ML-01-DATA-READINESS-EVIDENCE-AUDIT`: `CLOSED AS PARTIAL_READY`
- `FAZ-ML-02-FEATURE-STORE`: safe feature snapshot store added for offline research
- `FAZ-ML-03-DATASET-BUILDER-LABELING`: deterministic label set added for dataset export and analysis

Operational rules:
- do not connect ML dataset generation to broker submit, execution gating, or strategy routing
- use `TrainingDatasetExportMode.Internal` only for local/internal analysis
- use `TrainingDatasetExportMode.Sanitized` for shared or reviewable CSV exports
- keep sanitized exports free of internal identifiers such as user ids, bot ids, snapshot ids, strategy signal ids, execution order ids, and correlation ids

Current dataset contracts:
- feature snapshot schema version: `MLFS-1.v1`
- label schema version: `TDL-1.v1`

Deterministic labels currently exported:
- `label_good_entry`
- `label_good_exit`
- `label_outcome`
- `label_false_signal`
- `label_expected_move_pct`
- `label_max_favorable_excursion`
- `label_max_adverse_excursion`
- `label_realized_pnl`
- `label_estimated_pnl`
- `label_drawdown`
- `label_risk_violation_count`
- `label_duplicate_order_count`
- `label_stale_data_entry_count`
- `label_reduce_only_violation_count`
- `label_completeness`
- `label_version`

Label derivation rules:
- `Unknown` must remain explicit when scored outcome or directional basis is missing
- actual realized PnL may be taken from `SpotPortfolioFills` or `DemoLedgerTransactions` when linked to the resolved execution order
- estimated PnL and drawdown use deterministic order notional and scored outcome/excursion values when actual realized PnL is unavailable
- risk, duplicate, stale-data, and reduce-only violations are negative/safety labels and should not be suppressed

Validation minimum:
- run solution build
- run unit tests covering dataset, label, ML, PnL, and outcome slices
- run `TrainingDatasetBuilderIntegrationTests` when changing export or SQL-backed label wiring
