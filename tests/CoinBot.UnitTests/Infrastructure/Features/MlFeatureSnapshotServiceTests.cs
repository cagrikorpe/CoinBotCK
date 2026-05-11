using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Features;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Features;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.Persistence;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Features;

public sealed class MlFeatureSnapshotServiceTests
{
    [Fact]
    public async Task CaptureAsync_MapsSafeSnapshotFields_FromCandidateAndRuntimeContext()
    {
        await using var harness = await CreateHarnessAsync("ml-feature-map-01");
        var capturedAtUtc = harness.TimeProvider.GetUtcNow().UtcDateTime;
        var strategyVersionId = Guid.NewGuid();

        harness.DbContext.TradingStrategyVersions.Add(new TradingStrategyVersion
        {
            Id = strategyVersionId,
            OwnerUserId = harness.UserId,
            TradingStrategyId = harness.TradingStrategyId,
            SchemaVersion = 2,
            VersionNumber = 3,
            Status = StrategyVersionStatus.Published,
            DefinitionJson = """
                             {
                               "schemaVersion": 2,
                               "metadata": {
                                 "templateKey": "swing-breakout"
                               }
                             }
                             """,
            PublishedAtUtc = capturedAtUtc.AddMinutes(-5)
        });
        harness.DbContext.TradingFeatureSnapshots.Add(new TradingFeatureSnapshot
        {
            Id = Guid.NewGuid(),
            OwnerUserId = harness.UserId,
            BotId = harness.BotId,
            ExchangeAccountId = harness.ExchangeAccountId,
            StrategyKey = "ml-feature-strategy",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            EvaluatedAtUtc = capturedAtUtc.AddSeconds(-5),
            FeatureAnchorTimeUtc = capturedAtUtc.AddMinutes(-1),
            MarketDataTimestampUtc = capturedAtUtc.AddMinutes(-1),
            FeatureVersion = "AI-1.v1",
            SnapshotState = FeatureSnapshotState.Ready,
            QualityReasonCode = FeatureSnapshotQualityReason.None,
            MarketDataReasonCode = DegradedModeReasonCode.None,
            SampleCount = 240,
            RequiredSampleCount = 200,
            Plane = ExchangeDataPlane.Futures,
            TradingMode = ExecutionEnvironment.BinanceTestnet,
            FeatureSummary = "State=Ready",
            TopSignalHints = "Regime:BullTrend",
            PrimaryRegime = "BullTrend",
            MomentumBias = "Bullish",
            VolatilityState = "Compressed"
        });
        harness.DbContext.ExchangeAccountSyncStates.Add(new ExchangeAccountSyncState
        {
            Id = Guid.NewGuid(),
            OwnerUserId = harness.UserId,
            ExchangeAccountId = harness.ExchangeAccountId,
            Plane = ExchangeDataPlane.Futures,
            PrivateStreamConnectionState = ExchangePrivateStreamConnectionState.Connected,
            DriftStatus = ExchangeStateDriftStatus.InSync,
            LastPrivateStreamEventAtUtc = capturedAtUtc.AddSeconds(-15)
        });
        harness.DbContext.ExchangePositions.Add(new ExchangePosition
        {
            Id = Guid.NewGuid(),
            OwnerUserId = harness.UserId,
            ExchangeAccountId = harness.ExchangeAccountId,
            Plane = ExchangeDataPlane.Futures,
            Symbol = "BTCUSDT",
            PositionSide = "BOTH",
            Quantity = 0.02m,
            EntryPrice = 65000m,
            BreakEvenPrice = 65010m,
            UnrealizedProfit = 12.34m,
            MarginType = "ISOLATED",
            IsolatedWallet = 250m,
            ExchangeUpdatedAtUtc = capturedAtUtc.AddSeconds(-10),
            SyncedAtUtc = capturedAtUtc.AddSeconds(-10)
        });
        await harness.DbContext.SaveChangesAsync();

        var model = await harness.Service.CaptureAsync(
            new MlFeatureSnapshotCaptureRequest(
                harness.UserId,
                harness.BotId,
                harness.ExchangeAccountId,
                strategyVersionId,
                "ml-feature-strategy",
                "BTCUSDT",
                "1m",
                capturedAtUtc,
                94.25m,
                61.5m,
                72,
                3.5m,
                "CandidateScore=94.25; MarketScore=61.5; StrategyScore=72; RiskPenalty=3.5; LiquidityScore=88; VolatilityScore=45; FreshnessState=Fresh; FreshnessReason=FreshSharedKline; FreshnessSource=SharedKlineCache; HistoricalFallbackState=None",
                "Long",
                "Allowed",
                null,
                "Prepared",
                StrategySignalType.Entry,
                ExecutionOrderState.Received,
                false,
                false,
                ExecutionEnvironment.BinanceTestnet),
            CancellationToken.None);

        Assert.Equal("BTCUSDT", model.Symbol);
        Assert.Equal("1m", model.Timeframe);
        Assert.Equal("ml-feature-strategy", model.StrategyKey);
        Assert.Equal("swing-breakout", model.StrategyTemplateKey);
        Assert.Equal(2, model.StrategySchemaVersion);
        Assert.Equal("Long", model.SignalDirection);
        Assert.Equal(94.25m, model.ScannerScore);
        Assert.Equal(61.5m, model.MarketScore);
        Assert.Equal(72, model.StrategyScore);
        Assert.Equal(3.5m, model.RiskPenalty);
        Assert.Equal("BullTrend", model.TrendState);
        Assert.Equal("Compressed", model.VolatilityState);
        Assert.Equal("High", model.LiquidityState);
        Assert.Equal("Fresh", model.MarketFreshnessState);
        Assert.Equal("FreshSharedKline", model.MarketFreshnessReason);
        Assert.Equal("SharedKlineCache", model.MarketFreshnessSource);
        Assert.Equal("None", model.HistoricalFallbackState);
        Assert.Equal("Fresh", model.PrivatePlaneFreshnessState);
        Assert.Equal("PrivatePlaneFresh", model.PrivatePlaneFreshnessReason);
        Assert.Equal("Allowed", model.GuardDecision);
        Assert.Equal("Prepared", model.ExecutionDecision);
        Assert.Equal(StrategySignalType.Entry, model.OrderSignalType);
        Assert.Equal(ExecutionOrderState.Received, model.OrderState);
        Assert.False(model.SubmittedToBroker);
        Assert.False(model.ReduceOnly);
        Assert.Equal(12.34m, model.PositionUnrealizedPnl);
        Assert.Null(model.PositionRealizedPnl);
        Assert.Equal(72m, model.SignalConfidenceScore);
        Assert.Equal(60m, model.TrendAlignmentScore);
        Assert.Equal(45m, model.VolatilityScore);
        Assert.Equal(88m, model.LiquidityScore);
        Assert.Equal(62.34m, model.RecentPerformanceScore);
        Assert.Equal(0m, model.DrawdownPenalty);
        Assert.Equal(100m, model.SampleQualityScore);
        Assert.Equal(100m, model.FeatureCompletenessScore);
        Assert.Equal(76.344m, model.CombinedBaselineScore);
        Assert.Contains("CombinedBaselineScore=76.344", model.BaselineScoreSummary, StringComparison.Ordinal);
        Assert.Equal(76.344m, model.MlShadowScore);
        Assert.Equal(99.475m, model.MlConfidence);
        Assert.Equal("WouldAllow", model.MlShadowDecision);
        Assert.Equal(DeterministicMlShadowScoringService.ModelVersionValue, model.ModelVersion);
        Assert.Equal("AI-1.v1", model.FeatureSchemaVersion);
        Assert.Contains("ReasonCode=AllowedTradeObserved", model.ReasonSummary, StringComparison.Ordinal);
        Assert.False(model.IsDecisionInfluential);
        Assert.Equal("Ready", model.FeatureCompletenessState);
        Assert.Contains("SampleCount=240/200", model.FeatureCompletenessSummary, StringComparison.Ordinal);
        Assert.Equal("MLFS-1.v3", model.SchemaVersion);
        Assert.Equal(ExecutionEnvironment.BinanceTestnet, model.ExecutionEnvironment);

        var persisted = await harness.DbContext.MlFeatureSnapshots.AsNoTracking().SingleAsync();
        Assert.Equal(model.Id, persisted.Id);
    }

