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
        Assert.Equal("true", row.Values["label_good_entry"]);
        Assert.Null(row.Values["label_good_exit"]);
        Assert.Equal("Win", row.Values["label_outcome"]);
        Assert.Equal("false", row.Values["label_false_signal"]);
        Assert.Equal("0.015", row.Values["label_expected_move_pct"]);
        Assert.Equal("0.1", row.Values["label_max_favorable_excursion"]);
        Assert.Equal("0.05", row.Values["label_max_adverse_excursion"]);
        Assert.Equal("0.06", row.Values["label_estimated_pnl"]);
        Assert.Equal("0.05", row.Values["label_drawdown"]);
        Assert.Equal("0", row.Values["label_risk_violation_count"]);
        Assert.Equal("0", row.Values["label_duplicate_order_count"]);
        Assert.Equal("0", row.Values["label_stale_data_entry_count"]);
        Assert.Equal("0", row.Values["label_reduce_only_violation_count"]);
        Assert.Equal("Complete", row.Values["label_completeness"]);
        Assert.Equal("TDL-1.v1", row.Values["label_version"]);
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
        Assert.Equal("Win", row.Values["label_outcome"]);
        Assert.Equal("true", row.Values["label_good_entry"]);
        Assert.Equal("false", row.Values["label_false_signal"]);
        Assert.Null(row.Values["label_estimated_pnl"]);
        Assert.Equal("Complete", row.Values["label_completeness"]);
        Assert.Equal("false", row.Values["label_has_execution_order"]);
        Assert.Equal("0.02", row.Values["label_realized_return"]);
    }

    [Fact]
    public async Task BuildAsync_ProducesUnknownOutcomeLabels_WhenOutcomeCoverageCannotBeScored()
    {
        await using var dbContext = CreateDbContext();
        var builder = CreateService(dbContext, new DateTime(2026, 4, 24, 13, 30, 0, DateTimeKind.Utc));
        var featureSnapshotId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var decisionId = Guid.Parse("88888888-8888-8888-8888-888888888888");
        var featureAnchorTimeUtc = new DateTime(2026, 4, 24, 12, 20, 0, DateTimeKind.Utc);

        SeedFeatureSnapshot(dbContext, featureSnapshotId, featureAnchorTimeUtc);
        SeedShadowDecision(
            dbContext,
            decisionId,
            featureSnapshotId,
            strategySignalId: null,
            featureAnchorTimeUtc,
            finalAction: "ShadowOnly",
            hypotheticalSubmitAllowed: true,
            hypotheticalBlockReason: null,
            noSubmitReason: "ShadowModeActive",
            aiDirection: "Long");
        await dbContext.SaveChangesAsync();

        var dataset = await builder.BuildAsync(new TrainingDatasetBuildRequest("ml-user", TrainingEligibleOnly: false));
        var row = Assert.Single(dataset.Rows);

        Assert.False(row.IsTrainingEligible);
        Assert.Equal("Unknown", row.Values["label_outcome"]);
        Assert.Null(row.Values["label_good_entry"]);
        Assert.Null(row.Values["label_false_signal"]);
        Assert.Equal("Unknown", row.Values["label_completeness"]);
        Assert.Equal("TDL-1.v1", row.Values["label_version"]);
        Assert.Equal("false", row.Values["meta_is_training_eligible"]);
    }

    [Fact]
    public async Task BuildAsync_ProducesLossAndSafetyLabels_ForRiskBlockedExitContext()
    {
        await using var dbContext = CreateDbContext();
        var builder = CreateService(dbContext, new DateTime(2026, 4, 24, 14, 0, 0, DateTimeKind.Utc));
        var featureSnapshotId = Guid.Parse("11111111-aaaa-bbbb-cccc-111111111111");
        var decisionId = Guid.Parse("22222222-aaaa-bbbb-cccc-222222222222");
        var strategySignalId = Guid.Parse("33333333-aaaa-bbbb-cccc-333333333333");
        var featureAnchorTimeUtc = new DateTime(2026, 4, 24, 12, 40, 0, DateTimeKind.Utc);

        SeedFeatureSnapshot(dbContext, featureSnapshotId, featureAnchorTimeUtc);
        SeedShadowDecision(
            dbContext,
            decisionId,
            featureSnapshotId,
            strategySignalId,
            featureAnchorTimeUtc,
            finalAction: "NoSubmit",
            hypotheticalSubmitAllowed: false,
            hypotheticalBlockReason: "StaleMarketData",
            noSubmitReason: "MissingFreshSignalData",
            aiDirection: "Long",
            riskVetoPresent: true,
            pilotSafetyBlocked: true);
        SeedShadowOutcome(
            dbContext,
            decisionId,
            featureAnchorTimeUtc,
            futureCloseTimeUtc: featureAnchorTimeUtc.AddMinutes(2),
            referenceClosePrice: 100m,
            realizedReturn: -0.03m,
            outcomeScore: -0.25m);

        var selectedOrderId = SeedExecutionOrder(
            dbContext,
            strategySignalId,
            side: ExecutionOrderSide.Sell,
            stopLossPrice: 103m,
            takeProfitPrice: 96m,
            signalType: StrategySignalType.Exit,
            reduceOnly: false,
            createdDateUtc: featureAnchorTimeUtc.AddSeconds(50),
            submittedAtUtc: featureAnchorTimeUtc.AddSeconds(55),
            lastStateChangedAtUtc: featureAnchorTimeUtc.AddSeconds(56));

        SeedExecutionOrder(
            dbContext,
            strategySignalId,
            side: ExecutionOrderSide.Sell,
            stopLossPrice: 103m,
            takeProfitPrice: 96m,
            signalType: StrategySignalType.Exit,
            reduceOnly: true,
            submittedToBroker: false,
            duplicateSuppressed: true,
            state: ExecutionOrderState.Rejected,
            createdDateUtc: featureAnchorTimeUtc.AddSeconds(10),
            lastStateChangedAtUtc: featureAnchorTimeUtc.AddSeconds(11));

        SeedDemoLedgerTransaction(dbContext, selectedOrderId, -12.5m, featureAnchorTimeUtc.AddMinutes(3));
        SeedHistoricalCandles(
            dbContext,
            ("BTCUSDT", "1m", featureAnchorTimeUtc.AddMinutes(1), 98m, 101m, 96m),
            ("BTCUSDT", "1m", featureAnchorTimeUtc.AddMinutes(2), 97m, 99m, 95m));
        await dbContext.SaveChangesAsync();

        var dataset = await builder.BuildAsync(new TrainingDatasetBuildRequest("ml-user"));
        var row = Assert.Single(dataset.Rows);

        Assert.Equal("Loss", row.Values["label_outcome"]);
        Assert.Equal("false", row.Values["label_good_entry"]);
        Assert.Equal("false", row.Values["label_good_exit"]);
        Assert.Equal("true", row.Values["label_false_signal"]);
        Assert.Equal("-12.5", row.Values["label_realized_pnl"]);
        Assert.Equal("-0.03", row.Values["label_estimated_pnl"]);
        Assert.Equal("0.05", row.Values["label_drawdown"]);
        Assert.Equal("2", row.Values["label_risk_violation_count"]);
        Assert.Equal("1", row.Values["label_duplicate_order_count"]);
        Assert.Equal("2", row.Values["label_stale_data_entry_count"]);
        Assert.Equal("1", row.Values["label_reduce_only_violation_count"]);
        Assert.Equal("Complete", row.Values["label_completeness"]);
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
        Assert.Equal(TrainingDatasetExportMode.Internal, export.ExportMode);
        Assert.Contains("meta_split_bucket", export.ColumnOrder);
        Assert.Contains("meta_user_id", export.ColumnOrder);
        Assert.DoesNotContain("feature_last_decision_outcome", export.CsvContent, StringComparison.Ordinal);
        Assert.DoesNotContain("feature_top_signal_hints", export.CsvContent, StringComparison.Ordinal);
        Assert.Contains("label_outcome_score", export.CsvContent, StringComparison.Ordinal);
        Assert.Contains("training-dataset-all-symbols-all-timeframes-BarsForward-1-20260424.csv", export.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportCsvAsync_SanitizedMode_RemovesInternalMetadataColumns()
    {
        await using var dbContext = CreateDbContext();
        var builder = CreateService(dbContext, new DateTime(2026, 4, 24, 14, 15, 0, DateTimeKind.Utc));
        var featureSnapshotId = Guid.Parse("abababab-abab-abab-abab-abababababab");
        var decisionId = Guid.Parse("cdcdcdcd-cdcd-cdcd-cdcd-cdcdcdcdcdcd");
        var strategySignalId = Guid.Parse("efefefef-efef-efef-efef-efefefefefef");
        var anchorTimeUtc = new DateTime(2026, 4, 24, 12, 30, 0, DateTimeKind.Utc);

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
            realizedReturn: 0.01m,
            outcomeScore: 0.60m);
        SeedExecutionOrder(dbContext, strategySignalId, side: ExecutionOrderSide.Buy, stopLossPrice: 98m, takeProfitPrice: 105m);
        SeedHistoricalCandles(
            dbContext,
            ("BTCUSDT", "1m", anchorTimeUtc.AddMinutes(1), 101m, 102m, 99m));
        await dbContext.SaveChangesAsync();

        var export = await builder.ExportCsvAsync(new TrainingDatasetBuildRequest(
            "ml-user",
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
        Assert.DoesNotContain("ml-user", export.CsvContent, StringComparison.Ordinal);
        Assert.DoesNotContain(featureSnapshotId.ToString("D"), export.CsvContent, StringComparison.Ordinal);
        Assert.DoesNotContain(decisionId.ToString("D"), export.CsvContent, StringComparison.Ordinal);
        Assert.DoesNotContain(strategySignalId.ToString("D"), export.CsvContent, StringComparison.Ordinal);
        Assert.DoesNotContain($"corr-{featureSnapshotId:N}", export.CsvContent, StringComparison.Ordinal);
        Assert.DoesNotContain($"snapshot-{featureSnapshotId:N}", export.CsvContent, StringComparison.Ordinal);
        Assert.Contains("meta_symbol", export.ColumnOrder);
        Assert.Contains("label_outcome_score", export.CsvContent, StringComparison.Ordinal);
        Assert.Contains("-sanitized.csv", export.FileName, StringComparison.Ordinal);
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
        string aiDirection,
        bool riskVetoPresent = false,
        bool pilotSafetyBlocked = false)
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
            RiskVetoPresent = riskVetoPresent,
            RiskVetoReason = riskVetoPresent ? "MaxRisk" : null,
            PilotSafetyBlocked = pilotSafetyBlocked,
            PilotSafetyReason = pilotSafetyBlocked ? "PilotSafety" : null,
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

    private static Guid SeedExecutionOrder(
        ApplicationDbContext dbContext,
        Guid strategySignalId,
        ExecutionOrderSide side,
        decimal? stopLossPrice,
        decimal? takeProfitPrice,
        StrategySignalType signalType = StrategySignalType.Entry,
        bool reduceOnly = false,
        bool submittedToBroker = true,
        bool duplicateSuppressed = false,
        ExecutionOrderState state = ExecutionOrderState.Filled,
        decimal quantity = 0.01m,
        decimal price = 100m,
        decimal? averageFillPrice = 100m,
        decimal? filledQuantity = 0.01m,
        DateTime? createdDateUtc = null,
        DateTime? submittedAtUtc = null,
        DateTime? lastStateChangedAtUtc = null)
    {
        var executionOrderId = Guid.NewGuid();
        var createdUtc = createdDateUtc ?? new DateTime(2026, 4, 24, 12, 0, 15, DateTimeKind.Utc);
        var submittedUtc = submittedAtUtc ?? new DateTime(2026, 4, 24, 12, 0, 20, DateTimeKind.Utc);
        var stateChangedUtc = lastStateChangedAtUtc ?? new DateTime(2026, 4, 24, 12, 0, 30, DateTimeKind.Utc);
        dbContext.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = executionOrderId,
            OwnerUserId = "ml-user",
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = strategySignalId,
            SignalType = signalType,
            BotId = Guid.Parse("12121212-1212-1212-1212-121212121212"),
            Plane = ExchangeDataPlane.Futures,
            StrategyKey = "ml-shadow-core",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = side,
            OrderType = ExecutionOrderType.Market,
            Quantity = quantity,
            Price = price,
            FilledQuantity = filledQuantity ?? quantity,
            AverageFillPrice = averageFillPrice,
            StopLossPrice = stopLossPrice,
            TakeProfitPrice = takeProfitPrice,
            ReduceOnly = reduceOnly,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            State = state,
            IdempotencyKey = $"idem-{strategySignalId:N}",
            RootCorrelationId = $"corr-{strategySignalId:N}",
            SubmittedToBroker = submittedToBroker,
            DuplicateSuppressed = duplicateSuppressed,
            CreatedDate = createdUtc,
            SubmittedAtUtc = submittedToBroker ? submittedUtc : null,
            LastFilledAtUtc = state == ExecutionOrderState.Filled ? stateChangedUtc : null,
            LastStateChangedAtUtc = stateChangedUtc
        });

        return executionOrderId;
    }

    private static void SeedDemoLedgerTransaction(
        ApplicationDbContext dbContext,
        Guid executionOrderId,
        decimal realizedPnlDelta,
        DateTime occurredAtUtc)
    {
        dbContext.DemoLedgerTransactions.Add(new DemoLedgerTransaction
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "ml-user",
            OperationId = $"demo-op-{executionOrderId:N}",
            TransactionType = DemoLedgerTransactionType.FillApplied,
            BotId = Guid.Parse("12121212-1212-1212-1212-121212121212"),
            PositionScopeKey = "BTCUSDT:1m",
            OrderId = executionOrderId.ToString("N"),
            Symbol = "BTCUSDT",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = DemoTradeSide.Buy,
            Quantity = 0.01m,
            Price = 100m,
            RealizedPnlDelta = realizedPnlDelta,
            OccurredAtUtc = occurredAtUtc
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
