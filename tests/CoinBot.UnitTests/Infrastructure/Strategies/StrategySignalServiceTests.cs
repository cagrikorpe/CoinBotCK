using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Risk;
using CoinBot.Infrastructure.Strategies;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoinBot.UnitTests.Infrastructure.Strategies;

public sealed class StrategySignalServiceTests
{
    [Fact]
    public async Task GenerateAsync_PersistsEntrySignal_WithExplainabilityPayloadAndVersionLink()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        await using var dbContext = CreateDbContext();
        var strategy = CreateStrategy("user-signal-1", "momentum-core");
        var version = CreateVersion(strategy, 3, CreateDefinitionJson(minimumSampleCount: 100));

        dbContext.TradingStrategies.Add(strategy);
        dbContext.TradingStrategyVersions.Add(version);
        dbContext.RiskProfiles.Add(CreateRiskProfile(strategy.OwnerUserId, "Balanced", 5m, 80m, 2m));
        dbContext.DemoWallets.Add(CreateDemoWallet(strategy.OwnerUserId, "USDT", 10000m));
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, timeProvider);

        var result = await service.GenerateAsync(
            new GenerateStrategySignalsRequest(version.Id, CreateContext(ExecutionEnvironment.Demo, sampleCount: 120, rsiValue: 28m)));

        var signal = Assert.Single(result.Signals);
        var persistedSignal = await dbContext.TradingStrategySignals.SingleAsync();
        var loadedSignal = await service.GetAsync(signal.StrategySignalId);

        Assert.Equal(0, result.SuppressedDuplicateCount);
        Assert.Empty(result.Vetoes);
        Assert.True(result.EvaluationResult.EntryMatched);
        Assert.True(result.EvaluationResult.RiskPassed);
        Assert.Equal(strategy.Id, signal.TradingStrategyId);
        Assert.Equal(version.Id, signal.TradingStrategyVersionId);
        Assert.Equal(version.VersionNumber, signal.StrategyVersionNumber);
        Assert.Equal(version.SchemaVersion, signal.StrategySchemaVersion);
        Assert.Equal(StrategySignalType.Entry, signal.SignalType);
        Assert.Equal("BTCUSDT", signal.Symbol);
        Assert.Equal("1m", signal.Timeframe);
        Assert.Equal(timeProvider.GetUtcNow().UtcDateTime, persistedSignal.GeneratedAtUtc);
        Assert.False(string.IsNullOrWhiteSpace(persistedSignal.IndicatorSnapshotJson));
        Assert.False(string.IsNullOrWhiteSpace(persistedSignal.RuleResultSnapshotJson));
        Assert.NotNull(loadedSignal);
        Assert.Equal(1, loadedSignal!.ExplainabilityPayload.ExplainabilitySchemaVersion);
        Assert.Equal(version.Id, loadedSignal.ExplainabilityPayload.TradingStrategyVersionId);
        Assert.Equal("BTCUSDT", loadedSignal.ExplainabilityPayload.IndicatorSnapshot.Symbol);
        Assert.NotNull(loadedSignal.ExplainabilityPayload.RuleResultSnapshot.EntryRuleResult);
        Assert.NotNull(loadedSignal.ExplainabilityPayload.RuleResultSnapshot.RiskRuleResult);
        Assert.NotNull(loadedSignal.ExplainabilityPayload.RiskEvaluation);
        Assert.True(loadedSignal.ExplainabilityPayload.RuleResultSnapshot.EntryRuleResult!.Matched);
        Assert.True(loadedSignal.ExplainabilityPayload.RuleResultSnapshot.RiskRuleResult!.Matched);
        Assert.False(loadedSignal.ExplainabilityPayload.RiskEvaluation!.IsVetoed);
        Assert.True(loadedSignal.ExplainabilityPayload.RiskEvaluation.Snapshot.IsVirtualCheck);
    }

    [Fact]
    public async Task GenerateAsync_SuppressesDuplicateSignal_ForSameVersionAndIndicatorCloseTime()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        await using var dbContext = CreateDbContext();
        var strategy = CreateStrategy("user-signal-2", "breakout-core");
        var version = CreateVersion(strategy, 1, CreateDefinitionJson(minimumSampleCount: 100));

        dbContext.TradingStrategies.Add(strategy);
        dbContext.TradingStrategyVersions.Add(version);
        dbContext.RiskProfiles.Add(CreateRiskProfile(strategy.OwnerUserId, "Breakout", 5m, 80m, 2m));
        dbContext.DemoWallets.Add(CreateDemoWallet(strategy.OwnerUserId, "USDT", 10000m));
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, timeProvider);
        var request = new GenerateStrategySignalsRequest(
            version.Id,
            CreateContext(ExecutionEnvironment.Demo, sampleCount: 120, rsiValue: 28m));

        var firstResult = await service.GenerateAsync(request);
        var secondResult = await service.GenerateAsync(request);

        Assert.Single(firstResult.Signals);
        Assert.Empty(firstResult.Vetoes);
        Assert.Empty(secondResult.Signals);
        Assert.Empty(secondResult.Vetoes);
        Assert.Equal(1, secondResult.SuppressedDuplicateCount);
        Assert.Equal(1, await dbContext.TradingStrategySignals.CountAsync());
    }

    [Fact]
    public async Task GenerateAsync_PersistsVetoReport_WhenRiskFails()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        await using var dbContext = CreateDbContext();
        var strategy = CreateStrategy("user-signal-3", "risk-core");
        var version = CreateVersion(strategy, 5, CreateDefinitionJson(minimumSampleCount: 100));

        dbContext.TradingStrategies.Add(strategy);
        dbContext.TradingStrategyVersions.Add(version);
        dbContext.RiskProfiles.Add(CreateRiskProfile(strategy.OwnerUserId, "Conservative", 5m, 90m, 2m));
        dbContext.DemoWallets.Add(CreateDemoWallet(strategy.OwnerUserId, "USDT", 10000m));
        dbContext.DemoLedgerTransactions.Add(CreateLossTransaction(strategy.OwnerUserId, -750m));
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, timeProvider);

        var result = await service.GenerateAsync(
            new GenerateStrategySignalsRequest(version.Id, CreateContext(ExecutionEnvironment.Demo, sampleCount: 120, rsiValue: 28m)));

        Assert.Empty(result.Signals);
        var veto = Assert.Single(result.Vetoes);
        Assert.Equal(0, result.SuppressedDuplicateCount);
        Assert.True(result.EvaluationResult.EntryMatched);
        Assert.True(result.EvaluationResult.RiskPassed);
        Assert.Empty(await dbContext.TradingStrategySignals.ToListAsync());
        Assert.Equal(1, await dbContext.TradingStrategySignalVetoes.CountAsync());
        Assert.Equal(RiskVetoReasonCode.DailyLossLimitBreached, veto.RiskEvaluation.ReasonCode);
        Assert.True(veto.RiskEvaluation.Snapshot.IsVirtualCheck);
        Assert.Equal(750m, veto.RiskEvaluation.Snapshot.CurrentDailyLossAmount);

        var loadedVeto = await service.GetVetoAsync(veto.StrategySignalVetoId);

        Assert.NotNull(loadedVeto);
        Assert.Equal(RiskVetoReasonCode.DailyLossLimitBreached, loadedVeto!.RiskEvaluation.ReasonCode);
    }

    private static StrategySignalService CreateService(ApplicationDbContext dbContext, TimeProvider timeProvider)
    {
        return new StrategySignalService(
            dbContext,
            new StrategyEvaluatorService(new StrategyRuleParser()),
            new RiskPolicyEvaluator(
                dbContext,
                timeProvider,
                NullLogger<RiskPolicyEvaluator>.Instance),
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
            SchemaVersion = 1,
            VersionNumber = versionNumber,
            Status = StrategyVersionStatus.Published,
            DefinitionJson = definitionJson,
            PublishedAtUtc = new DateTime(2026, 3, 22, 11, 55, 0, DateTimeKind.Utc)
        };
    }

    private static RiskProfile CreateRiskProfile(
        string ownerUserId,
        string profileName,
        decimal maxDailyLossPercentage,
        decimal maxPositionSizePercentage,
        decimal maxLeverage)
    {
        return new RiskProfile
        {
            OwnerUserId = ownerUserId,
            ProfileName = profileName,
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

    private static DemoLedgerTransaction CreateLossTransaction(string ownerUserId, decimal realizedPnlDelta)
    {
        return new DemoLedgerTransaction
        {
            OwnerUserId = ownerUserId,
            OperationId = Guid.NewGuid().ToString("N"),
            TransactionType = DemoLedgerTransactionType.FillApplied,
            PositionScopeKey = "risk-position",
            Symbol = "BTCUSDT",
            QuoteAsset = "USDT",
            RealizedPnlDelta = realizedPnlDelta,
            OccurredAtUtc = new DateTime(2026, 3, 22, 11, 30, 0, DateTimeKind.Utc)
        };
    }

    private static StrategyEvaluationContext CreateContext(ExecutionEnvironment mode, int sampleCount, decimal? rsiValue)
    {
        return new StrategyEvaluationContext(mode, CreateIndicatorSnapshot(sampleCount, rsiValue));
    }

    private static StrategyIndicatorSnapshot CreateIndicatorSnapshot(int sampleCount, decimal? rsiValue)
    {
        return new StrategyIndicatorSnapshot(
            Symbol: "btcusdt",
            Timeframe: "1m",
            OpenTimeUtc: new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc),
            CloseTimeUtc: new DateTime(2026, 3, 22, 12, 1, 0, DateTimeKind.Utc),
            ReceivedAtUtc: new DateTime(2026, 3, 22, 12, 1, 1, DateTimeKind.Utc),
            SampleCount: sampleCount,
            RequiredSampleCount: 120,
            State: IndicatorDataState.Ready,
            DataQualityReasonCode: DegradedModeReasonCode.None,
            Rsi: new RelativeStrengthIndexSnapshot(14, IsReady: true, Value: rsiValue),
            Macd: new MovingAverageConvergenceDivergenceSnapshot(
                12,
                26,
                9,
                IsReady: true,
                MacdLine: 1.4m,
                SignalLine: 1.1m,
                Histogram: 0.3m),
            Bollinger: new BollingerBandsSnapshot(
                20,
                2m,
                IsReady: true,
                MiddleBand: 62000m,
                UpperBand: 62500m,
                LowerBand: 61500m,
                StandardDeviation: 250m),
            Source: "UnitTest");
    }

    private static string CreateDefinitionJson(int minimumSampleCount)
    {
        return
            $$"""
            {
              "schemaVersion": 1,
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
                    "value": {{minimumSampleCount}}
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