    [Fact]
    public async Task CaptureAsync_UsesExplicitUnavailableStates_WhenOptionalContextIsMissing()
    {
        await using var harness = await CreateHarnessAsync("ml-feature-missing-01");

        var model = await harness.Service.CaptureAsync(
            new MlFeatureSnapshotCaptureRequest(
                harness.UserId,
                harness.BotId,
                ExchangeAccountId: null,
                TradingStrategyVersionId: null,
                "ml-feature-strategy",
                "ETHUSDT",
                "1m",
                harness.TimeProvider.GetUtcNow().UtcDateTime,
                ScannerScore: null,
                MarketScore: null,
                StrategyScore: null,
                RiskPenalty: null,
                CandidateScoringSummary: null,
                StrategyDirection: null,
                GuardDecision: "Blocked",
                GuardReasonCode: "MissingFreshSignalData",
                ExecutionDecision: "Blocked",
                OrderSignalType: null,
                OrderState: null,
                SubmittedToBroker: null,
                ReduceOnly: null,
                ExecutionEnvironment: ExecutionEnvironment.BinanceTestnet),
            CancellationToken.None);

        Assert.Equal("Unavailable", model.SignalDirection);
        Assert.Null(model.ScannerScore);
        Assert.Null(model.MarketScore);
        Assert.Null(model.StrategyScore);
        Assert.Null(model.RiskPenalty);
        Assert.Equal("Unavailable", model.TrendState);
        Assert.Equal("Unavailable", model.VolatilityState);
        Assert.Equal("Unavailable", model.LiquidityState);
        Assert.Equal("Unavailable", model.MarketFreshnessState);
        Assert.Equal("None", model.HistoricalFallbackState);
        Assert.Equal("Unavailable", model.PrivatePlaneFreshnessState);
        Assert.Equal("ExchangeAccountMissing", model.PrivatePlaneFreshnessReason);
        Assert.Equal("Unavailable", model.FeatureCompletenessState);
        Assert.Equal("Unavailable:TradingFeatureSnapshotMissing", model.FeatureCompletenessSummary);
        Assert.Null(model.PositionUnrealizedPnl);
        Assert.Null(model.PositionRealizedPnl);
        Assert.Equal(0m, model.SignalConfidenceScore);
        Assert.Equal(25m, model.TrendAlignmentScore);
        Assert.Equal(25m, model.VolatilityScore);
        Assert.Equal(20m, model.LiquidityScore);
        Assert.Equal(50m, model.RecentPerformanceScore);
        Assert.Equal(0m, model.DrawdownPenalty);
        Assert.Equal(0m, model.SampleQualityScore);
        Assert.Equal(0m, model.FeatureCompletenessScore);
        Assert.Equal(13.5m, model.CombinedBaselineScore);
        Assert.Contains("FeatureCompletenessScore=0", model.BaselineScoreSummary, StringComparison.Ordinal);
        Assert.Null(model.MlShadowScore);
        Assert.Equal(0m, model.MlConfidence);
        Assert.Equal("NoDecision", model.MlShadowDecision);
        Assert.Equal(DeterministicMlShadowScoringService.ModelVersionValue, model.ModelVersion);
        Assert.Equal("Unavailable", model.FeatureSchemaVersion);
        Assert.Equal("FeatureSnapshotUnavailable", model.ReasonSummary);
        Assert.False(model.IsDecisionInfluential);
    }

