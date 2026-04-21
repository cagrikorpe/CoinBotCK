using CoinBot.Application.Abstractions.Dashboard;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace CoinBot.Infrastructure.Dashboard;

public sealed class UserDashboardPortfolioReadModelService(
    ApplicationDbContext dbContext,
    IMarketDataService? marketDataService = null) : IUserDashboardPortfolioReadModelService
{
    private const int TradeHistoryRowLimit = 50;
    private const decimal PnlConsistencyTolerance = 0.0001m;
    private const decimal PrecisionEpsilon = 0.000000000000000001m;

    public async Task<UserDashboardPortfolioSnapshot> GetSnapshotAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var normalizedUserId = dbContext.EnsureCurrentUserScope(userId);
        var activeAccounts = await dbContext.ExchangeAccounts
            .AsNoTracking()
            .Where(entity =>
                entity.OwnerUserId == normalizedUserId &&
                !entity.IsDeleted &&
                entity.ExchangeName == "Binance" &&
                entity.CredentialStatus == ExchangeCredentialStatus.Active)
            .Select(entity => entity.Id)
            .ToListAsync(cancellationToken);

        var balances = activeAccounts.Count == 0
            ? new List<UserDashboardBalanceSnapshot>()
            : await dbContext.ExchangeBalances
            .AsNoTracking()
            .Where(entity =>
                entity.OwnerUserId == normalizedUserId &&
                activeAccounts.Contains(entity.ExchangeAccountId) &&
                !entity.IsDeleted &&
                (entity.WalletBalance != 0m ||
                 entity.CrossWalletBalance != 0m ||
                 (entity.AvailableBalance ?? 0m) != 0m ||
                 (entity.MaxWithdrawAmount ?? 0m) != 0m))
            .OrderByDescending(entity => entity.SyncedAtUtc)
            .ThenBy(entity => entity.Asset)
            .Select(entity => new UserDashboardBalanceSnapshot(
                entity.Asset,
                entity.WalletBalance,
                entity.CrossWalletBalance,
                entity.AvailableBalance,
                entity.MaxWithdrawAmount,
                entity.ExchangeUpdatedAtUtc,
                entity.SyncedAtUtc,
                entity.LockedBalance,
                entity.Plane,
                entity.ExchangeAccountId))
            .ToListAsync(cancellationToken);

        var livePositions = activeAccounts.Count == 0
            ? new List<ExchangePosition>()
            : await dbContext.ExchangePositions
            .AsNoTracking()
            .Where(entity =>
                entity.OwnerUserId == normalizedUserId &&
                activeAccounts.Contains(entity.ExchangeAccountId) &&
                !entity.IsDeleted &&
                entity.Quantity != 0m)
            .OrderByDescending(entity => Math.Abs(entity.UnrealizedProfit))
            .ThenBy(entity => entity.Symbol)
            .ToListAsync(cancellationToken);
        var positions = livePositions
            .Select(MapLivePositionSnapshot)
            .ToList();

        var syncStates = activeAccounts.Count == 0
            ? new List<ExchangeAccountSyncState>()
            : await dbContext.ExchangeAccountSyncStates
            .AsNoTracking()
            .Where(entity =>
                entity.OwnerUserId == normalizedUserId &&
                activeAccounts.Contains(entity.ExchangeAccountId) &&
                !entity.IsDeleted)
            .OrderByDescending(entity => entity.LastPrivateStreamEventAtUtc ?? DateTime.MinValue)
            .ThenByDescending(entity => entity.LastPositionSyncedAtUtc ?? DateTime.MinValue)
            .ThenByDescending(entity => entity.LastBalanceSyncedAtUtc ?? DateTime.MinValue)
            .ToListAsync(cancellationToken);

        var demoPositions = await dbContext.DemoPositions
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == normalizedUserId &&
                !entity.IsDeleted)
            .OrderByDescending(entity => Math.Abs(entity.UnrealizedPnl))
            .ThenByDescending(entity => Math.Abs(entity.RealizedPnl))
            .ThenBy(entity => entity.Symbol)
            .ToListAsync(cancellationToken);

        foreach (var demoPosition in demoPositions.Where(entity => entity.Quantity != 0m))
        {
            positions.Add(new UserDashboardPositionSnapshot(
                demoPosition.Symbol,
                demoPosition.Quantity > 0m ? "LONG" : "SHORT",
                demoPosition.Quantity,
                demoPosition.AverageEntryPrice,
                demoPosition.AverageEntryPrice,
                demoPosition.UnrealizedPnl,
                demoPosition.MarginMode?.ToString() ?? "cross",
                demoPosition.IsolatedMargin ?? 0m,
                demoPosition.LastValuationAtUtc ?? demoPosition.UpdatedDate,
                demoPosition.LastFilledAtUtc ?? demoPosition.UpdatedDate,
                ExchangeDataPlane.Futures,
                demoPosition.RealizedPnl,
                demoPosition.CostBasis,
                demoPosition.LastMarkPrice ?? demoPosition.LastPrice,
                null,
                null));
        }

        var spotFillRows = activeAccounts.Count == 0
            ? []
            : await dbContext.SpotPortfolioFills
                .AsNoTracking()
                .Where(entity =>
                    entity.OwnerUserId == normalizedUserId &&
                    activeAccounts.Contains(entity.ExchangeAccountId) &&
                    entity.Plane == ExchangeDataPlane.Spot &&
                    !entity.IsDeleted)
                .OrderBy(entity => entity.OccurredAtUtc)
                .ThenBy(entity => entity.TradeId)
                .ToListAsync(cancellationToken);
        var spotHoldings = await BuildSpotHoldingsAsync(spotFillRows, balances, cancellationToken);

        foreach (var holding in spotHoldings)
        {
            positions.Add(new UserDashboardPositionSnapshot(
                holding.Symbol,
                "LONG",
                holding.Quantity,
                holding.AverageCost,
                holding.MarkPrice ?? holding.AverageCost,
                holding.UnrealizedPnl,
                "spot",
                0m,
                holding.LastMarkPriceAtUtc ?? holding.LastTradeAtUtc,
                holding.LastTradeAtUtc,
                holding.Plane,
                holding.RealizedPnl,
                holding.CostBasis,
                holding.MarkPrice,
                holding.AvailableQuantity,
                holding.LockedQuantity));
        }

        var tradeHistory = await BuildTradeHistoryAsync(normalizedUserId, livePositions, demoPositions, spotHoldings, cancellationToken);
        var liveUnrealizedPnl = positions.Sum(entity => entity.UnrealizedProfit);
        var demoRealizedPnl = demoPositions.Sum(entity => entity.RealizedPnl);
        var spotRealizedPnl = spotFillRows.Sum(entity => entity.RealizedPnlDelta);
        var demoLedgerRealizedPnl = await dbContext.DemoLedgerTransactions
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => entity.OwnerUserId == normalizedUserId && !entity.IsDeleted)
            .SumAsync(entity => entity.RealizedPnlDelta ?? 0m, cancellationToken);
        var ledgerRealizedPnl = demoLedgerRealizedPnl + spotRealizedPnl;
        var totalRealizedPnl = demoRealizedPnl + spotRealizedPnl;
        var totalUnrealizedPnl = liveUnrealizedPnl;
        var totalPnl = totalRealizedPnl + totalUnrealizedPnl;
        var pnlConsistencySummary = BuildPnlConsistencySummary(totalRealizedPnl, ledgerRealizedPnl, totalUnrealizedPnl, totalPnl);
        var expectancy = BuildExpectancySnapshot(tradeHistory);

        var latestSyncAtUtc = syncStates
            .SelectMany(entity => new[]
            {
                entity.LastPrivateStreamEventAtUtc,
                entity.LastBalanceSyncedAtUtc,
                entity.LastPositionSyncedAtUtc,
                entity.LastStateReconciledAtUtc
            })
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty()
            .Max();

        var latestState = syncStates.FirstOrDefault();

        return new UserDashboardPortfolioSnapshot(
            activeAccounts.Count,
            BuildSyncStatusLabel(latestState),
            BuildSyncStatusTone(latestState),
            latestSyncAtUtc == DateTime.MinValue ? null : latestSyncAtUtc,
            totalRealizedPnl,
            totalUnrealizedPnl,
            totalPnl,
            pnlConsistencySummary,
            balances,
            positions,
            tradeHistory,
            spotHoldings,
            expectancy);
    }

    private async Task<IReadOnlyCollection<UserDashboardTradeHistoryRowSnapshot>> BuildTradeHistoryAsync(
        string ownerUserId,
        IReadOnlyCollection<ExchangePosition> livePositions,
        IReadOnlyCollection<DemoPosition> demoPositions,
        IReadOnlyCollection<UserDashboardSpotHoldingSnapshot> spotHoldings,
        CancellationToken cancellationToken)
    {
        var orders = await dbContext.ExecutionOrders
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == ownerUserId &&
                !entity.IsDeleted)
            .OrderByDescending(entity => entity.CreatedDate)
            .ThenByDescending(entity => entity.LastStateChangedAtUtc)
            .Take(TradeHistoryRowLimit)
            .ToListAsync(cancellationToken);

        if (orders.Count == 0)
        {
            return Array.Empty<UserDashboardTradeHistoryRowSnapshot>();
        }

        var orderIds = orders.Select(entity => entity.Id).ToArray();
        var orderIdTexts = orders
            .Select(entity => entity.Id.ToString("N"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var signalIds = orders
            .Select(entity => entity.StrategySignalId)
            .Where(signalId => signalId != Guid.Empty)
            .Distinct()
            .ToArray();
        var botIds = orders
            .Where(entity => entity.BotId.HasValue)
            .Select(entity => entity.BotId!.Value)
            .Distinct()
            .ToArray();

        var transitions = await dbContext.ExecutionOrderTransitions
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == ownerUserId &&
                orderIds.Contains(entity.ExecutionOrderId) &&
                !entity.IsDeleted)
            .OrderBy(entity => entity.SequenceNumber)
            .ToListAsync(cancellationToken);

        var latestTransitions = transitions
            .GroupBy(entity => entity.ExecutionOrderId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.SequenceNumber).First());

        var ledgerRows = await dbContext.DemoLedgerTransactions
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == ownerUserId &&
                entity.OrderId != null &&
                orderIdTexts.Contains(entity.OrderId) &&
                !entity.IsDeleted)
            .ToListAsync(cancellationToken);

        var ledgerByOrderId = ledgerRows
            .GroupBy(entity => entity.OrderId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var spotLedgerRows = await dbContext.SpotPortfolioFills
            .AsNoTracking()
            .Where(entity =>
                entity.OwnerUserId == ownerUserId &&
                orderIds.Contains(entity.ExecutionOrderId) &&
                entity.Plane == ExchangeDataPlane.Spot &&
                !entity.IsDeleted)
            .OrderBy(entity => entity.OccurredAtUtc)
            .ThenBy(entity => entity.TradeId)
            .ToListAsync(cancellationToken);
        var spotLedgerByOrderId = spotLedgerRows
            .GroupBy(entity => entity.ExecutionOrderId)
            .ToDictionary(group => group.Key, group => (IReadOnlyCollection<SpotPortfolioFill>)group.ToList());
        var spotHoldingByKey = spotHoldings.ToDictionary(
            holding => CreateSpotHoldingKey(holding.ExchangeAccountId, holding.Symbol),
            holding => holding,
            StringComparer.Ordinal);

        var handoffAttempts = signalIds.Length == 0
            ? []
            : await dbContext.MarketScannerHandoffAttempts
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    entity.OwnerUserId == ownerUserId &&
                    entity.StrategySignalId.HasValue &&
                    signalIds.Contains(entity.StrategySignalId.Value) &&
                    !entity.IsDeleted)
                .OrderByDescending(entity => entity.CompletedAtUtc)
                .ThenByDescending(entity => entity.CreatedDate)
                .ToListAsync(cancellationToken);

        var handoffBySignalId = handoffAttempts
            .GroupBy(entity => entity.StrategySignalId!.Value)
            .ToDictionary(group => group.Key, group => group.First());

        var decisionTraceCounts = signalIds.Length == 0
            ? new Dictionary<Guid, int>()
            : await dbContext.DecisionTraces
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    entity.UserId == ownerUserId &&
                    entity.StrategySignalId.HasValue &&
                    signalIds.Contains(entity.StrategySignalId.Value) &&
                    !entity.IsDeleted)
                .GroupBy(entity => entity.StrategySignalId!.Value)
                .Select(group => new { StrategySignalId = group.Key, Count = group.Count() })
                .ToDictionaryAsync(entity => entity.StrategySignalId, entity => entity.Count, cancellationToken);

        var executionTraceCounts = await dbContext.ExecutionTraces
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.UserId == ownerUserId &&
                entity.ExecutionOrderId.HasValue &&
                orderIds.Contains(entity.ExecutionOrderId.Value) &&
                !entity.IsDeleted)
            .GroupBy(entity => entity.ExecutionOrderId!.Value)
            .Select(group => new { ExecutionOrderId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(entity => entity.ExecutionOrderId, entity => entity.Count, cancellationToken);

        var orderRootCorrelationIds = orders
            .Select(entity => entity.RootCorrelationId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var auditCounts = orderRootCorrelationIds.Length == 0
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            : await dbContext.AuditLogs
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    orderRootCorrelationIds.Contains(entity.CorrelationId) &&
                    !entity.IsDeleted)
                .GroupBy(entity => entity.CorrelationId)
                .Select(group => new { CorrelationId = group.Key, Count = group.Count() })
                .ToDictionaryAsync(entity => entity.CorrelationId, entity => entity.Count, StringComparer.Ordinal, cancellationToken);

        var botNames = botIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await dbContext.TradingBots
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    entity.OwnerUserId == ownerUserId &&
                    botIds.Contains(entity.Id) &&
                    !entity.IsDeleted)
                .ToDictionaryAsync(entity => entity.Id, entity => entity.Name, cancellationToken);

        var tradeRows = new List<UserDashboardTradeHistoryRowSnapshot>(orders.Count);

        foreach (var order in orders)
        {
            latestTransitions.TryGetValue(order.Id, out var latestTransition);
            handoffBySignalId.TryGetValue(order.StrategySignalId, out var handoffAttempt);
            decisionTraceCounts.TryGetValue(order.StrategySignalId, out var decisionTraceCount);
            executionTraceCounts.TryGetValue(order.Id, out var executionTraceCount);
            auditCounts.TryGetValue(order.RootCorrelationId, out var auditCount);
            botNames.TryGetValue(order.BotId ?? Guid.Empty, out var botName);

            ledgerByOrderId.TryGetValue(order.Id.ToString("N"), out var orderLedgerRows);
            spotLedgerByOrderId.TryGetValue(order.Id, out var orderSpotRows);
            var realizedPnl = orderSpotRows?.Sum(entity => entity.RealizedPnlDelta) ??
                orderLedgerRows?.Sum(entity => entity.RealizedPnlDelta ?? 0m) ??
                0m;
            var feeAmountInQuote = orderSpotRows?.Sum(entity => entity.FeeAmountInQuote) ??
                orderLedgerRows?.Sum(entity => entity.FeeAmountInQuote ?? 0m);
            var costImpact = orderSpotRows?.Sum(entity => Math.Abs(entity.QuoteQuantity)) ??
                orderLedgerRows?.Sum(entity =>
                entity.Quantity.HasValue && entity.Price.HasValue
                    ? Math.Abs(entity.Quantity.Value * entity.Price.Value)
                    : 0m);
            var matchingOpenPosition = demoPositions.FirstOrDefault(entity =>
                entity.BotId == order.BotId &&
                string.Equals(entity.Symbol, order.Symbol, StringComparison.Ordinal) &&
                entity.Quantity != 0m);
            var matchingLivePosition = ResolveMatchingLivePosition(order, livePositions);
            var matchingSpotHolding = order.ExchangeAccountId.HasValue &&
                                      spotHoldingByKey.TryGetValue(CreateSpotHoldingKey(order.ExchangeAccountId.Value, order.Symbol), out var resolvedSpotHolding)
                ? resolvedSpotHolding
                : null;
            var weightedAverageFillPrice = ResolveAverageFillPrice(order, orderSpotRows);
            var transitionTradeId = ExtractDetailToken(latestTransition?.Detail, "TradeId");
            var tradeIdsSummary = BuildTradeIdsSummary(orderSpotRows) ?? transitionTradeId;
            var filledQuantity = orderSpotRows?.Sum(entity => entity.Quantity) ?? order.FilledQuantity;
            var cumulativeQuoteQuantity = orderSpotRows?.Sum(entity => entity.QuoteQuantity) ??
                                          ExtractDetailDecimal(latestTransition?.Detail, "CumulativeQuoteQuantity");
            var clientOrderId = orderSpotRows?.FirstOrDefault()?.ClientOrderId ?? ResolveClientOrderId(order, latestTransition);
            feeAmountInQuote ??= ResolveFuturesFeeAmountInQuote(order, latestTransition);

            var tradeDirection = ResolveTradeDirectionLabel(order);
            var tradeAction = ResolveTradeActionLabel(order, tradeDirection);
            var isClosingTrade = IsClosingTrade(order);

            tradeRows.Add(new UserDashboardTradeHistoryRowSnapshot(
                order.Id,
                SanitizeToken(clientOrderId),
                SanitizeToken(order.RootCorrelationId) ?? "n/a",
                order.Symbol,
                order.Timeframe,
                order.Side.ToString(),
                order.Quantity,
                weightedAverageFillPrice,
                realizedPnl,
                matchingSpotHolding?.UnrealizedPnl ?? matchingOpenPosition?.UnrealizedPnl ?? matchingLivePosition?.UnrealizedProfit,
                feeAmountInQuote,
                costImpact,
                order.CreatedDate,
                IsTerminalState(order.State) ? order.LastStateChangedAtUtc : null,
                order.LastStateChangedAtUtc,
                order.State.ToString(),
                ResolveExecutionResultCategory(order.State),
                ResolveExecutionResultCode(order, latestTransition),
                BuildExecutionResultSummary(order, latestTransition, orderSpotRows),
                order.RejectionStage.ToString(),
                order.SubmittedToBroker,
                order.RetryEligible,
                order.CooldownApplied,
                BuildReasonChainSummary(handoffAttempt, order, latestTransition, decisionTraceCount, executionTraceCount, auditCount, botName, orderSpotRows),
                AiScoreAvailable: false,
                AiScoreValue: null,
                AiScoreLabel: "AI score placeholder",
                AiScoreSummary: "AI score snapshot placeholder contract is present; no model score has been generated for this order.",
                AiScoreSource: "portfolio-history-placeholder",
                AiScoreGeneratedAtUtc: order.LastStateChangedAtUtc,
                AiScoreIsPlaceholder: true,
                Plane: order.Plane,
                FilledQuantity: filledQuantity,
                CumulativeQuoteQuantity: cumulativeQuoteQuantity,
                FillCount: orderSpotRows?.Count ?? 0,
                TradeIdsSummary: tradeIdsSummary,
                Direction: tradeDirection,
                TradeAction: tradeAction,
                IsClosingTrade: isClosingTrade));
        }

        return tradeRows;
    }

    private static string BuildSyncStatusLabel(ExchangeAccountSyncState? syncState)
    {
        if (syncState is null)
        {
            return "Henüz senkron yok";
        }

        return syncState.PrivateStreamConnectionState switch
        {
            ExchangePrivateStreamConnectionState.Connected when syncState.DriftStatus == ExchangeStateDriftStatus.InSync => "Canli senkron bagli",
            ExchangePrivateStreamConnectionState.Connected => "Canli senkron bagli, drift izleniyor",
            ExchangePrivateStreamConnectionState.Reconnecting => "Canli senkron yeniden baglaniyor",
            ExchangePrivateStreamConnectionState.Connecting => "Canli senkron baglaniyor",
            ExchangePrivateStreamConnectionState.ListenKeyExpired => "Listen key yenilemesi gerekiyor",
            _ => "Canli senkron bagli degil"
        };
    }

    private static string BuildSyncStatusTone(ExchangeAccountSyncState? syncState)
    {
        if (syncState is null)
        {
            return "neutral";
        }

        return syncState.PrivateStreamConnectionState switch
        {
            ExchangePrivateStreamConnectionState.Connected when syncState.DriftStatus == ExchangeStateDriftStatus.InSync => "positive",
            ExchangePrivateStreamConnectionState.Connected => "warning",
            ExchangePrivateStreamConnectionState.Reconnecting => "warning",
            ExchangePrivateStreamConnectionState.Connecting => "neutral",
            ExchangePrivateStreamConnectionState.ListenKeyExpired => "negative",
            _ => "negative"
        };
    }

    private static string NormalizeRequired(string? value, string parameterName)
    {
        var normalizedValue = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            throw new ArgumentException("The value is required.", parameterName);
        }

        return normalizedValue;
    }

    private static UserDashboardExpectancySnapshot BuildExpectancySnapshot(
        IReadOnlyCollection<UserDashboardTradeHistoryRowSnapshot> tradeHistory)
    {
        var closedTrades = tradeHistory
            .Where(entity =>
                entity.SubmittedToBroker &&
                entity.FinalState.Equals("Filled", StringComparison.OrdinalIgnoreCase) &&
                entity.IsClosingTrade)
            .ToArray();

        if (closedTrades.Length == 0)
        {
            return new UserDashboardExpectancySnapshot(
                HasData: false,
                ClosedTradeCount: 0,
                WinningTradeCount: 0,
                LosingTradeCount: 0,
                BreakEvenTradeCount: 0,
                WinRatePercentage: 0m,
                AverageWin: 0m,
                AverageLoss: 0m,
                Expectancy: 0m,
                ProfitFactor: null,
                Summary: "Expectancy hazır değil. Kapanmış işlem bulunamadı.");
        }

        var winningTrades = closedTrades.Where(entity => entity.RealizedPnl > 0m).ToArray();
        var losingTrades = closedTrades.Where(entity => entity.RealizedPnl < 0m).ToArray();
        var breakEvenTradeCount = closedTrades.Length - winningTrades.Length - losingTrades.Length;
        var grossProfit = winningTrades.Sum(entity => entity.RealizedPnl);
        var grossLoss = Math.Abs(losingTrades.Sum(entity => entity.RealizedPnl));
        var averageWin = winningTrades.Length == 0
            ? 0m
            : NormalizeDecimal(grossProfit / winningTrades.Length);
        var averageLoss = losingTrades.Length == 0
            ? 0m
            : NormalizeDecimal(grossLoss / losingTrades.Length);
        var winRatePercentage = NormalizeDecimal((winningTrades.Length * 100m) / closedTrades.Length);
        var expectancy = NormalizeDecimal(closedTrades.Sum(entity => entity.RealizedPnl) / closedTrades.Length);
        decimal? profitFactor = null;

        if (grossLoss > 0m)
        {
            profitFactor = NormalizeDecimal(grossProfit / grossLoss);
        }
        else if (grossProfit > 0m)
        {
            profitFactor = decimal.MaxValue;
        }

        var longClosedTradeCount = closedTrades.Count(entity => string.Equals(entity.Direction, "Long", StringComparison.OrdinalIgnoreCase));
        var shortClosedTradeCount = closedTrades.Count(entity => string.Equals(entity.Direction, "Short", StringComparison.OrdinalIgnoreCase));
        var summary = $"ClosedTrades={closedTrades.Length}; LongClosed={longClosedTradeCount}; ShortClosed={shortClosedTradeCount}; Wins={winningTrades.Length}; Losses={losingTrades.Length}; BreakEven={breakEvenTradeCount}; WinRate={winRatePercentage:0.##}%; Expectancy={expectancy:0.####}; ProfitFactor={(profitFactor == decimal.MaxValue ? "∞" : profitFactor?.ToString("0.####", CultureInfo.InvariantCulture) ?? "n/a")}.";

        return new UserDashboardExpectancySnapshot(
            HasData: true,
            ClosedTradeCount: closedTrades.Length,
            WinningTradeCount: winningTrades.Length,
            LosingTradeCount: losingTrades.Length,
            BreakEvenTradeCount: breakEvenTradeCount,
            WinRatePercentage: winRatePercentage,
            AverageWin: averageWin,
            AverageLoss: averageLoss,
            Expectancy: expectancy,
            ProfitFactor: profitFactor,
            Summary: summary,
            LongClosedTradeCount: longClosedTradeCount,
            ShortClosedTradeCount: shortClosedTradeCount);
    }

    private static string BuildPnlConsistencySummary(
        decimal realizedPnl,
        decimal ledgerRealizedPnl,
        decimal unrealizedPnl,
        decimal totalPnl)
    {
        var discrepancy = realizedPnl - ledgerRealizedPnl;

        return Math.Abs(discrepancy) <= PnlConsistencyTolerance
            ? FormattableString.Invariant(
                $"PnL consistent. Realized={realizedPnl:0.####}; Unrealized={unrealizedPnl:0.####}; Total={totalPnl:0.####}; LedgerDelta={ledgerRealizedPnl:0.####}.")
            : FormattableString.Invariant(
                $"PnL discrepancy detected. PositionRealized={realizedPnl:0.####}; LedgerRealized={ledgerRealizedPnl:0.####}; Discrepancy={discrepancy:0.####}; Unrealized={unrealizedPnl:0.####}; Total={totalPnl:0.####}.");
    }

    private static string ResolveExecutionResultCategory(ExecutionOrderState state)
    {
        return state switch
        {
            ExecutionOrderState.Filled => "Filled",
            ExecutionOrderState.PartiallyFilled => "Partial",
            ExecutionOrderState.CancelRequested or ExecutionOrderState.Cancelled => "Canceled",
            ExecutionOrderState.Rejected => "Rejected",
            ExecutionOrderState.Failed => "Failed",
            _ => "Open"
        };
    }

    private static string ResolveExecutionResultCode(
        ExecutionOrder order,
        ExecutionOrderTransition? latestTransition)
    {
        if (!string.IsNullOrWhiteSpace(order.FailureCode))
        {
            return order.FailureCode.Trim();
        }

        if (!string.IsNullOrWhiteSpace(latestTransition?.EventCode))
        {
            return latestTransition.EventCode.Trim();
        }

        return order.State.ToString();
    }

    private static string ResolveExecutionResultSummary(
        ExecutionOrder order,
        ExecutionOrderTransition? latestTransition)
    {
        if (order.Plane == ExchangeDataPlane.Futures)
        {
            return BuildFuturesExecutionResultSummary(order, latestTransition);
        }

        var summary = SanitizeDetail(order.FailureDetail)
            ?? SanitizeDetail(latestTransition?.Detail);

        if (!string.IsNullOrWhiteSpace(summary))
        {
            return summary;
        }

        return order.State switch
        {
            ExecutionOrderState.Filled => "Execution completed and the order was filled.",
            ExecutionOrderState.PartiallyFilled => "Execution is partially filled.",
            ExecutionOrderState.CancelRequested => "Cancel request was submitted and is being reconciled.",
            ExecutionOrderState.Cancelled => "Execution was cancelled.",
            ExecutionOrderState.Rejected => "Execution was rejected before completion.",
            ExecutionOrderState.Failed => "Execution failed after submission lifecycle checks.",
            ExecutionOrderState.Submitted => "Execution was submitted to broker.",
            _ => "Execution lifecycle is open."
        };
    }

    private static string BuildExecutionResultSummary(
        ExecutionOrder order,
        ExecutionOrderTransition? latestTransition,
        IReadOnlyCollection<SpotPortfolioFill>? spotLedgerRows)
    {
        var baseSummary = ResolveExecutionResultSummary(order, latestTransition);

        if (spotLedgerRows is not { Count: > 0 })
        {
            return baseSummary;
        }

        var realizedPnl = spotLedgerRows.Sum(entity => entity.RealizedPnlDelta);
        var feeAmountInQuote = spotLedgerRows.Sum(entity => entity.FeeAmountInQuote);
        var fillCount = spotLedgerRows.Count;
        var tradeIdsSummary = BuildTradeIdsSummary(spotLedgerRows);

        return Truncate(
            $"{baseSummary} | Plane=Spot; FillCount={fillCount.ToString(CultureInfo.InvariantCulture)}; TradeIds={tradeIdsSummary ?? "n/a"}; RealizedPnl={realizedPnl.ToString("0.####", CultureInfo.InvariantCulture)}; FeeInQuote={feeAmountInQuote.ToString("0.####", CultureInfo.InvariantCulture)}",
            512)
            ?? baseSummary;
    }

    private static string BuildReasonChainSummary(
        MarketScannerHandoffAttempt? handoffAttempt,
        ExecutionOrder order,
        ExecutionOrderTransition? latestTransition,
        int decisionTraceCount,
        int executionTraceCount,
        int auditCount,
        string? botName,
        IReadOnlyCollection<SpotPortfolioFill>? spotLedgerRows = null)
    {
        var scannerSummary = Truncate(handoffAttempt?.SelectionReason ?? "ScannerLink=Missing", 192) ?? "ScannerLink=Missing";
        var strategySummary = handoffAttempt is null
            ? "StrategyLink=Missing"
            : $"StrategyOutcome={NormalizeOptional(handoffAttempt.StrategyDecisionOutcome) ?? "n/a"}; StrategyScore={handoffAttempt.StrategyScore?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}; StrategyVeto={NormalizeOptional(handoffAttempt.StrategyVetoReasonCode) ?? "None"}";
        var riskSummary = handoffAttempt is null
            ? "RiskLink=Missing"
            : $"RiskOutcome={NormalizeOptional(handoffAttempt.RiskOutcome) ?? "n/a"}; RiskVeto={NormalizeOptional(handoffAttempt.RiskVetoReasonCode) ?? "None"}; RiskSummary={Truncate(SanitizeDetail(handoffAttempt.RiskSummary), 160) ?? "n/a"}";
        var executionSummary = $"ExecutionState={order.State}; ResultCode={ResolveExecutionResultCode(order, latestTransition)}; Stage={order.RejectionStage}; Submitted={order.SubmittedToBroker}; Retry={order.RetryEligible}; Cooldown={order.CooldownApplied}";
        var auditSummary = $"AuditTrail=DecisionTrace:{decisionTraceCount}; ExecutionTrace:{executionTraceCount}; AuditLog:{auditCount}; Bot={NormalizeOptional(botName) ?? "n/a"}";
        var spotSummary = order.Plane == ExchangeDataPlane.Spot
            ? $"Plane=Spot; FillCount={spotLedgerRows?.Count.ToString(CultureInfo.InvariantCulture) ?? "0"}; TradeIds={BuildTradeIdsSummary(spotLedgerRows) ?? "n/a"}"
            : BuildFuturesPlaneSummary(order, latestTransition);

        return Truncate(
            $"{scannerSummary} | {strategySummary} | {riskSummary} | {executionSummary} | {auditSummary} | {spotSummary}",
            1024)
            ?? executionSummary;
    }

    private static string? ResolveClientOrderId(ExecutionOrder order, ExecutionOrderTransition? latestTransition)
    {
        var transitionClientOrderId = ExtractClientOrderId(latestTransition?.Detail);
        if (!string.IsNullOrWhiteSpace(transitionClientOrderId))
        {
            return transitionClientOrderId;
        }

        if (!string.IsNullOrWhiteSpace(order.ExternalOrderId))
        {
            return order.ExternalOrderId;
        }

        return null;
    }

    private static string? ExtractClientOrderId(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        foreach (var segment in detail.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            if (string.Equals(segment[..separatorIndex].Trim(), "ClientOrderId", StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeOptional(segment[(separatorIndex + 1)..]);
            }
        }

        return null;
    }

    private static string? SanitizeToken(string? value)
    {
        var normalized = NormalizeOptional(value);
        return normalized is null
            ? null
            : normalized.Length <= 24 ? normalized : normalized[..24];
    }

    private static string? SanitizeDetail(string? value)
    {
        var normalized = NormalizeOptional(value);
        if (normalized is null)
        {
            return null;
        }

        var latencyIndex = normalized.IndexOf(" LatencyReason=", StringComparison.Ordinal);
        if (latencyIndex > 0)
        {
            normalized = normalized[..latencyIndex].Trim();
        }

        return Truncate(normalized, 512);
    }

    private static bool IsTerminalState(ExecutionOrderState state)
    {
        return state is
            ExecutionOrderState.Filled or
            ExecutionOrderState.Cancelled or
            ExecutionOrderState.Rejected or
            ExecutionOrderState.Failed;
    }

    private async Task<IReadOnlyCollection<UserDashboardSpotHoldingSnapshot>> BuildSpotHoldingsAsync(
        IReadOnlyCollection<SpotPortfolioFill> spotFillRows,
        IReadOnlyCollection<UserDashboardBalanceSnapshot> balances,
        CancellationToken cancellationToken)
    {
        if (spotFillRows.Count == 0)
        {
            return Array.Empty<UserDashboardSpotHoldingSnapshot>();
        }

        var balanceLookup = balances
            .Where(entity => entity.Plane == ExchangeDataPlane.Spot && entity.ExchangeAccountId.HasValue)
            .ToDictionary(
                entity => $"{entity.ExchangeAccountId!.Value:N}:{entity.Asset}",
                entity => entity,
                StringComparer.Ordinal);
        var latestHoldings = spotFillRows
            .GroupBy(entity => CreateSpotHoldingKey(entity.ExchangeAccountId, entity.Symbol), StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(entity => entity.OccurredAtUtc)
                .ThenByDescending(entity => entity.TradeId)
                .First())
            .Where(entity => entity.HoldingQuantityAfter > 0m)
            .OrderByDescending(entity => Math.Abs(entity.HoldingQuantityAfter))
            .ThenBy(entity => entity.Symbol, StringComparer.Ordinal)
            .ToArray();
        var holdings = new List<UserDashboardSpotHoldingSnapshot>(latestHoldings.Length);

        foreach (var holding in latestHoldings)
        {
            cancellationToken.ThrowIfCancellationRequested();

            balanceLookup.TryGetValue($"{holding.ExchangeAccountId:N}:{holding.BaseAsset}", out var baseBalance);
            var lockedQuantity = ResolveLockedQuantity(baseBalance, holding.HoldingQuantityAfter);
            var availableQuantity = NormalizeDecimal(holding.HoldingQuantityAfter - lockedQuantity);
            var markPriceSnapshot = marketDataService is null
                ? null
                : await marketDataService.GetLatestPriceAsync(holding.Symbol, cancellationToken);
            var markPrice = markPriceSnapshot?.Price;
            var unrealizedPnl = markPrice.HasValue
                ? NormalizeDecimal((markPrice.Value - holding.HoldingAverageCostAfter) * holding.HoldingQuantityAfter)
                : 0m;

            holdings.Add(new UserDashboardSpotHoldingSnapshot(
                holding.ExchangeAccountId,
                holding.Symbol,
                holding.BaseAsset,
                holding.QuoteAsset,
                holding.HoldingQuantityAfter,
                availableQuantity,
                lockedQuantity,
                holding.HoldingAverageCostAfter,
                holding.HoldingCostBasisAfter,
                holding.CumulativeRealizedPnlAfter,
                unrealizedPnl,
                holding.CumulativeFeesInQuoteAfter,
                markPrice,
                holding.OccurredAtUtc,
                markPriceSnapshot?.ObservedAtUtc));
        }

        return holdings;
    }

    private static decimal? ResolveAverageFillPrice(
        ExecutionOrder order,
        IReadOnlyCollection<SpotPortfolioFill>? spotLedgerRows)
    {
        if (spotLedgerRows is not { Count: > 0 })
        {
            return order.AverageFillPrice;
        }

        var filledQuantity = spotLedgerRows.Sum(entity => entity.Quantity);
        if (filledQuantity <= 0m)
        {
            return order.AverageFillPrice;
        }

        return NormalizeDecimal(spotLedgerRows.Sum(entity => entity.QuoteQuantity) / filledQuantity);
    }

    private static UserDashboardPositionSnapshot MapLivePositionSnapshot(ExchangePosition entity)
    {
        var normalizedQuantity = NormalizeDecimal(entity.Quantity);

        return new UserDashboardPositionSnapshot(
            entity.Symbol,
            entity.PositionSide,
            entity.Quantity,
            entity.EntryPrice,
            entity.BreakEvenPrice,
            entity.UnrealizedProfit,
            entity.MarginType,
            entity.IsolatedWallet,
            entity.ExchangeUpdatedAtUtc,
            entity.SyncedAtUtc,
            entity.Plane,
            null,
            ResolvePositionCostBasis(normalizedQuantity, entity.EntryPrice),
            ResolveLivePositionMarkPrice(normalizedQuantity, entity.EntryPrice, entity.UnrealizedProfit),
            null,
            null);
    }

    private static ExchangePosition? ResolveMatchingLivePosition(
        ExecutionOrder order,
        IReadOnlyCollection<ExchangePosition> livePositions)
    {
        if (order.Plane == ExchangeDataPlane.Spot || livePositions.Count == 0)
        {
            return null;
        }

        var preferredPositionSide = order.Side == ExecutionOrderSide.Sell ? "SHORT" : "LONG";

        return livePositions
            .Where(entity =>
                entity.Plane == order.Plane &&
                string.Equals(entity.Symbol, order.Symbol, StringComparison.Ordinal) &&
                entity.Quantity != 0m &&
                (!order.ExchangeAccountId.HasValue || entity.ExchangeAccountId == order.ExchangeAccountId.Value))
            .OrderByDescending(entity => string.Equals(entity.PositionSide, preferredPositionSide, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(entity => Math.Abs(entity.Quantity))
            .ThenByDescending(entity => entity.ExchangeUpdatedAtUtc)
            .ThenByDescending(entity => entity.Id)
            .FirstOrDefault();
    }

    private static bool IsClosingTrade(ExecutionOrder order)
    {
        if (order.Plane == ExchangeDataPlane.Spot)
        {
            return order.Side == ExecutionOrderSide.Sell;
        }

        return order.ReduceOnly || order.SignalType == StrategySignalType.Exit;
    }

    private static string ResolveTradeDirectionLabel(ExecutionOrder order)
    {
        if (IsClosingTrade(order))
        {
            return order.Side == ExecutionOrderSide.Sell ? "Long" : "Short";
        }

        return order.Side == ExecutionOrderSide.Buy ? "Long" : "Short";
    }

    private static string ResolveTradeActionLabel(ExecutionOrder order, string directionLabel)
    {
        return $"{directionLabel} {(IsClosingTrade(order) ? "Exit" : "Entry")}";
    }

    private static decimal? ResolvePositionCostBasis(decimal quantity, decimal entryPrice)
    {
        return quantity == 0m
            ? 0m
            : NormalizeDecimal(Math.Abs(quantity * entryPrice));
    }

    private static decimal? ResolveLivePositionMarkPrice(decimal quantity, decimal entryPrice, decimal unrealizedProfit)
    {
        if (quantity == 0m)
        {
            return null;
        }

        return NormalizeDecimal(entryPrice + (unrealizedProfit / quantity));
    }

    private static string BuildFuturesExecutionResultSummary(
        ExecutionOrder order,
        ExecutionOrderTransition? latestTransition)
    {
        var failureDetail = SanitizeDetail(order.FailureDetail);
        var transitionDetail = SanitizeDetail(latestTransition?.Detail);
        var exchangeStatus = ResolveFuturesExchangeStatus(order, latestTransition);
        var executedQuantity = ExtractDetailDecimal(latestTransition?.Detail, "ExecutedQuantity") ?? order.FilledQuantity;
        var cumulativeQuoteQuantity = ExtractDetailDecimal(latestTransition?.Detail, "CumulativeQuoteQuantity");
        var tradeId = ExtractDetailToken(latestTransition?.Detail, "TradeId");
        var fee = ExtractDetailToken(latestTransition?.Detail, "Fee");
        var reconciliationStatus = ResolveFuturesReconciliationStatus(order, latestTransition);
        var reconciliationSummary = ResolveFuturesReconciliationSummary(order, latestTransition);

        if (string.IsNullOrWhiteSpace(failureDetail) &&
            !string.IsNullOrWhiteSpace(transitionDetail) &&
            string.IsNullOrWhiteSpace(ExtractDetailToken(latestTransition?.Detail, "ExchangeStatus")) &&
            string.IsNullOrWhiteSpace(ExtractDetailToken(latestTransition?.Detail, "ExecutedQuantity")) &&
            string.IsNullOrWhiteSpace(tradeId) &&
            string.IsNullOrWhiteSpace(ExtractDetailToken(latestTransition?.Detail, "ReconciliationStatus")) &&
            string.IsNullOrWhiteSpace(ExtractDetailToken(latestTransition?.Detail, "ReconciliationSummary")))
        {
            return transitionDetail;
        }

        var summary = string.IsNullOrWhiteSpace(failureDetail)
            ? $"Plane={order.Plane}; ExchangeStatus={exchangeStatus}"
            : failureDetail;
        if (executedQuantity > 0m)
        {
            summary += $"; ExecutedQuantity={executedQuantity.ToString("0.####", CultureInfo.InvariantCulture)}";
        }
        if (cumulativeQuoteQuantity.HasValue)
        {
            summary += $"; CumulativeQuoteQuantity={cumulativeQuoteQuantity.Value.ToString("0.####", CultureInfo.InvariantCulture)}";
        }
        if (!string.IsNullOrWhiteSpace(tradeId))
        {
            summary += $"; TradeId={tradeId}";
        }
        if (!string.IsNullOrWhiteSpace(fee))
        {
            summary += $"; Fee={fee}";
        }
        if (!string.IsNullOrWhiteSpace(reconciliationStatus))
        {
            summary += $"; ReconciliationStatus={reconciliationStatus}";
        }
        if (!string.IsNullOrWhiteSpace(reconciliationSummary))
        {
            summary += $"; ReconciliationSummary={reconciliationSummary}";
        }
        return Truncate(summary, 512) ?? summary;
    }
    private static string ResolveFuturesExchangeStatus(
        ExecutionOrder order,
        ExecutionOrderTransition? latestTransition)
    {
        return ExtractDetailToken(latestTransition?.Detail, "ExchangeStatus") ?? order.State switch
        {
            ExecutionOrderState.Filled => "FILLED",
            ExecutionOrderState.PartiallyFilled => "PARTIALLY_FILLED",
            ExecutionOrderState.CancelRequested => "PENDING_CANCEL",
            ExecutionOrderState.Cancelled => "CANCELED",
            ExecutionOrderState.Rejected => "REJECTED",
            ExecutionOrderState.Failed => "FAILED",
            ExecutionOrderState.Submitted => "NEW",
            _ => order.State.ToString().ToUpperInvariant()
        };
    }
    private static string? ResolveFuturesReconciliationStatus(
        ExecutionOrder order,
        ExecutionOrderTransition? latestTransition)
    {
        return order.ReconciliationStatus != ExchangeStateDriftStatus.Unknown
            ? order.ReconciliationStatus.ToString()
            : ExtractDetailToken(latestTransition?.Detail, "ReconciliationStatus");
    }
    private static string? ResolveFuturesReconciliationSummary(
        ExecutionOrder order,
        ExecutionOrderTransition? latestTransition)
    {
        return NormalizeOptional(order.ReconciliationSummary)
            ?? ExtractDetailToken(latestTransition?.Detail, "ReconciliationSummary");
    }
    private static decimal? ResolveFuturesFeeAmountInQuote(
        ExecutionOrder order,
        ExecutionOrderTransition? latestTransition)
    {
        if (order.Plane == ExchangeDataPlane.Spot)
        {
            return null;
        }

        var feeToken = ExtractDetailToken(latestTransition?.Detail, "Fee");
        if (string.IsNullOrWhiteSpace(feeToken))
        {
            return null;
        }

        var separatorIndex = feeToken.IndexOf(':');
        if (separatorIndex <= 0)
        {
            return null;
        }

        var feeAsset = NormalizeOptional(feeToken[..separatorIndex]);
        if (!string.Equals(feeAsset, NormalizeOptional(order.QuoteAsset), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return decimal.TryParse(
            feeToken[(separatorIndex + 1)..],
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var parsedValue)
            ? NormalizeDecimal(parsedValue)
            : null;
    }

    private static string BuildFuturesPlaneSummary(
        ExecutionOrder order,
        ExecutionOrderTransition? latestTransition)
    {
        var tradeId = ExtractDetailToken(latestTransition?.Detail, "TradeId");
        var executedQuantity = ExtractDetailDecimal(latestTransition?.Detail, "ExecutedQuantity");
        var cumulativeQuoteQuantity = ExtractDetailDecimal(latestTransition?.Detail, "CumulativeQuoteQuantity");
        var fee = ExtractDetailToken(latestTransition?.Detail, "Fee");
        var reconciliationStatus = ResolveFuturesReconciliationStatus(order, latestTransition);
        var reconciliationSummary = ResolveFuturesReconciliationSummary(order, latestTransition);

        var summary = $"Plane={order.Plane}";

        if (executedQuantity.HasValue)
        {
            summary += $"; ExecutedQuantity={executedQuantity.Value.ToString("0.####", CultureInfo.InvariantCulture)}";
        }

        if (cumulativeQuoteQuantity.HasValue)
        {
            summary += $"; CumulativeQuoteQuantity={cumulativeQuoteQuantity.Value.ToString("0.####", CultureInfo.InvariantCulture)}";
        }

        if (!string.IsNullOrWhiteSpace(tradeId))
        {
            summary += $"; TradeIds={tradeId}";
        }

        if (!string.IsNullOrWhiteSpace(fee))
        {
            summary += $"; Fee={fee}";
        }

        if (!string.IsNullOrWhiteSpace(reconciliationStatus))
        {
            summary += $"; ReconciliationStatus={reconciliationStatus}";
        }

        if (!string.IsNullOrWhiteSpace(reconciliationSummary))
        {
            summary += $"; ReconciliationSummary={reconciliationSummary}";
        }

        return summary;
    }

    private static decimal? ExtractDetailDecimal(string? detail, string key)
    {
        var token = ExtractDetailToken(detail, key);
        if (token is null)
        {
            return null;
        }

        return decimal.TryParse(token, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedValue)
            ? NormalizeDecimal(parsedValue)
            : null;
    }

    private static string? ExtractDetailToken(string? detail, string key)
    {
        return ExecutionDecisionDiagnostics.ExtractToken(key, detail);
    }

    private static string? BuildTradeIdsSummary(IEnumerable<SpotPortfolioFill>? rows)
    {
        if (rows is null)
        {
            return null;
        }

        var tradeIds = rows
            .Select(entity => entity.TradeId.ToString(CultureInfo.InvariantCulture))
            .ToArray();

        return tradeIds.Length == 0
            ? null
            : Truncate(string.Join(",", tradeIds), 128);
    }

    private static string CreateSpotHoldingKey(Guid exchangeAccountId, string symbol)
    {
        return $"{exchangeAccountId:N}:{symbol.Trim().ToUpperInvariant()}";
    }

    private static decimal ResolveLockedQuantity(
        UserDashboardBalanceSnapshot? balance,
        decimal holdingQuantity)
    {
        if (balance?.LockedBalance is not decimal lockedBalance || lockedBalance <= 0m)
        {
            return 0m;
        }

        return NormalizeDecimal(Math.Min(holdingQuantity, Math.Max(0m, lockedBalance)));
    }

    private static decimal NormalizeDecimal(decimal value)
    {
        return Math.Abs(value) <= PrecisionEpsilon
            ? 0m
            : value;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalizedValue = value?.Trim();
        return string.IsNullOrWhiteSpace(normalizedValue) ? null : normalizedValue;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}






