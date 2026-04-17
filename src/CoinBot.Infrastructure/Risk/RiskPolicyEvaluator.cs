using System.Globalization;
using System.Text.Json;
using CoinBot.Application.Abstractions.Risk;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Risk;

public sealed class RiskPolicyEvaluator(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<RiskPolicyEvaluator> logger) : IRiskPolicyEvaluator
{
    private static readonly string[] QuoteAssetSuffixes =
    [
        "USDT",
        "FDUSD",
        "USDC",
        "BUSD",
        "TRY",
        "EUR",
        "BTC",
        "ETH",
        "BNB"
    ];

    public async Task<RiskVetoResult> EvaluateAsync(
        RiskPolicyEvaluationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedUserId = NormalizeRequired(request.OwnerUserId, nameof(request.OwnerUserId));
        var normalizedSymbol = NormalizeRequired(request.Symbol, nameof(request.Symbol)).ToUpperInvariant();
        var normalizedTimeframe = NormalizeRequired(request.Timeframe, nameof(request.Timeframe));
        var normalizedBaseAsset = ResolveBaseAsset(normalizedSymbol);

        using var riskActivity = CoinBotActivity.StartActivity("CoinBot.Risk.Policy");
        riskActivity.SetTag("coinbot.risk.strategy_id", request.TradingStrategyId.ToString());
        riskActivity.SetTag("coinbot.risk.strategy_version_id", request.TradingStrategyVersionId.ToString());
        riskActivity.SetTag("coinbot.risk.signal_type", request.SignalType.ToString());
        riskActivity.SetTag("coinbot.risk.environment", request.Environment.ToString());
        riskActivity.SetTag("coinbot.risk.symbol", normalizedSymbol);
        riskActivity.SetTag("coinbot.risk.timeframe", normalizedTimeframe);

        var evaluatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var isVirtualCheck = request.Environment == ExecutionEnvironment.Demo;

        try
        {
            var riskProfile = await dbContext.RiskProfiles
                .IgnoreQueryFilters()
                .Where(entity => entity.OwnerUserId == normalizedUserId && !entity.IsDeleted)
                .OrderByDescending(entity => entity.UpdatedDate)
                .ThenByDescending(entity => entity.CreatedDate)
                .ThenByDescending(entity => entity.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (riskProfile is null)
            {
                var missingProfileSnapshot = CreateEmptySnapshot(
                    request,
                    normalizedUserId,
                    normalizedSymbol,
                    normalizedBaseAsset,
                    normalizedTimeframe,
                    isVirtualCheck,
                    evaluatedAtUtc);

                return new RiskVetoResult(
                    true,
                    RiskVetoReasonCode.RiskProfileMissing,
                    missingProfileSnapshot,
                    BuildReasonSummary(RiskVetoReasonCode.RiskProfileMissing, missingProfileSnapshot));
            }

            var coinSpecificLimits = ParseCoinSpecificLimits(riskProfile.CoinSpecificExposureLimitsJson);
            var snapshot = request.Environment == ExecutionEnvironment.Demo
                ? await BuildDemoSnapshotAsync(
                    normalizedUserId,
                    normalizedSymbol,
                    normalizedBaseAsset,
                    normalizedTimeframe,
                    request,
                    riskProfile,
                    coinSpecificLimits,
                    evaluatedAtUtc,
                    cancellationToken)
                : await BuildLiveSnapshotAsync(
                    normalizedUserId,
                    normalizedSymbol,
                    normalizedBaseAsset,
                    normalizedTimeframe,
                    request,
                    riskProfile,
                    coinSpecificLimits,
                    evaluatedAtUtc,
                    cancellationToken);

            var reasonCode = ResolveReasonCode(snapshot);
            riskActivity.SetTag("coinbot.risk.reason", reasonCode.ToString());
            riskActivity.SetTag("coinbot.risk.is_vetoed", reasonCode != RiskVetoReasonCode.None);

            return new RiskVetoResult(
                reasonCode != RiskVetoReasonCode.None,
                reasonCode,
                snapshot,
                BuildReasonSummary(reasonCode, snapshot));
        }
        catch (InvalidOperationException exception) when (
            string.Equals(exception.Message, "RiskProfileConfigurationInvalid", StringComparison.Ordinal))
        {
            var snapshot = CreateEmptySnapshot(
                request,
                normalizedUserId,
                normalizedSymbol,
                normalizedBaseAsset,
                normalizedTimeframe,
                isVirtualCheck,
                evaluatedAtUtc);

            riskActivity.SetTag("coinbot.risk.reason", RiskVetoReasonCode.RiskProfileConfigurationInvalid.ToString());
            riskActivity.SetTag("coinbot.risk.is_vetoed", true);

            return new RiskVetoResult(
                true,
                RiskVetoReasonCode.RiskProfileConfigurationInvalid,
                snapshot,
                BuildReasonSummary(RiskVetoReasonCode.RiskProfileConfigurationInvalid, snapshot));
        }
        catch (Exception exception)
        {
            riskActivity.SetTag("coinbot.risk.reason", RiskVetoReasonCode.AccountEquityUnavailable.ToString());
            riskActivity.SetTag("coinbot.risk.is_vetoed", true);
            logger.LogWarning(
                exception,
                "Risk policy evaluation failed closed for StrategyVersionId {StrategyVersionId}, SignalType {SignalType}, Symbol {Symbol}, Environment {Environment}.",
                request.TradingStrategyVersionId,
                request.SignalType,
                normalizedSymbol,
                request.Environment);

            var fallbackSnapshot = CreateEmptySnapshot(
                request,
                normalizedUserId,
                normalizedSymbol,
                normalizedBaseAsset,
                normalizedTimeframe,
                isVirtualCheck,
                evaluatedAtUtc);

            return new RiskVetoResult(
                true,
                RiskVetoReasonCode.AccountEquityUnavailable,
                fallbackSnapshot,
                BuildReasonSummary(RiskVetoReasonCode.AccountEquityUnavailable, fallbackSnapshot));
        }
    }

    private async Task<PreTradeRiskSnapshot> BuildDemoSnapshotAsync(
        string ownerUserId,
        string symbol,
        string baseAsset,
        string timeframe,
        RiskPolicyEvaluationRequest request,
        RiskProfile riskProfile,
        IReadOnlyDictionary<string, decimal> coinSpecificLimits,
        DateTime evaluatedAtUtc,
        CancellationToken cancellationToken)
    {
        var wallets = await dbContext.DemoWallets
            .Where(entity =>
                entity.OwnerUserId == ownerUserId &&
                !entity.IsDeleted &&
                entity.Asset == "USDT")
            .ToListAsync(cancellationToken);

        var positions = await dbContext.DemoPositions
            .Where(entity =>
                entity.OwnerUserId == ownerUserId &&
                !entity.IsDeleted &&
                entity.Quantity != 0m)
            .ToListAsync(cancellationToken);

        var transactions = await dbContext.DemoLedgerTransactions
            .Where(entity =>
                entity.OwnerUserId == ownerUserId &&
                !entity.IsDeleted)
            .ToListAsync(cancellationToken);

        var quoteBalance = wallets.Sum(entity => entity.AvailableBalance + entity.ReservedBalance);
        var grossExposure = positions.Sum(CalculateDemoPositionExposure);
        var equity = quoteBalance + positions.Sum(CalculateDemoPositionMarketValue);

        var (dailyLossAmount, weeklyLossAmount) = ResolveDemoLossAmounts(transactions, evaluatedAtUtc);
        var symbolExposure = positions
            .Where(entity => string.Equals(entity.Symbol, symbol, StringComparison.Ordinal))
            .Sum(CalculateDemoPositionExposure);
        var coinExposure = positions
            .Where(entity => string.Equals(NormalizeAsset(entity.BaseAsset), baseAsset, StringComparison.Ordinal))
            .Sum(CalculateDemoPositionExposure);
        var currentSymbolSignedQuantity = positions
            .Where(entity => string.Equals(entity.Symbol, symbol, StringComparison.Ordinal))
            .Sum(entity => entity.Quantity);
        var symbolHasOpenPosition = positions.Any(entity => string.Equals(entity.Symbol, symbol, StringComparison.Ordinal));

        return CreateSnapshot(
            request,
            ownerUserId,
            symbol,
            baseAsset,
            timeframe,
            isVirtualCheck: true,
            riskProfile,
            currentEquity: equity,
            currentGrossExposure: grossExposure,
            currentDailyLossAmount: dailyLossAmount,
            currentWeeklyLossAmount: weeklyLossAmount,
            currentSymbolExposureAmount: symbolExposure,
            currentCoinExposureAmount: coinExposure,
            openPositionCount: positions.Count,
            symbolHasOpenPosition,
            currentSymbolSignedQuantity,
            coinSpecificLimits,
            evaluatedAtUtc);
    }

    private async Task<PreTradeRiskSnapshot> BuildLiveSnapshotAsync(
        string ownerUserId,
        string symbol,
        string baseAsset,
        string timeframe,
        RiskPolicyEvaluationRequest request,
        RiskProfile riskProfile,
        IReadOnlyDictionary<string, decimal> coinSpecificLimits,
        DateTime evaluatedAtUtc,
        CancellationToken cancellationToken)
    {
        var balances = await dbContext.ExchangeBalances
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == ownerUserId &&
                entity.Plane == ExchangeDataPlane.Futures &&
                !entity.IsDeleted)
            .ToListAsync(cancellationToken);

        var positions = await dbContext.ExchangePositions
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == ownerUserId &&
                entity.Plane == ExchangeDataPlane.Futures &&
                !entity.IsDeleted &&
                entity.Quantity != 0m)
            .ToListAsync(cancellationToken);

