using CoinBot.Application.Abstractions.Alerts;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Features;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Features;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Persistence;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Features;

public sealed class TradingFeatureSnapshotServiceTests
{
    [Fact]
    public async Task CaptureAsync_ComputesDeterministicCoreFeatureSet_AndExplainabilitySummary()
    {
        await using var harness = await CreateHarnessAsync("feature-ready-01");
        var evaluatedAtUtc = harness.TimeProvider.GetUtcNow().UtcDateTime;
        var candles = CreateCandles("BTCUSDT", "1m", evaluatedAtUtc.AddMinutes(-240), 240, 65000m, 7m, 110m);

        await harness.CircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                "feature-test",
                candles[^1].CloseTimeUtc,
                Symbol: "BTCUSDT",
                Timeframe: "1m",
                ExpectedOpenTimeUtc: candles[^1].CloseTimeUtc.AddMilliseconds(1),
                ContinuityGapCount: 0),
            cancellationToken: CancellationToken.None);

        var snapshot = await harness.Service.CaptureAsync(
            new TradingFeatureCaptureRequest(
                harness.UserId,
                harness.BotId,
                "feature-strategy",
                "BTCUSDT",
                "1m",
                evaluatedAtUtc,
                harness.ExchangeAccountId,
                ExchangeDataPlane.Futures,
                HistoricalCandles: candles),
            CancellationToken.None);

        Assert.Equal(FeatureSnapshotState.Ready, snapshot.SnapshotState);
        Assert.Equal(DegradedModeReasonCode.None, snapshot.MarketDataReasonCode);
        Assert.Equal("AI-1.v1", snapshot.FeatureVersion);
        Assert.Equal(240, snapshot.SampleCount);
        Assert.Equal(200, snapshot.RequiredSampleCount);
        Assert.NotNull(snapshot.ReferencePrice);
        Assert.NotNull(snapshot.Trend.Ema20);
        Assert.NotNull(snapshot.Trend.Ema50);
        Assert.NotNull(snapshot.Trend.Ema200);
        Assert.True(snapshot.Trend.Ema20 > snapshot.Trend.Ema50);
        Assert.True(snapshot.Trend.Ema50 > snapshot.Trend.Ema200);
        Assert.NotNull(snapshot.Trend.Alma);
        Assert.NotNull(snapshot.Trend.Frama);
        Assert.NotNull(snapshot.Momentum.Rsi);
        Assert.NotNull(snapshot.Momentum.MacdHistogram);
        Assert.NotNull(snapshot.Momentum.KdjK);
        Assert.NotNull(snapshot.Momentum.FisherTransform);
        Assert.NotNull(snapshot.Volatility.Atr);
        Assert.NotNull(snapshot.Volatility.BollingerPercentB);
        Assert.NotNull(snapshot.Volatility.BollingerBandWidth);
        Assert.NotNull(snapshot.Volatility.KeltnerChannelRelation);
        Assert.NotNull(snapshot.Volatility.PmaxValue);
        Assert.NotNull(snapshot.Volatility.ChandelierExit);
        Assert.NotNull(snapshot.Volume.RelativeVolume);
        Assert.NotNull(snapshot.Volume.VolumeSpikeRatio);
        Assert.NotNull(snapshot.Volume.Obv);
        Assert.NotNull(snapshot.Volume.Mfi);
        Assert.NotNull(snapshot.Volume.KlingerOscillator);
        Assert.NotNull(snapshot.Volume.KlingerSignal);
        Assert.Equal(FeatureSnapshotQualityReason.None, snapshot.QualityReasonCode);
        Assert.Null(snapshot.MissingFeatureSummary);
        Assert.Equal("BullTrend", snapshot.PrimaryRegime);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.FeatureSummary));
        Assert.False(string.IsNullOrWhiteSpace(snapshot.TopSignalHints));
        Assert.Contains("State=Ready", snapshot.FeatureSummary, StringComparison.Ordinal);
        Assert.Contains("Quality=None", snapshot.FeatureSummary, StringComparison.Ordinal);
        Assert.Contains("Regime:", snapshot.TopSignalHints, StringComparison.Ordinal);
        Assert.Contains("Mfi=", snapshot.NormalizationMeta!, StringComparison.Ordinal);
        Assert.Contains("KlingerOscillator=", snapshot.NormalizationMeta!, StringComparison.Ordinal);

        var readBack = await harness.Service.GetLatestAsync(harness.UserId, harness.BotId, "BTCUSDT", "1m");

        Assert.NotNull(readBack);
        Assert.Equal(snapshot.Id, readBack!.Id);
        Assert.Equal(snapshot.FeatureSummary, readBack.FeatureSummary);
    }

    [Fact]
    public async Task CaptureAsync_ProducesExplicitMissingDataState_WhenMarketDataIsUnavailable()
    {
        await using var harness = await CreateHarnessAsync("feature-missing-01");

        var snapshot = await harness.Service.CaptureAsync(
            new TradingFeatureCaptureRequest(
                harness.UserId,
                harness.BotId,
                "feature-strategy",
                "BTCUSDT",
                "1m",
                harness.TimeProvider.GetUtcNow().UtcDateTime,
                harness.ExchangeAccountId,
                ExchangeDataPlane.Futures,
                HistoricalCandles: Array.Empty<MarketCandleSnapshot>()),
            CancellationToken.None);

        Assert.Equal(FeatureSnapshotState.MissingData, snapshot.SnapshotState);
        Assert.Equal(FeatureSnapshotQualityReason.MissingInputs, snapshot.QualityReasonCode);
        Assert.Equal(DegradedModeReasonCode.MarketDataUnavailable, snapshot.MarketDataReasonCode);
        Assert.Contains("MissingInputs=", snapshot.MissingFeatureSummary!, StringComparison.Ordinal);
        Assert.Contains("State=MissingData", snapshot.FeatureSummary, StringComparison.Ordinal);
        Assert.Contains("Quality=MissingInputs", snapshot.FeatureSummary, StringComparison.Ordinal);
        Assert.Contains("MarketDataMissing", snapshot.TopSignalHints, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureAsync_ProducesExplicitStaleState_WhenLatencySnapshotIsBreached()
    {
        await using var harness = await CreateHarnessAsync("feature-stale-01");
        var candles = CreateCandles("BTCUSDT", "1m", harness.TimeProvider.GetUtcNow().UtcDateTime.AddMinutes(-240), 240, 65000m, 2m, 90m);

        await harness.CircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                "feature-test",
                candles[^1].CloseTimeUtc,
                Symbol: "BTCUSDT",
                Timeframe: "1m",
                ExpectedOpenTimeUtc: candles[^1].CloseTimeUtc.AddMilliseconds(1),
                ContinuityGapCount: 0),
            cancellationToken: CancellationToken.None);
        harness.TimeProvider.Advance(TimeSpan.FromSeconds(10));

        var snapshot = await harness.Service.CaptureAsync(
            new TradingFeatureCaptureRequest(
                harness.UserId,
                harness.BotId,
                "feature-strategy",
                "BTCUSDT",
                "1m",
                harness.TimeProvider.GetUtcNow().UtcDateTime,
                harness.ExchangeAccountId,
                ExchangeDataPlane.Futures,
                HistoricalCandles: candles),
            CancellationToken.None);

        Assert.Equal(FeatureSnapshotState.Stale, snapshot.SnapshotState);
        Assert.Equal(FeatureSnapshotQualityReason.None, snapshot.QualityReasonCode);
        Assert.Equal(DegradedModeReasonCode.MarketDataLatencyCritical, snapshot.MarketDataReasonCode);
        Assert.Contains("State=Stale", snapshot.FeatureSummary, StringComparison.Ordinal);
        Assert.Contains("MarketDataStale", snapshot.TopSignalHints, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureAsync_MarksHasOpenPositionTrue_WhenLivePositionTruthComesFromFilledOrder()
    {
        await using var harness = await CreateHarnessAsync("feature-live-position-01");
        var evaluatedAtUtc = harness.TimeProvider.GetUtcNow().UtcDateTime;
        var candles = CreateCandles("BTCUSDT", "1m", evaluatedAtUtc.AddMinutes(-240), 240, 65000m, 3m, 110m);
        var bot = await harness.DbContext.TradingBots.SingleAsync(entity => entity.Id == harness.BotId);
        bot.TradingModeOverride = ExecutionEnvironment.Live;
        bot.TradingModeApprovedAtUtc = evaluatedAtUtc;
        bot.TradingModeApprovalReference = "feature-live-position";
        harness.DbContext.TradingStrategies.Add(new TradingStrategy
        {
            Id = Guid.NewGuid(),
            OwnerUserId = harness.UserId,
            StrategyKey = "feature-strategy",
            DisplayName = "Feature Strategy",
            PromotionState = StrategyPromotionState.LivePublished,
            PublishedMode = ExecutionEnvironment.Live,
            PublishedAtUtc = evaluatedAtUtc,
            LivePromotionApprovedAtUtc = evaluatedAtUtc,
            LivePromotionApprovalReference = "feature-live-position"
        });

        harness.DbContext.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = Guid.NewGuid(),
            OwnerUserId = harness.UserId,
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            SignalType = StrategySignalType.Entry,
            BotId = harness.BotId,
            ExchangeAccountId = harness.ExchangeAccountId,
            Plane = ExchangeDataPlane.Futures,
            StrategyKey = "feature-strategy",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.06m,
            FilledQuantity = 0.06m,
            AverageFillPrice = 65123m,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            State = ExecutionOrderState.Filled,
            IdempotencyKey = "feature-live-position-order",
            RootCorrelationId = "feature-live-position-order",
            SubmittedToBroker = true,
            SubmittedAtUtc = evaluatedAtUtc.AddSeconds(-5),
            LastFilledAtUtc = evaluatedAtUtc.AddSeconds(-4),
            LastStateChangedAtUtc = evaluatedAtUtc.AddSeconds(-4),
            CreatedDate = evaluatedAtUtc.AddSeconds(-5),
            UpdatedDate = evaluatedAtUtc.AddSeconds(-4)
        });
        await harness.DbContext.SaveChangesAsync();

        await harness.CircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                "feature-test",
                candles[^1].CloseTimeUtc,
                Symbol: "BTCUSDT",
                Timeframe: "1m",
                ExpectedOpenTimeUtc: candles[^1].CloseTimeUtc.AddMilliseconds(1),
                ContinuityGapCount: 0),
            cancellationToken: CancellationToken.None);

        var snapshot = await harness.Service.CaptureAsync(
            new TradingFeatureCaptureRequest(
                harness.UserId,
                harness.BotId,
                "feature-strategy",
                "BTCUSDT",
                "1m",
                evaluatedAtUtc,
                harness.ExchangeAccountId,
                ExchangeDataPlane.Futures,
                HistoricalCandles: candles),
            CancellationToken.None);

        Assert.True(snapshot.TradingContext.HasOpenPosition);
    }

    [Fact]
    public async Task CaptureAsync_DoesNotCarryStaleRiskVeto_FromOldFeatureCycle()
    {
        await using var harness = await CreateHarnessAsync("feature-stale-veto-01");
        var evaluatedAtUtc = harness.TimeProvider.GetUtcNow().UtcDateTime;
        harness.DbContext.TradingStrategySignalVetoes.Add(new TradingStrategySignalVeto
        {
            Id = Guid.NewGuid(),
            OwnerUserId = harness.UserId,
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategyVersionNumber = 1,
            StrategySchemaVersion = 2,
            SignalType = StrategySignalType.Entry,
            ExecutionEnvironment = ExecutionEnvironment.Demo,
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            IndicatorOpenTimeUtc = evaluatedAtUtc.AddMinutes(-11),
            IndicatorCloseTimeUtc = evaluatedAtUtc.AddMinutes(-10),
            IndicatorReceivedAtUtc = evaluatedAtUtc.AddMinutes(-10),
            EvaluatedAtUtc = evaluatedAtUtc.AddMinutes(-10),
            ReasonCode = RiskVetoReasonCode.RiskProfileMissing,
            RiskEvaluationJson = "{}"
        });
        await harness.DbContext.SaveChangesAsync();
        var candles = CreateCandles("BTCUSDT", "1m", evaluatedAtUtc.AddMinutes(-240), 240, 65000m, 3m, 110m);

        await harness.CircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                "feature-test",
                candles[^1].CloseTimeUtc,
                Symbol: "BTCUSDT",
                Timeframe: "1m",
                ExpectedOpenTimeUtc: candles[^1].CloseTimeUtc.AddMilliseconds(1),
                ContinuityGapCount: 0),
            cancellationToken: CancellationToken.None);

        var snapshot = await harness.Service.CaptureAsync(
            new TradingFeatureCaptureRequest(
                harness.UserId,
                harness.BotId,
                "feature-strategy",
                "BTCUSDT",
                "1m",
                evaluatedAtUtc,
                harness.ExchangeAccountId,
                ExchangeDataPlane.Futures,
                HistoricalCandles: candles),
            CancellationToken.None);

        Assert.Equal("None", snapshot.TradingContext.LastDecisionOutcome);
        Assert.Null(snapshot.TradingContext.LastDecisionCode);
        Assert.Null(snapshot.TradingContext.LastVetoReasonCode);
    }

    [Fact]
    public async Task CaptureAsync_DoesNotCarryStaleExecutionFailure_FromOldFeatureCycle()
    {
        await using var harness = await CreateHarnessAsync("feature-stale-order-01");
        var evaluatedAtUtc = harness.TimeProvider.GetUtcNow().UtcDateTime;
        harness.DbContext.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = Guid.NewGuid(),
            OwnerUserId = harness.UserId,
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            SignalType = StrategySignalType.Entry,
            BotId = harness.BotId,
            ExchangeAccountId = harness.ExchangeAccountId,
            Plane = ExchangeDataPlane.Futures,
            StrategyKey = "feature-strategy",
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Buy,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.01m,
            Price = 65000m,
            ExecutionEnvironment = ExecutionEnvironment.Demo,
            State = ExecutionOrderState.Rejected,
            IdempotencyKey = "feature-stale-order",
            RootCorrelationId = "feature-stale-order",
            FailureCode = "PrivatePlaneStale",
            FailureDetail = "old private-plane reject",
            RejectionStage = ExecutionRejectionStage.PreSubmit,
            LastStateChangedAtUtc = evaluatedAtUtc.AddMinutes(-10),
            CreatedDate = evaluatedAtUtc.AddMinutes(-10),
            UpdatedDate = evaluatedAtUtc.AddMinutes(-10)
        });
        await harness.DbContext.SaveChangesAsync();
        var candles = CreateCandles("BTCUSDT", "1m", evaluatedAtUtc.AddMinutes(-240), 240, 65000m, 3m, 110m);

        await harness.CircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                "feature-test",
                candles[^1].CloseTimeUtc,
                Symbol: "BTCUSDT",
                Timeframe: "1m",
                ExpectedOpenTimeUtc: candles[^1].CloseTimeUtc.AddMilliseconds(1),
                ContinuityGapCount: 0),
            cancellationToken: CancellationToken.None);

        var snapshot = await harness.Service.CaptureAsync(
            new TradingFeatureCaptureRequest(
                harness.UserId,
                harness.BotId,
                "feature-strategy",
                "BTCUSDT",
                "1m",
                evaluatedAtUtc,
                harness.ExchangeAccountId,
                ExchangeDataPlane.Futures,
                HistoricalCandles: candles),
            CancellationToken.None);

        Assert.Equal("None", snapshot.TradingContext.LastDecisionOutcome);
        Assert.Null(snapshot.TradingContext.LastDecisionCode);
        Assert.Null(snapshot.TradingContext.LastExecutionState);
        Assert.Null(snapshot.TradingContext.LastFailureCode);
        Assert.DoesNotContain("PrivatePlaneStale", snapshot.FeatureSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivatePlaneStale", snapshot.TopSignalHints, StringComparison.Ordinal);
    }
    [Fact]
    public async Task CaptureAsync_ProducesSameDerivedFields_ForSameInput()
    {
        await using var harness = await CreateHarnessAsync("feature-deterministic-01");
        var evaluatedAtUtc = harness.TimeProvider.GetUtcNow().UtcDateTime;
        var candles = CreateCandles("BTCUSDT", "1m", evaluatedAtUtc.AddMinutes(-220), 220, 65000m, 5m, 120m);

        await harness.CircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                "feature-test",
                candles[^1].CloseTimeUtc,
                Symbol: "BTCUSDT",
                Timeframe: "1m",
                ExpectedOpenTimeUtc: candles[^1].CloseTimeUtc.AddMilliseconds(1),
                ContinuityGapCount: 0),
            cancellationToken: CancellationToken.None);

        var first = await harness.Service.CaptureAsync(
            new TradingFeatureCaptureRequest(
                harness.UserId,
                harness.BotId,
                "feature-strategy",
                "BTCUSDT",
                "1m",
                evaluatedAtUtc,
                harness.ExchangeAccountId,
                ExchangeDataPlane.Futures,
                HistoricalCandles: candles),
            CancellationToken.None);
        var second = await harness.Service.CaptureAsync(
            new TradingFeatureCaptureRequest(
                harness.UserId,
                harness.BotId,
                "feature-strategy",
                "BTCUSDT",
                "1m",
                evaluatedAtUtc,
                harness.ExchangeAccountId,
                ExchangeDataPlane.Futures,
                HistoricalCandles: candles),
            CancellationToken.None);

        Assert.Equal(first.SnapshotState, second.SnapshotState);
        Assert.Equal(first.MarketDataReasonCode, second.MarketDataReasonCode);
        Assert.Equal(first.ReferencePrice, second.ReferencePrice);
        Assert.Equal(first.Trend, second.Trend);
        Assert.Equal(first.Momentum, second.Momentum);
        Assert.Equal(first.Volatility, second.Volatility);
        Assert.Equal(first.Volume, second.Volume);
        Assert.Equal(first.TradingContext, second.TradingContext);
        Assert.Equal(first.FeatureSummary, second.FeatureSummary);
        Assert.Equal(first.TopSignalHints, second.TopSignalHints);
        Assert.Equal(first.NormalizationMeta, second.NormalizationMeta);
    }

    [Fact]
    public async Task CaptureAsync_ReusesExistingSnapshot_ForSameFeatureAnchor()
    {
        await using var harness = await CreateHarnessAsync("feature-anchor-01");
        var firstEvaluatedAtUtc = harness.TimeProvider.GetUtcNow().UtcDateTime;
        var candles = CreateCandles("BTCUSDT", "1m", firstEvaluatedAtUtc.AddMinutes(-220), 220, 65000m, 5m, 120m);
        var featureAnchorTimeUtc = candles[^1].CloseTimeUtc;

        await harness.CircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                "feature-test",
                featureAnchorTimeUtc,
                Symbol: "BTCUSDT",
                Timeframe: "1m",
                ExpectedOpenTimeUtc: featureAnchorTimeUtc.AddMilliseconds(1),
                ContinuityGapCount: 0),
            cancellationToken: CancellationToken.None);

        var first = await harness.Service.CaptureAsync(
            new TradingFeatureCaptureRequest(
                harness.UserId,
                harness.BotId,
                "feature-strategy",
                "BTCUSDT",
                "1m",
                firstEvaluatedAtUtc,
                harness.ExchangeAccountId,
                ExchangeDataPlane.Futures,
                HistoricalCandles: candles,
                CorrelationId: "feature-anchor-correlation-1"),
            CancellationToken.None);

        harness.TimeProvider.Advance(TimeSpan.FromSeconds(30));
        var second = await harness.Service.CaptureAsync(
            new TradingFeatureCaptureRequest(
                harness.UserId,
                harness.BotId,
                "feature-strategy",
                "BTCUSDT",
                "1m",
                harness.TimeProvider.GetUtcNow().UtcDateTime,
                harness.ExchangeAccountId,
                ExchangeDataPlane.Futures,
                HistoricalCandles: candles,
                CorrelationId: "feature-anchor-correlation-2"),
            CancellationToken.None);

        var persistedRows = await harness.DbContext.TradingFeatureSnapshots.AsNoTracking().ToListAsync();

        Assert.Equal(first.Id, second.Id);
        Assert.Single(persistedRows);
        Assert.Equal(featureAnchorTimeUtc, first.FeatureAnchorTimeUtc);
        Assert.Equal(featureAnchorTimeUtc, second.FeatureAnchorTimeUtc);
        Assert.Equal("feature-anchor-correlation-1", first.CorrelationId);
        Assert.Equal("feature-anchor-correlation-1", second.CorrelationId);
        Assert.False(string.IsNullOrWhiteSpace(first.SnapshotKey));
        Assert.Equal(first.SnapshotKey, second.SnapshotKey);
    }

    [Fact]
    public async Task CaptureAsync_MarksInsufficientCandles_WithDeterministicQualityReason()
    {
        await using var harness = await CreateHarnessAsync("feature-warmup-01");
        var evaluatedAtUtc = harness.TimeProvider.GetUtcNow().UtcDateTime;
        var candles = CreateCandles("BTCUSDT", "1m", evaluatedAtUtc.AddMinutes(-120), 120, 65000m, 6m, 100m);

        await harness.CircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                "feature-test",
                candles[^1].CloseTimeUtc,
                Symbol: "BTCUSDT",
                Timeframe: "1m",
                ExpectedOpenTimeUtc: candles[^1].CloseTimeUtc.AddMilliseconds(1),
                ContinuityGapCount: 0),
            cancellationToken: CancellationToken.None);

        var snapshot = await harness.Service.CaptureAsync(
            new TradingFeatureCaptureRequest(
                harness.UserId,
                harness.BotId,
                "feature-strategy",
                "BTCUSDT",
                "1m",
                evaluatedAtUtc,
                harness.ExchangeAccountId,
                ExchangeDataPlane.Futures,
                HistoricalCandles: candles),
            CancellationToken.None);

        Assert.Equal(FeatureSnapshotState.WarmingUp, snapshot.SnapshotState);
        Assert.Equal(FeatureSnapshotQualityReason.InsufficientCandles, snapshot.QualityReasonCode);
        Assert.Contains("SampleCount=120/200", snapshot.MissingFeatureSummary, StringComparison.Ordinal);
        Assert.Contains("Quality=InsufficientCandles", snapshot.FeatureSummary, StringComparison.Ordinal);
        Assert.Contains("FeatureWarmup", snapshot.TopSignalHints, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureAsync_MarksInvalidSnapshot_WhenCandleRangeIsInvalid()
    {
        await using var harness = await CreateHarnessAsync("feature-invalid-range-01");
        var evaluatedAtUtc = harness.TimeProvider.GetUtcNow().UtcDateTime;
        var candles = CreateCandles("BTCUSDT", "1m", evaluatedAtUtc.AddMinutes(-240), 240, 65000m, 4m, 100m);
        candles[42] = candles[42] with { HighPrice = candles[42].LowPrice - 1m };

        await harness.CircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                "feature-test",
                candles[^1].CloseTimeUtc,
                Symbol: "BTCUSDT",
                Timeframe: "1m",
                ExpectedOpenTimeUtc: candles[^1].CloseTimeUtc.AddMilliseconds(1),
                ContinuityGapCount: 0),
            cancellationToken: CancellationToken.None);

        var snapshot = await harness.Service.CaptureAsync(
            new TradingFeatureCaptureRequest(
                harness.UserId,
                harness.BotId,
                "feature-strategy",
                "BTCUSDT",
                "1m",
                evaluatedAtUtc,
                harness.ExchangeAccountId,
                ExchangeDataPlane.Futures,
                HistoricalCandles: candles),
            CancellationToken.None);

        Assert.Equal(FeatureSnapshotState.Invalid, snapshot.SnapshotState);
        Assert.Equal(FeatureSnapshotQualityReason.InvalidRange, snapshot.QualityReasonCode);
        Assert.Contains("outside the allowed range", snapshot.MissingFeatureSummary, StringComparison.Ordinal);
        Assert.Contains("FeatureInvalid", snapshot.TopSignalHints, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureAsync_MarksIncompleteSnapshot_WhenDerivedVolumeIndicatorsAreMissing()
    {
        await using var harness = await CreateHarnessAsync("feature-incomplete-01");
        var evaluatedAtUtc = harness.TimeProvider.GetUtcNow().UtcDateTime;
        var candles = CreateCandles("BTCUSDT", "1m", evaluatedAtUtc.AddMinutes(-240), 240, 65000m, 4m, 0m)
            .Select(candle => candle with { Volume = 0m })
            .ToArray();

        await harness.CircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                "feature-test",
                candles[^1].CloseTimeUtc,
                Symbol: "BTCUSDT",
                Timeframe: "1m",
                ExpectedOpenTimeUtc: candles[^1].CloseTimeUtc.AddMilliseconds(1),
                ContinuityGapCount: 0),
            cancellationToken: CancellationToken.None);

        var snapshot = await harness.Service.CaptureAsync(
            new TradingFeatureCaptureRequest(
                harness.UserId,
                harness.BotId,
                "feature-strategy",
                "BTCUSDT",
                "1m",
                evaluatedAtUtc,
                harness.ExchangeAccountId,
                ExchangeDataPlane.Futures,
                HistoricalCandles: candles),
            CancellationToken.None);

        Assert.Equal(FeatureSnapshotState.Invalid, snapshot.SnapshotState);
        Assert.Equal(FeatureSnapshotQualityReason.IncompleteSnapshot, snapshot.QualityReasonCode);
        Assert.Contains("MissingFeatures=VolumeSpikeRatio,RelativeVolume", snapshot.MissingFeatureSummary, StringComparison.Ordinal);
        Assert.Null(snapshot.Volume.VolumeSpikeRatio);
        Assert.Null(snapshot.Volume.RelativeVolume);
    }

    private static async Task<TestHarness> CreateHarnessAsync(string userId)
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"), databaseRoot)
            .Options;
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 4, 6, 12, 0, 0, TimeSpan.Zero));
        var dbContext = new ApplicationDbContext(options, new TestDataScopeContext(userId, hasIsolationBypass: false));
        var exchangeAccountId = Guid.NewGuid();
        var botId = Guid.NewGuid();

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
            DisplayName = "Feature Futures",
            CredentialStatus = ExchangeCredentialStatus.Active,
            IsReadOnly = false
        });
        dbContext.TradingBots.Add(new TradingBot
        {
            Id = botId,
            OwnerUserId = userId,
            Name = "Feature Bot",
            StrategyKey = "feature-strategy",
            Symbol = "BTCUSDT",
            ExchangeAccountId = exchangeAccountId,
            IsEnabled = true
        });
        await dbContext.SaveChangesAsync();

        var circuitBreaker = new DataLatencyCircuitBreaker(
            dbContext,
            new FakeAlertService(),
            Options.Create(new DataLatencyGuardOptions()),
            timeProvider,
            NullLogger<DataLatencyCircuitBreaker>.Instance);
        var tradingModeService = new TradingModeService(dbContext, new NoopAuditLogService());
        var service = new TradingFeatureSnapshotService(
            dbContext,
            circuitBreaker,
            tradingModeService,
            new FakeHistoricalKlineClient(),
            Options.Create(new BotExecutionPilotOptions()),
            timeProvider,
            NullLogger<TradingFeatureSnapshotService>.Instance);

        return new TestHarness(dbContext, service, circuitBreaker, timeProvider, userId, botId, exchangeAccountId);
    }

    private static MarketCandleSnapshot[] CreateCandles(string symbol, string timeframe, DateTime startOpenTimeUtc, int count, decimal startPrice, decimal driftPerCandle, decimal startVolume)
    {
        return Enumerable.Range(0, count)
            .Select(index =>
            {
                var openPrice = startPrice + (driftPerCandle * index);
                var closePrice = openPrice + (driftPerCandle * 0.6m);
                var openTimeUtc = startOpenTimeUtc.AddMinutes(index);
                return new MarketCandleSnapshot(
                    symbol,
                    timeframe,
                    openTimeUtc,
                    openTimeUtc.AddMinutes(1).AddMilliseconds(-1),
                    openPrice,
                    closePrice + 8m,
                    openPrice - 8m,
                    closePrice,
                    startVolume + (index % 17),
                    true,
                    openTimeUtc.AddMinutes(1),
                    "unit-feature");
            })
            .ToArray();
    }

    private sealed record TestHarness(
        ApplicationDbContext DbContext,
        TradingFeatureSnapshotService Service,
        DataLatencyCircuitBreaker CircuitBreaker,
        AdjustableTimeProvider TimeProvider,
        string UserId,
        Guid BotId,
        Guid ExchangeAccountId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => DbContext.DisposeAsync();
    }

    private sealed class TestDataScopeContext(string? userId, bool hasIsolationBypass) : IDataScopeContext
    {
        public string? UserId => userId;
        public bool HasIsolationBypass => hasIsolationBypass;
    }

    private sealed class FakeHistoricalKlineClient : IBinanceHistoricalKlineClient
    {
        public Task<IReadOnlyCollection<MarketCandleSnapshot>> GetClosedCandlesAsync(string symbol, string interval, DateTime startOpenTimeUtc, DateTime endOpenTimeUtc, int limit, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyCollection<MarketCandleSnapshot>>(Array.Empty<MarketCandleSnapshot>());
        }
    }

    private sealed class FakeAlertService : IAlertService
    {
        public Task SendAsync(AlertNotification notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoopAuditLogService : CoinBot.Application.Abstractions.Auditing.IAuditLogService
    {
        public Task WriteAsync(CoinBot.Application.Abstractions.Auditing.AuditLogWriteRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
