using CoinBot.Application.Abstractions.Ai;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Ai;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.UnitTests.Infrastructure.Ai;

public sealed class TrainingDatasetBuilderServiceTests
{
    [Fact]
    public async Task BuildAsync_ProducesLeakageControlledTrainingRow_WithDirectionalExcursionsAndExecutionLabels()
    {
        await using var dbContext = CreateDbContext();
        var builder = CreateService(dbContext, new DateTime(2026, 4, 24, 12, 30, 0, DateTimeKind.Utc));
        var featureSnapshotId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var decisionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var strategySignalId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var featureAnchorTimeUtc = new DateTime(2026, 4, 24, 12, 0, 0, DateTimeKind.Utc);

        SeedFeatureSnapshot(dbContext, featureSnapshotId, featureAnchorTimeUtc);
        SeedShadowDecision(
            dbContext,
            decisionId,
            featureSnapshotId,
            strategySignalId,
            featureAnchorTimeUtc,
            finalAction: "ShadowOnly",
            hypotheticalSubmitAllowed: true,
            hypotheticalBlockReason: null,
            noSubmitReason: "ShadowModeActive",
            aiDirection: "Long");
        SeedShadowOutcome(
            dbContext,
            decisionId,
            featureAnchorTimeUtc,
            futureCloseTimeUtc: featureAnchorTimeUtc.AddMinutes(3),
            referenceClosePrice: 100m,
            realizedReturn: 0.06m,
            outcomeScore: 0.80m);
        SeedExecutionOrder(dbContext, strategySignalId, side: ExecutionOrderSide.Buy, stopLossPrice: 97m, takeProfitPrice: 108m);
        SeedHistoricalCandles(
            dbContext,
            ("BTCUSDT", "1m", featureAnchorTimeUtc.AddMinutes(1), 101m, 103m, 99m),
            ("BTCUSDT", "1m", featureAnchorTimeUtc.AddMinutes(2), 109m, 110m, 95m),
            ("BTCUSDT", "1m", featureAnchorTimeUtc.AddMinutes(3), 106m, 106m, 97m));
        await dbContext.SaveChangesAsync();

        var dataset = await builder.BuildAsync(new TrainingDatasetBuildRequest("ml-user"));
        var row = Assert.Single(dataset.Rows);

        Assert.Equal(1, dataset.SourceRowCount);
        Assert.Equal(1, dataset.TrainingEligibleRowCount);
        Assert.True(row.IsTrainingEligible);
        Assert.Equal("Train", row.SplitBucket);
        Assert.Equal("0.1", row.Values["label_mfe_return"]);
        Assert.Equal("-0.05", row.Values["label_mae_return"]);
        Assert.Equal("true", row.Values["label_take_profit_touched"]);
        Assert.Equal("true", row.Values["label_stop_loss_touched"]);
        Assert.Equal("true", row.Values["label_has_execution_order"]);
        Assert.Equal("true", row.Values["label_was_submitted_to_broker"]);
        Assert.Equal("true", row.Values["label_was_filled"]);
        Assert.Equal("true", row.Values["meta_is_training_eligible"]);
        Assert.DoesNotContain(dataset.Columns, column => column.Name == "feature_last_decision_outcome");
        Assert.DoesNotContain(dataset.Columns, column => column.Name == "feature_feature_summary");
        Assert.Contains(dataset.LabelDefinitions, definition => definition.Name == "label_mfe_return");
        Assert.Contains(dataset.LeakageRules, rule => rule.ColumnName == "feature_last_decision_outcome");
    }

