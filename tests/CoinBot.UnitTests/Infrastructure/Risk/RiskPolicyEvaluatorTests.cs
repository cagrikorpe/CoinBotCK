using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Risk;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Risk;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoinBot.UnitTests.Infrastructure.Risk;

public sealed class RiskPolicyEvaluatorTests
{
    [Fact]
    public async Task EvaluateAsync_AllowsDemoSignal_WithVirtualSnapshot_WhenWithinLimits()
    {
        await using var dbContext = CreateDbContext();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        dbContext.RiskProfiles.Add(CreateRiskProfile("user-risk-1", 10m, 60m, 2m));
        dbContext.DemoWallets.Add(CreateDemoWallet("user-risk-1", "USDT", 8000m));
        dbContext.DemoPositions.Add(CreateDemoPosition("user-risk-1", "BTCUSDT", 1m, 2000m, 2000m, 100m, 2100m));
        await dbContext.SaveChangesAsync();

        var evaluator = CreateEvaluator(dbContext, timeProvider);

        var result = await evaluator.EvaluateAsync(
            new RiskPolicyEvaluationRequest(
                "user-risk-1",
                Guid.NewGuid(),
                Guid.NewGuid(),
                StrategySignalType.Entry,
                ExecutionEnvironment.Demo,
                "BTCUSDT",
                "1m"));

        Assert.False(result.IsVetoed);
        Assert.Equal(RiskVetoReasonCode.None, result.ReasonCode);
        Assert.True(result.Snapshot.IsVirtualCheck);
        Assert.Equal(10100m, result.Snapshot.CurrentEquity);
        Assert.Equal(2100m, result.Snapshot.CurrentGrossExposure);
        Assert.Equal(1, result.Snapshot.OpenPositionCount);
    }

    [Fact]
    public async Task EvaluateAsync_Vetoes_WhenDailyLossLimitBreached()
    {
        await using var dbContext = CreateDbContext();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        dbContext.RiskProfiles.Add(CreateRiskProfile("user-risk-2", 5m, 90m, 2m));
        dbContext.DemoWallets.Add(CreateDemoWallet("user-risk-2", "USDT", 10000m));
        dbContext.DemoLedgerTransactions.Add(CreateLossTransaction("user-risk-2", -750m));
        await dbContext.SaveChangesAsync();

        var evaluator = CreateEvaluator(dbContext, timeProvider);

        var result = await evaluator.EvaluateAsync(
            new RiskPolicyEvaluationRequest(
                "user-risk-2",
                Guid.NewGuid(),
                Guid.NewGuid(),
                StrategySignalType.Entry,
                ExecutionEnvironment.Demo,
                "ETHUSDT",
                "1m"));

        Assert.True(result.IsVetoed);
        Assert.Equal(RiskVetoReasonCode.DailyLossLimitBreached, result.ReasonCode);
        Assert.True(result.Snapshot.IsVirtualCheck);
        Assert.Equal(750m, result.Snapshot.CurrentDailyLossAmount);
    }

    [Fact]
    public async Task EvaluateAsync_Vetoes_WhenExposureLimitBreached()
    {
        await using var dbContext = CreateDbContext();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        dbContext.RiskProfiles.Add(CreateRiskProfile("user-risk-3", 10m, 50m, 2m));
        dbContext.DemoWallets.Add(CreateDemoWallet("user-risk-3", "USDT", 4000m));
        dbContext.DemoPositions.Add(CreateDemoPosition("user-risk-3", "SOLUSDT", 100m, 6000m, 60m, 0m, 60m));
        await dbContext.SaveChangesAsync();

        var evaluator = CreateEvaluator(dbContext, timeProvider);

        var result = await evaluator.EvaluateAsync(
            new RiskPolicyEvaluationRequest(
                "user-risk-3",
                Guid.NewGuid(),
                Guid.NewGuid(),
                StrategySignalType.Entry,
                ExecutionEnvironment.Demo,
                "SOLUSDT",
                "1m"));

        Assert.True(result.IsVetoed);
        Assert.Equal(RiskVetoReasonCode.ExposureLimitBreached, result.ReasonCode);
        Assert.Equal(60m, result.Snapshot.CurrentExposurePercentage);
    }

    [Fact]
    public async Task EvaluateAsync_Vetoes_WhenLiveLeverageLimitBreached()
    {
        await using var dbContext = CreateDbContext();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        dbContext.RiskProfiles.Add(CreateRiskProfile("user-risk-4", 10m, 200m, 1.2m));
        dbContext.ExchangeBalances.Add(CreateExchangeBalance("user-risk-4", "USDT", 1000m));
        dbContext.ExchangePositions.Add(CreateExchangePosition("user-risk-4", "BTCUSDT", 1m, 1500m, 0m));
        await dbContext.SaveChangesAsync();

        var evaluator = CreateEvaluator(dbContext, timeProvider);

        var result = await evaluator.EvaluateAsync(
            new RiskPolicyEvaluationRequest(
                "user-risk-4",
                Guid.NewGuid(),
                Guid.NewGuid(),
                StrategySignalType.Entry,
                ExecutionEnvironment.Live,
                "BTCUSDT",
                "1m"));

        Assert.True(result.IsVetoed);
        Assert.Equal(RiskVetoReasonCode.LeverageLimitBreached, result.ReasonCode);
        Assert.False(result.Snapshot.IsVirtualCheck);
        Assert.Equal(1.5m, result.Snapshot.CurrentLeverage);
    }