    [Fact]
    public async Task CaptureAsync_MLShadow_NoDecision_WhenShadowModelMissing()
    {
        await using var harness = await CreateHarnessAsync("ml-feature-no-shadow-model", shadowScoringService: null, useDefaultShadowScoringService: false);

        var model = await harness.Service.CaptureAsync(
            new MlFeatureSnapshotCaptureRequest(
                harness.UserId,
                harness.BotId,
                ExchangeAccountId: null,
                TradingStrategyVersionId: null,
                "ml-feature-strategy",
                "BTCUSDT",
                "1m",
                harness.TimeProvider.GetUtcNow().UtcDateTime,
                ScannerScore: 50m,
                MarketScore: 50m,
                StrategyScore: 50,
                RiskPenalty: 0m,
                CandidateScoringSummary: "CandidateScore=50; MarketScore=50; StrategyScore=50; RiskPenalty=0",
                StrategyDirection: "Long",
                GuardDecision: "Allowed",
                GuardReasonCode: null,
                ExecutionDecision: "Prepared",
                OrderSignalType: StrategySignalType.Entry,
                OrderState: ExecutionOrderState.Received,
                SubmittedToBroker: false,
                ReduceOnly: false,
                ExecutionEnvironment: ExecutionEnvironment.BinanceTestnet),
            CancellationToken.None);

        Assert.Null(model.MlShadowScore);
        Assert.Equal(0m, model.MlConfidence);
        Assert.Equal("NoDecision", model.MlShadowDecision);
        Assert.Equal("Unavailable", model.ModelVersion);
        Assert.Equal("Unavailable", model.FeatureSchemaVersion);
        Assert.Equal("ShadowModelUnavailable", model.ReasonSummary);
        Assert.False(model.IsDecisionInfluential);
    }

