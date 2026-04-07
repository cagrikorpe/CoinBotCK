using CoinBot.Application.Abstractions.Ai;
using CoinBot.Application.Abstractions.Dashboard;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Ai;
using CoinBot.Infrastructure.Auditing;
using CoinBot.Infrastructure.Dashboard;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Dashboard;

public sealed class UserDashboardLiveReadModelServiceTests
{
    [Fact]
    public async Task GetSnapshotAsync_ProjectsAiHistoryControlAndOfficialOutcome()
    {
        await using var dbContext = CreateDbContext();
        var nowUtc = new DateTime(2026, 4, 6, 12, 0, 0, DateTimeKind.Utc);
        var ownerUserId = "dashboard-live-user";
        var exchangeAccountId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var botId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var featureSnapshotId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var orderId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var decisionId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

        SeedUserGraph(dbContext, ownerUserId, exchangeAccountId, botId, featureSnapshotId);
        SeedHistoricalCandles(dbContext, "BTCUSDT", "1m", nowUtc, [64850m, 65000m, 65325m]);
        dbContext.AiShadowDecisions.Add(new AiShadowDecision
        {
            Id = decisionId,
            OwnerUserId = ownerUserId,
            BotId = botId,
            ExchangeAccountId = exchangeAccountId,
            FeatureSnapshotId = featureSnapshotId,
            CorrelationId = "corr-live-1",
            StrategyKey = "ai-live",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            EvaluatedAtUtc = nowUtc,
            MarketDataTimestampUtc = nowUtc,
            FeatureVersion = "AI-1.v1",
            StrategyDirection = "Long",
            StrategyConfidenceScore = 82,
            StrategyDecisionOutcome = "Persisted",
            StrategyDecisionCode = "StrategyEntry",
            StrategySummary = "Strategy approved long entry.",
            AiDirection = "Long",
            AiConfidence = 0.82m,
            AiReasonSummary = "AI confirmed the long setup.",
            AiProviderName = "DeterministicStub",
            AiProviderModel = "stub-v1",
            AiLatencyMs = 12,
            AiIsFallback = false,
            RiskVetoPresent = false,
            PilotSafetyBlocked = false,
            TradingMode = ExecutionEnvironment.Demo,
            Plane = ExchangeDataPlane.Futures,
            FinalAction = "ShadowOnly",
            HypotheticalSubmitAllowed = true,
            NoSubmitReason = "ShadowModeActive",
            FeatureSummary = "EMA stack bullish.",
            AgreementState = "Agreement",
            CreatedDate = nowUtc,
            UpdatedDate = nowUtc
        });
        dbContext.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = orderId,
            OwnerUserId = ownerUserId,
            BotId = botId,
            ExchangeAccountId = exchangeAccountId,
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            SignalType = StrategySignalType.Entry,
            Plane = ExchangeDataPlane.Futures,
            StrategyKey = "ai-live",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.001m,
            Price = 65000m,
            ExecutionEnvironment = ExecutionEnvironment.Demo,
            ExecutorKind = ExecutionOrderExecutorKind.Binance,
            State = ExecutionOrderState.Rejected,
            IdempotencyKey = "idem-live-1",
            RootCorrelationId = "root-live-1",
            FailureCode = "TradeMasterDisarmed",
            FailureDetail = "Execution blocked because kill switch is off.",
            CreatedDate = nowUtc.AddMinutes(1),
            UpdatedDate = nowUtc.AddMinutes(1),
            LastStateChangedAtUtc = nowUtc.AddMinutes(1)
        });
        dbContext.ExchangeAccountSyncStates.Add(new ExchangeAccountSyncState
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            ExchangeAccountId = exchangeAccountId,
            Plane = ExchangeDataPlane.Futures,
            PrivateStreamConnectionState = ExchangePrivateStreamConnectionState.Connected,
            DriftStatus = ExchangeStateDriftStatus.InSync,
            LastPrivateStreamEventAtUtc = nowUtc,
            LastBalanceSyncedAtUtc = nowUtc,
            LastPositionSyncedAtUtc = nowUtc,
            LastStateReconciledAtUtc = nowUtc
        });
        dbContext.DegradedModeStates.Add(new DegradedModeState
        {
            Id = DegradedModeDefaults.SingletonId,
            StateCode = DegradedModeStateCode.Normal,
            ReasonCode = DegradedModeReasonCode.None,
            SignalFlowBlocked = false,
            ExecutionFlowBlocked = false,
            LatestHeartbeatSource = "shared-cache:kline",
            LatestSymbol = "BTCUSDT",
            LatestTimeframe = "1m",
            LatestDataTimestampAtUtc = nowUtc,
            LatestHeartbeatReceivedAtUtc = nowUtc,
            LastStateChangedAtUtc = nowUtc
        });
        await dbContext.SaveChangesAsync();

        var switchService = new GlobalExecutionSwitchService(
            dbContext,
            new AuditLogService(dbContext, new CorrelationContextAccessor()));
        await switchService.SetTradeMasterStateAsync(TradeMasterSwitchState.Armed, "unit-test");

        var fixedTimeProvider = new FixedTimeProvider(nowUtc.AddMinutes(2));
        var aiShadowDecisionService = new AiShadowDecisionService(dbContext, fixedTimeProvider);
        var service = new UserDashboardLiveReadModelService(
            dbContext,
            switchService,
            aiShadowDecisionService,
            Options.Create(new BotExecutionPilotOptions
            {
                Enabled = true,
                PilotActivationEnabled = true,
                PrivatePlaneFreshnessThresholdSeconds = 120
            }),
            fixedTimeProvider);

        var snapshot = await service.GetSnapshotAsync(ownerUserId);

        Assert.Equal("Armed", snapshot.Control.TradeMasterLabel);
        Assert.Equal("PilotEnabled", snapshot.Control.PilotActivationLabel);
        Assert.Equal("Fresh", snapshot.Control.MarketDataLabel);
        Assert.Equal("Fresh", snapshot.Control.PrivatePlaneLabel);
        Assert.Equal("ShadowOnly", snapshot.LatestNoTrade.Label);
        Assert.Equal("Rejected", snapshot.LatestReject.Label);
        Assert.Equal("TradeMasterDisarmed", snapshot.LatestReject.Code);

        var aiHistory = Assert.Single(snapshot.AiHistory);
        Assert.Equal(featureSnapshotId, aiHistory.FeatureSnapshotId);
        Assert.Equal("Trending", aiHistory.PrimaryRegime);
        Assert.Equal(AiShadowOutcomeState.Scored, aiHistory.OutcomeState);
        Assert.Equal(AiShadowFutureDataAvailability.Available, aiHistory.FutureDataAvailability);
        Assert.Equal("High", aiHistory.OutcomeConfidenceBucket);
        Assert.Equal("Long", aiHistory.RealizedDirectionality);
        Assert.True((aiHistory.OutcomeScore ?? 0m) > 0m);
        Assert.Equal(AiShadowOutcomeDefaults.OfficialHorizonKind, aiHistory.OutcomeHorizonKind);
        Assert.Equal(AiShadowOutcomeDefaults.OfficialHorizonValue, aiHistory.OutcomeHorizonValue);

        Assert.Single(snapshot.NoSubmitReasons);
        var outcomeSummary = Assert.IsType<UserDashboardAiOutcomeSummarySnapshot>(snapshot.AiOutcomeSummary);
        Assert.Equal("+1 bar close-to-close", outcomeSummary.HorizonLabel);
        Assert.Equal(1, outcomeSummary.TotalDecisionCount);
        Assert.Equal(1, outcomeSummary.ScoredCount);
        Assert.Equal(1, outcomeSummary.PositiveOutcomeCount);
        Assert.True(outcomeSummary.AverageOutcomeScore > 0m);
        Assert.Contains(snapshot.OutcomeStates ?? [], item => item.Label == nameof(AiShadowOutcomeState.Scored) && item.Count == 1);
        Assert.Contains(snapshot.FutureDataAvailabilityBuckets ?? [], item => item.Label == nameof(AiShadowFutureDataAvailability.Available) && item.Count == 1);
        Assert.Contains(snapshot.OutcomeConfidenceBuckets ?? [], item => item.Label == "High" && item.TotalCount == 1 && item.SuccessCount == 1);
        Assert.Single(await dbContext.AiShadowDecisionOutcomes.ToListAsync());
    }

    [Fact]
    public async Task GetSnapshotAsync_ProjectsMissingFutureDataOutcome_WhenFutureCandleIsUnavailable()
    {
        await using var dbContext = CreateDbContext();
        var nowUtc = new DateTime(2026, 4, 6, 13, 0, 0, DateTimeKind.Utc);
        var ownerUserId = "dashboard-live-missing-user";
        var exchangeAccountId = Guid.Parse("11111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var botId = Guid.Parse("22222222-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var featureSnapshotId = Guid.Parse("33333333-cccc-cccc-cccc-cccccccccccc");

        SeedUserGraph(dbContext, ownerUserId, exchangeAccountId, botId, featureSnapshotId);
        SeedHistoricalCandlesFromStart(dbContext, "BTCUSDT", "1m", nowUtc, [65020m]);
        dbContext.AiShadowDecisions.Add(new AiShadowDecision
        {
            Id = Guid.Parse("44444444-dddd-dddd-dddd-dddddddddddd"),
            OwnerUserId = ownerUserId,
            BotId = botId,
            ExchangeAccountId = exchangeAccountId,
            FeatureSnapshotId = featureSnapshotId,
            CorrelationId = "corr-live-2",
            StrategyKey = "ai-live",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            EvaluatedAtUtc = nowUtc,
            MarketDataTimestampUtc = nowUtc,
            FeatureVersion = "AI-1.v1",
            StrategyDirection = "Neutral",
            StrategyConfidenceScore = 55,
            StrategyDecisionOutcome = "Persisted",
            StrategyDecisionCode = "StrategyHold",
            StrategySummary = "Strategy stayed neutral.",
            AiDirection = "Neutral",
            AiConfidence = 0.55m,
            AiReasonSummary = "AI stayed neutral.",
            AiProviderName = "DeterministicStub",
            AiProviderModel = "stub-v1",
            AiLatencyMs = 9,
            AiIsFallback = false,
            RiskVetoPresent = false,
            PilotSafetyBlocked = false,
            TradingMode = ExecutionEnvironment.Demo,
            Plane = ExchangeDataPlane.Futures,
            FinalAction = "NoSubmit",
            HypotheticalSubmitAllowed = false,
            HypotheticalBlockReason = "ShadowHold",
            NoSubmitReason = "ShadowHold",
            FeatureSummary = "Range compression.",
            AgreementState = "Agreement",
            CreatedDate = nowUtc,
            UpdatedDate = nowUtc
        });
        await dbContext.SaveChangesAsync();

        var switchService = new GlobalExecutionSwitchService(
            dbContext,
            new AuditLogService(dbContext, new CorrelationContextAccessor()));
        await switchService.SetTradeMasterStateAsync(TradeMasterSwitchState.Armed, "unit-test");

        var fixedTimeProvider = new FixedTimeProvider(nowUtc.AddMinutes(1));
        var service = new UserDashboardLiveReadModelService(
            dbContext,
            switchService,
            new AiShadowDecisionService(dbContext, fixedTimeProvider),
            Options.Create(new BotExecutionPilotOptions
            {
                Enabled = true,
                PilotActivationEnabled = false,
                PrivatePlaneFreshnessThresholdSeconds = 120
            }),
            fixedTimeProvider);

        var snapshot = await service.GetSnapshotAsync(ownerUserId);

        var aiHistory = Assert.Single(snapshot.AiHistory);
        Assert.Equal(AiShadowOutcomeState.FutureDataUnavailable, aiHistory.OutcomeState);
        Assert.Equal(AiShadowFutureDataAvailability.MissingFutureCandle, aiHistory.FutureDataAvailability);
        Assert.Null(aiHistory.OutcomeScore);
        var outcomeSummary = Assert.IsType<UserDashboardAiOutcomeSummarySnapshot>(snapshot.AiOutcomeSummary);
        Assert.Equal(1, outcomeSummary.FutureDataUnavailableCount);
        Assert.Contains(snapshot.FutureDataAvailabilityBuckets ?? [], item => item.Label == nameof(AiShadowFutureDataAvailability.MissingFutureCandle) && item.Count == 1);
    }

    private static void SeedUserGraph(ApplicationDbContext dbContext, string ownerUserId, Guid exchangeAccountId, Guid botId, Guid featureSnapshotId)
    {
        dbContext.Users.Add(new ApplicationUser
        {
            Id = ownerUserId,
            UserName = ownerUserId,
            NormalizedUserName = ownerUserId.ToUpperInvariant(),
            Email = ownerUserId + "@coinbot.test",
            NormalizedEmail = (ownerUserId + "@coinbot.test").ToUpperInvariant(),
            FullName = ownerUserId,
            EmailConfirmed = true
        });
        dbContext.ExchangeAccounts.Add(new ExchangeAccount
        {
            Id = exchangeAccountId,
            OwnerUserId = ownerUserId,
            ExchangeName = "Binance",
            DisplayName = "Dashboard Live",
            CredentialStatus = ExchangeCredentialStatus.Active,
            ApiKeyCiphertext = "cipher-api-key",
            ApiSecretCiphertext = "cipher-api-secret"
        });
        dbContext.TradingBots.Add(new TradingBot
        {
            Id = botId,
            OwnerUserId = ownerUserId,
            Name = "Dashboard Bot",
            StrategyKey = "ai-live",
            Symbol = "BTCUSDT",
            ExchangeAccountId = exchangeAccountId,
            IsEnabled = true
        });
        dbContext.TradingFeatureSnapshots.Add(new TradingFeatureSnapshot
        {
            Id = featureSnapshotId,
            OwnerUserId = ownerUserId,
            BotId = botId,
            ExchangeAccountId = exchangeAccountId,
            StrategyKey = "ai-live",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            EvaluatedAtUtc = new DateTime(2026, 4, 6, 12, 0, 0, DateTimeKind.Utc),
            FeatureVersion = "AI-1.v1",
            SnapshotState = FeatureSnapshotState.Ready,
            MarketDataReasonCode = DegradedModeReasonCode.None,
            FeatureSummary = "EMA stack bullish.",
            TopSignalHints = "Volume spike and RSI support long bias.",
            PrimaryRegime = "Trending",
            MomentumBias = "Bullish",
            VolatilityState = "Expanding"
        });
    }

    private static void SeedHistoricalCandles(ApplicationDbContext dbContext, string symbol, string interval, DateTime decisionEvaluatedAtUtc, decimal[] closePrices)
    {
        var referenceCloseTimeUtc = decisionEvaluatedAtUtc;
        var startCloseTimeUtc = referenceCloseTimeUtc.AddMinutes(-(closePrices.Length - 2));
        for (var index = 0; index < closePrices.Length; index++)
        {
            var closeTimeUtc = startCloseTimeUtc.AddMinutes(index);
            var closePrice = closePrices[index];
            dbContext.HistoricalMarketCandles.Add(new HistoricalMarketCandle
            {
                Id = Guid.NewGuid(),
                Symbol = symbol,
                Interval = interval,
                OpenTimeUtc = closeTimeUtc.AddMinutes(-1),
                CloseTimeUtc = closeTimeUtc,
                OpenPrice = closePrice - 10m,
                HighPrice = closePrice + 15m,
                LowPrice = closePrice - 20m,
                ClosePrice = closePrice,
                Volume = 1000m + (index * 25m),
                ReceivedAtUtc = closeTimeUtc,
                Source = "unit-test"
            });
        }
    }


    private static void SeedHistoricalCandlesFromStart(ApplicationDbContext dbContext, string symbol, string interval, DateTime firstCloseTimeUtc, decimal[] closePrices)
    {
        for (var index = 0; index < closePrices.Length; index++)
        {
            var closeTimeUtc = firstCloseTimeUtc.AddMinutes(index);
            var closePrice = closePrices[index];
            dbContext.HistoricalMarketCandles.Add(new HistoricalMarketCandle
            {
                Id = Guid.NewGuid(),
                Symbol = symbol,
                Interval = interval,
                OpenTimeUtc = closeTimeUtc.AddMinutes(-1),
                CloseTimeUtc = closeTimeUtc,
                OpenPrice = closePrice - 10m,
                HighPrice = closePrice + 15m,
                LowPrice = closePrice - 20m,
                ClosePrice = closePrice,
                Volume = 1000m + (index * 25m),
                ReceivedAtUtc = closeTimeUtc,
                Source = "unit-test"
            });
        }
    }
    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;
        public bool HasIsolationBypass => true;
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        private readonly DateTimeOffset value = new(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
        public override DateTimeOffset GetUtcNow() => value;
    }
}



