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
    public async Task GetSnapshotAsync_ProjectsAiHistoryControlAndLatestReasons()
    {
        await using var dbContext = CreateDbContext();
        var nowUtc = new DateTime(2026, 4, 6, 12, 0, 0, DateTimeKind.Utc);
        var ownerUserId = "dashboard-live-user";
        var exchangeAccountId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var botId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var featureSnapshotId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var orderId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

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
        dbContext.TradingFeatureSnapshots.Add(new TradingFeatureSnapshot
        {
            Id = featureSnapshotId,
            OwnerUserId = ownerUserId,
            BotId = botId,
            ExchangeAccountId = exchangeAccountId,
            StrategyKey = "ai-live",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            EvaluatedAtUtc = nowUtc,
            FeatureVersion = "AI-1.v1",
            SnapshotState = FeatureSnapshotState.Ready,
            MarketDataReasonCode = DegradedModeReasonCode.None,
            FeatureSummary = "EMA stack bullish.",
            TopSignalHints = "Volume spike and RSI support long bias.",
            PrimaryRegime = "Trending",
            MomentumBias = "Bullish",
            VolatilityState = "Expanding"
        });
        dbContext.AiShadowDecisions.Add(new AiShadowDecision
        {
            Id = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
            OwnerUserId = ownerUserId,
            BotId = botId,
            ExchangeAccountId = exchangeAccountId,
            FeatureSnapshotId = featureSnapshotId,
            CorrelationId = "corr-live-1",
            StrategyKey = "ai-live",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            EvaluatedAtUtc = nowUtc,
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

        var service = new UserDashboardLiveReadModelService(
            dbContext,
            switchService,
            Options.Create(new BotExecutionPilotOptions
            {
                Enabled = true,
                PilotActivationEnabled = true,
                PrivatePlaneFreshnessThresholdSeconds = 120
            }),
            new FixedTimeProvider(nowUtc));

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
        Assert.Single(snapshot.NoSubmitReasons);
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