        var projectedPositionRows = await LivePositionTruthResolver.ResolveProjectedPositionsAsync(
            dbContext,
            ownerUserId,
            ExchangeDataPlane.Futures,
            exchangeAccountId: null,
            cancellationToken);
        var fallbackExecutionPositions = positions.Count == 0
            ? projectedPositionRows
                .Select(row => (Symbol: row.Symbol, NetQuantity: row.NetQuantity, EntryPrice: row.ReferencePrice))
                .ToArray()
            : Array.Empty<(string Symbol, decimal NetQuantity, decimal EntryPrice)>();

        var equity = balances.Sum(entity =>
            entity.CrossWalletBalance != 0m
                ? entity.CrossWalletBalance
                : entity.WalletBalance);

        var normalizedSymbol = NormalizeSymbol(symbol);
        var grossExposure = positions.Count != 0
            ? positions.Sum(entity => Math.Abs(entity.EntryPrice * ResolveSignedPositionQuantity(entity)))
            : fallbackExecutionPositions.Sum(entity => Math.Abs(entity.EntryPrice * entity.NetQuantity));
        var currentDailyLossAmount = positions.Count != 0
            ? positions
                .Where(entity => entity.UnrealizedProfit < 0m && NormalizeUtc(entity.SyncedAtUtc) >= evaluatedAtUtc.Date)
                .Sum(entity => Math.Abs(entity.UnrealizedProfit))
            : 0m;
        var currentWeeklyLossAmount = positions.Count != 0
            ? positions
                .Where(entity => entity.UnrealizedProfit < 0m && NormalizeUtc(entity.SyncedAtUtc) >= ResolveIsoWeekStart(evaluatedAtUtc))
                .Sum(entity => Math.Abs(entity.UnrealizedProfit))
            : 0m;
        var symbolExposure = positions.Count != 0
            ? positions
                .Where(entity => string.Equals(NormalizeSymbol(entity.Symbol), normalizedSymbol, StringComparison.Ordinal))
                .Sum(entity => Math.Abs(entity.EntryPrice * ResolveSignedPositionQuantity(entity)))
            : fallbackExecutionPositions
                .Where(entity => string.Equals(entity.Symbol, normalizedSymbol, StringComparison.Ordinal))
                .Sum(entity => Math.Abs(entity.EntryPrice * entity.NetQuantity));
        var coinExposure = positions.Count != 0
            ? positions
                .Where(entity => string.Equals(ResolveBaseAsset(entity.Symbol), baseAsset, StringComparison.Ordinal))
                .Sum(entity => Math.Abs(entity.EntryPrice * ResolveSignedPositionQuantity(entity)))
            : fallbackExecutionPositions
                .Where(entity => string.Equals(ResolveBaseAsset(entity.Symbol), baseAsset, StringComparison.Ordinal))
                .Sum(entity => Math.Abs(entity.EntryPrice * entity.NetQuantity));
        var currentSymbolSignedQuantity = projectedPositionRows
            .Where(entity => string.Equals(entity.Symbol, normalizedSymbol, StringComparison.Ordinal))
            .Sum(entity => entity.NetQuantity);
        var symbolHasOpenPosition = currentSymbolSignedQuantity != 0m;
        var openPositionCount = projectedPositionRows.Count;

