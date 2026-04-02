using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Alerts;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Exchange;

public sealed class ExchangeAppStateSyncService(
    ApplicationDbContext dbContext,
    IExchangeCredentialService exchangeCredentialService,
    IBinancePrivateRestClient privateRestClient,
    ExchangeAccountSnapshotHub snapshotHub,
    ExchangeAccountSyncStateService syncStateService,
    TimeProvider timeProvider,
    ILogger<ExchangeAppStateSyncService> logger,
    IAlertDispatchCoordinator? alertDispatchCoordinator = null,
    IHostEnvironment? hostEnvironment = null)
{
    private const string SystemActor = "system:exchange-app-state-sync";

    internal async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var accounts = await dbContext.ExchangeAccounts
            .AsNoTracking()
            .Where(entity => !entity.IsDeleted &&
                             entity.ExchangeName == "Binance" &&
                             entity.ApiKeyCiphertext != null &&
                             entity.ApiSecretCiphertext != null)
            .Select(entity => new ExchangeSyncAccountDescriptor(
                entity.Id,
                entity.OwnerUserId,
                entity.ExchangeName))
            .ToListAsync(cancellationToken);

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

                await syncStateService.RecordReconciliationAsync(
                    account,
                    drift.Status,
                    drift.Summary,
                    timeProvider.GetUtcNow().UtcDateTime,
                    drift.Status == ExchangeStateDriftStatus.DriftDetected
                        ? timeProvider.GetUtcNow().UtcDateTime
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
                    "Credential access is blocked for synchronization.",
                    observedAtUtc,
                    driftDetectedAtUtc: null,
                    errorCode: "CredentialAccessBlocked",
                    cancellationToken);

                logger.LogInformation(
                    "Exchange-app reconciliation skipped for account {ExchangeAccountId} because synchronization access is blocked.",
                    account.ExchangeAccountId);

                await TrySendSyncFailureAlertAsync(
                    account.ExchangeAccountId,
                    "CredentialAccessBlocked",
                    "Credential access is blocked for synchronization.",
                    observedAtUtc,
                    cancellationToken);
            }
            catch
            {
                var observedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
                await syncStateService.RecordReconciliationAsync(
                    account,
                    ExchangeStateDriftStatus.Unknown,
                    "Reconciliation failed before exchange-app comparison completed.",
                    observedAtUtc,
                    driftDetectedAtUtc: null,
                    errorCode: "ReconciliationFailed",
                    cancellationToken);

                logger.LogWarning(
                    "Exchange-app reconciliation failed for account {ExchangeAccountId}.",
                    account.ExchangeAccountId);

                await TrySendSyncFailureAlertAsync(
                    account.ExchangeAccountId,
                    "ReconciliationFailed",
                    "Reconciliation failed before exchange-app comparison completed.",
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
            .Where(entity => entity.ExchangeAccountId == snapshot.ExchangeAccountId && !entity.IsDeleted)
            .Select(entity => new ExchangeBalanceSnapshot(
                entity.Asset,
                entity.WalletBalance,
                entity.CrossWalletBalance,
                entity.AvailableBalance,
                entity.MaxWithdrawAmount,
                entity.ExchangeUpdatedAtUtc))
            .ToListAsync(cancellationToken);
        var persistedPositions = await dbContext.ExchangePositions
            .AsNoTracking()
            .Where(entity => entity.ExchangeAccountId == snapshot.ExchangeAccountId && !entity.IsDeleted)
            .Select(entity => new ExchangePositionSnapshot(
                entity.Symbol,
                entity.PositionSide,
                entity.Quantity,
                entity.EntryPrice,
                entity.BreakEvenPrice,
                entity.UnrealizedProfit,
                entity.MarginType,
                entity.IsolatedWallet,
                entity.ExchangeUpdatedAtUtc))
            .ToListAsync(cancellationToken);

        var balanceMismatches = CountBalanceMismatches(snapshot.Balances, persistedBalances);
        var positionMismatches = CountPositionMismatches(snapshot.Positions, persistedPositions);
        var driftDetected = balanceMismatches > 0 || positionMismatches > 0;
        var summary = driftDetected
            ? $"BalanceMismatches={balanceMismatches}; PositionMismatches={positionMismatches}; SnapshotSource={snapshot.Source}"
            : $"BalanceMismatches=0; PositionMismatches=0; SnapshotSource={snapshot.Source}";

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
                expectedBalance.CrossWalletBalance != actualBalance.CrossWalletBalance)
            {
                mismatchCount++;
            }
        }

        return mismatchCount;
    }

    private static int CountPositionMismatches(
        IReadOnlyCollection<ExchangePositionSnapshot> expectedPositions,
        IReadOnlyCollection<ExchangePositionSnapshot> actualPositions)
    {
        var expected = expectedPositions.ToDictionary(
            position => CreatePositionKey(position.Symbol, position.PositionSide),
            StringComparer.Ordinal);
        var actual = actualPositions.ToDictionary(
            position => CreatePositionKey(position.Symbol, position.PositionSide),
            StringComparer.Ordinal);
        var mismatchCount = 0;

        foreach (var key in expected.Keys.Union(actual.Keys, StringComparer.Ordinal))
        {
            if (!expected.TryGetValue(key, out var expectedPosition) ||
                !actual.TryGetValue(key, out var actualPosition))
            {
                mismatchCount++;
                continue;
            }

            if (expectedPosition.Quantity != actualPosition.Quantity ||
                expectedPosition.EntryPrice != actualPosition.EntryPrice ||
                expectedPosition.BreakEvenPrice != actualPosition.BreakEvenPrice ||
                expectedPosition.UnrealizedProfit != actualPosition.UnrealizedProfit ||
                expectedPosition.IsolatedWallet != actualPosition.IsolatedWallet)
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

    private static string CreatePositionKey(string symbol, string positionSide)
    {
        return $"{NormalizeCode(symbol)}:{NormalizeCode(positionSide)}";
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
                Code: $"SYNC_FAILED_{failureCode.ToUpperInvariant()}",
                Severity: CoinBot.Application.Abstractions.Alerts.AlertSeverity.Warning,
                Title: "SyncFailed",
                Message:
                    $"EventType=SyncFailed; SyncKind=AppState; ExchangeAccountId={exchangeAccountId:N}; Result=Failed; FailureCode={failureCode}; Reason={reason}; TimestampUtc={observedAtUtc:O}; Environment={ResolveEnvironmentLabel()}",
                CorrelationId: null),
            $"sync-failed:app-state:{exchangeAccountId:N}:{failureCode}",
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
