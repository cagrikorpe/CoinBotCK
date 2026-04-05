using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Infrastructure.Administration;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Observability;
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
        var decisionTrace = await dbContext.DecisionTraces.SingleAsync();
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
        Assert.True(loadedSignal.ExplainabilityPayload.RuleResultSnapshot.EntryRuleResult!.Matched);
        Assert.True(loadedSignal.ExplainabilityPayload.RuleResultSnapshot.RiskRuleResult!.Matched);
        Assert.Equal(100, loadedSignal.ExplainabilityPayload.ConfidenceSnapshot.ScorePercentage);
        Assert.Equal(StrategySignalConfidenceBand.High, loadedSignal.ExplainabilityPayload.ConfidenceSnapshot.Band);
        Assert.False(loadedSignal.ExplainabilityPayload.ConfidenceSnapshot.IsVetoed);
        Assert.True(loadedSignal.ExplainabilityPayload.ConfidenceSnapshot.IsVirtualRiskCheck);
        Assert.Equal("Entry signal created", loadedSignal.ExplainabilityPayload.UiLog.Title);
        Assert.Contains("RSI 28 <= 30", loadedSignal.ExplainabilityPayload.UiLog.Drivers, StringComparer.Ordinal);
        Assert.True(loadedSignal.ExplainabilityPayload.DuplicateSignalSuppression.Enabled);
        Assert.False(loadedSignal.ExplainabilityPayload.DuplicateSignalSuppression.WasSuppressed);
        Assert.Contains(version.Id.ToString("N"), loadedSignal.ExplainabilityPayload.DuplicateSignalSuppression.Fingerprint, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentEquity", persistedSignal.RiskEvaluationJson ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentDailyLossAmount", persistedSignal.RiskEvaluationJson ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(signal.StrategySignalId, decisionTrace.StrategySignalId);
        Assert.Equal("Persisted", decisionTrace.DecisionOutcome);
        Assert.Equal("BTCUSDT", decisionTrace.Symbol);
        Assert.Contains("\"templateKey\":\"rsi-reversal\"", decisionTrace.SnapshotJson, StringComparison.Ordinal);
        Assert.Contains("\"templateRevisionNumber\":1", decisionTrace.SnapshotJson, StringComparison.Ordinal);
        Assert.Contains("\"templateSource\":\"BuiltIn\"", decisionTrace.SnapshotJson, StringComparison.Ordinal);
        Assert.Contains("\"aggregateScore\":100", decisionTrace.SnapshotJson, StringComparison.Ordinal);
        Assert.Contains("\"passedRules\"", decisionTrace.SnapshotJson, StringComparison.Ordinal);
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
        var persistedVeto = await dbContext.TradingStrategySignalVetoes.SingleAsync();
        Assert.Equal(0, result.SuppressedDuplicateCount);
        Assert.True(result.EvaluationResult.EntryMatched);
        Assert.True(result.EvaluationResult.RiskPassed);
        Assert.Empty(await dbContext.TradingStrategySignals.ToListAsync());
        Assert.Equal(1, await dbContext.TradingStrategySignalVetoes.CountAsync());
        Assert.Equal(RiskVetoReasonCode.DailyLossLimitBreached, veto.ConfidenceSnapshot.RiskReasonCode);
        Assert.True(veto.ConfidenceSnapshot.IsVetoed);
        Assert.True(veto.ConfidenceSnapshot.IsVirtualRiskCheck);
        Assert.Equal(39, veto.ConfidenceSnapshot.ScorePercentage);
        Assert.Equal("Entry signal vetoed", veto.UiLog.Title);
        Assert.DoesNotContain("CurrentEquity", persistedVeto.RiskEvaluationJson, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentDailyLossAmount", persistedVeto.RiskEvaluationJson, StringComparison.Ordinal);

        var loadedVeto = await service.GetVetoAsync(veto.StrategySignalVetoId);

        Assert.NotNull(loadedVeto);
        Assert.Equal(RiskVetoReasonCode.DailyLossLimitBreached, loadedVeto!.ConfidenceSnapshot.RiskReasonCode);
        Assert.Equal("Entry signal vetoed", loadedVeto.UiLog.Title);
    }

    [Fact]
    public async Task GenerateAsync_PersistsSignal_WhenUsingIndicatorEngineProducedSnapshot()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        await using var dbContext = CreateDbContext();
        var strategy = CreateStrategy("user-signal-4", "indicator-engine");
        var version = CreateVersion(strategy, 2, CreateIndicatorReadyDefinitionJson());

        dbContext.TradingStrategies.Add(strategy);
        dbContext.TradingStrategyVersions.Add(version);
        dbContext.RiskProfiles.Add(CreateRiskProfile(strategy.OwnerUserId, "EngineReady", 5m, 80m, 2m));
        dbContext.DemoWallets.Add(CreateDemoWallet(strategy.OwnerUserId, "USDT", 10000m));
        await dbContext.SaveChangesAsync();

        var indicatorDataService = new IndicatorDataService(
            new FakeMarketDataService(),
            new IndicatorStreamHub(),
            Microsoft.Extensions.Options.Options.Create(new IndicatorEngineOptions()),
            NullLogger<IndicatorDataService>.Instance);
        var historicalCandles = Enumerable.Range(0, 34)
            .Select(index => CreateHistoricalIndicatorCandle(index))
            .ToArray();
        var latestSnapshot = await indicatorDataService.PrimeAsync("BTCUSDT", "1m", historicalCandles);
        var service = CreateService(dbContext, timeProvider);

        var result = await service.GenerateAsync(
            new GenerateStrategySignalsRequest(
                version.Id,
                new StrategyEvaluationContext(ExecutionEnvironment.Demo, latestSnapshot!)));

        var signal = Assert.Single(result.Signals);

        Assert.True(result.EvaluationResult.EntryMatched);
        Assert.Equal(StrategySignalType.Entry, signal.SignalType);
        Assert.Equal("BTCUSDT", signal.Symbol);
        Assert.Equal("1m", signal.Timeframe);
    }

    [Fact]
    public async Task GenerateAsync_FailsClosed_WhenDraftVersionIsRequested()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        await using var dbContext = CreateDbContext();
        var strategy = CreateStrategy("user-signal-draft", "draft-core");
        var draftVersion = CreateVersion(strategy, 1, CreateDefinitionJson(minimumSampleCount: 100), StrategyVersionStatus.Draft);

        dbContext.TradingStrategies.Add(strategy);
        dbContext.TradingStrategyVersions.Add(draftVersion);
        dbContext.RiskProfiles.Add(CreateRiskProfile(strategy.OwnerUserId, "Balanced", 5m, 80m, 2m));
        dbContext.DemoWallets.Add(CreateDemoWallet(strategy.OwnerUserId, "USDT", 10000m));
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, timeProvider);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAsync(
            new GenerateStrategySignalsRequest(draftVersion.Id, CreateContext(ExecutionEnvironment.Demo, sampleCount: 120, rsiValue: 28m))));

        Assert.Contains("published", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_FailsClosed_WhenExplicitActiveVersionDoesNotMatchRequestedVersion()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        await using var dbContext = CreateDbContext();
        var strategy = CreateStrategy("user-signal-active", "active-core");
        var activeVersion = CreateVersion(strategy, 1, CreateDefinitionJson(minimumSampleCount: 100));
        var inactiveVersion = CreateVersion(strategy, 2, CreateDefinitionJson(minimumSampleCount: 100));
        strategy.UsesExplicitVersionLifecycle = true;
        strategy.ActiveTradingStrategyVersionId = activeVersion.Id;
        strategy.ActiveVersionActivatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        dbContext.TradingStrategies.Add(strategy);
        dbContext.TradingStrategyVersions.AddRange(activeVersion, inactiveVersion);
        dbContext.RiskProfiles.Add(CreateRiskProfile(strategy.OwnerUserId, "Balanced", 5m, 80m, 2m));
        dbContext.DemoWallets.Add(CreateDemoWallet(strategy.OwnerUserId, "USDT", 10000m));
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, timeProvider);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAsync(
            new GenerateStrategySignalsRequest(inactiveVersion.Id, CreateContext(ExecutionEnvironment.Demo, sampleCount: 120, rsiValue: 28m))));

        Assert.Contains("active", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_PersistsNoSignalCandidateTrace_WithoutConfusingItWithRiskVeto()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        await using var dbContext = CreateDbContext();
        var strategy = CreateStrategy("user-signal-none", "no-signal-core");
        var version = CreateVersion(strategy, 1, CreateDefinitionJson(minimumSampleCount: 100, rsiThreshold: 10m));

        dbContext.TradingStrategies.Add(strategy);
        dbContext.TradingStrategyVersions.Add(version);
        dbContext.RiskProfiles.Add(CreateRiskProfile(strategy.OwnerUserId, "Balanced", 5m, 80m, 2m));
        dbContext.DemoWallets.Add(CreateDemoWallet(strategy.OwnerUserId, "USDT", 10000m));
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, timeProvider);
        var result = await service.GenerateAsync(
            new GenerateStrategySignalsRequest(version.Id, CreateContext(ExecutionEnvironment.Demo, sampleCount: 120, rsiValue: 28m)));
        var decisionTrace = await dbContext.DecisionTraces.SingleAsync();

        Assert.Empty(result.Signals);
        Assert.Empty(result.Vetoes);
        Assert.False(result.EvaluationResult.EntryMatched);
        Assert.Equal(0, await dbContext.TradingStrategySignals.CountAsync());
        Assert.Equal(0, await dbContext.TradingStrategySignalVetoes.CountAsync());
        Assert.Equal("NoSignalCandidate", decisionTrace.DecisionOutcome);
        Assert.Contains("\"decisionOutcome\":\"NoSignalCandidate\"", decisionTrace.SnapshotJson, StringComparison.Ordinal);
    }

    private static StrategySignalService CreateService(ApplicationDbContext dbContext, TimeProvider timeProvider)
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

    private static TradingStrategyVersion CreateVersion(TradingStrategy strategy, int versionNumber, string definitionJson, StrategyVersionStatus status = StrategyVersionStatus.Published)
    {
        return new TradingStrategyVersion
        {
            Id = Guid.NewGuid(),
            OwnerUserId = strategy.OwnerUserId,
            TradingStrategyId = strategy.Id,
            SchemaVersion = 2,
            VersionNumber = versionNumber,
            Status = status,
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

    private static string CreateDefinitionJson(int minimumSampleCount, decimal rsiThreshold = 30m)
    {
        return
            $$"""
            {
              "schemaVersion": 2,
              "metadata": {
                "templateKey": "rsi-reversal",
                "templateName": "RSI Reversal",
                "templateRevisionNumber": 1,
                "templateSource": "BuiltIn"
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
                    "value": {{rsiThreshold}}
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

    private static string CreateIndicatorReadyDefinitionJson()
    {
        return
            """
            {
              "schemaVersion": 2,
              "metadata": {
                "templateKey": "indicator-ready",
                "templateName": "Indicator Ready",
                "templateRevisionNumber": 1,
                "templateSource": "BuiltIn"
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
                    "path": "indicator.macd.isReady",
                    "comparison": "equals",
                    "value": true
                  },
                  {
                    "path": "indicator.state",
                    "comparison": "equals",
                    "value": "Ready"
                  }
                ]
              }
            }
            """;
    }

    private static MarketCandleSnapshot CreateHistoricalIndicatorCandle(int minuteOffset)
    {
        var openTimeUtc = new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc).AddMinutes(minuteOffset);
        var closeTimeUtc = openTimeUtc.AddMinutes(1).AddMilliseconds(-1);

        return new MarketCandleSnapshot(
            "BTCUSDT",
            "1m",
            openTimeUtc,
            closeTimeUtc,
            OpenPrice: 100m,
            HighPrice: 100m,
            LowPrice: 100m,
            ClosePrice: 100m,
            Volume: 10m,
            IsClosed: true,
            ReceivedAtUtc: closeTimeUtc,
            Source: "UnitTest.History");
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class FakeMarketDataService : IMarketDataService
    {
        public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask<MarketPriceSnapshot?> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<MarketPriceSnapshot?>(null);
        }

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<SymbolMetadataSnapshot?>(null);
        }

        public async IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(
            IEnumerable<string> symbols,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }
}