    [Fact]
    public async Task CaptureAsync_MLShadow_NoDecision_WhenFeatureDataIncomplete()
    {
        await using var harness = await CreateHarnessAsync("ml-feature-shadow-incomplete");
        var capturedAtUtc = harness.TimeProvider.GetUtcNow().UtcDateTime;

        harness.DbContext.TradingFeatureSnapshots.Add(new TradingFeatureSnapshot
        {
            Id = Guid.NewGuid(),
            OwnerUserId = harness.UserId,
            BotId = harness.BotId,
            ExchangeAccountId = harness.ExchangeAccountId,
            StrategyKey = "ml-feature-strategy",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            EvaluatedAtUtc = capturedAtUtc.AddSeconds(-5),
            FeatureAnchorTimeUtc = capturedAtUtc.AddMinutes(-1),
            MarketDataTimestampUtc = capturedAtUtc.AddMinutes(-1),
            FeatureVersion = "AI-1.v1",
            SnapshotState = FeatureSnapshotState.MissingData,
            QualityReasonCode = FeatureSnapshotQualityReason.IncompleteSnapshot,
            SampleCount = 60,
            RequiredSampleCount = 200
        });
        await harness.DbContext.SaveChangesAsync();

        var model = await harness.Service.CaptureAsync(
            new MlFeatureSnapshotCaptureRequest(
                harness.UserId,
                harness.BotId,
                harness.ExchangeAccountId,
                TradingStrategyVersionId: null,
                "ml-feature-strategy",
                "BTCUSDT",
                "1m",
                capturedAtUtc,
                ScannerScore: 55m,
                MarketScore: 55m,
                StrategyScore: 55,
                RiskPenalty: 10m,
                CandidateScoringSummary: "CandidateScore=55; MarketScore=55; StrategyScore=55; RiskPenalty=10; LiquidityScore=55; VolatilityScore=55",
                StrategyDirection: "Long",
                GuardDecision: "Allowed",
                GuardReasonCode: null,
                ExecutionDecision: "Prepared",
                OrderSignalType: StrategySignalType.Entry,
                OrderState: ExecutionOrderState.Received,
                SubmittedToBroker: false,
                ReduceOnly: false,
                ExecutionEnvironment: ExecutionEnvironment.BinanceTestnet),
            CancellationToken.None);

        Assert.Equal("NoDecision", model.MlShadowDecision);
        Assert.Equal(24m, model.MlConfidence);
        Assert.Equal(30.7m, model.MlShadowScore);
        Assert.Equal("AI-1.v1", model.FeatureSchemaVersion);
        Assert.Contains("ReasonCode=FeatureDataIncomplete", model.ReasonSummary, StringComparison.Ordinal);
        Assert.False(model.IsDecisionInfluential);
    }