    [Fact]
    public async Task BuildAsync_ScoresMissingOutcome_AndCarriesBlockedTradeLabels()
    {
        await using var dbContext = CreateDbContext();
        var builder = CreateService(dbContext, new DateTime(2026, 4, 24, 13, 0, 0, DateTimeKind.Utc));
        var featureSnapshotId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var decisionId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var featureAnchorTimeUtc = new DateTime(2026, 4, 24, 12, 10, 0, DateTimeKind.Utc);

        SeedFeatureSnapshot(dbContext, featureSnapshotId, featureAnchorTimeUtc);
        SeedShadowDecision(
            dbContext,
            decisionId,
            featureSnapshotId,
            strategySignalId: null,
            featureAnchorTimeUtc,
            finalAction: "NoSubmit",
            hypotheticalSubmitAllowed: false,
            hypotheticalBlockReason: "TradeMasterDisarmed",
            noSubmitReason: "TradeMasterDisarmed",
            aiDirection: "Long");
        SeedHistoricalCandles(
            dbContext,
            ("BTCUSDT", "1m", featureAnchorTimeUtc, 100m, 100m, 100m),
            ("BTCUSDT", "1m", featureAnchorTimeUtc.AddMinutes(1), 101m, 101m, 99m),
            ("BTCUSDT", "1m", featureAnchorTimeUtc.AddMinutes(2), 102m, 103m, 101m));
        await dbContext.SaveChangesAsync();

        var dataset = await builder.BuildAsync(new TrainingDatasetBuildRequest("ml-user", HorizonValue: 2));
        var row = Assert.Single(dataset.Rows);

        Assert.Single(dbContext.AiShadowDecisionOutcomes);
        Assert.Equal("true", row.Values["label_was_blocked"]);
        Assert.Equal("TradeMasterDisarmed", row.Values["label_block_reason"]);
        Assert.Equal("false", row.Values["label_has_execution_order"]);
        Assert.Equal("0.02", row.Values["label_realized_return"]);
    }

