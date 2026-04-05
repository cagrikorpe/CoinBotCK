using CoinBot.Application.Abstractions.Dashboard;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace CoinBot.Infrastructure.Dashboard;

public sealed class UserDashboardPortfolioReadModelService(
    ApplicationDbContext dbContext) : IUserDashboardPortfolioReadModelService
{
    private const int TradeHistoryRowLimit = 50;
    private const decimal PnlConsistencyTolerance = 0.0001m;

    public async Task<UserDashboardPortfolioSnapshot> GetSnapshotAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var normalizedUserId = NormalizeRequired(userId, nameof(userId));
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
                entity.Plane))
            .ToListAsync(cancellationToken);

        var positions = activeAccounts.Count == 0
            ? new List<UserDashboardPositionSnapshot>()
            : await dbContext.ExchangePositions
            .AsNoTracking()
            .Where(entity =>
                entity.OwnerUserId == normalizedUserId &&
                activeAccounts.Contains(entity.ExchangeAccountId) &&
                !entity.IsDeleted)
            .OrderByDescending(entity => Math.Abs(entity.UnrealizedProfit))
            .ThenBy(entity => entity.Symbol)
            .Select(entity => new UserDashboardPositionSnapshot(
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
                entity.Plane))
            .ToListAsync(cancellationToken);

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
                demoPosition.LastFilledAtUtc ?? demoPosition.UpdatedDate));
        }

        var tradeHistory = await BuildTradeHistoryAsync(normalizedUserId, demoPositions, cancellationToken);
        var liveUnrealizedPnl = positions.Sum(entity => entity.UnrealizedProfit);
        var demoRealizedPnl = demoPositions.Sum(entity => entity.RealizedPnl);
        var ledgerRealizedPnl = await dbContext.DemoLedgerTransactions
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => entity.OwnerUserId == normalizedUserId && !entity.IsDeleted)
            .SumAsync(entity => entity.RealizedPnlDelta ?? 0m, cancellationToken);
        var totalRealizedPnl = demoRealizedPnl;
        var totalUnrealizedPnl = liveUnrealizedPnl;
        var totalPnl = totalRealizedPnl + totalUnrealizedPnl;
        var pnlConsistencySummary = BuildPnlConsistencySummary(totalRealizedPnl, ledgerRealizedPnl, totalUnrealizedPnl, totalPnl);

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
            tradeHistory);
    }

    private async Task<IReadOnlyCollection<UserDashboardTradeHistoryRowSnapshot>> BuildTradeHistoryAsync(
        string ownerUserId,
        IReadOnlyCollection<DemoPosition> demoPositions,
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
            var realizedPnl = orderLedgerRows?.Sum(entity => entity.RealizedPnlDelta ?? 0m) ?? 0m;
            var feeAmountInQuote = orderLedgerRows?.Sum(entity => entity.FeeAmountInQuote ?? 0m);
            var costImpact = orderLedgerRows?.Sum(entity =>
                entity.Quantity.HasValue && entity.Price.HasValue
                    ? Math.Abs(entity.Quantity.Value * entity.Price.Value)
                    : 0m);
            var matchingOpenPosition = demoPositions.FirstOrDefault(entity =>
                entity.BotId == order.BotId &&
                string.Equals(entity.Symbol, order.Symbol, StringComparison.Ordinal) &&
                entity.Quantity != 0m);

            tradeRows.Add(new UserDashboardTradeHistoryRowSnapshot(
                order.Id,
                SanitizeToken(ResolveClientOrderId(order, latestTransition)),
                SanitizeToken(order.RootCorrelationId) ?? "n/a",
                order.Symbol,
                order.Timeframe,
                order.Side.ToString(),
                order.Quantity,
                order.AverageFillPrice,
                realizedPnl,
                matchingOpenPosition?.UnrealizedPnl,
                feeAmountInQuote,
                costImpact,
                order.CreatedDate,
                IsTerminalState(order.State) ? order.LastStateChangedAtUtc : null,
                order.LastStateChangedAtUtc,
                order.State.ToString(),
                ResolveExecutionResultCategory(order.State),
                ResolveExecutionResultCode(order, latestTransition),
                ResolveExecutionResultSummary(order, latestTransition),
                order.RejectionStage.ToString(),
                order.SubmittedToBroker,
                order.RetryEligible,
                order.CooldownApplied,
                BuildReasonChainSummary(handoffAttempt, order, latestTransition, decisionTraceCount, executionTraceCount, auditCount, botName),
                AiScoreAvailable: false,
                AiScoreValue: null,
                AiScoreLabel: "AI score placeholder",
                AiScoreSummary: "AI score snapshot placeholder contract is present; no model score has been generated for this order.",
                AiScoreSource: "portfolio-history-placeholder",
                AiScoreGeneratedAtUtc: order.LastStateChangedAtUtc,
                AiScoreIsPlaceholder: true));
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

    private static string BuildReasonChainSummary(
        MarketScannerHandoffAttempt? handoffAttempt,
        ExecutionOrder order,
        ExecutionOrderTransition? latestTransition,
        int decisionTraceCount,
        int executionTraceCount,
        int auditCount,
        string? botName)
    {
        var scannerSummary = handoffAttempt?.SelectionReason ?? "ScannerLink=Missing";
        var strategySummary = handoffAttempt is null
            ? "StrategyLink=Missing"
            : $"StrategyOutcome={NormalizeOptional(handoffAttempt.StrategyDecisionOutcome) ?? "n/a"}; StrategyScore={handoffAttempt.StrategyScore?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}; StrategyVeto={NormalizeOptional(handoffAttempt.StrategyVetoReasonCode) ?? "None"}";
        var riskSummary = handoffAttempt is null
            ? "RiskLink=Missing"
            : $"RiskOutcome={NormalizeOptional(handoffAttempt.RiskOutcome) ?? "n/a"}; RiskVeto={NormalizeOptional(handoffAttempt.RiskVetoReasonCode) ?? "None"}; RiskSummary={SanitizeDetail(handoffAttempt.RiskSummary) ?? "n/a"}";
        var executionSummary = $"ExecutionState={order.State}; ResultCode={ResolveExecutionResultCode(order, latestTransition)}; Stage={order.RejectionStage}; Submitted={order.SubmittedToBroker}; Retry={order.RetryEligible}; Cooldown={order.CooldownApplied}";
        var auditSummary = $"AuditTrail=DecisionTrace:{decisionTraceCount}; ExecutionTrace:{executionTraceCount}; AuditLog:{auditCount}; Bot={NormalizeOptional(botName) ?? "n/a"}";

        return Truncate(
            $"{scannerSummary} | {strategySummary} | {riskSummary} | {executionSummary} | {auditSummary}",
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
