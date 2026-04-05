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

public sealed class RiskPolicyEvaluatorEdgeCaseTests
{
    [Fact]
    public async Task EvaluateAsync_Allows_WhenDailyAndWeeklyLossesAreExactlyAtThreshold()
    {
        await using var dbContext = CreateDbContext();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 25, 12, 0, 0, TimeSpan.Zero));
        dbContext.RiskProfiles.Add(CreateRiskProfile(
            "user-loss-threshold",
            5m,
            100m,
            2m,
            maxWeeklyLossPercentage: 5m));
        dbContext.DemoWallets.Add(CreateDemoWallet("user-loss-threshold", "USDT", 10000m));
        dbContext.DemoLedgerTransactions.Add(CreateLossTransaction(
            "user-loss-threshold",
            -500m,
            new DateTime(2026, 3, 25, 10, 0, 0, DateTimeKind.Utc)));
        await dbContext.SaveChangesAsync();

        var evaluator = CreateEvaluator(dbContext, timeProvider);

        var result = await evaluator.EvaluateAsync(
            new RiskPolicyEvaluationRequest(
                "user-loss-threshold",
                Guid.NewGuid(),
                Guid.NewGuid(),
                StrategySignalType.Entry,
                ExecutionEnvironment.Demo,
                "BTCUSDT",
                "1m"));

        Assert.False(result.IsVetoed);
        Assert.Equal(RiskVetoReasonCode.None, result.ReasonCode);
        Assert.Equal(5m, result.Snapshot.CurrentDailyLossPercentage);
        Assert.Equal(5m, result.Snapshot.CurrentWeeklyLossPercentage);
        Assert.Contains("Reason=None", result.ReasonSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_Allows_WhenProjectedLimitsAreExactlyAtExposureAndPositionThresholds()
    {
        await using var dbContext = CreateDbContext();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        dbContext.RiskProfiles.Add(CreateRiskProfile(
            "user-projected-threshold",
            10m,
            200m,
            10m,
            maxWeeklyLossPercentage: 50m,
            maxSymbolExposurePercentage: 20m,
            maxConcurrentPositions: 2,
            coinSpecificExposureLimitsJson: "{\"BTC\":20}"));
        dbContext.DemoWallets.Add(CreateDemoWallet("user-projected-threshold", "USDT", 800m));
        dbContext.DemoPositions.Add(CreateDemoPosition("user-projected-threshold", "BTCUSDT", 1m, 100m, 100m, 0m, 100m));
        dbContext.DemoPositions.Add(CreateDemoPosition("user-projected-threshold", "ETHUSDT", 1m, 100m, 100m, 0m, 100m));
        await dbContext.SaveChangesAsync();

        var evaluator = CreateEvaluator(dbContext, timeProvider);

        var result = await evaluator.EvaluateAsync(
            new RiskPolicyEvaluationRequest(
                "user-projected-threshold",
                Guid.NewGuid(),
                Guid.NewGuid(),
                StrategySignalType.Entry,
                ExecutionEnvironment.Demo,
                "BTCUSDT",
                "1m",
                Quantity: 1m,
                Price: 100m));

        Assert.False(result.IsVetoed);
        Assert.Equal(RiskVetoReasonCode.None, result.ReasonCode);
        Assert.Equal(10m, result.Snapshot.CurrentSymbolExposurePercentage);
        Assert.Equal(20m, result.Snapshot.ProjectedSymbolExposurePercentage);
        Assert.Equal(20m, result.Snapshot.ProjectedCoinExposurePercentage);
        Assert.Equal(2, result.Snapshot.ProjectedOpenPositionCount);
        Assert.Contains("Reason=None", result.ReasonSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_FailsClosed_WhenLiveEquityCannotBeResolved()
    {
        await using var dbContext = CreateDbContext();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        dbContext.RiskProfiles.Add(CreateRiskProfile("user-live-equity-missing", 10m, 200m, 2m));
        dbContext.ExchangePositions.Add(CreateExchangePosition("user-live-equity-missing", "BTCUSDT", 1m, 1500m, 0m));
        await dbContext.SaveChangesAsync();

        var evaluator = CreateEvaluator(dbContext, timeProvider);

        var result = await evaluator.EvaluateAsync(
            new RiskPolicyEvaluationRequest(
                "user-live-equity-missing",
                Guid.NewGuid(),
                Guid.NewGuid(),
                StrategySignalType.Entry,
                ExecutionEnvironment.Live,
                "BTCUSDT",
                "1m"));

        Assert.True(result.IsVetoed);
        Assert.Equal(RiskVetoReasonCode.AccountEquityUnavailable, result.ReasonCode);
        Assert.Contains("Reason=AccountEquityUnavailable", result.ReasonSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_UsesWalletBalanceFallback_WhenCrossWalletBalanceIsUnavailable()
    {
        await using var dbContext = CreateDbContext();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        dbContext.RiskProfiles.Add(CreateRiskProfile("user-live-wallet-fallback", 10m, 200m, 2m));
        dbContext.ExchangeBalances.Add(new ExchangeBalance
        {
            OwnerUserId = "user-live-wallet-fallback",
            ExchangeAccountId = Guid.NewGuid(),
            Asset = "USDT",
            WalletBalance = 1500m,
            CrossWalletBalance = 0m,
            ExchangeUpdatedAtUtc = new DateTime(2026, 3, 22, 11, 59, 0, DateTimeKind.Utc),
            SyncedAtUtc = new DateTime(2026, 3, 22, 11, 59, 0, DateTimeKind.Utc)
        });
        await dbContext.SaveChangesAsync();

        var evaluator = CreateEvaluator(dbContext, timeProvider);

        var result = await evaluator.EvaluateAsync(
            new RiskPolicyEvaluationRequest(
                "user-live-wallet-fallback",
                Guid.NewGuid(),
                Guid.NewGuid(),
                StrategySignalType.Entry,
                ExecutionEnvironment.Live,
                "BTCUSDT",
                "1m"));

        Assert.False(result.IsVetoed);
        Assert.Equal(1500m, result.Snapshot.CurrentEquity);
        Assert.Contains("Reason=None", result.ReasonSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_ReturnsStablePrimaryReason_WhenMultipleLimitsAreBreached()
    {
        await using var dbContext = CreateDbContext();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        dbContext.RiskProfiles.Add(CreateRiskProfile(
            "user-multi-veto",
            5m,
            50m,
            1m,
            maxWeeklyLossPercentage: 6m,
            maxSymbolExposurePercentage: 10m,
            maxConcurrentPositions: 1,
            coinSpecificExposureLimitsJson: "{\"BTC\":10}"));
        dbContext.DemoWallets.Add(CreateDemoWallet("user-multi-veto", "USDT", 10000m));
        dbContext.DemoLedgerTransactions.Add(CreateLossTransaction("user-multi-veto", -700m));
        dbContext.DemoPositions.Add(CreateDemoPosition("user-multi-veto", "BTCUSDT", 1m, 1000m, 1000m, 0m, 1000m));
        await dbContext.SaveChangesAsync();

        var evaluator = CreateEvaluator(dbContext, timeProvider);
        var request = new RiskPolicyEvaluationRequest(
            "user-multi-veto",
            Guid.NewGuid(),
            Guid.NewGuid(),
            StrategySignalType.Entry,
            ExecutionEnvironment.Demo,
            "BTCUSDT",
            "1m",
            Quantity: 1m,
            Price: 1000m);

        var first = await evaluator.EvaluateAsync(request);
        var second = await evaluator.EvaluateAsync(request);

        Assert.True(first.IsVetoed);
        Assert.True(second.IsVetoed);
        Assert.Equal(RiskVetoReasonCode.DailyLossLimitBreached, first.ReasonCode);
        Assert.Equal(first.ReasonCode, second.ReasonCode);
        Assert.Equal(first.ReasonSummary, second.ReasonSummary);
    }

    [Fact]
    public async Task EvaluateAsync_SelectsMostRecentRiskProfileDeterministically_WhenTimestampsMatch()
    {
        await using var dbContext = CreateDbContext();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        var createdAtUtc = new DateTime(2026, 3, 22, 11, 0, 0, DateTimeKind.Utc);
        dbContext.RiskProfiles.AddRange(
            CreateRiskProfile(
                "user-profile-order",
                10m,
                200m,
                10m,
                id: Guid.Parse("00000000-0000-0000-0000-000000000111"),
                createdAtUtc: createdAtUtc),
            CreateRiskProfile(
                "user-profile-order",
                5m,
                200m,
                10m,
                id: Guid.Parse("00000000-0000-0000-0000-000000000222"),
                createdAtUtc: createdAtUtc));
        dbContext.DemoWallets.Add(CreateDemoWallet("user-profile-order", "USDT", 10000m));
        dbContext.DemoLedgerTransactions.Add(CreateLossTransaction("user-profile-order", -600m));
        await dbContext.SaveChangesAsync();

        var evaluator = CreateEvaluator(dbContext, timeProvider);

        var result = await evaluator.EvaluateAsync(
            new RiskPolicyEvaluationRequest(
                "user-profile-order",
                Guid.NewGuid(),
                Guid.NewGuid(),
                StrategySignalType.Entry,
                ExecutionEnvironment.Demo,
                "BTCUSDT",
                "1m"));

        Assert.True(result.IsVetoed);
        Assert.Equal(RiskVetoReasonCode.DailyLossLimitBreached, result.ReasonCode);
        Assert.Equal(5m, result.Snapshot.MaxDailyLossPercentage);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000222"), result.Snapshot.RiskProfileId);
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
        decimal maxLeverage,
        decimal? maxWeeklyLossPercentage = null,
        decimal? maxSymbolExposurePercentage = null,
        int? maxConcurrentPositions = null,
        string? coinSpecificExposureLimitsJson = null,
        Guid? id = null,
        DateTime? createdAtUtc = null)
    {
        var normalizedCreatedAtUtc = createdAtUtc ?? new DateTime(2026, 3, 22, 10, 0, 0, DateTimeKind.Utc);
        return new RiskProfile
        {
            Id = id ?? Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            ProfileName = "Risk Profile",
            MaxDailyLossPercentage = maxDailyLossPercentage,
            MaxPositionSizePercentage = maxPositionSizePercentage,
            MaxLeverage = maxLeverage,
            MaxWeeklyLossPercentage = maxWeeklyLossPercentage,
            MaxSymbolExposurePercentage = maxSymbolExposurePercentage,
            MaxConcurrentPositions = maxConcurrentPositions,
            CoinSpecificExposureLimitsJson = coinSpecificExposureLimitsJson,
            CreatedDate = normalizedCreatedAtUtc,
            UpdatedDate = normalizedCreatedAtUtc
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

    private static DemoLedgerTransaction CreateLossTransaction(
        string ownerUserId,
        decimal realizedPnlDelta,
        DateTime? occurredAtUtc = null)
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
            OccurredAtUtc = occurredAtUtc ?? new DateTime(2026, 3, 22, 11, 0, 0, DateTimeKind.Utc)
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