    [Fact]
    public async Task CaptureAsync_MLShadow_NoInfluence_DoesNotReportWouldAllow_WhenTradeWasBlocked()
    {
        await using var harness = await CreateHarnessAsync("ml-feature-shadow-blocked");
        var capturedAtUtc = harness.TimeProvider.GetUtcNow().UtcDateTime;

        harness.DbContext.TradingFeatureSnapshots.Add(new TradingFeatureSnapshot
        {
            Id = Guid.NewGuid(),
            OwnerUserId = harness.UserId,
            BotId = harness.BotId,
            ExchangeAccountId = harness.ExchangeAccountId,
            StrategyKey = "ml-feature-strategy",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            EvaluatedAtUtc = capturedAtUtc.AddSeconds(-5),
            FeatureAnchorTimeUtc = capturedAtUtc.AddMinutes(-1),
            MarketDataTimestampUtc = capturedAtUtc.AddMinutes(-1),
            FeatureVersion = "AI-1.v1",
            SnapshotState = FeatureSnapshotState.Ready,
            QualityReasonCode = FeatureSnapshotQualityReason.None,
            SampleCount = 240,
            RequiredSampleCount = 200
        });
        await harness.DbContext.SaveChangesAsync();

        var model = await harness.Service.CaptureAsync(
            new MlFeatureSnapshotCaptureRequest(
                harness.UserId,
                harness.BotId,
                harness.ExchangeAccountId,
                TradingStrategyVersionId: null,
                "ml-feature-strategy",
                "BTCUSDT",
                "1m",
                capturedAtUtc,
                ScannerScore: 94.25m,
                MarketScore: 61.5m,
                StrategyScore: 80,
                RiskPenalty: 3.5m,
                CandidateScoringSummary: "CandidateScore=94.25; MarketScore=61.5; StrategyScore=80; RiskPenalty=3.5; LiquidityScore=88; VolatilityScore=45",
                StrategyDirection: "Long",
                GuardDecision: "Blocked",
                GuardReasonCode: "RiskBlocked",
                ExecutionDecision: "Blocked",
                OrderSignalType: StrategySignalType.Entry,
                OrderState: ExecutionOrderState.Received,
                SubmittedToBroker: false,
                ReduceOnly: false,
                ExecutionEnvironment: ExecutionEnvironment.BinanceTestnet),
            CancellationToken.None);

        Assert.Equal(70.95m, model.MlShadowScore);
        Assert.Equal("NoDecision", model.MlShadowDecision);
        Assert.Contains("ReasonCode=BlockedTradeShadowAllowSuppressed", model.ReasonSummary, StringComparison.Ordinal);
        Assert.False(model.IsDecisionInfluential);
    }

    [Fact]
    public async Task CaptureAsync_MLShadow_NoInfluence_DoesNotReportWouldSuppress_WhenTradeWasAllowed()
    {
        await using var harness = await CreateHarnessAsync("ml-feature-shadow-allowed");
        var capturedAtUtc = harness.TimeProvider.GetUtcNow().UtcDateTime;

        harness.DbContext.TradingFeatureSnapshots.Add(new TradingFeatureSnapshot
        {
            Id = Guid.NewGuid(),
            OwnerUserId = harness.UserId,
            BotId = harness.BotId,
            ExchangeAccountId = harness.ExchangeAccountId,
            StrategyKey = "ml-feature-strategy",
            Symbol = "ETHUSDT",
            Timeframe = "1m",
            EvaluatedAtUtc = capturedAtUtc.AddSeconds(-5),
            FeatureAnchorTimeUtc = capturedAtUtc.AddMinutes(-1),
            MarketDataTimestampUtc = capturedAtUtc.AddMinutes(-1),
            FeatureVersion = "AI-1.v1",
            SnapshotState = FeatureSnapshotState.Ready,
            QualityReasonCode = FeatureSnapshotQualityReason.None,
            SampleCount = 240,
            RequiredSampleCount = 200
        });
        await harness.DbContext.SaveChangesAsync();

        var model = await harness.Service.CaptureAsync(
            new MlFeatureSnapshotCaptureRequest(
                harness.UserId,
                harness.BotId,
                harness.ExchangeAccountId,
                TradingStrategyVersionId: null,
                "ml-feature-strategy",
                "ETHUSDT",
                "1m",
                capturedAtUtc,
                ScannerScore: 20m,
                MarketScore: 20m,
                StrategyScore: 10,
                RiskPenalty: 80m,
                CandidateScoringSummary: "CandidateScore=20; MarketScore=20; StrategyScore=10; RiskPenalty=80; LiquidityScore=20; VolatilityScore=20",
                StrategyDirection: "Long",
                GuardDecision: "Allowed",
                GuardReasonCode: null,
                ExecutionDecision: "Prepared",
                OrderSignalType: StrategySignalType.Entry,
                OrderState: ExecutionOrderState.Received,
                SubmittedToBroker: false,
                ReduceOnly: false,
                ExecutionEnvironment: ExecutionEnvironment.BinanceTestnet),
            CancellationToken.None);

        Assert.Equal(10.8m, model.MlShadowScore);
        Assert.Equal("NoDecision", model.MlShadowDecision);
        Assert.Contains("ReasonCode=AllowedTradeShadowSuppressSuppressed", model.ReasonSummary, StringComparison.Ordinal);
        Assert.False(model.IsDecisionInfluential);
    }

