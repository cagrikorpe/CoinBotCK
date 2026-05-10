using CoinBot.Application.Abstractions.Ai;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Ai;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Persistence;
using CoinBot.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.IntegrationTests.Ai;

public sealed class TrainingDatasetBuilderIntegrationTests
{
    [Fact]
    public async Task BuildAsync_ScoresMissingOutcome_AndExportsTrainingReadyRow_OnSqlServer()
    {
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString($"CoinBotTrainingDataset_{Guid.NewGuid():N}");
        const string userId = "ml-dataset-int-user";
        var botId = Guid.NewGuid();
        var featureSnapshotId = Guid.NewGuid();
        var decisionId = Guid.NewGuid();
        var strategySignalId = Guid.NewGuid();
        var anchorTimeUtc = new DateTime(2026, 4, 24, 12, 0, 0, DateTimeKind.Utc);

        await using var dbContext = CreateDbContext(connectionString);

        try
        {
            await dbContext.Database.EnsureDeletedAsync();
            await dbContext.Database.MigrateAsync();

            await SeedGraphAsync(dbContext, userId, botId, featureSnapshotId, decisionId, strategySignalId, anchorTimeUtc);

            var timeProvider = new FixedTimeProvider(new DateTime(2026, 4, 24, 14, 0, 0, DateTimeKind.Utc));
            var builder = new TrainingDatasetBuilderService(
                dbContext,
                new AiShadowDecisionService(dbContext, timeProvider),
                timeProvider);

            var request = new TrainingDatasetBuildRequest(userId, HorizonValue: 2);
            var dataset = await builder.BuildAsync(request);
            var export = await builder.ExportCsvAsync(request);
            var row = Assert.Single(dataset.Rows);

            Assert.Single(await dbContext.AiShadowDecisionOutcomes.AsNoTracking().ToListAsync());
            Assert.Equal(1, dataset.SourceRowCount);
            Assert.True(row.IsTrainingEligible);
            Assert.Equal("Scored", row.Values["label_outcome_state"]);
            Assert.Equal("Win", row.Values["label_outcome"]);
            Assert.Equal("true", row.Values["label_good_entry"]);
            Assert.Null(row.Values["label_good_exit"]);
            Assert.Equal("false", row.Values["label_false_signal"]);
            Assert.Equal("0.011", row.Values["label_expected_move_pct"]);
            Assert.Equal("0.02", row.Values["label_realized_return"]);
            Assert.Equal("0.03", row.Values["label_mfe_return"]);
            Assert.Equal("-0.01", row.Values["label_mae_return"]);
            Assert.Equal("0.03", row.Values["label_max_favorable_excursion"]);
            Assert.Equal("0.01", row.Values["label_max_adverse_excursion"]);
            Assert.Equal("7.25", row.Values["label_realized_pnl"]);
            Assert.Equal("0.2", row.Values["label_estimated_pnl"]);
            Assert.Equal("0.1", row.Values["label_drawdown"]);
            Assert.Equal("0", row.Values["label_risk_violation_count"]);
            Assert.Equal("0", row.Values["label_duplicate_order_count"]);
            Assert.Equal("0", row.Values["label_stale_data_entry_count"]);
            Assert.Equal("0", row.Values["label_reduce_only_violation_count"]);
            Assert.Equal("Complete", row.Values["label_completeness"]);
            Assert.Equal("TDL-1.v1", row.Values["label_version"]);
            Assert.Contains("meta_is_training_eligible", export.CsvContent, StringComparison.Ordinal);
            Assert.Contains("label_outcome_score", export.CsvContent, StringComparison.Ordinal);
            Assert.Contains("label_good_entry", export.CsvContent, StringComparison.Ordinal);
            Assert.Contains("label_version", export.CsvContent, StringComparison.Ordinal);
        }
        finally
        {
            await SqlServerIntegrationDatabase.CleanupDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task ExportCsvAsync_SanitizedMode_RemovesInternalMetadataColumns_OnSqlServer()
    {
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString($"CoinBotTrainingDatasetSanitized_{Guid.NewGuid():N}");
        const string userId = "ml-dataset-int-user";
        var botId = Guid.NewGuid();
        var featureSnapshotId = Guid.NewGuid();
        var decisionId = Guid.NewGuid();
        var anchorTimeUtc = new DateTime(2026, 4, 24, 12, 30, 0, DateTimeKind.Utc);

        await using var dbContext = CreateDbContext(connectionString);

        try
        {
            await dbContext.Database.EnsureDeletedAsync();
            await dbContext.Database.MigrateAsync();

            await SeedGraphAsync(dbContext, userId, botId, featureSnapshotId, decisionId, Guid.NewGuid(), anchorTimeUtc);

            var timeProvider = new FixedTimeProvider(new DateTime(2026, 4, 24, 14, 15, 0, DateTimeKind.Utc));
            var builder = new TrainingDatasetBuilderService(
                dbContext,
                new AiShadowDecisionService(dbContext, timeProvider),
                timeProvider);

            var export = await builder.ExportCsvAsync(new TrainingDatasetBuildRequest(
                userId,
                HorizonValue: 2,
                ExportMode: TrainingDatasetExportMode.Sanitized));

            Assert.Equal(TrainingDatasetExportMode.Sanitized, export.ExportMode);
            Assert.DoesNotContain("meta_user_id", export.ColumnOrder);
            Assert.DoesNotContain("meta_bot_id", export.ColumnOrder);
            Assert.DoesNotContain("meta_feature_snapshot_id", export.ColumnOrder);
            Assert.DoesNotContain("meta_ai_shadow_decision_id", export.ColumnOrder);
            Assert.DoesNotContain("meta_strategy_signal_id", export.ColumnOrder);
            Assert.DoesNotContain("meta_execution_order_id", export.ColumnOrder);
            Assert.DoesNotContain("meta_correlation_id", export.ColumnOrder);
            Assert.DoesNotContain("meta_snapshot_key", export.ColumnOrder);
            Assert.DoesNotContain(userId, export.CsvContent, StringComparison.Ordinal);
            Assert.DoesNotContain(botId.ToString("D"), export.CsvContent, StringComparison.Ordinal);
            Assert.DoesNotContain(featureSnapshotId.ToString("D"), export.CsvContent, StringComparison.Ordinal);
            Assert.DoesNotContain(decisionId.ToString("D"), export.CsvContent, StringComparison.Ordinal);
            Assert.DoesNotContain($"corr-{featureSnapshotId:N}", export.CsvContent, StringComparison.Ordinal);
            Assert.DoesNotContain($"shadow-{decisionId:N}", export.CsvContent, StringComparison.Ordinal);
            Assert.DoesNotContain($"snapshot-{featureSnapshotId:N}", export.CsvContent, StringComparison.Ordinal);
            Assert.Contains("meta_symbol", export.ColumnOrder);
            Assert.Contains("label_outcome_score", export.CsvContent, StringComparison.Ordinal);
            Assert.Contains("label_outcome", export.CsvContent, StringComparison.Ordinal);
            Assert.Contains("label_completeness", export.CsvContent, StringComparison.Ordinal);
            Assert.Contains("label_version", export.CsvContent, StringComparison.Ordinal);
            Assert.Contains("-sanitized.csv", export.FileName, StringComparison.Ordinal);
        }
        finally
        {
            await SqlServerIntegrationDatabase.CleanupDatabaseAsync(connectionString);
        }
    }

    private static ApplicationDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private static async Task SeedGraphAsync(
        ApplicationDbContext dbContext,
        string userId,
        Guid botId,
        Guid featureSnapshotId,
        Guid decisionId,
        Guid strategySignalId,
        DateTime anchorTimeUtc)
    {
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
        dbContext.TradingBots.Add(new TradingBot
        {
            Id = botId,
            OwnerUserId = userId,
            Name = "ML Dataset Bot",
            StrategyKey = "ml-shadow-core",
            Symbol = "BTCUSDT",
            IsEnabled = true
        });
        dbContext.TradingFeatureSnapshots.Add(new TradingFeatureSnapshot
        {
            Id = featureSnapshotId,
            OwnerUserId = userId,
            BotId = botId,
            StrategyKey = "ml-shadow-core",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            EvaluatedAtUtc = anchorTimeUtc.AddSeconds(5),
            FeatureAnchorTimeUtc = anchorTimeUtc,
            MarketDataTimestampUtc = anchorTimeUtc,
            FeatureVersion = "AI-1.v1",
            SnapshotState = FeatureSnapshotState.Ready,
            QualityReasonCode = FeatureSnapshotQualityReason.None,
            MarketDataReasonCode = DegradedModeReasonCode.None,
            SampleCount = 240,
            RequiredSampleCount = 200,
            ReferencePrice = 100m,
            Ema20 = 101m,
            Ema50 = 100m,
            Ema200 = 98m,
            Alma = 100.4m,
            Frama = 100.2m,
            Rsi = 58m,
            MacdLine = 0.8m,
            MacdSignal = 0.5m,
            MacdHistogram = 0.3m,
            KdjK = 54m,
            KdjD = 49m,
            KdjJ = 64m,
            FisherTransform = 0.21m,
            Atr = 1.1m,
            BollingerPercentB = 0.61m,
            BollingerBandWidth = 0.17m,
            KeltnerChannelRelation = 0.42m,
            PmaxValue = 97m,
            ChandelierExit = 96m,
            VolumeSpikeRatio = 1.12m,
            RelativeVolume = 1.09m,
            Obv = 1500m,
            Mfi = 57m,
            KlingerOscillator = 9m,
            KlingerSignal = 7m,
            Plane = ExchangeDataPlane.Futures,
            TradingMode = ExecutionEnvironment.Live,
            HasOpenPosition = false,
            IsInCooldown = false,
            PrimaryRegime = "BullTrend",
            MomentumBias = "Bullish",
            VolatilityState = "Normal",
            FeatureSummary = "State=Ready",
            TopSignalHints = "Momentum+Trend",
            SnapshotKey = $"snapshot-{featureSnapshotId:N}",
            CorrelationId = $"corr-{featureSnapshotId:N}"
        });
        dbContext.AiShadowDecisions.Add(new AiShadowDecision
        {
            Id = decisionId,
            OwnerUserId = userId,
            BotId = botId,
            FeatureSnapshotId = featureSnapshotId,
            StrategySignalId = strategySignalId,
            CorrelationId = $"shadow-{decisionId:N}",
            StrategyKey = "ml-shadow-core",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            EvaluatedAtUtc = anchorTimeUtc.AddSeconds(10),
            MarketDataTimestampUtc = anchorTimeUtc,
            FeatureVersion = "AI-1.v1",
            StrategyDirection = "Long",
            StrategyConfidenceScore = 80,
            StrategyDecisionOutcome = "Persisted",
            StrategyDecisionCode = "Entry",
            StrategySummary = "Strategy summary",
            AiDirection = "Long",
            AiConfidence = 0.82m,
            AiReasonSummary = "AI reason",
            AiProviderName = "DeterministicStub",
            AiProviderModel = "deterministic-v1",
            AiLatencyMs = 5,
            TradingMode = ExecutionEnvironment.Live,
            Plane = ExchangeDataPlane.Futures,
            FinalAction = "ShadowOnly",
            HypotheticalSubmitAllowed = true,
            NoSubmitReason = "ShadowModeActive",
            AgreementState = "Agreement"
        });

        var executionOrderId = Guid.NewGuid();
        dbContext.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = executionOrderId,
            OwnerUserId = userId,
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = strategySignalId,
            SignalType = StrategySignalType.Entry,
            BotId = botId,
            Plane = ExchangeDataPlane.Futures,
            StrategyKey = "ml-shadow-core",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.1m,
            Price = 100m,
            FilledQuantity = 0.1m,
            AverageFillPrice = 100m,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            State = ExecutionOrderState.Filled,
            IdempotencyKey = $"idem-{strategySignalId:N}",
            RootCorrelationId = $"corr-{strategySignalId:N}",
            SubmittedToBroker = true,
            SubmittedAtUtc = anchorTimeUtc.AddSeconds(15),
            LastFilledAtUtc = anchorTimeUtc.AddSeconds(20),
            LastStateChangedAtUtc = anchorTimeUtc.AddSeconds(20)
        });

        dbContext.DemoLedgerTransactions.Add(new DemoLedgerTransaction
        {
            Id = Guid.NewGuid(),
            OwnerUserId = userId,
            OperationId = $"demo-op-{executionOrderId:N}",
            TransactionType = DemoLedgerTransactionType.FillApplied,
            BotId = botId,
            PositionScopeKey = "BTCUSDT:1m",
            OrderId = executionOrderId.ToString("N"),
            Symbol = "BTCUSDT",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = DemoTradeSide.Buy,
            Quantity = 0.1m,
            Price = 100m,
            RealizedPnlDelta = 7.25m,
            OccurredAtUtc = anchorTimeUtc.AddMinutes(3)
        });

        SeedHistoricalCandle(dbContext, anchorTimeUtc, 100m, 100m, 100m);
        SeedHistoricalCandle(dbContext, anchorTimeUtc.AddMinutes(1), 101m, 102m, 99m);
        SeedHistoricalCandle(dbContext, anchorTimeUtc.AddMinutes(2), 102m, 103m, 101m);

        await dbContext.SaveChangesAsync();
    }

    private static void SeedHistoricalCandle(
        ApplicationDbContext dbContext,
        DateTime closeTimeUtc,
        decimal closePrice,
        decimal highPrice,
        decimal lowPrice)
    {
        dbContext.HistoricalMarketCandles.Add(new HistoricalMarketCandle
        {
            Id = Guid.NewGuid(),
            Symbol = "BTCUSDT",
            Interval = "1m",
            OpenTimeUtc = closeTimeUtc.AddMinutes(-1),
            CloseTimeUtc = closeTimeUtc,
            OpenPrice = closePrice,
            HighPrice = highPrice,
            LowPrice = lowPrice,
            ClosePrice = closePrice,
            Volume = 1200m,
            ReceivedAtUtc = closeTimeUtc,
            Source = "integration-test"
        });
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
