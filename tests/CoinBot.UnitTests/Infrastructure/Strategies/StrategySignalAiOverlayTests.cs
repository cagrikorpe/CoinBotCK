using CoinBot.Application.Abstractions.Ai;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Features;
using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Ai;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Risk;
using CoinBot.Infrastructure.Strategies;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Strategies;

public sealed class StrategySignalAiOverlayTests
{
    [Fact]
    public async Task GenerateAsync_SuppressesEntrySignal_WhenAiFeatureSnapshotIsNotReady()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 4, 6, 12, 0, 0, TimeSpan.Zero));
        await using var dbContext = CreateDbContext();
        var strategy = CreateStrategy("ai-overlay-user-1", "ai-overlay-core");
        var version = CreateVersion(strategy, 1, CreateDefinitionJson());

        dbContext.TradingStrategies.Add(strategy);
        dbContext.TradingStrategyVersions.Add(version);
        dbContext.RiskProfiles.Add(CreateRiskProfile(strategy.OwnerUserId));
        dbContext.DemoWallets.Add(CreateDemoWallet(strategy.OwnerUserId));
        await dbContext.SaveChangesAsync();

        var service = CreateService(
            dbContext,
            timeProvider,
            new AiSignalOptions
            {
                Enabled = true,
                SelectedProvider = DeterministicStubAiSignalProviderAdapter.ProviderNameValue,
                MinimumConfidence = 0.70m
            });

        var result = await service.GenerateAsync(
            new GenerateStrategySignalsRequest(
                version.Id,
                CreateContext(),
                CreateFeatureSnapshot(FeatureSnapshotState.Stale)));
        var decisionTrace = await dbContext.DecisionTraces.SingleAsync();

        Assert.Empty(result.Signals);
        Assert.Empty(result.Vetoes);
        var aiEvaluation = Assert.Single(result.AiEvaluations);
        Assert.True(aiEvaluation.IsFallback);
        Assert.Equal(AiSignalFallbackReason.FeatureSnapshotNotReady, aiEvaluation.FallbackReason);
        Assert.Equal("SuppressedByAi", decisionTrace.DecisionOutcome);
        Assert.Equal("AiFallback", decisionTrace.DecisionReasonType);
        Assert.Equal("AiFeatureSnapshotNotReady", decisionTrace.DecisionReasonCode);
    }

    [Fact]
    public async Task GenerateAsync_SuppressesEntrySignal_WhenAiConfidenceIsBelowThreshold()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 4, 6, 12, 0, 0, TimeSpan.Zero));
        await using var dbContext = CreateDbContext();
        var strategy = CreateStrategy("ai-overlay-user-2", "ai-overlay-core");
        var version = CreateVersion(strategy, 1, CreateDefinitionJson());

        dbContext.TradingStrategies.Add(strategy);
        dbContext.TradingStrategyVersions.Add(version);
        dbContext.RiskProfiles.Add(CreateRiskProfile(strategy.OwnerUserId));
        dbContext.DemoWallets.Add(CreateDemoWallet(strategy.OwnerUserId));
        await dbContext.SaveChangesAsync();

        var service = CreateService(
            dbContext,
            timeProvider,
            new AiSignalOptions
            {
                Enabled = true,
                SelectedProvider = DeterministicStubAiSignalProviderAdapter.ProviderNameValue,
                MinimumConfidence = 0.96m
            });

        var result = await service.GenerateAsync(
            new GenerateStrategySignalsRequest(
                version.Id,
                CreateContext(),
                CreateFeatureSnapshot(FeatureSnapshotState.Ready)));
        var decisionTrace = await dbContext.DecisionTraces.SingleAsync();

        Assert.Empty(result.Signals);
        Assert.Empty(result.Vetoes);
        var aiEvaluation = Assert.Single(result.AiEvaluations);
        Assert.False(aiEvaluation.IsFallback);
        Assert.Equal(AiSignalDirection.Long, aiEvaluation.SignalDirection);
        Assert.Equal("AiOverlay", decisionTrace.DecisionReasonType);
        Assert.Equal("AiLowConfidence", decisionTrace.DecisionReasonCode);
    }

    [Fact]
    public async Task GenerateAsync_PersistsSignal_WithAiEvaluationMetadata_WhenOverlayAllows()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 4, 6, 12, 0, 0, TimeSpan.Zero));
        await using var dbContext = CreateDbContext();
        var strategy = CreateStrategy("ai-overlay-user-3", "ai-overlay-core");
        var version = CreateVersion(strategy, 1, CreateDefinitionJson());

        dbContext.TradingStrategies.Add(strategy);
        dbContext.TradingStrategyVersions.Add(version);
        dbContext.RiskProfiles.Add(CreateRiskProfile(strategy.OwnerUserId));
        dbContext.DemoWallets.Add(CreateDemoWallet(strategy.OwnerUserId));
        await dbContext.SaveChangesAsync();

        var featureSnapshot = CreateFeatureSnapshot(FeatureSnapshotState.Ready);
        var service = CreateService(
            dbContext,
            timeProvider,
            new AiSignalOptions
            {
                Enabled = true,
                SelectedProvider = DeterministicStubAiSignalProviderAdapter.ProviderNameValue,
                MinimumConfidence = 0.70m
            });

        var result = await service.GenerateAsync(
            new GenerateStrategySignalsRequest(
                version.Id,
                CreateContext(),
                featureSnapshot));
        var signal = Assert.Single(result.Signals);
        var aiEvaluation = Assert.Single(result.AiEvaluations);
        var loadedSignal = await service.GetAsync(signal.StrategySignalId);
        var decisionTrace = await dbContext.DecisionTraces.SingleAsync();

        Assert.False(aiEvaluation.IsFallback);
        Assert.Equal(AiSignalDirection.Long, aiEvaluation.SignalDirection);
        Assert.NotNull(loadedSignal);
        Assert.NotNull(loadedSignal!.ExplainabilityPayload.ConfidenceSnapshot.AiEvaluation);
        Assert.Equal(featureSnapshot.Id, loadedSignal.ExplainabilityPayload.ConfidenceSnapshot.AiEvaluation!.FeatureSnapshotId);
        Assert.Contains("\"aiEvaluation\"", decisionTrace.SnapshotJson, StringComparison.Ordinal);
        Assert.Contains(featureSnapshot.Id.ToString(), decisionTrace.SnapshotJson, StringComparison.Ordinal);
        Assert.Contains("DeterministicStub", decisionTrace.SnapshotJson, StringComparison.Ordinal);
    }

    private static StrategySignalService CreateService(ApplicationDbContext dbContext, TimeProvider timeProvider, AiSignalOptions aiSignalOptions)
    {
        var correlationContextAccessor = new CorrelationContextAccessor();

        return new StrategySignalService(
            dbContext,
            new StrategyEvaluatorService(new StrategyRuleParser()),
            new RiskPolicyEvaluator(
                dbContext,
                timeProvider,
                NullLogger<RiskPolicyEvaluator>.Instance),
            new TraceService(
                dbContext,
                correlationContextAccessor,
                timeProvider),
            correlationContextAccessor,
            new AiSignalEvaluator(
                [new DeterministicStubAiSignalProviderAdapter(), new OfflineAiSignalProviderAdapter(), new OpenAiSignalProviderAdapter(), new GeminiAiSignalProviderAdapter()],
                Options.Create(aiSignalOptions),
                timeProvider,
                NullLogger<AiSignalEvaluator>.Instance),
            Options.Create(aiSignalOptions),
            timeProvider,
            NullLogger<StrategySignalService>.Instance);
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private static TradingStrategy CreateStrategy(string ownerUserId, string strategyKey)
    {
        return new TradingStrategy
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            StrategyKey = strategyKey,
            DisplayName = $"{strategyKey} strategy"
        };
    }

    private static TradingStrategyVersion CreateVersion(TradingStrategy strategy, int versionNumber, string definitionJson)
    {
        return new TradingStrategyVersion
        {
            Id = Guid.NewGuid(),
            OwnerUserId = strategy.OwnerUserId,
            TradingStrategyId = strategy.Id,
            SchemaVersion = 2,
            VersionNumber = versionNumber,
            Status = StrategyVersionStatus.Published,
            DefinitionJson = definitionJson,
            PublishedAtUtc = new DateTime(2026, 4, 6, 11, 55, 0, DateTimeKind.Utc)
        };
    }

    private static RiskProfile CreateRiskProfile(string ownerUserId)
    {
        return new RiskProfile
        {
            OwnerUserId = ownerUserId,
            ProfileName = "Balanced",
            MaxDailyLossPercentage = 5m,
            MaxPositionSizePercentage = 80m,
            MaxLeverage = 2m
        };
    }

    private static DemoWallet CreateDemoWallet(string ownerUserId)
    {
        return new DemoWallet
        {
            OwnerUserId = ownerUserId,
            Asset = "USDT",
            AvailableBalance = 10000m,
            ReservedBalance = 0m,
            LastActivityAtUtc = new DateTime(2026, 4, 6, 12, 0, 0, DateTimeKind.Utc)
        };
    }

    private static StrategyEvaluationContext CreateContext()
    {
        return new StrategyEvaluationContext(ExecutionEnvironment.Demo, CreateIndicatorSnapshot());
    }

    private static StrategyIndicatorSnapshot CreateIndicatorSnapshot()
    {
        return new StrategyIndicatorSnapshot(
            Symbol: "btcusdt",
            Timeframe: "1m",
            OpenTimeUtc: new DateTime(2026, 4, 6, 11, 59, 0, DateTimeKind.Utc),
            CloseTimeUtc: new DateTime(2026, 4, 6, 12, 0, 0, DateTimeKind.Utc),
            ReceivedAtUtc: new DateTime(2026, 4, 6, 12, 0, 1, DateTimeKind.Utc),
            SampleCount: 240,
            RequiredSampleCount: 120,
            State: IndicatorDataState.Ready,
            DataQualityReasonCode: DegradedModeReasonCode.None,
            Rsi: new RelativeStrengthIndexSnapshot(14, IsReady: true, Value: 28m),
            Macd: new MovingAverageConvergenceDivergenceSnapshot(12, 26, 9, true, 1.4m, 1.1m, 0.3m),
            Bollinger: new BollingerBandsSnapshot(20, 2m, true, 62000m, 62500m, 61500m, 250m),
            Source: "UnitTest");
    }

    private static TradingFeatureSnapshotModel CreateFeatureSnapshot(FeatureSnapshotState state)
    {
        return new TradingFeatureSnapshotModel(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "ai-overlay-user",
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            "ai-overlay-core",
            "BTCUSDT",
            "1m",
            new DateTime(2026, 4, 6, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 6, 11, 59, 59, DateTimeKind.Utc),
            "AI-1.v1",
            state,
            state == FeatureSnapshotState.Ready ? DegradedModeReasonCode.None : DegradedModeReasonCode.MarketDataLatencyBreached,
            240,
            200,
            64000m,
            new TradingTrendFeatureSnapshot(63800m, 63500m, 62000m, 63750m, 63690m),
            new TradingMomentumFeatureSnapshot(29m, 1.5m, 1.0m, 0.5m, 21m, 25m, 13m, -0.8m),
            new TradingVolatilityFeatureSnapshot(450m, 0.18m, 0.09m, -0.35m, 63200m, 62800m),
            new TradingVolumeFeatureSnapshot(1.40m, 1.35m, 1250m),
            new TradingContextFeatureSnapshot(ExchangeDataPlane.Futures, ExecutionEnvironment.Demo, false, false, null, null, null, null, null),
            "Ready feature snapshot.",
            "RSI oversold; MACD improving; relative volume elevated.",
            "TrendUp",
            "Bullish",
            "Contained",
            null);
    }

    private static string CreateDefinitionJson()
    {
        return
            """
            {
              "schemaVersion": 2,
              "metadata": {
                "templateKey": "ai-overlay-template",
                "templateName": "AI Overlay Template"
              },
              "entry": {
                "operator": "all",
                "rules": [
                  {
                    "path": "context.mode",
                    "comparison": "equals",
                    "value": "Demo"
                  },
                  {
                    "path": "indicator.rsi.value",
                    "comparison": "lessThanOrEqual",
                    "value": 30
                  }
                ]
              },
              "risk": {
                "operator": "all",
                "rules": [
                  {
                    "path": "indicator.sampleCount",
                    "comparison": "greaterThanOrEqual",
                    "value": 100
                  }
                ]
              }
            }
            """;
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }
}