        return CreateSnapshot(
            request,
            ownerUserId,
            symbol,
            baseAsset,
            timeframe,
            isVirtualCheck: false,
            riskProfile,
            currentEquity: equity,
            currentGrossExposure: grossExposure,
            currentDailyLossAmount,
            currentWeeklyLossAmount,
            symbolExposure,
            coinExposure,
            openPositionCount,
            symbolHasOpenPosition,
            currentSymbolSignedQuantity,
            coinSpecificLimits,
            evaluatedAtUtc);
    }

    private static decimal ResolveSignedOrderQuantity(ExecutionOrder entity)
    {
        var quantity = entity.FilledQuantity;
        if (quantity == 0m)
        {
            return 0m;
        }

        return entity.Side == ExecutionOrderSide.Buy
            ? quantity
            : -quantity;
    }

    private static decimal ResolveSignedPositionQuantity(ExchangePosition entity)
    {
        var quantity = entity.Quantity;
        if (quantity == 0m)
        {
            return 0m;
        }

        return NormalizePositionSide(entity.PositionSide) == "SHORT"
            ? -Math.Abs(quantity)
            : quantity;
    }

    private static string NormalizePositionSide(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "BOTH"
            : value.Trim().ToUpperInvariant();
    }

    private static string NormalizeSymbol(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToUpperInvariant();
    }

    private static PreTradeRiskSnapshot CreateSnapshot(
        RiskPolicyEvaluationRequest request,
        string ownerUserId,
        string symbol,
        string baseAsset,
        string timeframe,
        bool isVirtualCheck,
        RiskProfile riskProfile,
        decimal currentEquity,
        decimal currentGrossExposure,
        decimal currentDailyLossAmount,
        decimal currentWeeklyLossAmount,
        decimal currentSymbolExposureAmount,
        decimal currentCoinExposureAmount,
        int openPositionCount,
        bool symbolHasOpenPosition,
        decimal currentSymbolSignedQuantity,
        IReadOnlyDictionary<string, decimal> coinSpecificLimits,
        DateTime evaluatedAtUtc)
    {
        var requestedQuantity = request.Quantity is > 0m ? request.Quantity.Value : 0m;
        var requestedPrice = request.Price is > 0m ? request.Price.Value : 0m;
        var requestedNotional = requestedQuantity > 0m && requestedPrice > 0m
            ? Math.Abs(requestedQuantity * requestedPrice)
            : 0m;
        var requestedSignedQuantity = ResolveRequestedSignedQuantity(request.Side, requestedQuantity);
        var requestOpposesCurrentSymbolPosition = requestedNotional > 0m &&
            currentSymbolExposureAmount > 0m &&
            currentSymbolSignedQuantity != 0m &&
            requestedSignedQuantity != 0m &&
            Math.Sign(currentSymbolSignedQuantity) != Math.Sign(requestedSignedQuantity);
        var projectedSymbolExposureAmount = requestOpposesCurrentSymbolPosition
            ? Math.Abs(currentSymbolExposureAmount - requestedNotional)
            : currentSymbolExposureAmount + requestedNotional;
        var projectedGrossExposure = requestOpposesCurrentSymbolPosition
            ? Math.Max(0m, currentGrossExposure - currentSymbolExposureAmount) + projectedSymbolExposureAmount
            : currentGrossExposure + requestedNotional;
        var projectedCoinExposureAmount = requestOpposesCurrentSymbolPosition
            ? Math.Max(0m, currentCoinExposureAmount - currentSymbolExposureAmount) + projectedSymbolExposureAmount
            : currentCoinExposureAmount + requestedNotional;
        var currentLeverage = ResolvePercentageRatio(currentGrossExposure, currentEquity, multiplier: 1m);
        var projectedLeverage = ResolvePercentageRatio(projectedGrossExposure, currentEquity, multiplier: 1m);
        var currentExposurePercentage = ResolvePercentageRatio(currentGrossExposure, currentEquity, multiplier: 100m);
        var projectedExposurePercentage = ResolvePercentageRatio(projectedGrossExposure, currentEquity, multiplier: 100m);
        var currentDailyLossPercentage = ResolvePercentageRatio(currentDailyLossAmount, currentEquity, multiplier: 100m);
        var currentWeeklyLossPercentage = ResolvePercentageRatio(currentWeeklyLossAmount, currentEquity, multiplier: 100m);
        var currentSymbolExposurePercentage = ResolvePercentageRatio(currentSymbolExposureAmount, currentEquity, multiplier: 100m);
        var projectedSymbolExposurePercentage = ResolvePercentageRatio(projectedSymbolExposureAmount, currentEquity, multiplier: 100m);
        var currentCoinExposurePercentage = ResolvePercentageRatio(currentCoinExposureAmount, currentEquity, multiplier: 100m);
        var projectedCoinExposurePercentage = ResolvePercentageRatio(projectedCoinExposureAmount, currentEquity, multiplier: 100m);
        var maxWeeklyLossPercentage = riskProfile.MaxWeeklyLossPercentage ?? (riskProfile.MaxDailyLossPercentage * 5m);
        var maxSymbolExposurePercentage = riskProfile.MaxSymbolExposurePercentage;
        var maxConcurrentPositions = riskProfile.MaxConcurrentPositions;
        decimal? maxCoinExposurePercentage = coinSpecificLimits.TryGetValue(baseAsset, out var coinLimit)
            ? coinLimit
            : null;
        var projectedOpenPositionCount = requestedNotional <= 0m
            ? openPositionCount
            : !symbolHasOpenPosition
                ? openPositionCount + 1
                : requestOpposesCurrentSymbolPosition && projectedSymbolExposureAmount == 0m
                    ? Math.Max(0, openPositionCount - 1)
                    : openPositionCount;

        return new PreTradeRiskSnapshot(
            IsVirtualCheck: isVirtualCheck,
            RiskProfileId: riskProfile.Id,
            RiskProfileName: riskProfile.ProfileName,
            KillSwitchEnabled: riskProfile.KillSwitchEnabled,
            CurrentEquity: currentEquity,
            CurrentGrossExposure: currentGrossExposure,
            CurrentLeverage: currentLeverage,
            CurrentExposurePercentage: currentExposurePercentage,
            CurrentDailyLossAmount: currentDailyLossAmount,
            CurrentDailyLossPercentage: currentDailyLossPercentage,
            MaxDailyLossPercentage: riskProfile.MaxDailyLossPercentage,
            MaxExposurePercentage: riskProfile.MaxPositionSizePercentage,
            MaxLeverage: riskProfile.MaxLeverage,
            OpenPositionCount: openPositionCount,
            EvaluatedAtUtc: evaluatedAtUtc,
            OwnerUserId: ownerUserId,
            BotId: request.BotId,
            Symbol: symbol,
            BaseAsset: baseAsset,
            Timeframe: timeframe,
            Side: request.Side,
            RequestedQuantity: request.Quantity,
            RequestedPrice: request.Price,
            RequestedNotional: requestedNotional,
            CurrentWeeklyLossAmount: currentWeeklyLossAmount,
            CurrentWeeklyLossPercentage: currentWeeklyLossPercentage,
            MaxWeeklyLossPercentage: maxWeeklyLossPercentage,
            ProjectedGrossExposure: projectedGrossExposure,
            ProjectedLeverage: projectedLeverage,
            ProjectedExposurePercentage: projectedExposurePercentage,
            CurrentSymbolExposureAmount: currentSymbolExposureAmount,
            ProjectedSymbolExposureAmount: projectedSymbolExposureAmount,
            CurrentSymbolExposurePercentage: currentSymbolExposurePercentage,
            ProjectedSymbolExposurePercentage: projectedSymbolExposurePercentage,
            MaxSymbolExposurePercentage: maxSymbolExposurePercentage,
            ProjectedOpenPositionCount: projectedOpenPositionCount,
            MaxConcurrentPositions: maxConcurrentPositions,
            CurrentCoinExposureAmount: currentCoinExposureAmount,
            ProjectedCoinExposureAmount: projectedCoinExposureAmount,
            CurrentCoinExposurePercentage: currentCoinExposurePercentage,
            ProjectedCoinExposurePercentage: projectedCoinExposurePercentage,
            MaxCoinExposurePercentage: maxCoinExposurePercentage);
    }

    private static PreTradeRiskSnapshot CreateEmptySnapshot(
        RiskPolicyEvaluationRequest request,
        string ownerUserId,
        string symbol,
        string baseAsset,
        string timeframe,
        bool isVirtualCheck,
        DateTime evaluatedAtUtc)
    {
        return new PreTradeRiskSnapshot(
            isVirtualCheck,
            null,
            null,
            false,
            0m,
            0m,
            0m,
            0m,
            0m,
            0m,
            null,
            null,
            null,
            0,
            evaluatedAtUtc,
            ownerUserId,
            request.BotId,
            symbol,
            baseAsset,
            timeframe,
            request.Side,
            request.Quantity,
            request.Price,
            0m,
            0m,
            0m,
            null,
            0m,
            0m,
            0m,
            0m,
            0m,
            0m,
            0m,
            null,
            0,
            null,
            0m,
            0m,
            0m,
            0m,
            null);
    }

    private static RiskVetoReasonCode ResolveReasonCode(PreTradeRiskSnapshot snapshot)
    {
        if (snapshot.KillSwitchEnabled)
        {
            return RiskVetoReasonCode.KillSwitchEnabled;
        }

        if (snapshot.CurrentEquity <= 0m)
        {
            return RiskVetoReasonCode.AccountEquityUnavailable;
        }

        if (snapshot.MaxDailyLossPercentage.HasValue &&
            snapshot.CurrentDailyLossPercentage > snapshot.MaxDailyLossPercentage.Value)
        {
            return RiskVetoReasonCode.DailyLossLimitBreached;
        }

        if (snapshot.MaxWeeklyLossPercentage.HasValue &&
            snapshot.CurrentWeeklyLossPercentage > snapshot.MaxWeeklyLossPercentage.Value)
        {
            return RiskVetoReasonCode.WeeklyLossLimitBreached;
        }

        if (snapshot.MaxConcurrentPositions.HasValue &&
            snapshot.ProjectedOpenPositionCount > snapshot.MaxConcurrentPositions.Value)
        {
            return RiskVetoReasonCode.MaxConcurrentPositionsBreached;
        }

        if (snapshot.MaxSymbolExposurePercentage.HasValue &&
            snapshot.ProjectedSymbolExposurePercentage > snapshot.MaxSymbolExposurePercentage.Value)
        {
            return RiskVetoReasonCode.SymbolExposureLimitBreached;
        }

        if (snapshot.MaxCoinExposurePercentage.HasValue &&
            snapshot.ProjectedCoinExposurePercentage > snapshot.MaxCoinExposurePercentage.Value)
        {
            return RiskVetoReasonCode.CoinSpecificLimitBreached;
        }

        if (snapshot.MaxLeverage.HasValue &&
            snapshot.ProjectedLeverage > snapshot.MaxLeverage.Value)
        {
            return RiskVetoReasonCode.LeverageLimitBreached;
        }

        if (snapshot.MaxExposurePercentage.HasValue &&
            snapshot.ProjectedExposurePercentage > snapshot.MaxExposurePercentage.Value)
        {
            return RiskVetoReasonCode.ExposureLimitBreached;
        }

        return RiskVetoReasonCode.None;
    }

    private static string BuildReasonSummary(RiskVetoReasonCode reasonCode, PreTradeRiskSnapshot snapshot)
    {
        var scope = $"Scope=User:{snapshot.OwnerUserId ?? "n/a"};Bot:{snapshot.BotId?.ToString("N") ?? "n/a"};Symbol:{snapshot.Symbol ?? "n/a"};Coin:{snapshot.BaseAsset ?? "n/a"};Timeframe:{snapshot.Timeframe ?? "n/a"}";
        var summary = FormattableString.Invariant(
            $"Reason={reasonCode}; {scope}; DailyLoss={snapshot.CurrentDailyLossPercentage:0.####}/{FormatLimit(snapshot.MaxDailyLossPercentage)}%; WeeklyLoss={snapshot.CurrentWeeklyLossPercentage:0.####}/{FormatLimit(snapshot.MaxWeeklyLossPercentage)}%; Leverage={snapshot.CurrentLeverage:0.####}->{snapshot.ProjectedLeverage:0.####}/{FormatLimit(snapshot.MaxLeverage)}x; PortfolioExposure={snapshot.CurrentExposurePercentage:0.####}->{snapshot.ProjectedExposurePercentage:0.####}/{FormatLimit(snapshot.MaxExposurePercentage)}%; SymbolExposure={snapshot.CurrentSymbolExposurePercentage:0.####}->{snapshot.ProjectedSymbolExposurePercentage:0.####}/{FormatLimit(snapshot.MaxSymbolExposurePercentage)}%; OpenPositions={snapshot.OpenPositionCount}->{snapshot.ProjectedOpenPositionCount}/{snapshot.MaxConcurrentPositions?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}; CoinExposure[{snapshot.BaseAsset ?? "n/a"}]={snapshot.CurrentCoinExposurePercentage:0.####}->{snapshot.ProjectedCoinExposurePercentage:0.####}/{FormatLimit(snapshot.MaxCoinExposurePercentage)}%.");

        return summary.Length <= 512 ? summary : summary[..512];
    }

    private static (decimal DailyLossAmount, decimal WeeklyLossAmount) ResolveDemoLossAmounts(
        IEnumerable<DemoLedgerTransaction> transactions,
        DateTime evaluatedAtUtc)
    {
        var utcDayStart = evaluatedAtUtc.Date;
        var utcDayEnd = utcDayStart.AddDays(1);
        var utcWeekStart = ResolveIsoWeekStart(evaluatedAtUtc);
        var utcWeekEnd = utcWeekStart.AddDays(7);

        var dailyLossAmount = transactions
            .Where(entity =>
                NormalizeUtc(entity.OccurredAtUtc) >= utcDayStart &&
                NormalizeUtc(entity.OccurredAtUtc) < utcDayEnd &&
                entity.RealizedPnlDelta.HasValue &&
                entity.RealizedPnlDelta.Value < 0m)
            .Sum(entity => -entity.RealizedPnlDelta!.Value);
        var weeklyLossAmount = transactions
            .Where(entity =>
                NormalizeUtc(entity.OccurredAtUtc) >= utcWeekStart &&
                NormalizeUtc(entity.OccurredAtUtc) < utcWeekEnd &&
                entity.RealizedPnlDelta.HasValue &&
                entity.RealizedPnlDelta.Value < 0m)
            .Sum(entity => -entity.RealizedPnlDelta!.Value);

        return (dailyLossAmount, weeklyLossAmount);
    }

    private static IReadOnlyDictionary<string, decimal> ParseCoinSpecificLimits(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, decimal>(StringComparer.Ordinal);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, decimal>>(json)
                ?? new Dictionary<string, decimal>();

            return parsed
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && entry.Value >= 0m)
                .ToDictionary(
                    entry => NormalizeAsset(entry.Key),
                    entry => entry.Value,
                    StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("RiskProfileConfigurationInvalid");
        }
    }

    private static DateTime ResolveIsoWeekStart(DateTime evaluatedAtUtc)
    {
        var utcDayStart = evaluatedAtUtc.Date;
        var delta = ((7 + (int)utcDayStart.DayOfWeek - (int)DayOfWeek.Monday) % 7);
        return utcDayStart.AddDays(-delta);
    }

    private static decimal ResolvePercentageRatio(decimal numerator, decimal denominator, decimal multiplier)
    {
        return denominator > 0m
            ? (numerator / denominator) * multiplier
            : 0m;
    }


    private static decimal ResolveRequestedSignedQuantity(ExecutionOrderSide? side, decimal quantity)
    {
        if (quantity <= 0m || side is null)
        {
            return 0m;
        }

        return side == ExecutionOrderSide.Buy
            ? quantity
            : -quantity;
    }

    private static decimal CalculateDemoPositionExposure(DemoPosition position)
    {
        if (position.PositionKind == DemoPositionKind.Futures)
        {
            if (position.LastMarkPrice is decimal futuresMarkPrice && futuresMarkPrice > 0m)
            {
                return Math.Abs(futuresMarkPrice * position.Quantity);
            }

            if (position.LastPrice is decimal futuresLastPrice && futuresLastPrice > 0m)
            {
                return Math.Abs(futuresLastPrice * position.Quantity);
            }
        }

        if (position.LastMarkPrice is decimal lastMarkPrice && lastMarkPrice > 0m)
        {
            return Math.Abs(lastMarkPrice * position.Quantity);
        }

        if (position.AverageEntryPrice > 0m)
        {
            return Math.Abs(position.AverageEntryPrice * position.Quantity);
        }

        return Math.Abs(position.CostBasis + position.UnrealizedPnl);
    }

    private static decimal CalculateDemoPositionMarketValue(DemoPosition position)
    {
        if (position.PositionKind == DemoPositionKind.Futures)
        {
            return position.UnrealizedPnl;
        }

        if (position.LastMarkPrice is decimal lastMarkPrice && lastMarkPrice > 0m)
        {
            return Math.Abs(lastMarkPrice * position.Quantity);
        }

        return Math.Abs(position.CostBasis + position.UnrealizedPnl);
    }

    private static string ResolveBaseAsset(string symbol)
    {
        var normalizedSymbol = NormalizeAsset(symbol);

        foreach (var quoteAsset in QuoteAssetSuffixes)
        {
            if (normalizedSymbol.EndsWith(quoteAsset, StringComparison.Ordinal) &&
                normalizedSymbol.Length > quoteAsset.Length)
            {
                return normalizedSymbol[..^quoteAsset.Length];
            }
        }

        return normalizedSymbol;
    }

    private static string NormalizeAsset(string value)
    {
        return value.Trim().ToUpperInvariant();
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

    private static string FormatLimit(decimal? limit)
    {
        return limit.HasValue
            ? limit.Value.ToString("0.####", CultureInfo.InvariantCulture)
            : "n/a";
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }
}