    [Fact]
    public void MlFeatureSnapshotStore_DoesNotExposeSensitiveIdentifierOrSecretFields()
    {
        var bannedPropertyNames = new[]
        {
            "OwnerUserId",
            "UserId",
            "ExchangeAccountId",
            "CorrelationId",
            "IdempotencyKey",
            "ExternalOrderId",
            "GuardSummary",
            "ScoringSummary",
            "IndicatorSnapshotJson",
            "RuleResultSnapshotJson",
            "RiskEvaluationJson",
            "SnapshotJson",
            "ApiKey",
            "ApiSecret",
            "ConnectionString"
        };

        var entityProperties = typeof(MlFeatureSnapshot).GetProperties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        var modelProperties = typeof(MlFeatureSnapshotModel).GetProperties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var propertyName in bannedPropertyNames)
        {
            Assert.DoesNotContain(propertyName, entityProperties);
            Assert.DoesNotContain(propertyName, modelProperties);
        }
    }

    private static async Task<TestHarness> CreateHarnessAsync(
        string userId,
        IMlShadowScoringService? shadowScoringService = null,
        bool useDefaultShadowScoringService = true)
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"), databaseRoot)
            .Options;
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero));
        var dbContext = new ApplicationDbContext(options, new TestDataScopeContext(userId, hasIsolationBypass: false));
        var botId = Guid.NewGuid();
        var exchangeAccountId = Guid.NewGuid();
        var tradingStrategyId = Guid.NewGuid();

        dbContext.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = userId,
            NormalizedUserName = userId.ToUpperInvariant(),
            Email = $"{userId}@coinbot.test",
            NormalizedEmail = $"{userId.ToUpperInvariant()}@COINBOT.TEST",
            FullName = userId,
            EmailConfirmed = true
        });
        dbContext.ExchangeAccounts.Add(new ExchangeAccount
        {
            Id = exchangeAccountId,
            OwnerUserId = userId,
            ExchangeName = "Binance",
            DisplayName = "ML Feature Futures",
            CredentialStatus = ExchangeCredentialStatus.Active,
            IsReadOnly = false
        });
        dbContext.TradingBots.Add(new TradingBot
        {
            Id = botId,
            OwnerUserId = userId,
            Name = "ML Feature Bot",
            StrategyKey = "ml-feature-strategy",
            Symbol = "BTCUSDT",
            ExchangeAccountId = exchangeAccountId,
            IsEnabled = true
        });
        dbContext.TradingStrategies.Add(new TradingStrategy
        {
            Id = tradingStrategyId,
            OwnerUserId = userId,
            StrategyKey = "ml-feature-strategy",
            DisplayName = "ML Feature Strategy",
            PromotionState = StrategyPromotionState.LivePublished,
            PublishedMode = ExecutionEnvironment.BinanceTestnet,
            PublishedAtUtc = timeProvider.GetUtcNow().UtcDateTime
        });
        await dbContext.SaveChangesAsync();

        var service = new MlFeatureSnapshotService(
            dbContext,
            Options.Create(new BotExecutionPilotOptions { PrivatePlaneFreshnessThresholdSeconds = 90 }),
            timeProvider,
            NullLogger<MlFeatureSnapshotService>.Instance,
            Options.Create(new ExecutionRuntimeOptions()),
            shadowScoringService ?? (useDefaultShadowScoringService ? new DeterministicMlShadowScoringService() : null));

        return new TestHarness(dbContext, service, timeProvider, userId, botId, exchangeAccountId, tradingStrategyId);
    }

    private sealed record TestHarness(
        ApplicationDbContext DbContext,
        MlFeatureSnapshotService Service,
        AdjustableTimeProvider TimeProvider,
        string UserId,
        Guid BotId,
        Guid ExchangeAccountId,
        Guid TradingStrategyId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => DbContext.DisposeAsync();
    }

    private sealed class TestDataScopeContext(string? userId, bool hasIsolationBypass) : IDataScopeContext
    {
        public string? UserId => userId;

        public bool HasIsolationBypass => hasIsolationBypass;
    }
}
