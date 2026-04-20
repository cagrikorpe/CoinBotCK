using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Risk;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Risk;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
    public async Task EvaluateAsync_ResolvesRiskProfile_ByOwnerId_WhenAmbientUserScopeIsMissing()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var databaseRoot = new InMemoryDatabaseRoot();
        await using (var seedContext = CreateDbContext(databaseName, databaseRoot, hasIsolationBypass: true))
        {
            seedContext.RiskProfiles.Add(CreateRiskProfile("scoped-risk-user", 10m, 60m, 2m));
            await seedContext.SaveChangesAsync();
        }

        await using var dbContext = CreateDbContext(databaseName, databaseRoot, userId: null, hasIsolationBypass: false);
        var evaluator = CreateEvaluator(
            dbContext,
            new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero)));

        var result = await evaluator.EvaluateAsync(
            new RiskPolicyEvaluationRequest(
                "scoped-risk-user",
                Guid.NewGuid(),
                Guid.NewGuid(),
                StrategySignalType.Entry,
                ExecutionEnvironment.Demo,
                "BTCUSDT",
                "1m"));

        Assert.NotEqual(RiskVetoReasonCode.RiskProfileMissing, result.ReasonCode);
        Assert.NotNull(result.Snapshot.RiskProfileId);
        Assert.Equal("Risk Profile", result.Snapshot.RiskProfileName);
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
        Assert.Contains("Reason=DailyLossLimitBreached", result.ReasonSummary, StringComparison.Ordinal);
        Assert.Contains("DailyLoss=7.5/5%", result.ReasonSummary, StringComparison.Ordinal);
        Assert.True(result.Snapshot.IsVirtualCheck);
        Assert.Equal(750m, result.Snapshot.CurrentDailyLossAmount);
    }

    [Fact]
    public async Task EvaluateAsync_VetoesWeeklyLossSeparatelyFromDailyLoss_WhenLossIsOutsideCurrentDayButInsideCurrentIsoWeek()
    {
        await using var dbContext = CreateDbContext();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 25, 12, 0, 0, TimeSpan.Zero));
        dbContext.RiskProfiles.Add(CreateRiskProfile(
            "user-weekly-loss",
            5m,
            100m,
            3m,
            maxWeeklyLossPercentage: 3m));
        dbContext.DemoWallets.Add(CreateDemoWallet("user-weekly-loss", "USDT", 10000m));
        dbContext.DemoLedgerTransactions.Add(CreateLossTransaction(
            "user-weekly-loss",
            -400m,
            new DateTime(2026, 3, 24, 10, 0, 0, DateTimeKind.Utc)));
        await dbContext.SaveChangesAsync();

        var evaluator = CreateEvaluator(dbContext, timeProvider);

        var result = await evaluator.EvaluateAsync(
            new RiskPolicyEvaluationRequest(
                "user-weekly-loss",
                Guid.NewGuid(),
                Guid.NewGuid(),
                StrategySignalType.Entry,
                ExecutionEnvironment.Demo,
                "ETHUSDT",
                "1m"));

        Assert.True(result.IsVetoed);
        Assert.Equal(RiskVetoReasonCode.WeeklyLossLimitBreached, result.ReasonCode);
        Assert.Equal(0m, result.Snapshot.CurrentDailyLossPercentage);
        Assert.Equal(4m, result.Snapshot.CurrentWeeklyLossPercentage);
        Assert.Equal(3m, result.Snapshot.MaxWeeklyLossPercentage);
        Assert.Contains("WeeklyLoss=4/3%", result.ReasonSummary, StringComparison.Ordinal);
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
    public async Task EvaluateAsync_DoesNotCountUnfilledSubmittedLiveMarketOrder_AsOpenPosition()
    {
        await using var dbContext = CreateDbContext();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 4, 20, 8, 0, 0, TimeSpan.Zero));
        var exchangeAccountId = Guid.NewGuid();
        dbContext.RiskProfiles.Add(CreateRiskProfile(
            "user-live-pending",
            10m,
            200m,
            5m,
            maxConcurrentPositions: 1));
        dbContext.ExchangeBalances.Add(new ExchangeBalance
        {
            OwnerUserId = "user-live-pending",
            ExchangeAccountId = exchangeAccountId,
            Plane = ExchangeDataPlane.Futures,
            Asset = "USDT",
            WalletBalance = 1000m,
            CrossWalletBalance = 1000m,
            ExchangeUpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime.AddMinutes(-1),
            SyncedAtUtc = timeProvider.GetUtcNow().UtcDateTime.AddMinutes(-1)
        });
        dbContext.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "user-live-pending",
            ExchangeAccountId = exchangeAccountId,
            Plane = ExchangeDataPlane.Futures,
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            SignalType = StrategySignalType.Entry,
            StrategyKey = "risk-pending",
            Symbol = "SOLUSDT",
            Timeframe = "1m",
            BaseAsset = "SOL",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.06m,
            Price = 85m,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            ExecutorKind = ExecutionOrderExecutorKind.Binance,
            State = ExecutionOrderState.Submitted,
            SubmittedToBroker = true,
            SubmittedAtUtc = timeProvider.GetUtcNow().UtcDateTime.AddSeconds(-10),
            LastStateChangedAtUtc = timeProvider.GetUtcNow().UtcDateTime.AddSeconds(-10),
            IdempotencyKey = "risk-pending-order",
            RootCorrelationId = "risk-pending-root"
        });
        await dbContext.SaveChangesAsync();

        var evaluator = CreateEvaluator(dbContext, timeProvider);

        var result = await evaluator.EvaluateAsync(
            new RiskPolicyEvaluationRequest(
                "user-live-pending",
                Guid.NewGuid(),
                Guid.NewGuid(),
                StrategySignalType.Entry,
                ExecutionEnvironment.Live,
                "SOLUSDT",
                "1m",
                Side: ExecutionOrderSide.Buy,
                Quantity: 0.06m,
                Price: 85m));

        Assert.False(result.IsVetoed);
        Assert.Equal(0, result.Snapshot.OpenPositionCount);
        Assert.Equal(1, result.Snapshot.ProjectedOpenPositionCount);
        Assert.Equal(0m, result.Snapshot.CurrentSymbolExposureAmount);
    }

    [Fact]
    public async Task EvaluateAsync_VetoesProjectedLeverage_UsingRequestNotional()
    {
        await using var dbContext = CreateDbContext();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        dbContext.RiskProfiles.Add(CreateRiskProfile("user-projected-lev", 10m, 500m, 1.5m));
        dbContext.DemoWallets.Add(CreateDemoWallet("user-projected-lev", "USDT", 0m));
        dbContext.DemoPositions.Add(CreateDemoPosition("user-projected-lev", "ETHUSDT", 1m, 1000m, 1000m, 0m, 1000m));
        await dbContext.SaveChangesAsync();

        var evaluator = CreateEvaluator(dbContext, timeProvider);

        var result = await evaluator.EvaluateAsync(
            new RiskPolicyEvaluationRequest(
                "user-projected-lev",
                Guid.NewGuid(),
                Guid.NewGuid(),
                StrategySignalType.Entry,
                ExecutionEnvironment.Demo,
                "BTCUSDT",
                "1m",
                BotId: Guid.NewGuid(),
                Side: ExecutionOrderSide.Buy,
                Quantity: 1m,
                Price: 1000m));

        Assert.True(result.IsVetoed);
        Assert.Equal(RiskVetoReasonCode.LeverageLimitBreached, result.ReasonCode);
        Assert.Equal(1m, result.Snapshot.CurrentLeverage);
        Assert.Equal(2m, result.Snapshot.ProjectedLeverage);
        Assert.Equal(1.5m, result.Snapshot.MaxLeverage);
        Assert.Equal(1000m, result.Snapshot.RequestedNotional);
        Assert.Contains("Leverage=1->2/1.5x", result.ReasonSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_VetoesProjectedSymbolExposure_ForRequestedSymbolOnly()
    {
        await using var dbContext = CreateDbContext();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        dbContext.RiskProfiles.Add(CreateRiskProfile(
            "user-symbol-exposure",
            10m,
            200m,
            10m,
            maxSymbolExposurePercentage: 15m));
        dbContext.DemoWallets.Add(CreateDemoWallet("user-symbol-exposure", "USDT", 900m));
        dbContext.DemoPositions.Add(CreateDemoPosition("user-symbol-exposure", "BTCUSDT", 1m, 100m, 100m, 0m, 100m));
        await dbContext.SaveChangesAsync();

        var evaluator = CreateEvaluator(dbContext, timeProvider);

        var ethResult = await evaluator.EvaluateAsync(
            new RiskPolicyEvaluationRequest(
                "user-symbol-exposure",
                Guid.NewGuid(),
                Guid.NewGuid(),
                StrategySignalType.Entry,
                ExecutionEnvironment.Demo,
                "ETHUSDT",
                "1m",
                Quantity: 1m,
                Price: 100m));
        var btcResult = await evaluator.EvaluateAsync(
            new RiskPolicyEvaluationRequest(
                "user-symbol-exposure",
                Guid.NewGuid(),
                Guid.NewGuid(),
                StrategySignalType.Entry,
                ExecutionEnvironment.Demo,
                "BTCUSDT",
                "1m",
                Quantity: 1m,
                Price: 100m));

        Assert.False(ethResult.IsVetoed);
        Assert.Equal("ETHUSDT", ethResult.Snapshot.Symbol);
        Assert.Equal(0m, ethResult.Snapshot.CurrentSymbolExposurePercentage);
        Assert.Equal(10m, ethResult.Snapshot.ProjectedSymbolExposurePercentage);

        Assert.True(btcResult.IsVetoed);
        Assert.Equal(RiskVetoReasonCode.SymbolExposureLimitBreached, btcResult.ReasonCode);
        Assert.Equal("BTCUSDT", btcResult.Snapshot.Symbol);
        Assert.Equal(10m, btcResult.Snapshot.CurrentSymbolExposurePercentage);
        Assert.Equal(20m, btcResult.Snapshot.ProjectedSymbolExposurePercentage);
        Assert.Equal(15m, btcResult.Snapshot.MaxSymbolExposurePercentage);
    }

    [Fact]
    public async Task EvaluateAsync_VetoesMaxConcurrentPositions_WhenNewSymbolWouldOpenAnotherPosition()
    {
        await using var dbContext = CreateDbContext();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        dbContext.RiskProfiles.Add(CreateRiskProfile(
            "user-max-positions",
            10m,
            200m,
            10m,
            maxConcurrentPositions: 1));
        dbContext.DemoWallets.Add(CreateDemoWallet("user-max-positions", "USDT", 1000m));
        dbContext.DemoPositions.Add(CreateDemoPosition("user-max-positions", "BTCUSDT", 1m, 100m, 100m, 0m, 100m));
        await dbContext.SaveChangesAsync();

        var evaluator = CreateEvaluator(dbContext, timeProvider);

        var result = await evaluator.EvaluateAsync(
            new RiskPolicyEvaluationRequest(
                "user-max-positions",
                Guid.NewGuid(),
                Guid.NewGuid(),
                StrategySignalType.Entry,
                ExecutionEnvironment.Demo,
                "ETHUSDT",
                "1m",
                Quantity: 1m,
                Price: 100m));

        Assert.True(result.IsVetoed);
        Assert.Equal(RiskVetoReasonCode.MaxConcurrentPositionsBreached, result.ReasonCode);
        Assert.Equal(1, result.Snapshot.OpenPositionCount);
        Assert.Equal(2, result.Snapshot.ProjectedOpenPositionCount);
        Assert.Equal(1, result.Snapshot.MaxConcurrentPositions);
        Assert.Contains("OpenPositions=1->2/1", result.ReasonSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_VetoesCoinSpecificLimit_ForSameBaseAssetVariants_AndDoesNotLeakToOtherCoins()
    {
        await using var dbContext = CreateDbContext();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        dbContext.RiskProfiles.Add(CreateRiskProfile(
            "user-coin-limit",
            10m,
            200m,
            10m,
            coinSpecificExposureLimitsJson: "{\"BTC\":15}"));
        dbContext.DemoWallets.Add(CreateDemoWallet("user-coin-limit", "USDT", 900m));
        dbContext.DemoPositions.Add(CreateDemoPosition("user-coin-limit", "BTCUSDT", 1m, 100m, 100m, 0m, 100m));
        await dbContext.SaveChangesAsync();

        var evaluator = CreateEvaluator(dbContext, timeProvider);

        var ethResult = await evaluator.EvaluateAsync(
            new RiskPolicyEvaluationRequest(
                "user-coin-limit",
                Guid.NewGuid(),
                Guid.NewGuid(),
                StrategySignalType.Entry,
                ExecutionEnvironment.Demo,
                "ETHUSDT",
                "1m",
                Quantity: 1m,
                Price: 100m));
        var btcFdusdResult = await evaluator.EvaluateAsync(
            new RiskPolicyEvaluationRequest(
                "user-coin-limit",
                Guid.NewGuid(),
                Guid.NewGuid(),
                StrategySignalType.Entry,
                ExecutionEnvironment.Demo,
                "BTCFDUSD",
                "1m",
                Quantity: 1m,
                Price: 100m));

        Assert.False(ethResult.IsVetoed);
        Assert.Equal("ETH", ethResult.Snapshot.BaseAsset);
        Assert.Null(ethResult.Snapshot.MaxCoinExposurePercentage);

        Assert.True(btcFdusdResult.IsVetoed);
        Assert.Equal(RiskVetoReasonCode.CoinSpecificLimitBreached, btcFdusdResult.ReasonCode);
        Assert.Equal("BTC", btcFdusdResult.Snapshot.BaseAsset);
        Assert.Equal(10m, btcFdusdResult.Snapshot.CurrentCoinExposurePercentage);
        Assert.Equal(20m, btcFdusdResult.Snapshot.ProjectedCoinExposurePercentage);
        Assert.Equal(15m, btcFdusdResult.Snapshot.MaxCoinExposurePercentage);
        Assert.Contains("CoinExposure[BTC]=10->20/15%", btcFdusdResult.ReasonSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_DoesNotLeakRiskStateAcrossUsers()
    {
        await using var dbContext = CreateDbContext();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        dbContext.RiskProfiles.Add(CreateRiskProfile("user-risky", 5m, 50m, 2m));
        dbContext.RiskProfiles.Add(CreateRiskProfile("user-clean", 10m, 100m, 5m));
        dbContext.DemoWallets.Add(CreateDemoWallet("user-risky", "USDT", 1000m));
        dbContext.DemoWallets.Add(CreateDemoWallet("user-clean", "USDT", 1000m));
        dbContext.DemoPositions.Add(CreateDemoPosition("user-risky", "BTCUSDT", 10m, 1000m, 100m, 0m, 100m));
        dbContext.DemoLedgerTransactions.Add(CreateLossTransaction("user-risky", -200m));
        await dbContext.SaveChangesAsync();

        var evaluator = CreateEvaluator(dbContext, timeProvider);

        var cleanResult = await evaluator.EvaluateAsync(
            new RiskPolicyEvaluationRequest(
                "user-clean",
                Guid.NewGuid(),
                Guid.NewGuid(),
                StrategySignalType.Entry,
                ExecutionEnvironment.Demo,
                "BTCUSDT",
                "1m"));

        Assert.False(cleanResult.IsVetoed);
        Assert.Equal("user-clean", cleanResult.Snapshot.OwnerUserId);
        Assert.Equal(0m, cleanResult.Snapshot.CurrentDailyLossPercentage);
        Assert.Equal(0m, cleanResult.Snapshot.CurrentExposurePercentage);
    }

    [Fact]
    public async Task EvaluateAsync_FailsClosed_WithExactReason_WhenCoinSpecificLimitConfigurationIsInvalid()
    {
        await using var dbContext = CreateDbContext();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        dbContext.RiskProfiles.Add(CreateRiskProfile(
            "user-invalid-config",
            10m,
            100m,
            2m,
            coinSpecificExposureLimitsJson: "{broken-json"));
        dbContext.DemoWallets.Add(CreateDemoWallet("user-invalid-config", "USDT", 1000m));
        await dbContext.SaveChangesAsync();

        var evaluator = CreateEvaluator(dbContext, timeProvider);

        var result = await evaluator.EvaluateAsync(
            new RiskPolicyEvaluationRequest(
                "user-invalid-config",
                Guid.NewGuid(),
                Guid.NewGuid(),
                StrategySignalType.Entry,
                ExecutionEnvironment.Demo,
                "BTCUSDT",
                "1m"));

        Assert.True(result.IsVetoed);
        Assert.Equal(RiskVetoReasonCode.RiskProfileConfigurationInvalid, result.ReasonCode);
        Assert.Contains("Reason=RiskProfileConfigurationInvalid", result.ReasonSummary, StringComparison.Ordinal);
        Assert.Equal("BTCUSDT", result.Snapshot.Symbol);
        Assert.Equal("BTC", result.Snapshot.BaseAsset);
    }

    [Fact]
    public async Task EvaluateAsync_ReducesProjectedExposure_WhenBuyOffsetsExistingShortPosition()
    {
        await using var dbContext = CreateDbContext();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        dbContext.RiskProfiles.Add(CreateRiskProfile("user-short-risk", 10m, 200m, 5m, maxConcurrentPositions: 2));
        dbContext.DemoWallets.Add(CreateDemoWallet("user-short-risk", "USDT", 1000m));
        dbContext.DemoPositions.Add(CreateDemoPosition("user-short-risk", "BTCUSDT", -1m, 100m, 100m, 0m, 100m));
        await dbContext.SaveChangesAsync();

        var evaluator = CreateEvaluator(dbContext, timeProvider);
        var result = await evaluator.EvaluateAsync(
            new RiskPolicyEvaluationRequest(
                "user-short-risk",
                Guid.NewGuid(),
                Guid.NewGuid(),
                StrategySignalType.Entry,
                ExecutionEnvironment.Demo,
                "BTCUSDT",
                "1m",
                Side: ExecutionOrderSide.Buy,
                Quantity: 1m,
                Price: 100m));

        Assert.False(result.IsVetoed);
        Assert.Equal(100m, result.Snapshot.CurrentGrossExposure);
        Assert.Equal(0m, result.Snapshot.ProjectedGrossExposure);
        Assert.Equal(100m, result.Snapshot.CurrentSymbolExposureAmount);
        Assert.Equal(0m, result.Snapshot.ProjectedSymbolExposureAmount);
        Assert.Equal(1, result.Snapshot.OpenPositionCount);
        Assert.Equal(0, result.Snapshot.ProjectedOpenPositionCount);
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

    private static ApplicationDbContext CreateDbContext(
        string? databaseName = null,
        InMemoryDatabaseRoot? databaseRoot = null,
        string? userId = null,
        bool hasIsolationBypass = true)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        if (databaseRoot is null)
        {
            optionsBuilder.UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString("N"));
        }
        else
        {
            optionsBuilder.UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString("N"), databaseRoot);
        }

        return new ApplicationDbContext(optionsBuilder.Options, new TestDataScopeContext(userId, hasIsolationBypass));
    }
    private static RiskProfile CreateRiskProfile(
        string ownerUserId,
        decimal maxDailyLossPercentage,
        decimal maxPositionSizePercentage,
        decimal maxLeverage,
        decimal? maxWeeklyLossPercentage = null,
        decimal? maxSymbolExposurePercentage = null,
        int? maxConcurrentPositions = null,
        string? coinSpecificExposureLimitsJson = null)
    {
        return new RiskProfile
        {
            OwnerUserId = ownerUserId,
            ProfileName = "Risk Profile",
            MaxDailyLossPercentage = maxDailyLossPercentage,
            MaxPositionSizePercentage = maxPositionSizePercentage,
            MaxLeverage = maxLeverage,
            MaxWeeklyLossPercentage = maxWeeklyLossPercentage,
            MaxSymbolExposurePercentage = maxSymbolExposurePercentage,
            MaxConcurrentPositions = maxConcurrentPositions,
            CoinSpecificExposureLimitsJson = coinSpecificExposureLimitsJson
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

    private sealed class TestDataScopeContext(string? userId, bool hasIsolationBypass) : IDataScopeContext
    {
        public string? UserId => userId;

        public bool HasIsolationBypass => hasIsolationBypass;
    }
}
