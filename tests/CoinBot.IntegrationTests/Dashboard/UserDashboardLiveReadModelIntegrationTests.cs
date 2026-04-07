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
using CoinBot.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoinBot.IntegrationTests.Dashboard;

public sealed class UserDashboardLiveReadModelIntegrationTests
{
    [Fact]
    public async Task GetSnapshotAsync_ProjectsAiHistoryControlRejectSummaryAndOutcome_OnSqlServer()
    {
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString($"CoinBotDashboardLive_{Guid.NewGuid():N}");
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        var ownerUserId = "dashboard-live-int-user";
        var nowUtc = new DateTime(2026, 4, 6, 12, 0, 0, DateTimeKind.Utc);
        var exchangeAccountId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        var featureSnapshotId = Guid.NewGuid();
        var decisionId = Guid.NewGuid();

        await using var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        try
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
                DisplayName = "Dashboard Live SQL",
                CredentialStatus = ExchangeCredentialStatus.Active,
                ApiKeyCiphertext = "cipher-api-key",
                ApiSecretCiphertext = "cipher-api-secret"
            });
            dbContext.TradingBots.Add(new TradingBot
            {
                Id = botId,
                OwnerUserId = ownerUserId,
                Name = "Dashboard Live Bot",
                StrategyKey = "ui-live",
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
                StrategyKey = "ui-live",
                Symbol = "BTCUSDT",
                Timeframe = "1m",
                EvaluatedAtUtc = nowUtc,
                FeatureVersion = "AI-1.v1",
                SnapshotState = FeatureSnapshotState.Ready,
                MarketDataReasonCode = DegradedModeReasonCode.None,
                FeatureSummary = "Feature summary.",
                TopSignalHints = "Top hints.",
                PrimaryRegime = "Trending",
                MomentumBias = "Bullish",
                VolatilityState = "Expanding"
            });
            dbContext.AiShadowDecisions.Add(new AiShadowDecision
            {
                Id = decisionId,
                OwnerUserId = ownerUserId,
                BotId = botId,
                ExchangeAccountId = exchangeAccountId,
                FeatureSnapshotId = featureSnapshotId,
                CorrelationId = "corr-ui-live-1",
                StrategyKey = "ui-live",
                Symbol = "BTCUSDT",
                Timeframe = "1m",
                EvaluatedAtUtc = nowUtc,
                MarketDataTimestampUtc = nowUtc,
                FeatureVersion = "AI-1.v1",
                StrategyDirection = "Long",
                StrategyConfidenceScore = 78,
                StrategyDecisionOutcome = "Persisted",
                StrategyDecisionCode = "StrategyEntry",
                StrategySummary = "Strategy favored long.",
                AiDirection = "Long",
                AiConfidence = 0.78m,
                AiReasonSummary = "AI liked the long setup.",
                AiProviderName = "DeterministicStub",
                AiProviderModel = "stub-v1",
                AiLatencyMs = 9,
                AiIsFallback = false,
                AiFallbackReason = null,
                RiskVetoPresent = false,
                PilotSafetyBlocked = false,
                TradingMode = ExecutionEnvironment.Demo,
                Plane = ExchangeDataPlane.Futures,
                FinalAction = "ShadowOnly",
                HypotheticalSubmitAllowed = true,
                NoSubmitReason = "ShadowModeActive",
                FeatureSummary = "Feature summary.",
                AgreementState = "Agreement",
                CreatedDate = nowUtc,
                UpdatedDate = nowUtc
            });
            dbContext.ExecutionOrders.Add(new ExecutionOrder
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                BotId = botId,
                ExchangeAccountId = exchangeAccountId,
                TradingStrategyId = Guid.NewGuid(),
                TradingStrategyVersionId = Guid.NewGuid(),
                StrategySignalId = Guid.NewGuid(),
                SignalType = StrategySignalType.Entry,
                Plane = ExchangeDataPlane.Futures,
                StrategyKey = "ui-live",
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
                IdempotencyKey = "idem-ui-live-1",
                RootCorrelationId = "root-ui-live-1",
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
            SeedHistoricalCandles(dbContext, "BTCUSDT", "1m", nowUtc, [64850m, 65000m, 65300m]);
            await dbContext.SaveChangesAsync();

            var switchService = new GlobalExecutionSwitchService(
                dbContext,
                new AuditLogService(dbContext, new CorrelationContextAccessor()));
            await switchService.SetTradeMasterStateAsync(TradeMasterSwitchState.Armed, "integration-test");

            var fixedTimeProvider = new FixedTimeProvider(nowUtc.AddMinutes(2));
            var aiShadowDecisionService = new AiShadowDecisionService(dbContext, fixedTimeProvider);
            var service = new UserDashboardLiveReadModelService(
                dbContext,
                switchService,
                aiShadowDecisionService,
                Options.Create(new BotExecutionPilotOptions
                {
                    Enabled = true,
                    PilotActivationEnabled = false,
                    PrivatePlaneFreshnessThresholdSeconds = 120
                }),
                fixedTimeProvider);

            var snapshot = await service.GetSnapshotAsync(ownerUserId);
            var outcome = await dbContext.AiShadowDecisionOutcomes.SingleAsync();

            Assert.Equal("Armed", snapshot.Control.TradeMasterLabel);
            Assert.Equal("ShadowOnly", snapshot.Control.PilotActivationLabel);
            Assert.Equal("Fresh", snapshot.Control.MarketDataLabel);
            Assert.Equal("Fresh", snapshot.Control.PrivatePlaneLabel);
            Assert.Equal("ShadowOnly", snapshot.LatestNoTrade.Label);
            Assert.Equal("TradeMasterDisarmed", snapshot.LatestReject.Code);
            Assert.Equal(AiShadowOutcomeState.Scored, outcome.OutcomeState);
            Assert.Equal(AiShadowFutureDataAvailability.Available, outcome.FutureDataAvailability);
            Assert.Equal("Long", outcome.RealizedDirectionality);
            Assert.True((outcome.OutcomeScore ?? 0m) > 0m);

            var aiHistory = Assert.Single(snapshot.AiHistory);
            Assert.Equal(featureSnapshotId, aiHistory.FeatureSnapshotId);
            Assert.Equal("Trending", aiHistory.PrimaryRegime);
            Assert.Equal(AiShadowOutcomeState.Scored, aiHistory.OutcomeState);
            Assert.Equal(AiShadowFutureDataAvailability.Available, aiHistory.FutureDataAvailability);
            Assert.Equal("High", aiHistory.OutcomeConfidenceBucket);
            Assert.NotNull(snapshot.AiOutcomeSummary);
            Assert.Equal(1, snapshot.AiOutcomeSummary!.ScoredCount);
            Assert.Equal(1, snapshot.AiOutcomeSummary.PositiveOutcomeCount);
            Assert.Contains(snapshot.OutcomeStates ?? [], item => item.Label == nameof(AiShadowOutcomeState.Scored) && item.Count == 1);
            Assert.Contains(snapshot.FutureDataAvailabilityBuckets ?? [], item => item.Label == nameof(AiShadowFutureDataAvailability.Available) && item.Count == 1);
            Assert.Contains(snapshot.OutcomeConfidenceBuckets ?? [], item => item.Label == "High" && item.TotalCount == 1);
        }
        finally
        {
            await dbContext.Database.EnsureDeletedAsync();
        }
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
                Source = "integration-test"
            });
        }
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