    [Fact]
    public async Task ExportCsvAsync_UsesDeterministicTimeSeriesSplit_AndExcludesLeakyColumns()
    {
        await using var dbContext = CreateDbContext();
        var builder = CreateService(dbContext, new DateTime(2026, 4, 24, 14, 0, 0, DateTimeKind.Utc));
        var startTimeUtc = new DateTime(2026, 4, 24, 10, 0, 0, DateTimeKind.Utc);

        for (var index = 0; index < 5; index++)
        {
            var featureSnapshotId = Guid.NewGuid();
            var decisionId = Guid.NewGuid();
            var strategySignalId = Guid.NewGuid();
            var anchorTimeUtc = startTimeUtc.AddMinutes(index * 5);

            SeedFeatureSnapshot(dbContext, featureSnapshotId, anchorTimeUtc);
            SeedShadowDecision(
                dbContext,
                decisionId,
                featureSnapshotId,
                strategySignalId,
                anchorTimeUtc,
                finalAction: "ShadowOnly",
                hypotheticalSubmitAllowed: true,
                hypotheticalBlockReason: null,
                noSubmitReason: "ShadowModeActive",
                aiDirection: "Long");
            SeedShadowOutcome(
                dbContext,
                decisionId,
                anchorTimeUtc,
                futureCloseTimeUtc: anchorTimeUtc.AddMinutes(1),
                referenceClosePrice: 100m,
                realizedReturn: 0.01m + (index * 0.001m),
                outcomeScore: 0.55m);
            SeedHistoricalCandles(
                dbContext,
                ("BTCUSDT", "1m", anchorTimeUtc.AddMinutes(1), 101m + index, 102m + index, 99m + index));
        }

        await dbContext.SaveChangesAsync();

        var dataset = await builder.BuildAsync(new TrainingDatasetBuildRequest("ml-user"));
        var export = await builder.ExportCsvAsync(new TrainingDatasetBuildRequest("ml-user"));
        var splitBuckets = dataset.Rows.Select(row => row.SplitBucket).Distinct(StringComparer.Ordinal).ToArray();

        Assert.Equal(5, dataset.RowCount);
        Assert.Contains("Train", splitBuckets);
        Assert.Contains("Validation", splitBuckets);
        Assert.Contains("Test", splitBuckets);
        Assert.Contains("meta_split_bucket", export.ColumnOrder);
        Assert.DoesNotContain("feature_last_decision_outcome", export.CsvContent, StringComparison.Ordinal);
        Assert.DoesNotContain("feature_top_signal_hints", export.CsvContent, StringComparison.Ordinal);
        Assert.Contains("label_outcome_score", export.CsvContent, StringComparison.Ordinal);
        Assert.Contains("training-dataset-all-symbols-all-timeframes-BarsForward-1-20260424.csv", export.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_UsesPrecomputedOutcome_WhenCoverageAlreadyExists()
    {
        await using var dbContext = CreateDbContext();
        var timeProvider = new FixedTimeProvider(new DateTime(2026, 4, 24, 14, 30, 0, DateTimeKind.Utc));
        var builder = new TrainingDatasetBuilderService(
            dbContext,
            new ThrowingCoverageAiShadowDecisionService(),
            timeProvider);
        var featureSnapshotId = Guid.Parse("f1111111-1111-1111-1111-111111111111");
        var decisionId = Guid.Parse("f2222222-2222-2222-2222-222222222222");
        var anchorTimeUtc = new DateTime(2026, 4, 24, 12, 20, 0, DateTimeKind.Utc);

        SeedFeatureSnapshot(dbContext, featureSnapshotId, anchorTimeUtc);
        SeedShadowDecision(
            dbContext,
            decisionId,
            featureSnapshotId,
            strategySignalId: null,
            anchorTimeUtc,
            finalAction: "ShadowOnly",
            hypotheticalSubmitAllowed: true,
            hypotheticalBlockReason: null,
            noSubmitReason: "ShadowModeActive",
            aiDirection: "Long");
        SeedShadowOutcome(
            dbContext,
            decisionId,
            anchorTimeUtc,
            futureCloseTimeUtc: anchorTimeUtc.AddMinutes(1),
            referenceClosePrice: 100m,
            realizedReturn: 0.01m,
            outcomeScore: 0.40m);
        await dbContext.SaveChangesAsync();

        var dataset = await builder.BuildAsync(new TrainingDatasetBuildRequest("ml-user"));
        var row = Assert.Single(dataset.Rows);

        Assert.Equal(1, dataset.RowCount);
        Assert.Equal("0.4", row.Values["label_outcome_score"]);
        Assert.Single(dbContext.AiShadowDecisionOutcomes);
    }

    private static TrainingDatasetBuilderService CreateService(ApplicationDbContext dbContext, DateTime utcNow)
    {
        var timeProvider = new FixedTimeProvider(utcNow);
        return new TrainingDatasetBuilderService(
            dbContext,
            new AiShadowDecisionService(dbContext, timeProvider),
            timeProvider);
    }

    private static void SeedFeatureSnapshot(ApplicationDbContext dbContext, Guid featureSnapshotId, DateTime featureAnchorTimeUtc)
    {
        dbContext.TradingFeatureSnapshots.Add(new TradingFeatureSnapshot
        {
            Id = featureSnapshotId,
            OwnerUserId = "ml-user",
            BotId = Guid.Parse("12121212-1212-1212-1212-121212121212"),
            StrategyKey = "ml-shadow-core",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            EvaluatedAtUtc = featureAnchorTimeUtc.AddSeconds(5),
            FeatureAnchorTimeUtc = featureAnchorTimeUtc,
            MarketDataTimestampUtc = featureAnchorTimeUtc,
            FeatureVersion = "AI-1.v1",
            SnapshotState = FeatureSnapshotState.Ready,
            QualityReasonCode = FeatureSnapshotQualityReason.None,
            MarketDataReasonCode = DegradedModeReasonCode.None,
            SampleCount = 240,
            RequiredSampleCount = 200,
            ReferencePrice = 100m,
            Ema20 = 101m,
            Ema50 = 99m,
            Ema200 = 95m,
            Alma = 100.5m,
            Frama = 100.2m,
            Rsi = 61m,
            MacdLine = 1.2m,
            MacdSignal = 0.8m,
            MacdHistogram = 0.4m,
            KdjK = 55m,
            KdjD = 49m,
            KdjJ = 67m,
            FisherTransform = 0.33m,
            Atr = 1.5m,
            BollingerPercentB = 0.62m,
            BollingerBandWidth = 0.18m,
            KeltnerChannelRelation = 0.45m,
            PmaxValue = 98m,
            ChandelierExit = 97m,
            VolumeSpikeRatio = 1.2m,
            RelativeVolume = 1.1m,
            Obv = 2200m,
            Mfi = 58m,
            KlingerOscillator = 12m,
            KlingerSignal = 8m,
            Plane = ExchangeDataPlane.Futures,
            TradingMode = ExecutionEnvironment.Live,
            HasOpenPosition = false,
            IsInCooldown = false,
            PrimaryRegime = "BullTrend",
            MomentumBias = "Bullish",
            VolatilityState = "Normal",
            FeatureSummary = "State=Ready",
            TopSignalHints = "Momentum+Trend",
            NormalizationMeta = "compact",
            SnapshotKey = $"snapshot-{featureSnapshotId:N}",
            CorrelationId = $"corr-{featureSnapshotId:N}"
        });
    }

    private static void SeedShadowDecision(
        ApplicationDbContext dbContext,
        Guid decisionId,
        Guid featureSnapshotId,
        Guid? strategySignalId,
        DateTime featureAnchorTimeUtc,
        string finalAction,
        bool hypotheticalSubmitAllowed,
        string? hypotheticalBlockReason,
        string noSubmitReason,
        string aiDirection)
    {
        dbContext.AiShadowDecisions.Add(new AiShadowDecision
        {
            Id = decisionId,
            OwnerUserId = "ml-user",
            BotId = Guid.Parse("12121212-1212-1212-1212-121212121212"),
            FeatureSnapshotId = featureSnapshotId,
            StrategySignalId = strategySignalId,
            CorrelationId = $"shadow-corr-{decisionId:N}",
            StrategyKey = "ml-shadow-core",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            EvaluatedAtUtc = featureAnchorTimeUtc.AddSeconds(10),
            MarketDataTimestampUtc = featureAnchorTimeUtc,
            FeatureVersion = "AI-1.v1",
            StrategyDirection = aiDirection,
            StrategyConfidenceScore = 78,
            StrategyDecisionOutcome = "Persisted",
            StrategyDecisionCode = "Entry",
            StrategySummary = "Strategy summary",
            AiDirection = aiDirection,
            AiConfidence = 0.78m,
            AiReasonSummary = "AI reason",
            AiProviderName = "DeterministicStub",
            AiProviderModel = "deterministic-v1",
            AiLatencyMs = 5,
            TradingMode = ExecutionEnvironment.Live,
            Plane = ExchangeDataPlane.Futures,
            FinalAction = finalAction,
            HypotheticalSubmitAllowed = hypotheticalSubmitAllowed,
            HypotheticalBlockReason = hypotheticalBlockReason,
            NoSubmitReason = noSubmitReason,
            AgreementState = "Agreement"
        });
    }

    private static void SeedShadowOutcome(
        ApplicationDbContext dbContext,
        Guid decisionId,
        DateTime referenceCloseTimeUtc,
        DateTime futureCloseTimeUtc,
        decimal referenceClosePrice,
        decimal realizedReturn,
        decimal outcomeScore)
    {
        dbContext.AiShadowDecisionOutcomes.Add(new AiShadowDecisionOutcome
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "ml-user",
            AiShadowDecisionId = decisionId,
            BotId = Guid.Parse("12121212-1212-1212-1212-121212121212"),
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            DecisionEvaluatedAtUtc = referenceCloseTimeUtc.AddSeconds(10),
            HorizonKind = AiShadowOutcomeHorizonKind.BarsForward,
            HorizonValue = 1,
            OutcomeState = AiShadowOutcomeState.Scored,
            OutcomeScore = outcomeScore,
            RealizedDirectionality = realizedReturn >= 0m ? "Long" : "Short",
            ConfidenceBucket = "High",
            FutureDataAvailability = AiShadowFutureDataAvailability.Available,
            ReferenceCandleCloseTimeUtc = referenceCloseTimeUtc,
            FutureCandleCloseTimeUtc = futureCloseTimeUtc,
            ReferenceClosePrice = referenceClosePrice,
            FutureClosePrice = referenceClosePrice * (1m + realizedReturn),
            RealizedReturn = realizedReturn,
            FalsePositive = false,
            FalseNeutral = false,
            Overtrading = false,
            SuppressionCandidate = false,
            SuppressionAligned = false,
            ScoredAtUtc = futureCloseTimeUtc.AddSeconds(5)
        });
    }

    private static void SeedExecutionOrder(
        ApplicationDbContext dbContext,
        Guid strategySignalId,
        ExecutionOrderSide side,
        decimal? stopLossPrice,
        decimal? takeProfitPrice)
    {
        dbContext.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "ml-user",
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = strategySignalId,
            SignalType = StrategySignalType.Entry,
            BotId = Guid.Parse("12121212-1212-1212-1212-121212121212"),
            Plane = ExchangeDataPlane.Futures,
            StrategyKey = "ml-shadow-core",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = side,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.01m,
            Price = 100m,
            FilledQuantity = 0.01m,
            AverageFillPrice = 100m,
            StopLossPrice = stopLossPrice,
            TakeProfitPrice = takeProfitPrice,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            State = ExecutionOrderState.Filled,
            IdempotencyKey = $"idem-{strategySignalId:N}",
            RootCorrelationId = $"corr-{strategySignalId:N}",
            SubmittedToBroker = true,
            SubmittedAtUtc = new DateTime(2026, 4, 24, 12, 0, 20, DateTimeKind.Utc),
            LastFilledAtUtc = new DateTime(2026, 4, 24, 12, 0, 30, DateTimeKind.Utc),
            LastStateChangedAtUtc = new DateTime(2026, 4, 24, 12, 0, 30, DateTimeKind.Utc)
        });
    }

    private static void SeedHistoricalCandles(
        ApplicationDbContext dbContext,
        params (string Symbol, string Timeframe, DateTime CloseTimeUtc, decimal ClosePrice, decimal HighPrice, decimal LowPrice)[] candles)
    {
        foreach (var candle in candles)
        {
            dbContext.HistoricalMarketCandles.Add(new HistoricalMarketCandle
            {
                Id = Guid.NewGuid(),
                Symbol = candle.Symbol,
                Interval = candle.Timeframe,
                OpenTimeUtc = candle.CloseTimeUtc.AddMinutes(-1),
                CloseTimeUtc = candle.CloseTimeUtc,
                OpenPrice = candle.ClosePrice,
                HighPrice = candle.HighPrice,
                LowPrice = candle.LowPrice,
                ClosePrice = candle.ClosePrice,
                Volume = 1000m,
                ReceivedAtUtc = candle.CloseTimeUtc,
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

    private sealed class ThrowingCoverageAiShadowDecisionService : IAiShadowDecisionService
    {
        public Task<AiShadowDecisionSnapshot> CaptureAsync(AiShadowDecisionWriteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AiShadowDecisionSnapshot?> GetLatestAsync(string userId, Guid botId, string symbol, string timeframe, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyCollection<AiShadowDecisionSnapshot>> ListRecentAsync(string userId, Guid botId, string symbol, string timeframe, int take = 20, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AiShadowDecisionSummarySnapshot> GetSummaryAsync(string userId, Guid botId, string symbol, string timeframe, int take = 200, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AiShadowDecisionOutcomeSnapshot> ScoreOutcomeAsync(string userId, Guid decisionId, AiShadowOutcomeHorizonKind horizonKind = AiShadowOutcomeDefaults.OfficialHorizonKind, int horizonValue = AiShadowOutcomeDefaults.OfficialHorizonValue, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Precomputed outcome should have been reused.");

        public Task<int> EnsureOutcomeCoverageAsync(string userId, AiShadowOutcomeHorizonKind horizonKind = AiShadowOutcomeDefaults.OfficialHorizonKind, int horizonValue = AiShadowOutcomeDefaults.OfficialHorizonValue, int take = 200, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AiShadowDecisionOutcomeSummarySnapshot> GetOutcomeSummaryAsync(string userId, AiShadowOutcomeHorizonKind horizonKind = AiShadowOutcomeDefaults.OfficialHorizonKind, int horizonValue = AiShadowOutcomeDefaults.OfficialHorizonValue, int take = 200, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
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
