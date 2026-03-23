using System.Globalization;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.DemoPortfolio;

public sealed class DemoConsistencyWatchdogService(
    ApplicationDbContext dbContext,
    IOptions<DemoSessionOptions> options,
    TimeProvider timeProvider,
    ILogger<DemoConsistencyWatchdogService> logger)
{
    private readonly DemoSessionOptions optionsValue = options.Value;

    public async Task<DemoConsistencyCheckResult> EvaluateAsync(
        DemoSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var sessionStartedAtUtc = NormalizeTimestamp(session.StartedAtUtc);
        var walletEntries = await dbContext.DemoLedgerEntries
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity =>
                entity.OwnerUserId == session.OwnerUserId &&
                !entity.IsDeleted &&
                entity.CreatedDate >= sessionStartedAtUtc)
            .ToListAsync(cancellationToken);
        var wallets = await dbContext.DemoWallets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity =>
                entity.OwnerUserId == session.OwnerUserId &&
                !entity.IsDeleted)
            .ToListAsync(cancellationToken);
        var positionTransactions = await dbContext.DemoLedgerTransactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity =>
                entity.OwnerUserId == session.OwnerUserId &&
                !entity.IsDeleted &&
                entity.CreatedDate >= sessionStartedAtUtc &&
                entity.Symbol != null &&
                entity.PositionQuantityAfter != null)
            .ToListAsync(cancellationToken);
        var positions = await dbContext.DemoPositions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity =>
                entity.OwnerUserId == session.OwnerUserId &&
                !entity.IsDeleted)
            .ToListAsync(cancellationToken);
        var reservationEntries = await (
                from transaction in dbContext.DemoLedgerTransactions.IgnoreQueryFilters().AsNoTracking()
                join entry in dbContext.DemoLedgerEntries.IgnoreQueryFilters().AsNoTracking()
                    on transaction.Id equals entry.DemoLedgerTransactionId
                where transaction.OwnerUserId == session.OwnerUserId &&
                      !transaction.IsDeleted &&
                      transaction.CreatedDate >= sessionStartedAtUtc &&
                      transaction.OrderId != null &&
                      !entry.IsDeleted
                select new DemoReservationEntry(transaction.OrderId!, entry.ReservedDelta))
            .ToListAsync(cancellationToken);
        var demoOrders = await dbContext.ExecutionOrders
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity =>
                entity.OwnerUserId == session.OwnerUserId &&
                !entity.IsDeleted &&
                entity.ExecutionEnvironment == ExecutionEnvironment.Demo &&
                entity.CreatedDate >= sessionStartedAtUtc)
            .ToListAsync(cancellationToken);
        var walletMismatches = EvaluateWalletDrift(walletEntries, wallets);
        var positionMismatches = EvaluatePositionDrift(positionTransactions, positions);
        var orderMismatches = EvaluateOrderDrift(reservationEntries, demoOrders);
        var status = walletMismatches.Count == 0 &&
                     positionMismatches.Count == 0 &&
                     orderMismatches.Count == 0
            ? DemoConsistencyStatus.InSync
            : DemoConsistencyStatus.DriftDetected;
        var summary = BuildSummary(walletMismatches, positionMismatches, orderMismatches);
        var evaluatedAtUtc = NormalizeTimestamp(timeProvider.GetUtcNow().UtcDateTime);

        if (status == DemoConsistencyStatus.DriftDetected)
        {
            logger.LogWarning(
                "Demo consistency watchdog detected drift for session {DemoSessionId}. {Summary}",
                session.Id,
                summary);
        }

        return new DemoConsistencyCheckResult(
            status,
            evaluatedAtUtc,
            summary,
            walletMismatches.Count,
            positionMismatches.Count,
            orderMismatches.Count);
    }

    private List<string> EvaluateWalletDrift(
        IReadOnlyCollection<DemoLedgerEntry> walletEntries,
        IReadOnlyCollection<DemoWallet> wallets)
    {
        var expectedByAsset = walletEntries
            .GroupBy(entity => entity.Asset, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (Available: ClampZero(group.Sum(entity => entity.AvailableDelta)), Reserved: ClampZero(group.Sum(entity => entity.ReservedDelta))),
                StringComparer.Ordinal);
        var walletByAsset = wallets.ToDictionary(entity => entity.Asset, StringComparer.Ordinal);
        var mismatches = new List<string>();

        foreach (var asset in expectedByAsset.Keys.Concat(walletByAsset.Keys).Distinct(StringComparer.Ordinal).OrderBy(asset => asset, StringComparer.Ordinal))
        {
            expectedByAsset.TryGetValue(asset, out var expected);
            walletByAsset.TryGetValue(asset, out var wallet);

            if (AreEqual(expected.Available, wallet?.AvailableBalance) &&
                AreEqual(expected.Reserved, wallet?.ReservedBalance))
            {
                continue;
            }

            mismatches.Add(
                FormattableString.Invariant(
                    $"Wallet[{asset}] Expected={FormatDecimal(expected.Available)}/{FormatDecimal(expected.Reserved)} Actual={FormatDecimal(wallet?.AvailableBalance ?? 0m)}/{FormatDecimal(wallet?.ReservedBalance ?? 0m)}"));
        }

        return mismatches;
    }

    private List<string> EvaluatePositionDrift(
        IReadOnlyCollection<DemoLedgerTransaction> positionTransactions,
        IReadOnlyCollection<DemoPosition> positions)
    {
        var latestByKey = positionTransactions
            .GroupBy(entity => CreatePositionKey(entity.PositionScopeKey, entity.Symbol ?? string.Empty), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(entity => entity.CreatedDate).ThenByDescending(entity => entity.OccurredAtUtc).ThenByDescending(entity => entity.Id).First(),
                StringComparer.Ordinal);
        var positionsByKey = positions.ToDictionary(entity => CreatePositionKey(entity.PositionScopeKey, entity.Symbol), StringComparer.Ordinal);
        var mismatches = new List<string>();

        foreach (var key in latestByKey.Keys.Concat(positionsByKey.Keys).Distinct(StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal))
        {
            latestByKey.TryGetValue(key, out var transaction);
            positionsByKey.TryGetValue(key, out var position);

            if (transaction is null)
            {
                if (position is not null && HasMaterialPositionState(position))
                {
                    mismatches.Add($"Position[{key}] MissingSnapshot");
                }

                continue;
            }

            if (position is null)
            {
                if (!AreEqual(transaction.PositionQuantityAfter, 0m))
                {
                    mismatches.Add($"Position[{key}] MissingRow");
                }

                continue;
            }

            if (!string.Equals(position.BaseAsset, transaction.BaseAsset, StringComparison.Ordinal) ||
                !string.Equals(position.QuoteAsset, transaction.QuoteAsset, StringComparison.Ordinal) ||
                position.BotId != transaction.BotId ||
                position.PositionKind != transaction.PositionKind.GetValueOrDefault(position.PositionKind) ||
                position.MarginMode != transaction.MarginMode ||
                !AreEqual(position.Leverage, transaction.Leverage) ||
                !AreEqual(position.Quantity, transaction.PositionQuantityAfter) ||
                !AreEqual(position.CostBasis, transaction.PositionCostBasisAfter) ||
                !AreEqual(position.AverageEntryPrice, transaction.PositionAverageEntryPriceAfter) ||
                !AreEqual(position.RealizedPnl, transaction.CumulativeRealizedPnlAfter) ||
                !AreEqual(position.UnrealizedPnl, transaction.UnrealizedPnlAfter) ||
                !AreEqual(position.TotalFeesInQuote, transaction.CumulativeFeesInQuoteAfter) ||
                !AreEqual(position.NetFundingInQuote, transaction.NetFundingInQuoteAfter) ||
                !AreEqual(position.LastPrice, transaction.LastPriceAfter) ||
                !AreEqual(position.LastMarkPrice, transaction.MarkPriceAfter) ||
                !AreEqual(position.MaintenanceMarginRate, transaction.MaintenanceMarginRateAfter) ||
                !AreEqual(position.MaintenanceMargin, transaction.MaintenanceMarginAfter) ||
                !AreEqual(position.MarginBalance, transaction.MarginBalanceAfter) ||
                !AreEqual(position.LiquidationPrice, transaction.LiquidationPriceAfter))
            {
                mismatches.Add($"Position[{key}] SnapshotMismatch");
            }
        }

        return mismatches;
    }

    private List<string> EvaluateOrderDrift(
        IReadOnlyCollection<DemoReservationEntry> reservationEntries,
        IReadOnlyCollection<ExecutionOrder> demoOrders)
    {
        var openStates = new HashSet<ExecutionOrderState> { ExecutionOrderState.Submitted, ExecutionOrderState.PartiallyFilled };
        var outstandingByOrderId = reservationEntries
            .GroupBy(entity => entity.OrderId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => ClampZero(group.Sum(entity => entity.ReservedDelta)), StringComparer.Ordinal);
        var mismatches = new List<string>();

        foreach (var order in demoOrders)
        {
            outstandingByOrderId.TryGetValue(order.Id.ToString("N"), out var outstandingReservation);

            if (openStates.Contains(order.State))
            {
                if (Math.Abs(outstandingReservation) <= optionsValue.ConsistencyTolerance)
                {
                    mismatches.Add($"Order[{order.Id:N}] MissingReservation");
                }

                continue;
            }

            if (Math.Abs(outstandingReservation) > optionsValue.ConsistencyTolerance)
            {
                mismatches.Add($"Order[{order.Id:N}] ReservationLeak");
            }
        }

        return mismatches;
    }

    private string BuildSummary(
        IReadOnlyCollection<string> walletMismatches,
        IReadOnlyCollection<string> positionMismatches,
        IReadOnlyCollection<string> orderMismatches)
    {
        var summary = FormattableString.Invariant(
            $"WalletMismatches={walletMismatches.Count}; PositionMismatches={positionMismatches.Count}; OrderMismatches={orderMismatches.Count}");
        var details = walletMismatches.Concat(positionMismatches).Concat(orderMismatches).Take(3).ToArray();

        if (details.Length == 0)
        {
            return summary;
        }

        var combined = $"{summary}; Details={string.Join(", ", details)}";
        return combined.Length <= 512 ? combined : combined[..512];
    }

    private static bool HasMaterialPositionState(DemoPosition position)
    {
        return position.Quantity != 0m ||
               position.CostBasis != 0m ||
               position.AverageEntryPrice != 0m ||
               position.RealizedPnl != 0m ||
               position.UnrealizedPnl != 0m ||
               position.TotalFeesInQuote != 0m ||
               position.NetFundingInQuote != 0m ||
               position.LastMarkPrice.HasValue ||
               position.LastPrice.HasValue ||
               position.LastFillPrice.HasValue ||
               position.Leverage.HasValue ||
               position.MarginMode.HasValue ||
               position.MaintenanceMarginRate.HasValue ||
               position.MaintenanceMargin.HasValue ||
               position.MarginBalance.HasValue ||
               position.LiquidationPrice.HasValue;
    }

    private static string CreatePositionKey(string positionScopeKey, string symbol) => $"{positionScopeKey}|{symbol}";

    private bool AreEqual(decimal actual, decimal expected) => Math.Abs(actual - expected) <= optionsValue.ConsistencyTolerance;

    private bool AreEqual(decimal? actual, decimal? expected) => !actual.HasValue && !expected.HasValue || actual.HasValue && expected.HasValue && AreEqual(actual.Value, expected.Value);

    private decimal ClampZero(decimal value) => Math.Abs(value) <= optionsValue.ConsistencyTolerance ? 0m : value;

    private static string FormatDecimal(decimal value) => value.ToString("0.##################", CultureInfo.InvariantCulture);

    private static DateTime NormalizeTimestamp(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private sealed record DemoReservationEntry(string OrderId, decimal ReservedDelta);
}

public sealed record DemoConsistencyCheckResult(
    DemoConsistencyStatus Status,
    DateTime EvaluatedAtUtc,
    string? Summary,
    int WalletMismatchCount,
    int PositionMismatchCount,
    int OrderMismatchCount);
