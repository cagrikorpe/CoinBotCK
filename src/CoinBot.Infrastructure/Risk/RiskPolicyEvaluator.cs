using CoinBot.Application.Abstractions.Risk;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Risk;

public sealed class RiskPolicyEvaluator(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<RiskPolicyEvaluator> logger) : IRiskPolicyEvaluator
{
    public async Task<RiskVetoResult> EvaluateAsync(
        RiskPolicyEvaluationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var riskActivity = CoinBotActivity.StartActivity("CoinBot.Risk.Policy");
        riskActivity.SetTag("coinbot.risk.strategy_id", request.TradingStrategyId.ToString());
        riskActivity.SetTag("coinbot.risk.strategy_version_id", request.TradingStrategyVersionId.ToString());
        riskActivity.SetTag("coinbot.risk.signal_type", request.SignalType.ToString());
        riskActivity.SetTag("coinbot.risk.environment", request.Environment.ToString());
        riskActivity.SetTag("coinbot.risk.symbol", request.Symbol);
        riskActivity.SetTag("coinbot.risk.timeframe", request.Timeframe);

        var evaluatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var isVirtualCheck = request.Environment == ExecutionEnvironment.Demo;

        try
        {
            var riskProfile = await dbContext.RiskProfiles
                .Where(entity => entity.OwnerUserId == request.OwnerUserId && !entity.IsDeleted)
                .OrderByDescending(entity => entity.UpdatedDate)
                .ThenByDescending(entity => entity.CreatedDate)
                .FirstOrDefaultAsync(cancellationToken);

            if (riskProfile is null)
            {
                return new RiskVetoResult(
                    IsVetoed: true,
                    ReasonCode: RiskVetoReasonCode.RiskProfileMissing,
                    Snapshot: CreateSnapshot(
                        isVirtualCheck,
                        profile: null,
                        currentEquity: 0m,
                        currentGrossExposure: 0m,
                        currentDailyLossAmount: 0m,
                        openPositionCount: 0,
                        evaluatedAtUtc));
            }

            var snapshot = request.Environment == ExecutionEnvironment.Demo
                ? await BuildDemoSnapshotAsync(request.OwnerUserId, riskProfile, evaluatedAtUtc, cancellationToken)
                : await BuildLiveSnapshotAsync(request.OwnerUserId, riskProfile, evaluatedAtUtc, cancellationToken);

            var reasonCode = ResolveReasonCode(riskProfile, snapshot);
            riskActivity.SetTag("coinbot.risk.reason", reasonCode.ToString());
            riskActivity.SetTag("coinbot.risk.is_vetoed", reasonCode != RiskVetoReasonCode.None);

            return new RiskVetoResult(
                IsVetoed: reasonCode != RiskVetoReasonCode.None,
                ReasonCode: reasonCode,
                Snapshot: snapshot);
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
                request.Symbol,
                request.Environment);

            return new RiskVetoResult(
                IsVetoed: true,
                ReasonCode: RiskVetoReasonCode.AccountEquityUnavailable,
                Snapshot: CreateSnapshot(
                    isVirtualCheck,
                    profile: null,
                    currentEquity: 0m,
                    currentGrossExposure: 0m,
                    currentDailyLossAmount: 0m,
                    openPositionCount: 0,
                    evaluatedAtUtc));
        }
    }

    private async Task<PreTradeRiskSnapshot> BuildDemoSnapshotAsync(
        string ownerUserId,
        RiskProfile riskProfile,
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

        var utcDayStart = evaluatedAtUtc.Date;
        var utcDayEnd = utcDayStart.AddDays(1);
        var currentDailyLossAmount = transactions
            .Where(entity =>
                entity.OccurredAtUtc >= utcDayStart &&
                entity.OccurredAtUtc < utcDayEnd &&
                entity.RealizedPnlDelta.HasValue &&
                entity.RealizedPnlDelta.Value < 0m)
            .Sum(entity => -entity.RealizedPnlDelta!.Value);

        return CreateSnapshot(
            isVirtualCheck: true,
            profile: riskProfile,
            currentEquity: equity,
            currentGrossExposure: grossExposure,
            currentDailyLossAmount: currentDailyLossAmount,
            openPositionCount: positions.Count,
            evaluatedAtUtc);
    }

    private async Task<PreTradeRiskSnapshot> BuildLiveSnapshotAsync(
        string ownerUserId,
        RiskProfile riskProfile,
        DateTime evaluatedAtUtc,
        CancellationToken cancellationToken)
    {
        var balances = await dbContext.ExchangeBalances
            .Where(entity => entity.OwnerUserId == ownerUserId && !entity.IsDeleted)
            .ToListAsync(cancellationToken);

        var positions = await dbContext.ExchangePositions
            .Where(entity =>
                entity.OwnerUserId == ownerUserId &&
                !entity.IsDeleted &&
                entity.Quantity != 0m)
            .ToListAsync(cancellationToken);

        var equity = balances.Sum(entity =>
            entity.CrossWalletBalance != 0m
                ? entity.CrossWalletBalance
                : entity.WalletBalance);

        var grossExposure = positions.Sum(entity => Math.Abs(entity.EntryPrice * entity.Quantity));
        var currentDailyLossAmount = positions
            .Where(entity => entity.UnrealizedProfit < 0m)
            .Sum(entity => Math.Abs(entity.UnrealizedProfit));

        return CreateSnapshot(
            isVirtualCheck: false,
            profile: riskProfile,
            currentEquity: equity,
            currentGrossExposure: grossExposure,
            currentDailyLossAmount: currentDailyLossAmount,
            openPositionCount: positions.Count,
            evaluatedAtUtc);
    }

    private static PreTradeRiskSnapshot CreateSnapshot(
        bool isVirtualCheck,
        RiskProfile? profile,
        decimal currentEquity,
        decimal currentGrossExposure,
        decimal currentDailyLossAmount,
        int openPositionCount,
        DateTime evaluatedAtUtc)
    {
        var currentLeverage = currentEquity > 0m
            ? currentGrossExposure / currentEquity
            : 0m;
        var currentExposurePercentage = currentEquity > 0m
            ? (currentGrossExposure / currentEquity) * 100m
            : 0m;
        var currentDailyLossPercentage = currentEquity > 0m
            ? (currentDailyLossAmount / currentEquity) * 100m
            : 0m;

        return new PreTradeRiskSnapshot(
            IsVirtualCheck: isVirtualCheck,
            RiskProfileId: profile?.Id,
            RiskProfileName: profile?.ProfileName,
            KillSwitchEnabled: profile?.KillSwitchEnabled ?? false,
            CurrentEquity: currentEquity,
            CurrentGrossExposure: currentGrossExposure,
            CurrentLeverage: currentLeverage,
            CurrentExposurePercentage: currentExposurePercentage,
            CurrentDailyLossAmount: currentDailyLossAmount,
            CurrentDailyLossPercentage: currentDailyLossPercentage,
            MaxDailyLossPercentage: profile?.MaxDailyLossPercentage,
            MaxExposurePercentage: profile?.MaxPositionSizePercentage,
            MaxLeverage: profile?.MaxLeverage,
            OpenPositionCount: openPositionCount,
            EvaluatedAtUtc: evaluatedAtUtc);
    }

    private static RiskVetoReasonCode ResolveReasonCode(RiskProfile riskProfile, PreTradeRiskSnapshot snapshot)
    {
        if (riskProfile.KillSwitchEnabled)
        {
            return RiskVetoReasonCode.KillSwitchEnabled;
        }

        if (snapshot.CurrentEquity <= 0m)
        {
            return RiskVetoReasonCode.AccountEquityUnavailable;
        }

        if (snapshot.CurrentDailyLossPercentage > riskProfile.MaxDailyLossPercentage)
        {
            return RiskVetoReasonCode.DailyLossLimitBreached;
        }

        if (snapshot.CurrentExposurePercentage > riskProfile.MaxPositionSizePercentage)
        {
            return RiskVetoReasonCode.ExposureLimitBreached;
        }

        if (snapshot.CurrentLeverage > riskProfile.MaxLeverage)
        {
            return RiskVetoReasonCode.LeverageLimitBreached;
        }

        return RiskVetoReasonCode.None;
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
}
