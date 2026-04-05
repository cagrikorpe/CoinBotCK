using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Alerts;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Exchange;

public sealed class SpotExchangeAppStateSyncService(
    ApplicationDbContext dbContext,
    IExchangeCredentialService exchangeCredentialService,
    IBinanceSpotPrivateRestClient privateRestClient,
    ExchangeAccountSnapshotHub snapshotHub,
    ExchangeAccountSyncStateService syncStateService,
    TimeProvider timeProvider,
    ILogger<SpotExchangeAppStateSyncService> logger,
    IAlertDispatchCoordinator? alertDispatchCoordinator = null,
    IHostEnvironment? hostEnvironment = null)
{
    private const string SystemActor = "system:spot-exchange-app-state-sync";

    internal async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var accounts = await ExchangeSyncAccountSelection.ListAsync(
            dbContext,
            ExchangeDataPlane.Spot,
            cancellationToken);

        foreach (var account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var credentialAccess = await exchangeCredentialService.GetAsync(
                    new ExchangeCredentialAccessRequest(
                        account.ExchangeAccountId,
                        SystemActor,
                        ExchangeCredentialAccessPurpose.Synchronization),
                    cancellationToken);

                var snapshot = await privateRestClient.GetAccountSnapshotAsync(
                    account.ExchangeAccountId,
                    account.OwnerUserId,
                    account.ExchangeName,
                    credentialAccess.ApiKey,
                    credentialAccess.ApiSecret,
                    cancellationToken);

                var drift = await DetectDriftAsync(snapshot, cancellationToken);
                snapshotHub.Publish(snapshot);

                var observedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
                await syncStateService.RecordReconciliationAsync(
                    account,
                    drift.Status,
                    drift.Summary,
                    observedAtUtc,
                    drift.Status == ExchangeStateDriftStatus.DriftDetected
                        ? observedAtUtc
                        : null,
                    errorCode: null,
                    cancellationToken);
            }
            catch (InvalidOperationException)
            {
                var observedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
                await syncStateService.RecordReconciliationAsync(
                    account,
                    ExchangeStateDriftStatus.Unknown,
                    "Spot credential access is blocked for synchronization.",
                    observedAtUtc,
                    driftDetectedAtUtc: null,
                    errorCode: "CredentialAccessBlocked",
                    cancellationToken);

                logger.LogInformation(
                    "Spot exchange-app reconciliation skipped for account {ExchangeAccountId} because synchronization access is blocked.",
                    account.ExchangeAccountId);

                await TrySendSyncFailureAlertAsync(
                    account.ExchangeAccountId,
                    "CredentialAccessBlocked",
                    "Spot credential access is blocked for synchronization.",
                    observedAtUtc,
                    cancellationToken);
            }
            catch
            {
                var observedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
                await syncStateService.RecordReconciliationAsync(
                    account,
                    ExchangeStateDriftStatus.Unknown,
                    "Spot reconciliation failed before exchange-app comparison completed.",
                    observedAtUtc,
                    driftDetectedAtUtc: null,
                    errorCode: "ReconciliationFailed",
                    cancellationToken);

                logger.LogWarning(
                    "Spot exchange-app reconciliation failed for account {ExchangeAccountId}.",
                    account.ExchangeAccountId);

                await TrySendSyncFailureAlertAsync(
                    account.ExchangeAccountId,
                    "ReconciliationFailed",
                    "Spot reconciliation failed before exchange-app comparison completed.",
                    observedAtUtc,
                    cancellationToken);
            }
        }
    }

    private async Task<DriftResult> DetectDriftAsync(
        ExchangeAccountSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var persistedBalances = await dbContext.ExchangeBalances
            .AsNoTracking()
            .Where(entity =>
                entity.ExchangeAccountId == snapshot.ExchangeAccountId &&
                entity.Plane == snapshot.Plane &&
                !entity.IsDeleted)
            .Select(entity => new ExchangeBalanceSnapshot(
                entity.Asset,
                entity.WalletBalance,
                entity.CrossWalletBalance,
                entity.AvailableBalance,
                entity.MaxWithdrawAmount,
                entity.ExchangeUpdatedAtUtc,
                entity.LockedBalance,
                entity.Plane))
            .ToListAsync(cancellationToken);

        var balanceMismatches = CountBalanceMismatches(snapshot.Balances, persistedBalances);
        var driftDetected = balanceMismatches > 0;
        var summary = driftDetected
            ? $"Plane=Spot; BalanceMismatches={balanceMismatches}; SnapshotSource={snapshot.Source}"
            : $"Plane=Spot; BalanceMismatches=0; SnapshotSource={snapshot.Source}";

        return new DriftResult(
            driftDetected
                ? ExchangeStateDriftStatus.DriftDetected
                : ExchangeStateDriftStatus.InSync,
            summary);
    }

    private static int CountBalanceMismatches(
        IReadOnlyCollection<ExchangeBalanceSnapshot> expectedBalances,
        IReadOnlyCollection<ExchangeBalanceSnapshot> actualBalances)
    {
        var expected = expectedBalances.ToDictionary(
            balance => NormalizeCode(balance.Asset),
            StringComparer.Ordinal);
        var actual = actualBalances.ToDictionary(
            balance => NormalizeCode(balance.Asset),
            StringComparer.Ordinal);
        var mismatchCount = 0;

        foreach (var key in expected.Keys.Union(actual.Keys, StringComparer.Ordinal))
        {
            if (!expected.TryGetValue(key, out var expectedBalance) ||
                !actual.TryGetValue(key, out var actualBalance))
            {
                mismatchCount++;
                continue;
            }

            if (expectedBalance.WalletBalance != actualBalance.WalletBalance ||
                expectedBalance.CrossWalletBalance != actualBalance.CrossWalletBalance ||
                expectedBalance.AvailableBalance != actualBalance.AvailableBalance ||
                expectedBalance.MaxWithdrawAmount != actualBalance.MaxWithdrawAmount ||
                expectedBalance.LockedBalance != actualBalance.LockedBalance)
            {
                mismatchCount++;
            }
        }

        return mismatchCount;
    }

    private static string NormalizeCode(string value)
    {
        return value.Trim().ToUpperInvariant();
    }

    private sealed record DriftResult(ExchangeStateDriftStatus Status, string Summary);

    private async Task TrySendSyncFailureAlertAsync(
        Guid exchangeAccountId,
        string failureCode,
        string reason,
        DateTime observedAtUtc,
        CancellationToken cancellationToken)
    {
        if (alertDispatchCoordinator is null)
        {
            return;
        }

        await alertDispatchCoordinator.SendAsync(
            new CoinBot.Application.Abstractions.Alerts.AlertNotification(
                Code: $"SYNC_FAILED_SPOT_{failureCode.ToUpperInvariant()}",
                Severity: CoinBot.Application.Abstractions.Alerts.AlertSeverity.Warning,
                Title: "SyncFailed",
                Message:
                    $"EventType=SyncFailed; SyncKind=SpotAppState; ExchangeAccountId={exchangeAccountId:N}; Result=Failed; FailureCode={failureCode}; Reason={reason}; TimestampUtc={observedAtUtc:O}; Environment={ResolveEnvironmentLabel()}",
                CorrelationId: null),
            $"sync-failed:spot-app-state:{exchangeAccountId:N}:{failureCode}",
            TimeSpan.FromMinutes(5),
            cancellationToken);
    }

    private string ResolveEnvironmentLabel()
    {
        var runtimeLabel = hostEnvironment?.EnvironmentName ?? "Unknown";
        var planeLabel = hostEnvironment?.IsDevelopment() == true
            ? "Testnet"
            : "Live";

        return $"{runtimeLabel}/{planeLabel}";
    }
}