    [Fact]
    public async Task EvaluateAsync_UsesFuturesDemoEquity_NotSpotLikeNotionalValue()
    {
        await using var dbContext = CreateDbContext();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        dbContext.RiskProfiles.Add(CreateRiskProfile("user-risk-5", 10m, 200m, 2m));
        dbContext.DemoWallets.Add(CreateDemoWallet("user-risk-5", "USDT", 1000m));
        dbContext.DemoPositions.Add(new DemoPosition
        {
            OwnerUserId = "user-risk-5",
            PositionScopeKey = "scope-ethusdt",
            Symbol = "ETHUSDT",
            BaseAsset = "ETH",
            QuoteAsset = "USDT",
            PositionKind = DemoPositionKind.Futures,
            MarginMode = DemoMarginMode.Cross,
            Leverage = 10m,
            Quantity = 1m,
            CostBasis = 1000m,
            AverageEntryPrice = 1000m,
            UnrealizedPnl = 50m,
            LastPrice = 1010m,
            LastMarkPrice = 1050m,
            MaintenanceMarginRate = 0.005m,
            MaintenanceMargin = 5.25m,
            MarginBalance = 1050m
        });
        await dbContext.SaveChangesAsync();

        var evaluator = CreateEvaluator(dbContext, timeProvider);

        var result = await evaluator.EvaluateAsync(
            new RiskPolicyEvaluationRequest(
                "user-risk-5",
                Guid.NewGuid(),
                Guid.NewGuid(),
                StrategySignalType.Entry,
                ExecutionEnvironment.Demo,
                "ETHUSDT",
                "1m"));

        Assert.False(result.IsVetoed);
        Assert.Equal(1050m, result.Snapshot.CurrentEquity);
        Assert.Equal(1050m, result.Snapshot.CurrentGrossExposure);
        Assert.Equal(1m, result.Snapshot.CurrentLeverage);
    }

    private static RiskPolicyEvaluator CreateEvaluator(ApplicationDbContext dbContext, TimeProvider timeProvider)
    {
        return new RiskPolicyEvaluator(
            dbContext,
            timeProvider,
            NullLogger<RiskPolicyEvaluator>.Instance);
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private static RiskProfile CreateRiskProfile(
        string ownerUserId,
        decimal maxDailyLossPercentage,
        decimal maxPositionSizePercentage,
        decimal maxLeverage)
    {
        return new RiskProfile
        {
            OwnerUserId = ownerUserId,
            ProfileName = "Risk Profile",
            MaxDailyLossPercentage = maxDailyLossPercentage,
            MaxPositionSizePercentage = maxPositionSizePercentage,
            MaxLeverage = maxLeverage
        };
    }

    private static DemoWallet CreateDemoWallet(string ownerUserId, string asset, decimal availableBalance)
    {
        return new DemoWallet
        {
            OwnerUserId = ownerUserId,
            Asset = asset,
            AvailableBalance = availableBalance,
            ReservedBalance = 0m,
            LastActivityAtUtc = new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc)
        };
    }

    private static DemoPosition CreateDemoPosition(
        string ownerUserId,
        string symbol,
        decimal quantity,
        decimal costBasis,
        decimal averageEntryPrice,
        decimal unrealizedPnl,
        decimal lastMarkPrice)
    {
        return new DemoPosition
        {
            OwnerUserId = ownerUserId,
            PositionScopeKey = $"scope-{symbol}",
            Symbol = symbol,
            BaseAsset = symbol[..^4],
            QuoteAsset = "USDT",
            Quantity = quantity,
            CostBasis = costBasis,
            AverageEntryPrice = averageEntryPrice,
            UnrealizedPnl = unrealizedPnl,
            LastMarkPrice = lastMarkPrice
        };
    }

    private static DemoLedgerTransaction CreateLossTransaction(string ownerUserId, decimal realizedPnlDelta)
    {
        return new DemoLedgerTransaction
        {
            OwnerUserId = ownerUserId,
            OperationId = Guid.NewGuid().ToString("N"),
            TransactionType = DemoLedgerTransactionType.FillApplied,
            PositionScopeKey = "risk-position",
            Symbol = "ETHUSDT",
            QuoteAsset = "USDT",
            RealizedPnlDelta = realizedPnlDelta,
            OccurredAtUtc = new DateTime(2026, 3, 22, 11, 0, 0, DateTimeKind.Utc)
        };
    }

    private static ExchangeBalance CreateExchangeBalance(string ownerUserId, string asset, decimal walletBalance)
    {
        return new ExchangeBalance
        {
            OwnerUserId = ownerUserId,
            ExchangeAccountId = Guid.NewGuid(),
            Asset = asset,
            WalletBalance = walletBalance,
            CrossWalletBalance = walletBalance,
            ExchangeUpdatedAtUtc = new DateTime(2026, 3, 22, 11, 59, 0, DateTimeKind.Utc),
            SyncedAtUtc = new DateTime(2026, 3, 22, 11, 59, 0, DateTimeKind.Utc)
        };
    }

    private static ExchangePosition CreateExchangePosition(
        string ownerUserId,
        string symbol,
        decimal quantity,
        decimal entryPrice,
        decimal unrealizedProfit)
    {
        return new ExchangePosition
        {
            OwnerUserId = ownerUserId,
            ExchangeAccountId = Guid.NewGuid(),
            Symbol = symbol,
            PositionSide = "BOTH",
            Quantity = quantity,
            EntryPrice = entryPrice,
            BreakEvenPrice = entryPrice,
            UnrealizedProfit = unrealizedProfit,
            MarginType = "cross",
            ExchangeUpdatedAtUtc = new DateTime(2026, 3, 22, 11, 59, 0, DateTimeKind.Utc),
            SyncedAtUtc = new DateTime(2026, 3, 22, 11, 59, 0, DateTimeKind.Utc)
        };
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }
}
