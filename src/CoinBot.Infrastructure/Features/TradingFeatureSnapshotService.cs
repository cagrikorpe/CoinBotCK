using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Features;
using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Features;

public sealed class TradingFeatureSnapshotService(
    ApplicationDbContext dbContext,
    IDataLatencyCircuitBreaker dataLatencyCircuitBreaker,
    ITradingModeResolver tradingModeResolver,
    IBinanceHistoricalKlineClient historicalKlineClient,
    IOptions<BotExecutionPilotOptions> pilotOptions,
    TimeProvider timeProvider,
    ILogger<TradingFeatureSnapshotService> logger,
    IOptions<ExecutionRuntimeOptions>? executionRuntimeOptions = null) : ITradingFeatureSnapshotService
{
    private const string FeatureVersionValue = "AI-1.v1";
    private const int RequiredSampleCount = 200;
    private const int MaxCandleLookback = 240;
    private const int AtrPeriod = 14;
    private const int BollingerPeriod = 20;
    private const decimal BollingerMultiplier = 2m;
    private const int FramaPeriod = 16;
    private const int AlmaPeriod = 20;
    private const double AlmaOffset = 0.85d;
    private const double AlmaSigma = 6d;
    private const int KdjPeriod = 9;
    private const int FisherPeriod = 10;
    private const int RelativeVolumePeriod = 20;
    private const int MfiPeriod = 14;
    private const int KlingerFastPeriod = 34;
    private const int KlingerSlowPeriod = 55;
    private const int KlingerSignalPeriod = 13;
    private const int ChandelierPeriod = 22;
    private const decimal ChandelierAtrMultiplier = 3m;
    private const int PmaxAtrPeriod = 10;
    private const int PmaxMaPeriod = 10;
    private const decimal PmaxAtrMultiplier = 3m;
    private static readonly TimeSpan MaxVetoCarryForwardAge = TimeSpan.FromMinutes(2);
    private readonly BotExecutionPilotOptions pilotOptionsValue = pilotOptions.Value;
    private readonly ExecutionRuntimeOptions executionRuntimeOptionsValue = executionRuntimeOptions?.Value ?? new ExecutionRuntimeOptions();

    public async Task<TradingFeatureSnapshotModel> CaptureAsync(
        TradingFeatureCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedUserId = dbContext.EnsureCurrentUserScope(request.UserId);
        var normalizedSymbol = NormalizeSymbol(request.Symbol);
        var normalizedTimeframe = NormalizeRequired(request.Timeframe, nameof(request.Timeframe));
        var normalizedStrategyKey = NormalizeRequired(request.StrategyKey, nameof(request.StrategyKey));
        var evaluatedAtUtc = request.EvaluatedAtUtc == default
            ? timeProvider.GetUtcNow().UtcDateTime
            : NormalizeTimestamp(request.EvaluatedAtUtc);
        var candles = await ResolveCandlesAsync(request, normalizedSymbol, normalizedTimeframe, evaluatedAtUtc, cancellationToken);
        var latencySnapshot = await dataLatencyCircuitBreaker.GetSnapshotAsync(
            symbol: normalizedSymbol,
            timeframe: normalizedTimeframe,
            cancellationToken: cancellationToken);
        var tradingModeResolution = await tradingModeResolver.ResolveAsync(
            new TradingModeResolutionRequest(
                UserId: normalizedUserId,
                BotId: request.BotId,
                StrategyKey: normalizedStrategyKey),
            cancellationToken);
        var featureAnchorTimeUtc = ResolveFeatureAnchorTimeUtc(
            request,
            candles,
            normalizedTimeframe,
            evaluatedAtUtc);
        var snapshotKey = BuildSnapshotKey(
            normalizedUserId,
            request.BotId,
            normalizedStrategyKey,
            normalizedSymbol,
            normalizedTimeframe,
            request.Plane,
            tradingModeResolution.EffectiveMode,
            featureAnchorTimeUtc);
        var existingSnapshot = await dbContext.TradingFeatureSnapshots
            .AsNoTracking()
            .Where(item =>
                item.OwnerUserId == normalizedUserId &&
                item.SnapshotKey == snapshotKey &&
                !item.IsDeleted)
            .OrderBy(item => item.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken);
        if (existingSnapshot is not null)
        {
            return MapSnapshot(existingSnapshot);
        }
        var latestOrder = await ResolveLatestOrderAsync(normalizedUserId, request.BotId, normalizedSymbol, normalizedTimeframe, evaluatedAtUtc, cancellationToken);
        var latestVeto = await ResolveLatestVetoAsync(normalizedUserId, normalizedSymbol, normalizedTimeframe, evaluatedAtUtc, cancellationToken);
        var hasOpenPosition = await ResolveHasOpenPositionAsync(
            normalizedUserId,
            request.BotId,
            request.ExchangeAccountId,
            normalizedSymbol,
            request.Plane,
            tradingModeResolution.EffectiveMode,
            cancellationToken);
        var isInCooldown = await ResolveCooldownActiveAsync(
            normalizedUserId,
            request.BotId,
            normalizedSymbol,
            evaluatedAtUtc,
            cancellationToken);
        var derived = DeriveFeatures(
            candles,
            request.IndicatorSnapshot,
            request.ReferencePrice,
            latencySnapshot,
            request.Plane,
            tradingModeResolution.EffectiveMode,
            hasOpenPosition,
            isInCooldown,
            latestOrder,
            latestVeto);

        var entity = new TradingFeatureSnapshot
        {
            OwnerUserId = normalizedUserId,
            BotId = request.BotId,
            ExchangeAccountId = request.ExchangeAccountId,
            CorrelationId = NormalizeOptional(request.CorrelationId),
            SnapshotKey = snapshotKey,
            StrategyKey = normalizedStrategyKey,
            Symbol = normalizedSymbol,
            Timeframe = normalizedTimeframe,
            EvaluatedAtUtc = evaluatedAtUtc,
            FeatureAnchorTimeUtc = featureAnchorTimeUtc,
            MarketDataTimestampUtc = derived.MarketDataTimestampUtc,
            FeatureVersion = FeatureVersionValue,
            SnapshotState = derived.SnapshotState,
            QualityReasonCode = derived.QualityReasonCode,
            MissingFeatureSummary = derived.MissingFeatureSummary,
            MarketDataReasonCode = derived.MarketDataReasonCode,
            SampleCount = candles.Count,
            RequiredSampleCount = RequiredSampleCount,
            ReferencePrice = derived.ReferencePrice,
            Ema20 = derived.Ema20,
            Ema50 = derived.Ema50,
            Ema200 = derived.Ema200,
            Alma = derived.Alma,
            Frama = derived.Frama,
            Rsi = derived.Rsi,
            MacdLine = derived.MacdLine,
            MacdSignal = derived.MacdSignal,
            MacdHistogram = derived.MacdHistogram,
            KdjK = derived.KdjK,
            KdjD = derived.KdjD,
            KdjJ = derived.KdjJ,
            FisherTransform = derived.FisherTransform,
            Atr = derived.Atr,
            BollingerPercentB = derived.BollingerPercentB,
            BollingerBandWidth = derived.BollingerBandWidth,
            KeltnerChannelRelation = derived.KeltnerChannelRelation,
            PmaxValue = derived.PmaxValue,
            ChandelierExit = derived.ChandelierExit,
            VolumeSpikeRatio = derived.VolumeSpikeRatio,
            RelativeVolume = derived.RelativeVolume,
            Obv = derived.Obv,
            Mfi = derived.Mfi,
            KlingerOscillator = derived.KlingerOscillator,
            KlingerSignal = derived.KlingerSignal,
            Plane = request.Plane,
            TradingMode = tradingModeResolution.EffectiveMode,
            HasOpenPosition = hasOpenPosition,
            IsInCooldown = isInCooldown,
            LastVetoReasonCode = derived.LastVetoReasonCode,
            LastDecisionOutcome = derived.LastDecisionOutcome,
            LastDecisionCode = derived.LastDecisionCode,
            LastExecutionState = derived.LastExecutionState,
            LastFailureCode = derived.LastFailureCode,
            FeatureSummary = derived.FeatureSummary,
            TopSignalHints = derived.TopSignalHints,
            PrimaryRegime = derived.PrimaryRegime,
            MomentumBias = derived.MomentumBias,
            VolatilityState = derived.VolatilityState,
            NormalizationMeta = derived.NormalizationMeta
        };

        dbContext.TradingFeatureSnapshots.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        return MapSnapshot(entity);
    }

    public async Task<TradingFeatureSnapshotModel?> GetLatestAsync(
        string userId,
        Guid botId,
        string symbol,
        string timeframe,
        CancellationToken cancellationToken = default)
    {
        var normalizedUserId = dbContext.EnsureCurrentUserScope(userId);
        var normalizedSymbol = NormalizeSymbol(symbol);
        var normalizedTimeframe = NormalizeRequired(timeframe, nameof(timeframe));

        var entity = await dbContext.TradingFeatureSnapshots
            .AsNoTracking()
            .Where(item => item.OwnerUserId == normalizedUserId &&
                           item.BotId == botId &&
                           item.Symbol == normalizedSymbol &&
                           item.Timeframe == normalizedTimeframe &&
                           !item.IsDeleted)
            .OrderByDescending(item => item.FeatureAnchorTimeUtc ?? item.EvaluatedAtUtc)
            .ThenByDescending(item => item.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken);

        return entity is null ? null : MapSnapshot(entity);
    }

    public async Task<IReadOnlyCollection<TradingFeatureSnapshotModel>> ListRecentAsync(
        string userId,
        Guid botId,
        string symbol,
        string timeframe,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        var normalizedUserId = dbContext.EnsureCurrentUserScope(userId);
        var normalizedSymbol = NormalizeSymbol(symbol);
        var normalizedTimeframe = NormalizeRequired(timeframe, nameof(timeframe));
        var normalizedTake = take <= 0 ? 20 : Math.Min(take, 200);

        var entities = await dbContext.TradingFeatureSnapshots
            .AsNoTracking()
            .Where(item => item.OwnerUserId == normalizedUserId &&
                           item.BotId == botId &&
                           item.Symbol == normalizedSymbol &&
                           item.Timeframe == normalizedTimeframe &&
                           !item.IsDeleted)
            .OrderByDescending(item => item.FeatureAnchorTimeUtc ?? item.EvaluatedAtUtc)
            .ThenByDescending(item => item.CreatedDate)
            .Take(normalizedTake)
            .ToListAsync(cancellationToken);

        return entities.Select(MapSnapshot).ToArray();
    }

    private async Task<IReadOnlyList<FeatureCandle>> ResolveCandlesAsync(
        TradingFeatureCaptureRequest request,
        string symbol,
        string timeframe,
        DateTime evaluatedAtUtc,
        CancellationToken cancellationToken)
    {
        var candles = NormalizeCandles(request.HistoricalCandles, symbol, timeframe);

        if (candles.Count < RequiredSampleCount)
        {
            var persistedCandles = await dbContext.HistoricalMarketCandles
                .AsNoTracking()
                .Where(item => item.Symbol == symbol &&
                               item.Interval == timeframe &&
                               !item.IsDeleted &&
                               item.CloseTimeUtc <= evaluatedAtUtc)
                .OrderByDescending(item => item.OpenTimeUtc)
                .Take(MaxCandleLookback)
                .Select(item => new FeatureCandle(
                    item.Symbol,
                    item.Interval,
                    NormalizeTimestamp(item.OpenTimeUtc),
                    NormalizeTimestamp(item.CloseTimeUtc),
                    item.OpenPrice,
                    item.HighPrice,
                    item.LowPrice,
                    item.ClosePrice,
                    item.Volume,
                    NormalizeTimestamp(item.ReceivedAtUtc),
                    item.Source))
                .ToListAsync(cancellationToken);

            candles = MergeCandles(candles, persistedCandles);
        }

        if (candles.Count < RequiredSampleCount)
        {
            candles = MergeCandles(
                candles,
                await TryBackfillCandlesAsync(symbol, timeframe, evaluatedAtUtc, cancellationToken));
        }

        return candles
            .OrderBy(item => item.OpenTimeUtc)
            .TakeLast(MaxCandleLookback)
            .ToArray();
    }

    private async Task<IReadOnlyCollection<FeatureCandle>> TryBackfillCandlesAsync(
        string symbol,
        string timeframe,
        DateTime evaluatedAtUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var interval = ResolveIntervalDuration(timeframe);
            var currentOpenTimeUtc = AlignToIntervalBoundary(evaluatedAtUtc, timeframe);
            var lastClosedOpenTimeUtc = currentOpenTimeUtc - interval;
            var startOpenTimeUtc = lastClosedOpenTimeUtc - ((MaxCandleLookback - 1) * interval);
            var backfill = await historicalKlineClient.GetClosedCandlesAsync(
                symbol,
                timeframe,
                startOpenTimeUtc,
                lastClosedOpenTimeUtc,
                MaxCandleLookback,
                cancellationToken);

            return NormalizeCandles(backfill, symbol, timeframe);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Trading feature snapshot backfill failed for {Symbol} {Timeframe}.",
                symbol,
                timeframe);
            return Array.Empty<FeatureCandle>();
        }
    }

    private static IReadOnlyList<FeatureCandle> NormalizeCandles(
        IReadOnlyCollection<MarketCandleSnapshot>? snapshots,
        string symbol,
        string timeframe)
    {
        if (snapshots is null || snapshots.Count == 0)
        {
            return Array.Empty<FeatureCandle>();
        }

        return snapshots
            .Where(item => item.IsClosed)
            .Select(item => new FeatureCandle(
                MarketDataSymbolNormalizer.Normalize(item.Symbol),
                NormalizeRequired(item.Interval, nameof(item.Interval)),
                NormalizeTimestamp(item.OpenTimeUtc),
                NormalizeTimestamp(item.CloseTimeUtc),
                item.OpenPrice,
                item.HighPrice,
                item.LowPrice,
                item.ClosePrice,
                item.Volume,
                NormalizeTimestamp(item.ReceivedAtUtc),
                item.Source.Trim()))
            .Where(item => item.Symbol == symbol && item.Timeframe == timeframe)
            .OrderBy(item => item.OpenTimeUtc)
            .ThenBy(item => item.ReceivedAtUtc)
            .GroupBy(item => item.OpenTimeUtc)
            .Select(group => group.OrderByDescending(item => item.ReceivedAtUtc).First())
            .ToArray();
    }

    private static IReadOnlyList<FeatureCandle> MergeCandles(
        IReadOnlyList<FeatureCandle> primary,
        IReadOnlyCollection<FeatureCandle> secondary)
    {
        if (secondary.Count == 0)
        {
            return primary;
        }

        return primary
            .Concat(secondary)
            .OrderBy(item => item.OpenTimeUtc)
            .ThenBy(item => item.ReceivedAtUtc)
            .GroupBy(item => item.OpenTimeUtc)
            .Select(group => group.OrderByDescending(item => item.ReceivedAtUtc).First())
            .ToArray();
    }

    private async Task<ExecutionOrder?> ResolveLatestOrderAsync(
        string userId,
        Guid botId,
        string symbol,
        string timeframe,
        DateTime evaluatedAtUtc,
        CancellationToken cancellationToken)
    {
        var minDecisionAtUtc = evaluatedAtUtc.Subtract(MaxVetoCarryForwardAge);
        return await dbContext.ExecutionOrders
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => item.OwnerUserId == userId &&
                           item.BotId == botId &&
                           item.Symbol == symbol &&
                           item.Timeframe == timeframe &&
                           item.LastStateChangedAtUtc >= minDecisionAtUtc &&
                           item.LastStateChangedAtUtc <= evaluatedAtUtc &&
                           !item.IsDeleted)
            .OrderByDescending(item => item.LastStateChangedAtUtc)
            .ThenByDescending(item => item.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<TradingStrategySignalVeto?> ResolveLatestVetoAsync(
        string userId,
        string symbol,
        string timeframe,
        DateTime evaluatedAtUtc,
        CancellationToken cancellationToken)
    {
        return await dbContext.TradingStrategySignalVetoes
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => item.OwnerUserId == userId &&
                           item.Symbol == symbol &&
                           item.Timeframe == timeframe &&
                           item.EvaluatedAtUtc >= evaluatedAtUtc.Subtract(MaxVetoCarryForwardAge) &&
                           item.EvaluatedAtUtc <= evaluatedAtUtc &&
                           !item.IsDeleted)
            .OrderByDescending(item => item.EvaluatedAtUtc)
            .ThenByDescending(item => item.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<bool> ResolveHasOpenPositionAsync(
        string userId,
        Guid botId,
        Guid? exchangeAccountId,
        string symbol,
        ExchangeDataPlane plane,
        ExecutionEnvironment tradingMode,
        CancellationToken cancellationToken)
    {
        if (UsesInternalDemoExecution(tradingMode))
        {
            return await dbContext.DemoPositions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .AnyAsync(item => item.OwnerUserId == userId &&
                                  item.BotId == botId &&
                                  item.Symbol == symbol &&
                                  !item.IsDeleted &&
                                  item.Quantity != 0m,
                    cancellationToken);
        }

        if (plane == ExchangeDataPlane.Spot)
        {
            return await dbContext.SpotPortfolioFills
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(item => item.OwnerUserId == userId &&
                               item.Symbol == symbol &&
                               !item.IsDeleted &&
                               (!exchangeAccountId.HasValue || item.ExchangeAccountId == exchangeAccountId.Value))
                .OrderByDescending(item => item.OccurredAtUtc)
                .Select(item => (decimal?)item.HoldingQuantityAfter)
                .FirstOrDefaultAsync(cancellationToken) is decimal holdingQuantityAfter && holdingQuantityAfter > 0m;
        }

        return await LivePositionTruthResolver.ResolveNetQuantityAsync(
            dbContext,
            userId,
            plane,
            exchangeAccountId,
            symbol,
            cancellationToken) != 0m;
    }

    private bool UsesInternalDemoExecution(ExecutionEnvironment tradingMode)
    {
        return tradingMode == ExecutionEnvironment.Demo &&
            executionRuntimeOptionsValue.AllowInternalDemoExecution;
    }

    private async Task<bool> ResolveCooldownActiveAsync(
        string userId,
        Guid botId,
        string symbol,
        DateTime evaluatedAtUtc,
        CancellationToken cancellationToken)
    {
        var latestBotCooldownOrder = await dbContext.ExecutionOrders
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => item.OwnerUserId == userId &&
                           item.BotId == botId &&
                           item.CooldownApplied &&
                           !item.IsDeleted)
            .OrderByDescending(item => item.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestBotCooldownOrder is not null &&
            pilotOptionsValue.PerBotCooldownSeconds > 0 &&
            latestBotCooldownOrder.CreatedDate.AddSeconds(pilotOptionsValue.PerBotCooldownSeconds) > evaluatedAtUtc)
        {
            return true;
        }

        var latestSymbolCooldownOrder = await dbContext.ExecutionOrders
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => item.OwnerUserId == userId &&
                           item.Symbol == symbol &&
                           item.CooldownApplied &&
                           !item.IsDeleted)
            .OrderByDescending(item => item.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken);

        return latestSymbolCooldownOrder is not null &&
               pilotOptionsValue.PerSymbolCooldownSeconds > 0 &&
               latestSymbolCooldownOrder.CreatedDate.AddSeconds(pilotOptionsValue.PerSymbolCooldownSeconds) > evaluatedAtUtc;
    }

    private static DerivedFeatureSnapshot DeriveFeatures(
        IReadOnlyList<FeatureCandle> candles,
        StrategyIndicatorSnapshot? indicatorSnapshot,
        decimal? referencePrice,
        DegradedModeSnapshot latencySnapshot,
        ExchangeDataPlane plane,
        ExecutionEnvironment tradingMode,
        bool hasOpenPosition,
        bool isInCooldown,
        ExecutionOrder? latestOrder,
        TradingStrategySignalVeto? latestVeto)
    {
        var marketDataTimestampUtc = candles.Count > 0
            ? candles[^1].CloseTimeUtc
            : indicatorSnapshot?.CloseTimeUtc ?? latencySnapshot.LatestDataTimestampAtUtc;
        var resolvedReferencePrice = referencePrice > 0m
            ? referencePrice
            : candles.Count > 0 ? candles[^1].ClosePrice : null;
        var highs = candles.Select(item => item.HighPrice).ToArray();
        var lows = candles.Select(item => item.LowPrice).ToArray();
        var closes = candles.Select(item => item.ClosePrice).ToArray();
        var volumes = candles.Select(item => item.Volume).ToArray();
        var latestClose = closes.Length > 0 ? closes[^1] : resolvedReferencePrice;
        var invalidNumericValueDetected = false;

        var ema20 = ComputeEma(closes, 20, ref invalidNumericValueDetected);
        var ema50 = ComputeEma(closes, 50, ref invalidNumericValueDetected);
        var ema200 = ComputeEma(closes, 200, ref invalidNumericValueDetected);
        var alma = ComputeAlma(closes, AlmaPeriod, AlmaOffset, AlmaSigma, ref invalidNumericValueDetected);
        var frama = ComputeFrama(highs, lows, closes, FramaPeriod, ref invalidNumericValueDetected);
        var atr = ComputeAtr(highs, lows, closes, AtrPeriod);
        var macd = indicatorSnapshot?.Macd.IsReady == true
            ? (indicatorSnapshot.Macd.MacdLine, indicatorSnapshot.Macd.SignalLine, indicatorSnapshot.Macd.Histogram)
            : ComputeMacd(closes, 12, 26, 9, ref invalidNumericValueDetected);
        var rsi = indicatorSnapshot?.Rsi.IsReady == true
            ? indicatorSnapshot.Rsi.Value
            : ComputeRsi(closes, 14);
        var bollinger = indicatorSnapshot?.Bollinger.IsReady == true
            ? (indicatorSnapshot.Bollinger.MiddleBand, indicatorSnapshot.Bollinger.UpperBand, indicatorSnapshot.Bollinger.LowerBand, indicatorSnapshot.Bollinger.StandardDeviation)
            : ComputeBollinger(closes, BollingerPeriod, BollingerMultiplier, ref invalidNumericValueDetected);
        var bollingerPercentB = ComputeBollingerPercentB(latestClose, bollinger.Item2, bollinger.Item3);
        var bollingerBandWidth = ComputeBollingerBandwidth(bollinger.Item1, bollinger.Item2, bollinger.Item3);
        var (kdjK, kdjD, kdjJ) = ComputeKdj(highs, lows, closes, KdjPeriod);
        var fisher = ComputeFisherTransform(highs, lows, closes, FisherPeriod, ref invalidNumericValueDetected);
        var relativeVolume = ComputeRelativeVolume(volumes, RelativeVolumePeriod);
        var volumeSpikeRatio = ComputeVolumeSpike(volumes, RelativeVolumePeriod);
        var obv = ComputeObv(closes, volumes);
        var mfi = ComputeMfi(highs, lows, closes, volumes, MfiPeriod);
        var (klingerOscillator, klingerSignal) = ComputeKlinger(highs, lows, closes, volumes, KlingerFastPeriod, KlingerSlowPeriod, KlingerSignalPeriod, ref invalidNumericValueDetected);
        var keltnerChannelRelation = ComputeKeltnerChannelRelation(latestClose, ema20, atr);
        var pmaxValue = ComputePmax(highs, lows, closes, PmaxMaPeriod, PmaxAtrPeriod, PmaxAtrMultiplier, ref invalidNumericValueDetected);
        var chandelierExit = ComputeChandelierExit(highs, atr, ChandelierPeriod, ChandelierAtrMultiplier);

        var baseSnapshotState = ResolveSnapshotState(candles.Count, marketDataTimestampUtc, latencySnapshot, indicatorSnapshot);
        var qualityEvaluation = EvaluateSnapshotQuality(
            candles,
            indicatorSnapshot,
            baseSnapshotState,
            marketDataTimestampUtc,
            resolvedReferencePrice,
            latestClose,
            invalidNumericValueDetected,
            ema20,
            ema50,
            ema200,
            alma,
            frama,
            rsi,
            macd.MacdLine,
            macd.SignalLine,
            macd.Histogram,
            kdjK,
            kdjD,
            kdjJ,
            fisher,
            atr,
            bollingerPercentB,
            bollingerBandWidth,
            keltnerChannelRelation,
            pmaxValue,
            chandelierExit,
            volumeSpikeRatio,
            relativeVolume,
            obv,
            mfi,
            klingerOscillator,
            klingerSignal);
        var snapshotState = ResolveQualifiedSnapshotState(baseSnapshotState, qualityEvaluation.QualityReasonCode);
        var marketDataReasonCode = ResolveMarketDataReasonCode(snapshotState, latencySnapshot, indicatorSnapshot);
        var primaryRegime = ResolvePrimaryRegime(latestClose, ema50, ema200, frama, pmaxValue, bollingerBandWidth);
        var momentumBias = ResolveMomentumBias(rsi, macd.Histogram, fisher, mfi, klingerOscillator);
        var volatilityState = ResolveVolatilityState(snapshotState, atr, latestClose, bollingerBandWidth, relativeVolume);
        var (lastDecisionOutcome, lastDecisionCode, lastExecutionState, lastFailureCode, lastVetoReasonCode) =
            ResolveDecisionContext(latestOrder, latestVeto);
        var topSignalHints = BuildTopSignalHints(
            snapshotState,
            qualityEvaluation.QualityReasonCode,
            qualityEvaluation.MissingFeatureSummary,
            marketDataReasonCode,
            primaryRegime,
            momentumBias,
            volatilityState,
            relativeVolume,
            volumeSpikeRatio,
            mfi,
            klingerOscillator,
            hasOpenPosition,
            isInCooldown,
            lastFailureCode,
            lastVetoReasonCode);
        var featureSummary = BuildFeatureSummary(
            snapshotState,
            qualityEvaluation.QualityReasonCode,
            qualityEvaluation.MissingFeatureSummary,
            marketDataReasonCode,
            primaryRegime,
            momentumBias,
            volatilityState,
            topSignalHints);
        var normalizationMeta = BuildNormalizationMeta(
            latestClose,
            atr,
            relativeVolume,
            volumeSpikeRatio,
            mfi,
            klingerOscillator,
            klingerSignal,
            candles.Count,
            indicatorSnapshot is not null && indicatorSnapshot.State == IndicatorDataState.Ready,
            qualityEvaluation.QualityReasonCode);

        return new DerivedFeatureSnapshot(
            marketDataTimestampUtc,
            resolvedReferencePrice,
            snapshotState,
            qualityEvaluation.QualityReasonCode,
            qualityEvaluation.MissingFeatureSummary,
            marketDataReasonCode,
            ema20,
            ema50,
            ema200,
            alma,
            frama,
            rsi,
            macd.MacdLine,
            macd.SignalLine,
            macd.Histogram,
            kdjK,
            kdjD,
            kdjJ,
            fisher,
            atr,
            bollingerPercentB,
            bollingerBandWidth,
            keltnerChannelRelation,
            pmaxValue,
            chandelierExit,
            volumeSpikeRatio,
            relativeVolume,
            obv,
            mfi,
            klingerOscillator,
            klingerSignal,
            primaryRegime,
            momentumBias,
            volatilityState,
            featureSummary,
            topSignalHints,
            normalizationMeta,
            lastDecisionOutcome,
            lastDecisionCode,
            lastExecutionState,
            lastFailureCode,
            lastVetoReasonCode,
            plane,
            tradingMode,
            hasOpenPosition,
            isInCooldown);
    }

    private static FeatureSnapshotState ResolveSnapshotState(
        int sampleCount,
        DateTime? marketDataTimestampUtc,
        DegradedModeSnapshot latencySnapshot,
        StrategyIndicatorSnapshot? indicatorSnapshot)
    {
        if (!marketDataTimestampUtc.HasValue || sampleCount == 0)
        {
            return FeatureSnapshotState.MissingData;
        }

        if (!latencySnapshot.IsNormal)
        {
            return FeatureSnapshotState.Stale;
        }

        if (indicatorSnapshot?.State == IndicatorDataState.MissingData)
        {
            return FeatureSnapshotState.MissingData;
        }

        if (sampleCount < RequiredSampleCount || indicatorSnapshot?.State == IndicatorDataState.WarmingUp)
        {
            return FeatureSnapshotState.WarmingUp;
        }

        return FeatureSnapshotState.Ready;
    }

    private static FeatureSnapshotState ResolveQualifiedSnapshotState(
        FeatureSnapshotState baseSnapshotState,
        FeatureSnapshotQualityReason qualityReasonCode)
    {
        return qualityReasonCode switch
        {
            FeatureSnapshotQualityReason.None => baseSnapshotState,
            FeatureSnapshotQualityReason.InsufficientCandles => baseSnapshotState == FeatureSnapshotState.Stale
                ? FeatureSnapshotState.Stale
                : FeatureSnapshotState.WarmingUp,
            FeatureSnapshotQualityReason.MissingInputs => baseSnapshotState == FeatureSnapshotState.Stale
                ? FeatureSnapshotState.Stale
                : FeatureSnapshotState.MissingData,
            FeatureSnapshotQualityReason.InvalidNumericValue or FeatureSnapshotQualityReason.InvalidRange or FeatureSnapshotQualityReason.IncompleteSnapshot => FeatureSnapshotState.Invalid,
            _ => baseSnapshotState
        };
    }

    private static FeatureSnapshotQualityEvaluation EvaluateSnapshotQuality(
        IReadOnlyList<FeatureCandle> candles,
        StrategyIndicatorSnapshot? indicatorSnapshot,
        FeatureSnapshotState baseSnapshotState,
        DateTime? marketDataTimestampUtc,
        decimal? referencePrice,
        decimal? latestClose,
        bool invalidNumericValueDetected,
        params decimal?[] requiredIndicators)
    {
        if (candles.Count == 0 || !marketDataTimestampUtc.HasValue)
        {
            return new FeatureSnapshotQualityEvaluation(
                FeatureSnapshotQualityReason.MissingInputs,
                BuildMissingInputSummary(candles.Count == 0, !marketDataTimestampUtc.HasValue, !referencePrice.HasValue, !latestClose.HasValue, indicatorSnapshot));
        }

        if (baseSnapshotState == FeatureSnapshotState.WarmingUp)
        {
            return new FeatureSnapshotQualityEvaluation(
                FeatureSnapshotQualityReason.InsufficientCandles,
                FormattableString.Invariant($"SampleCount={candles.Count}/{RequiredSampleCount}; IndicatorState={indicatorSnapshot?.State.ToString() ?? "Unavailable"}"));
        }

        if (baseSnapshotState == FeatureSnapshotState.MissingData)
        {
            return new FeatureSnapshotQualityEvaluation(
                FeatureSnapshotQualityReason.MissingInputs,
                BuildMissingInputSummary(false, false, !referencePrice.HasValue, !latestClose.HasValue, indicatorSnapshot));
        }

        if (HasInvalidCandleRanges(candles) || HasInvalidIndicatorRanges(referencePrice, latestClose, requiredIndicators))
        {
            return new FeatureSnapshotQualityEvaluation(
                FeatureSnapshotQualityReason.InvalidRange,
                "One or more candle or indicator values are outside the allowed range.");
        }

        if (invalidNumericValueDetected)
        {
            return new FeatureSnapshotQualityEvaluation(
                FeatureSnapshotQualityReason.InvalidNumericValue,
                "Indicator computation produced NaN, infinity, or overflowed numeric output.");
        }

        if (baseSnapshotState == FeatureSnapshotState.Ready)
        {
            var missingIndicators = new List<string>();
            var indicatorNames = new[]
            {
                "Ema20",
                "Ema50",
                "Ema200",
                "Alma",
                "Frama",
                "Rsi",
                "MacdLine",
                "MacdSignal",
                "MacdHistogram",
                "KdjK",
                "KdjD",
                "KdjJ",
                "FisherTransform",
                "Atr",
                "BollingerPercentB",
                "BollingerBandWidth",
                "KeltnerChannelRelation",
                "PmaxValue",
                "ChandelierExit",
                "VolumeSpikeRatio",
                "RelativeVolume",
                "Obv",
                "Mfi",
                "KlingerOscillator",
                "KlingerSignal"
            };

            for (var index = 0; index < indicatorNames.Length && index < requiredIndicators.Length; index++)
            {
                if (!requiredIndicators[index].HasValue)
                {
                    missingIndicators.Add(indicatorNames[index]);
                }
            }

            if (missingIndicators.Count > 0)
            {
                return new FeatureSnapshotQualityEvaluation(
                    FeatureSnapshotQualityReason.IncompleteSnapshot,
                    BuildMissingFeatureSummary(missingIndicators));
            }
        }

        return new FeatureSnapshotQualityEvaluation(FeatureSnapshotQualityReason.None, null);
    }

    private static bool HasInvalidCandleRanges(IReadOnlyList<FeatureCandle> candles)
    {
        return candles.Any(candle =>
            candle.OpenPrice <= 0m ||
            candle.ClosePrice <= 0m ||
            candle.HighPrice <= 0m ||
            candle.LowPrice <= 0m ||
            candle.Volume < 0m ||
            candle.HighPrice < candle.LowPrice ||
            candle.HighPrice < candle.OpenPrice ||
            candle.HighPrice < candle.ClosePrice ||
            candle.LowPrice > candle.OpenPrice ||
            candle.LowPrice > candle.ClosePrice ||
            candle.CloseTimeUtc < candle.OpenTimeUtc ||
            candle.ReceivedAtUtc < candle.CloseTimeUtc);
    }

    private static bool HasInvalidIndicatorRanges(decimal? referencePrice, decimal? latestClose, params decimal?[] requiredIndicators)
    {
        if (referencePrice.HasValue && referencePrice.Value <= 0m)
        {
            return true;
        }

        if (latestClose.HasValue && latestClose.Value <= 0m)
        {
            return true;
        }

        if (requiredIndicators.Length < 25)
        {
            return false;
        }

        var rsi = requiredIndicators[5];
        var atr = requiredIndicators[13];
        var bollingerBandWidth = requiredIndicators[15];
        var volumeSpikeRatio = requiredIndicators[19];
        var relativeVolume = requiredIndicators[20];
        var mfi = requiredIndicators[22];

        return (rsi.HasValue && (rsi.Value < 0m || rsi.Value > 100m)) ||
               (mfi.HasValue && (mfi.Value < 0m || mfi.Value > 100m)) ||
               (atr.HasValue && atr.Value < 0m) ||
               (bollingerBandWidth.HasValue && bollingerBandWidth.Value < 0m) ||
               (volumeSpikeRatio.HasValue && volumeSpikeRatio.Value < 0m) ||
               (relativeVolume.HasValue && relativeVolume.Value < 0m);
    }

    private static string BuildMissingInputSummary(
        bool missingCandles,
        bool missingMarketTimestamp,
        bool missingReferencePrice,
        bool missingLatestClose,
        StrategyIndicatorSnapshot? indicatorSnapshot)
    {
        var missingInputs = new List<string>();
        if (missingCandles)
        {
            missingInputs.Add("Candles");
        }

        if (missingMarketTimestamp)
        {
            missingInputs.Add("MarketDataTimestampUtc");
        }

        if (missingReferencePrice)
        {
            missingInputs.Add("ReferencePrice");
        }

        if (missingLatestClose)
        {
            missingInputs.Add("LatestClose");
        }

        if (indicatorSnapshot?.State == IndicatorDataState.MissingData)
        {
            missingInputs.Add("IndicatorSnapshot");
        }

        return missingInputs.Count == 0
            ? "MissingInputs=Unknown"
            : $"MissingInputs={string.Join(',', missingInputs.Distinct(StringComparer.Ordinal))}";
    }

    private static string BuildMissingFeatureSummary(IEnumerable<string> missingIndicators)
    {
        return $"MissingFeatures={string.Join(',', missingIndicators.Where(static item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.Ordinal))}";
    }

    private static DegradedModeReasonCode ResolveMarketDataReasonCode(
        FeatureSnapshotState snapshotState,
        DegradedModeSnapshot latencySnapshot,
        StrategyIndicatorSnapshot? indicatorSnapshot)
    {
        if (snapshotState == FeatureSnapshotState.Stale)
        {
            return latencySnapshot.ReasonCode == DegradedModeReasonCode.None
                ? DegradedModeReasonCode.MarketDataLatencyBreached
                : latencySnapshot.ReasonCode;
        }

        if (snapshotState == FeatureSnapshotState.MissingData)
        {
            return indicatorSnapshot?.DataQualityReasonCode is { } indicatorReasonCode && indicatorReasonCode != DegradedModeReasonCode.None
                ? indicatorReasonCode
                : DegradedModeReasonCode.MarketDataUnavailable;
        }

        if (snapshotState == FeatureSnapshotState.WarmingUp)
        {
            return indicatorSnapshot?.DataQualityReasonCode ?? DegradedModeReasonCode.None;
        }

        return DegradedModeReasonCode.None;
    }

    private static string ResolvePrimaryRegime(
        decimal? latestClose,
        decimal? ema50,
        decimal? ema200,
        decimal? frama,
        decimal? pmaxValue,
        decimal? bollingerBandWidth)
    {
        if (latestClose is null)
        {
            return "Unknown";
        }

        if (pmaxValue.HasValue && latestClose.Value > pmaxValue.Value && ema50.HasValue && ema200.HasValue && ema50.Value >= ema200.Value)
        {
            return "BullTrend";
        }

        if (pmaxValue.HasValue && latestClose.Value < pmaxValue.Value && ema50.HasValue && ema200.HasValue && ema50.Value <= ema200.Value)
        {
            return "BearTrend";
        }

        if (frama.HasValue && ema50.HasValue && Math.Abs(frama.Value - ema50.Value) <= Math.Max(1m, ema50.Value * 0.0025m))
        {
            return "Range";
        }

        if (bollingerBandWidth.HasValue && bollingerBandWidth.Value <= 0.05m)
        {
            return "Compression";
        }

        return "Transition";
    }

    private static string ResolveMomentumBias(decimal? rsi, decimal? macdHistogram, decimal? fisher, decimal? mfi, decimal? klingerOscillator)
    {
        if (rsi is null && macdHistogram is null && fisher is null && mfi is null && klingerOscillator is null)
        {
            return "Unknown";
        }

        if ((rsi.HasValue && rsi.Value <= 30m) || (mfi.HasValue && mfi.Value <= 20m))
        {
            return "Oversold";
        }

        if ((rsi.HasValue && rsi.Value >= 70m) || (mfi.HasValue && mfi.Value >= 80m))
        {
            return "Overbought";
        }

        var bullishVotes = 0;
        var bearishVotes = 0;

        if (rsi.HasValue)
        {
            if (rsi.Value >= 55m) bullishVotes++;
            if (rsi.Value <= 45m) bearishVotes++;
        }

        if (macdHistogram.HasValue)
        {
            if (macdHistogram.Value >= 0m) bullishVotes++;
            if (macdHistogram.Value <= 0m) bearishVotes++;
        }

        if (fisher.HasValue)
        {
            if (fisher.Value >= 0m) bullishVotes++;
            if (fisher.Value <= 0m) bearishVotes++;
        }

        if (mfi.HasValue)
        {
            if (mfi.Value >= 55m) bullishVotes++;
            if (mfi.Value <= 45m) bearishVotes++;
        }

        if (klingerOscillator.HasValue)
        {
            if (klingerOscillator.Value >= 0m) bullishVotes++;
            if (klingerOscillator.Value <= 0m) bearishVotes++;
        }

        if (bullishVotes >= 3)
        {
            return "Bullish";
        }

        if (bearishVotes >= 3)
        {
            return "Bearish";
        }

        return "Neutral";
    }

    private static string ResolveVolatilityState(
        FeatureSnapshotState snapshotState,
        decimal? atr,
        decimal? latestClose,
        decimal? bollingerBandWidth,
        decimal? relativeVolume)
    {
        if (snapshotState == FeatureSnapshotState.Stale)
        {
            return "Stale";
        }

        if (!latestClose.HasValue || latestClose.Value <= 0m)
        {
            return "Unknown";
        }

        var atrPercent = atr.HasValue && latestClose.Value > 0m
            ? atr.Value / latestClose.Value
            : (decimal?)null;

        if ((atrPercent.HasValue && atrPercent.Value >= 0.03m) ||
            (bollingerBandWidth.HasValue && bollingerBandWidth.Value >= 0.12m) ||
            (relativeVolume.HasValue && relativeVolume.Value >= 1.75m))
        {
            return "Expanded";
        }

        if (bollingerBandWidth.HasValue && bollingerBandWidth.Value <= 0.05m)
        {
            return "Compressed";
        }

        return "Normal";
    }

    private static (string? LastDecisionOutcome, string? LastDecisionCode, string? LastExecutionState, string? LastFailureCode, string? LastVetoReasonCode)
        ResolveDecisionContext(
            ExecutionOrder? latestOrder,
            TradingStrategySignalVeto? latestVeto)
    {
        var vetoIsNewer = latestVeto is not null &&
                          (latestOrder is null || latestVeto.EvaluatedAtUtc >= latestOrder.LastStateChangedAtUtc);

        if (vetoIsNewer)
        {
            return (
                "Blocked",
                latestVeto!.ReasonCode.ToString(),
                "Vetoed",
                null,
                latestVeto.ReasonCode.ToString());
        }

        if (latestOrder is null)
        {
            return ("None", null, null, null, latestVeto?.ReasonCode.ToString());
        }

        var outcome = latestOrder.State is ExecutionOrderState.Rejected or ExecutionOrderState.Failed
            ? "Blocked"
            : "Allowed";
        var decisionCode = NormalizeOptional(latestOrder.FailureCode) ?? latestOrder.State.ToString();

        return (
            outcome,
            decisionCode,
            latestOrder.State.ToString(),
            NormalizeOptional(latestOrder.FailureCode),
            latestVeto?.ReasonCode.ToString());
    }

    private static string BuildTopSignalHints(
        FeatureSnapshotState snapshotState,
        FeatureSnapshotQualityReason qualityReasonCode,
        string? missingFeatureSummary,
        DegradedModeReasonCode marketDataReasonCode,
        string primaryRegime,
        string momentumBias,
        string volatilityState,
        decimal? relativeVolume,
        decimal? volumeSpikeRatio,
        decimal? mfi,
        decimal? klingerOscillator,
        bool hasOpenPosition,
        bool isInCooldown,
        string? latestFailureCode,
        string? lastVetoReasonCode)
    {
        var hints = new List<string>();

        if (qualityReasonCode != FeatureSnapshotQualityReason.None)
        {
            hints.Add($"Quality:{qualityReasonCode}");
        }

        if (!string.IsNullOrWhiteSpace(missingFeatureSummary))
        {
            hints.Add($"Missing:{TrimSummaryValue(missingFeatureSummary!)}");
        }

        if (snapshotState == FeatureSnapshotState.Stale)
        {
            hints.Add($"MarketDataStale:{marketDataReasonCode}");
        }
        else if (snapshotState == FeatureSnapshotState.MissingData)
        {
            hints.Add($"MarketDataMissing:{marketDataReasonCode}");
        }
        else if (snapshotState == FeatureSnapshotState.WarmingUp)
        {
            hints.Add("FeatureWarmup");
        }
        else if (snapshotState == FeatureSnapshotState.Invalid)
        {
            hints.Add("FeatureInvalid");
        }

        if (!string.Equals(primaryRegime, "Unknown", StringComparison.Ordinal))
        {
            hints.Add($"Regime:{primaryRegime}");
        }

        if (!string.Equals(momentumBias, "Unknown", StringComparison.Ordinal) &&
            !string.Equals(momentumBias, "Neutral", StringComparison.Ordinal))
        {
            hints.Add($"Momentum:{momentumBias}");
        }

        if (!string.Equals(volatilityState, "Unknown", StringComparison.Ordinal) &&
            !string.Equals(volatilityState, "Normal", StringComparison.Ordinal))
        {
            hints.Add($"Volatility:{volatilityState}");
        }

        if (relativeVolume.HasValue && relativeVolume.Value >= 1.5m)
        {
            hints.Add("RelativeVolumeElevated");
        }

        if (volumeSpikeRatio.HasValue && volumeSpikeRatio.Value >= 1.75m)
        {
            hints.Add("VolumeSpikeDetected");
        }

        if (mfi.HasValue)
        {
            if (mfi.Value <= 20m)
            {
                hints.Add("MfiOversold");
            }
            else if (mfi.Value >= 80m)
            {
                hints.Add("MfiOverbought");
            }
        }

        if (klingerOscillator.HasValue)
        {
            hints.Add(klingerOscillator.Value >= 0m ? "KlingerBullish" : "KlingerBearish");
        }

        if (hasOpenPosition)
        {
            hints.Add("OpenPositionPresent");
        }

        if (isInCooldown)
        {
            hints.Add("CooldownActive");
        }

        if (!string.IsNullOrWhiteSpace(lastVetoReasonCode))
        {
            hints.Add($"LastVeto:{lastVetoReasonCode}");
        }

        if (!string.IsNullOrWhiteSpace(latestFailureCode))
        {
            hints.Add($"LastFailure:{latestFailureCode}");
        }

        return string.Join(" | ", hints
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .Take(6));
    }

    private static string BuildFeatureSummary(
        FeatureSnapshotState snapshotState,
        FeatureSnapshotQualityReason qualityReasonCode,
        string? missingFeatureSummary,
        DegradedModeReasonCode marketDataReasonCode,
        string primaryRegime,
        string momentumBias,
        string volatilityState,
        string topSignalHints)
    {
        var normalizedHints = string.IsNullOrWhiteSpace(topSignalHints)
            ? "none"
            : topSignalHints;
        var normalizedMissingSummary = string.IsNullOrWhiteSpace(missingFeatureSummary)
            ? "none"
            : missingFeatureSummary;

        return FormattableString.Invariant(
            $"State={snapshotState}; Quality={qualityReasonCode}; Missing={normalizedMissingSummary}; MarketReason={marketDataReasonCode}; Regime={primaryRegime}; Momentum={momentumBias}; Volatility={volatilityState}; Hints={normalizedHints}");
    }

    private static string BuildNormalizationMeta(
        decimal? latestClose,
        decimal? atr,
        decimal? relativeVolume,
        decimal? volumeSpikeRatio,
        decimal? mfi,
        decimal? klingerOscillator,
        decimal? klingerSignal,
        int sampleCount,
        bool indicatorParityAvailable,
        FeatureSnapshotQualityReason qualityReasonCode)
    {
        var atrPercent = latestClose.HasValue && latestClose.Value > 0m && atr.HasValue
            ? atr.Value / latestClose.Value
            : (decimal?)null;

        return FormattableString.Invariant(
            $"ClosePrice={FormatDecimal(latestClose)}; AtrPct={FormatDecimal(atrPercent)}; RelativeVolume={FormatDecimal(relativeVolume)}; VolumeSpikeRatio={FormatDecimal(volumeSpikeRatio)}; Mfi={FormatDecimal(mfi)}; KlingerOscillator={FormatDecimal(klingerOscillator)}; KlingerSignal={FormatDecimal(klingerSignal)}; SampleCount={sampleCount}; IndicatorParity={indicatorParityAvailable}; Quality={qualityReasonCode}");
    }

    private static string TrimSummaryValue(string value, int maxLength = 96)
    {
        var normalizedValue = value.Trim();
        return normalizedValue.Length <= maxLength
            ? normalizedValue
            : normalizedValue[..maxLength];
    }

    private static decimal? ComputeEma(IReadOnlyList<decimal> values, int period, ref bool invalidNumericValueDetected)
    {
        if (values.Count < period || period <= 0)
        {
            return null;
        }

        var multiplier = 2d / (period + 1d);
        var ema = (double)values.Take(period).Average();

        for (var index = period; index < values.Count; index++)
        {
            ema = (((double)values[index] - ema) * multiplier) + ema;
        }

        return TryConvertDecimal(ema, ref invalidNumericValueDetected);
    }

    private static decimal? ComputeAlma(IReadOnlyList<decimal> values, int period, double offset, double sigma, ref bool invalidNumericValueDetected)
    {
        if (values.Count < period || period <= 0)
        {
            return null;
        }

        var window = values.Skip(values.Count - period).Select(value => (double)value).ToArray();
        var m = offset * (period - 1);
        var s = period / sigma;
        var weightedSum = 0d;
        var weightTotal = 0d;

        for (var index = 0; index < period; index++)
        {
            var weight = Math.Exp(-Math.Pow(index - m, 2d) / (2d * Math.Pow(s, 2d)));
            weightedSum += window[index] * weight;
            weightTotal += weight;
        }

        return weightTotal <= 0d ? null : TryConvertDecimal(weightedSum / weightTotal, ref invalidNumericValueDetected);
    }

    private static decimal? ComputeFrama(
        IReadOnlyList<decimal> highs,
        IReadOnlyList<decimal> lows,
        IReadOnlyList<decimal> closes,
        int period,
        ref bool invalidNumericValueDetected)
    {
        if (closes.Count < period || highs.Count < period || lows.Count < period || period < 4)
        {
            return null;
        }

        var frama = (double)closes[period - 1];
        var halfPeriod = period / 2;

        for (var index = period - 1; index < closes.Count; index++)
        {
            var windowStart = index - period + 1;
            var firstHalfHigh = highs.Skip(windowStart).Take(halfPeriod).Max();
            var firstHalfLow = lows.Skip(windowStart).Take(halfPeriod).Min();
            var secondHalfHigh = highs.Skip(windowStart + halfPeriod).Take(period - halfPeriod).Max();
            var secondHalfLow = lows.Skip(windowStart + halfPeriod).Take(period - halfPeriod).Min();
            var fullHigh = highs.Skip(windowStart).Take(period).Max();
            var fullLow = lows.Skip(windowStart).Take(period).Min();
            var n1 = (double)((firstHalfHigh - firstHalfLow) / Math.Max(1, halfPeriod));
            var n2 = (double)((secondHalfHigh - secondHalfLow) / Math.Max(1, period - halfPeriod));
            var n3 = (double)((fullHigh - fullLow) / period);

            if (n1 <= 0d || n2 <= 0d || n3 <= 0d)
            {
                frama = (double)closes[index];
                continue;
            }

            var dimension = (Math.Log(n1 + n2) - Math.Log(n3)) / Math.Log(2d);
            var alpha = Math.Exp(-4.6d * (dimension - 1d));
            alpha = Math.Clamp(alpha, 0.01d, 1d);
            frama = alpha * (double)closes[index] + (1d - alpha) * frama;
        }

        return TryConvertDecimal(frama, ref invalidNumericValueDetected);
    }

    private static decimal? ComputeRsi(IReadOnlyList<decimal> closes, int period)
    {
        if (closes.Count <= period || period <= 0)
        {
            return null;
        }

        var gains = 0m;
        var losses = 0m;
        for (var index = 1; index <= period; index++)
        {
            var delta = closes[index] - closes[index - 1];
            if (delta >= 0m)
            {
                gains += delta;
            }
            else
            {
                losses -= delta;
            }
        }

        var averageGain = gains / period;
        var averageLoss = losses / period;
        for (var index = period + 1; index < closes.Count; index++)
        {
            var delta = closes[index] - closes[index - 1];
            var gain = delta > 0m ? delta : 0m;
            var loss = delta < 0m ? -delta : 0m;
            averageGain = ((averageGain * (period - 1)) + gain) / period;
            averageLoss = ((averageLoss * (period - 1)) + loss) / period;
        }

        if (averageLoss == 0m)
        {
            return 100m;
        }

        var rs = averageGain / averageLoss;
        return 100m - (100m / (1m + rs));
    }

    private static (decimal? MacdLine, decimal? SignalLine, decimal? Histogram) ComputeMacd(
        IReadOnlyList<decimal> closes,
        int fastPeriod,
        int slowPeriod,
        int signalPeriod,
        ref bool invalidNumericValueDetected)
    {
        if (closes.Count < slowPeriod + signalPeriod)
        {
            return (null, null, null);
        }

        var fastSeries = ComputeEmaSeries(closes, fastPeriod, ref invalidNumericValueDetected);
        var slowSeries = ComputeEmaSeries(closes, slowPeriod, ref invalidNumericValueDetected);
        var macdSeries = new List<decimal>();

        for (var index = 0; index < closes.Count; index++)
        {
            if (!fastSeries[index].HasValue || !slowSeries[index].HasValue)
            {
                continue;
            }

            macdSeries.Add(fastSeries[index]!.Value - slowSeries[index]!.Value);
        }

        var signal = ComputeEma(macdSeries, signalPeriod, ref invalidNumericValueDetected);
        var macdLine = macdSeries.Count > 0 ? macdSeries[^1] : (decimal?)null;
        var histogram = macdLine.HasValue && signal.HasValue ? macdLine.Value - signal.Value : (decimal?)null;
        return (macdLine, signal, histogram);
    }

    private static (decimal? MiddleBand, decimal? UpperBand, decimal? LowerBand, decimal? StandardDeviation) ComputeBollinger(
        IReadOnlyList<decimal> closes,
        int period,
        decimal multiplier,
        ref bool invalidNumericValueDetected)
    {
        if (closes.Count < period || period <= 0)
        {
            return (null, null, null, null);
        }

        var window = closes.Skip(closes.Count - period).Select(value => (double)value).ToArray();
        var average = window.Average();
        var variance = window.Select(value => Math.Pow(value - average, 2d)).Average();
        var standardDeviation = Math.Sqrt(variance);
        var middle = TryConvertDecimal(average, ref invalidNumericValueDetected);
        var deviation = TryConvertDecimal(standardDeviation, ref invalidNumericValueDetected);
        if (!middle.HasValue || !deviation.HasValue)
        {
            return (null, null, null, null);
        }

        return (middle, middle + (deviation * multiplier), middle - (deviation * multiplier), deviation);
    }

    private static decimal? ComputeAtr(IReadOnlyList<decimal> highs, IReadOnlyList<decimal> lows, IReadOnlyList<decimal> closes, int period)
    {
        if (closes.Count <= period || highs.Count <= period || lows.Count <= period)
        {
            return null;
        }

        var trueRanges = new List<decimal>(closes.Count - 1);
        for (var index = 1; index < closes.Count; index++)
        {
            var highLow = highs[index] - lows[index];
            var highClose = Math.Abs(highs[index] - closes[index - 1]);
            var lowClose = Math.Abs(lows[index] - closes[index - 1]);
            trueRanges.Add(Math.Max(highLow, Math.Max(highClose, lowClose)));
        }

        if (trueRanges.Count < period)
        {
            return null;
        }

        var atr = trueRanges.Take(period).Average();
        for (var index = period; index < trueRanges.Count; index++)
        {
            atr = ((atr * (period - 1)) + trueRanges[index]) / period;
        }

        return atr;
    }

    private static (decimal? K, decimal? D, decimal? J) ComputeKdj(IReadOnlyList<decimal> highs, IReadOnlyList<decimal> lows, IReadOnlyList<decimal> closes, int period)
    {
        if (closes.Count < period || highs.Count < period || lows.Count < period)
        {
            return (null, null, null);
        }

        var k = 50m;
        var d = 50m;
        for (var index = period - 1; index < closes.Count; index++)
        {
            var windowHigh = highs.Skip(index - period + 1).Take(period).Max();
            var windowLow = lows.Skip(index - period + 1).Take(period).Min();
            var denominator = windowHigh - windowLow;
            var rsv = denominator <= 0m ? 50m : ((closes[index] - windowLow) / denominator) * 100m;
            k = ((2m * k) + rsv) / 3m;
            d = ((2m * d) + k) / 3m;
        }

        return (k, d, (3m * k) - (2m * d));
    }

    private static decimal? ComputeFisherTransform(IReadOnlyList<decimal> highs, IReadOnlyList<decimal> lows, IReadOnlyList<decimal> closes, int period, ref bool invalidNumericValueDetected)
    {
        if (closes.Count < period || highs.Count < period || lows.Count < period)
        {
            return null;
        }

        var value = 0d;
        var fisher = 0d;
        for (var index = period - 1; index < closes.Count; index++)
        {
            var windowHigh = highs.Skip(index - period + 1).Take(period).Max();
            var windowLow = lows.Skip(index - period + 1).Take(period).Min();
            var medianPrice = (double)((highs[index] + lows[index] + closes[index]) / 3m);
            var denominator = (double)(windowHigh - windowLow);
            var normalized = denominator <= 0d ? 0d : 2d * (((medianPrice - (double)windowLow) / denominator) - 0.5d);
            value = Math.Clamp((0.33d * normalized) + (0.67d * value), -0.999d, 0.999d);
            fisher = (0.5d * Math.Log((1d + value) / (1d - value))) + (0.5d * fisher);
        }

        return TryConvertDecimal(fisher, ref invalidNumericValueDetected);
    }

    private static decimal? ComputeRelativeVolume(IReadOnlyList<decimal> volumes, int period)
    {
        if (volumes.Count <= period)
        {
            return null;
        }

        var latest = volumes[^1];
        var baseline = volumes.Skip(volumes.Count - period - 1).Take(period).Average();
        return baseline <= 0m ? null : latest / baseline;
    }

    private static decimal? ComputeVolumeSpike(IReadOnlyList<decimal> volumes, int period)
    {
        if (volumes.Count <= period)
        {
            return null;
        }

        var latest = volumes[^1];
        var baselineWindow = volumes.Skip(volumes.Count - period - 1).Take(period).OrderBy(value => value).ToArray();
        var baseline = baselineWindow.Length == 0 ? 0m : baselineWindow[baselineWindow.Length / 2];
        return baseline <= 0m ? null : latest / baseline;
    }

    private static decimal? ComputeObv(IReadOnlyList<decimal> closes, IReadOnlyList<decimal> volumes)
    {
        if (closes.Count == 0 || closes.Count != volumes.Count)
        {
            return null;
        }

        var obv = 0m;
        for (var index = 1; index < closes.Count; index++)
        {
            if (closes[index] > closes[index - 1]) obv += volumes[index];
            else if (closes[index] < closes[index - 1]) obv -= volumes[index];
        }

        return obv;
    }

    private static decimal? ComputeMfi(
        IReadOnlyList<decimal> highs,
        IReadOnlyList<decimal> lows,
        IReadOnlyList<decimal> closes,
        IReadOnlyList<decimal> volumes,
        int period)
    {
        if (highs.Count != lows.Count || lows.Count != closes.Count || closes.Count != volumes.Count || closes.Count <= period)
        {
            return null;
        }

        var positiveFlow = 0m;
        var negativeFlow = 0m;
        var startIndex = closes.Count - period;

        for (var index = startIndex; index < closes.Count; index++)
        {
            var typicalPrice = (highs[index] + lows[index] + closes[index]) / 3m;
            var previousTypicalPrice = (highs[index - 1] + lows[index - 1] + closes[index - 1]) / 3m;
            var rawMoneyFlow = typicalPrice * volumes[index];

            if (typicalPrice > previousTypicalPrice)
            {
                positiveFlow += rawMoneyFlow;
            }
            else if (typicalPrice < previousTypicalPrice)
            {
                negativeFlow += rawMoneyFlow;
            }
        }

        if (negativeFlow == 0m)
        {
            return 100m;
        }

        var moneyFlowRatio = positiveFlow / negativeFlow;
        return 100m - (100m / (1m + moneyFlowRatio));
    }

    private static (decimal? KlingerOscillator, decimal? KlingerSignal) ComputeKlinger(
        IReadOnlyList<decimal> highs,
        IReadOnlyList<decimal> lows,
        IReadOnlyList<decimal> closes,
        IReadOnlyList<decimal> volumes,
        int fastPeriod,
        int slowPeriod,
        int signalPeriod,
        ref bool invalidNumericValueDetected)
    {
        if (highs.Count != lows.Count || lows.Count != closes.Count || closes.Count != volumes.Count || closes.Count < slowPeriod + signalPeriod + 1)
        {
            return (null, null);
        }

        var volumeForce = new List<decimal>(closes.Count - 1);
        var previousPriceSum = highs[0] + lows[0] + closes[0];
        var previousTrend = 0m;
        var previousCumulativeMeasurement = 0m;
        var previousDailyMeasurement = 0m;

        for (var index = 1; index < closes.Count; index++)
        {
            var currentPriceSum = highs[index] + lows[index] + closes[index];
            var trend = currentPriceSum > previousPriceSum
                ? 1m
                : currentPriceSum < previousPriceSum
                    ? -1m
                    : previousTrend == 0m ? 1m : previousTrend;
            var dailyMeasurement = highs[index] - lows[index];
            var cumulativeMeasurement = trend == previousTrend
                ? previousCumulativeMeasurement + dailyMeasurement
                : previousDailyMeasurement + dailyMeasurement;

            if (cumulativeMeasurement <= 0m)
            {
                volumeForce.Add(0m);
            }
            else
            {
                var force = volumes[index] * Math.Abs(2m * ((dailyMeasurement / cumulativeMeasurement) - 1m)) * trend * 100m;
                volumeForce.Add(force);
            }

            previousPriceSum = currentPriceSum;
            previousTrend = trend;
            previousDailyMeasurement = dailyMeasurement;
            previousCumulativeMeasurement = cumulativeMeasurement;
        }

        var fastSeries = ComputeEmaSeries(volumeForce, fastPeriod, ref invalidNumericValueDetected);
        var slowSeries = ComputeEmaSeries(volumeForce, slowPeriod, ref invalidNumericValueDetected);
        var oscillatorSeries = new List<decimal>();

        for (var index = 0; index < volumeForce.Count; index++)
        {
            if (!fastSeries[index].HasValue || !slowSeries[index].HasValue)
            {
                continue;
            }

            oscillatorSeries.Add(fastSeries[index]!.Value - slowSeries[index]!.Value);
        }

        var signal = ComputeEma(oscillatorSeries, signalPeriod, ref invalidNumericValueDetected);
        var oscillator = oscillatorSeries.Count > 0 ? oscillatorSeries[^1] : (decimal?)null;
        return (oscillator, signal);
    }

    private static decimal? ComputeBollingerPercentB(decimal? latestClose, decimal? upperBand, decimal? lowerBand)
    {
        if (!latestClose.HasValue || !upperBand.HasValue || !lowerBand.HasValue)
        {
            return null;
        }

        var denominator = upperBand.Value - lowerBand.Value;
        return denominator == 0m ? null : (latestClose.Value - lowerBand.Value) / denominator;
    }

    private static decimal? ComputeBollingerBandwidth(decimal? middleBand, decimal? upperBand, decimal? lowerBand)
    {
        if (!middleBand.HasValue || !upperBand.HasValue || !lowerBand.HasValue || middleBand.Value == 0m)
        {
            return null;
        }

        return (upperBand.Value - lowerBand.Value) / middleBand.Value;
    }

    private static decimal? ComputeKeltnerChannelRelation(decimal? latestClose, decimal? ema20, decimal? atr)
    {
        if (!latestClose.HasValue || !ema20.HasValue || !atr.HasValue || atr.Value <= 0m)
        {
            return null;
        }

        return (latestClose.Value - ema20.Value) / (2m * atr.Value);
    }

    private static decimal? ComputePmax(IReadOnlyList<decimal> highs, IReadOnlyList<decimal> lows, IReadOnlyList<decimal> closes, int maPeriod, int atrPeriod, decimal atrMultiplier, ref bool invalidNumericValueDetected)
    {
        if (closes.Count < Math.Max(maPeriod, atrPeriod) + 2)
        {
            return null;
        }

        var atrSeries = ComputeAtrSeries(highs, lows, closes, atrPeriod);
        var maSeries = ComputeEmaSeries(closes, maPeriod, ref invalidNumericValueDetected);
        decimal? previousLongStop = null;
        decimal? previousShortStop = null;
        var bullish = true;
        decimal? pmax = null;

        for (var index = 0; index < closes.Count; index++)
        {
            if (!maSeries[index].HasValue || !atrSeries[index].HasValue)
            {
                continue;
            }

            var ma = maSeries[index]!.Value;
            var atr = atrSeries[index]!.Value;
            var longStop = ma - (atrMultiplier * atr);
            var shortStop = ma + (atrMultiplier * atr);
            if (previousLongStop.HasValue && ma > previousLongStop.Value) longStop = Math.Max(longStop, previousLongStop.Value);
            if (previousShortStop.HasValue && ma < previousShortStop.Value) shortStop = Math.Min(shortStop, previousShortStop.Value);
            if (bullish) bullish = !(ma < (previousLongStop ?? longStop));
            else if (ma > (previousShortStop ?? shortStop)) bullish = true;
            previousLongStop = longStop;
            previousShortStop = shortStop;
            pmax = bullish ? longStop : shortStop;
        }

        return pmax;
    }

    private static decimal? ComputeChandelierExit(IReadOnlyList<decimal> highs, decimal? atr, int period, decimal atrMultiplier)
    {
        if (!atr.HasValue || highs.Count < period)
        {
            return null;
        }

        var highestHigh = highs.Skip(highs.Count - period).Max();
        return highestHigh - (atrMultiplier * atr.Value);
    }

    private static IReadOnlyList<decimal?> ComputeEmaSeries(IReadOnlyList<decimal> values, int period, ref bool invalidNumericValueDetected)
    {
        var result = Enumerable.Repeat<decimal?>(null, values.Count).ToArray();
        if (values.Count < period)
        {
            return result;
        }

        var multiplier = 2d / (period + 1d);
        var ema = (double)values.Take(period).Average();
        result[period - 1] = TryConvertDecimal(ema, ref invalidNumericValueDetected);
        for (var index = period; index < values.Count; index++)
        {
            ema = (((double)values[index] - ema) * multiplier) + ema;
            result[index] = TryConvertDecimal(ema, ref invalidNumericValueDetected);
        }

        return result;
    }

    private static IReadOnlyList<decimal?> ComputeAtrSeries(IReadOnlyList<decimal> highs, IReadOnlyList<decimal> lows, IReadOnlyList<decimal> closes, int period)
    {
        var result = Enumerable.Repeat<decimal?>(null, closes.Count).ToArray();
        if (closes.Count <= period || highs.Count <= period || lows.Count <= period)
        {
            return result;
        }

        var trueRanges = new List<decimal>(closes.Count - 1);
        for (var index = 1; index < closes.Count; index++)
        {
            var highLow = highs[index] - lows[index];
            var highClose = Math.Abs(highs[index] - closes[index - 1]);
            var lowClose = Math.Abs(lows[index] - closes[index - 1]);
            trueRanges.Add(Math.Max(highLow, Math.Max(highClose, lowClose)));
        }

        var atr = trueRanges.Take(period).Average();
        result[period] = atr;
        for (var index = period; index < trueRanges.Count; index++)
        {
            atr = ((atr * (period - 1)) + trueRanges[index]) / period;
            result[index + 1] = atr;
        }

        return result;
    }

    private static decimal? TryConvertDecimal(double value, ref bool invalidNumericValueDetected)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            invalidNumericValueDetected = true;
            return null;
        }

        try
        {
            return decimal.Round((decimal)value, 8, MidpointRounding.AwayFromZero);
        }
        catch (OverflowException)
        {
            invalidNumericValueDetected = true;
            return null;
        }
    }

    private static TradingFeatureSnapshotModel MapSnapshot(TradingFeatureSnapshot entity)
    {
        return new TradingFeatureSnapshotModel(
            entity.Id,
            entity.OwnerUserId,
            entity.BotId,
            entity.ExchangeAccountId,
            entity.StrategyKey,
            entity.Symbol,
            entity.Timeframe,
            entity.EvaluatedAtUtc,
            entity.MarketDataTimestampUtc,
            entity.FeatureVersion,
            entity.SnapshotState,
            entity.MarketDataReasonCode,
            entity.SampleCount,
            entity.RequiredSampleCount,
            entity.ReferencePrice,
            new TradingTrendFeatureSnapshot(entity.Ema20, entity.Ema50, entity.Ema200, entity.Alma, entity.Frama),
            new TradingMomentumFeatureSnapshot(entity.Rsi, entity.MacdLine, entity.MacdSignal, entity.MacdHistogram, entity.KdjK, entity.KdjD, entity.KdjJ, entity.FisherTransform),
            new TradingVolatilityFeatureSnapshot(entity.Atr, entity.BollingerPercentB, entity.BollingerBandWidth, entity.KeltnerChannelRelation, entity.PmaxValue, entity.ChandelierExit),
            new TradingVolumeFeatureSnapshot(entity.VolumeSpikeRatio, entity.RelativeVolume, entity.Obv, entity.Mfi, entity.KlingerOscillator, entity.KlingerSignal),
            new TradingContextFeatureSnapshot(entity.Plane, entity.TradingMode, entity.HasOpenPosition, entity.IsInCooldown, entity.LastVetoReasonCode, entity.LastDecisionOutcome, entity.LastDecisionCode, entity.LastExecutionState, entity.LastFailureCode),
            entity.FeatureSummary,
            entity.TopSignalHints,
            entity.PrimaryRegime,
            entity.MomentumBias,
            entity.VolatilityState,
            entity.NormalizationMeta,
            entity.QualityReasonCode,
            entity.MissingFeatureSummary,
            entity.FeatureAnchorTimeUtc,
            entity.CorrelationId,
            entity.SnapshotKey);
    }

    private static string NormalizeSymbol(string symbol) => MarketDataSymbolNormalizer.Normalize(symbol);

    private static string NormalizeRequired(string? value, string parameterName)
    {
        var normalizedValue = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedValue)) throw new ArgumentException("The value is required.", parameterName);
        return normalizedValue;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalizedValue = value?.Trim();
        return string.IsNullOrWhiteSpace(normalizedValue) ? null : normalizedValue;
    }

    private static DateTime ResolveFeatureAnchorTimeUtc(
        TradingFeatureCaptureRequest request,
        IReadOnlyList<FeatureCandle> candles,
        string timeframe,
        DateTime evaluatedAtUtc)
    {
        if (request.FeatureAnchorTimeUtc.HasValue)
        {
            return NormalizeTimestamp(request.FeatureAnchorTimeUtc.Value);
        }

        if (request.IndicatorSnapshot is not null)
        {
            return NormalizeTimestamp(request.IndicatorSnapshot.CloseTimeUtc);
        }

        if (candles.Count > 0)
        {
            return NormalizeTimestamp(candles[^1].CloseTimeUtc);
        }

        return AlignToIntervalBoundary(evaluatedAtUtc, timeframe);
    }

    private static string BuildSnapshotKey(
        string userId,
        Guid botId,
        string strategyKey,
        string symbol,
        string timeframe,
        ExchangeDataPlane plane,
        ExecutionEnvironment tradingMode,
        DateTime featureAnchorTimeUtc)
    {
        var rawKey = FormattableString.Invariant(
            $"{FeatureVersionValue}|{userId}|{botId:N}|{strategyKey}|{symbol}|{timeframe}|{plane}|{tradingMode}|{featureAnchorTimeUtc:O}");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexString(hash);
    }

    private static DateTime NormalizeTimestamp(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private static DateTime AlignToIntervalBoundary(DateTime utcNow, string timeframe)
    {
        var normalizedUtcNow = utcNow.Kind == DateTimeKind.Utc ? utcNow : utcNow.ToUniversalTime();
        var interval = ResolveIntervalDuration(timeframe);
        var ticks = normalizedUtcNow.Ticks - (normalizedUtcNow.Ticks % interval.Ticks);
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    private static TimeSpan ResolveIntervalDuration(string timeframe)
    {
        var normalizedTimeframe = NormalizeRequired(timeframe, nameof(timeframe));
        var magnitudeText = normalizedTimeframe[..^1];
        var unit = normalizedTimeframe[^1];
        if (!int.TryParse(magnitudeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var magnitude) || magnitude <= 0)
        {
            throw new InvalidOperationException($"Unsupported timeframe '{timeframe}'.");
        }

        return unit switch
        {
            'm' => TimeSpan.FromMinutes(magnitude),
            'h' => TimeSpan.FromHours(magnitude),
            'd' => TimeSpan.FromDays(magnitude),
            _ => throw new InvalidOperationException($"Unsupported timeframe '{timeframe}'.")
        };
    }

    private static string FormatDecimal(decimal? value) => value?.ToString("0.########", CultureInfo.InvariantCulture) ?? "n/a";

    private sealed record FeatureCandle(
        string Symbol,
        string Timeframe,
        DateTime OpenTimeUtc,
        DateTime CloseTimeUtc,
        decimal OpenPrice,
        decimal HighPrice,
        decimal LowPrice,
        decimal ClosePrice,
        decimal Volume,
        DateTime ReceivedAtUtc,
        string Source);

    private sealed record DerivedFeatureSnapshot(
        DateTime? MarketDataTimestampUtc,
        decimal? ReferencePrice,
        FeatureSnapshotState SnapshotState,
        FeatureSnapshotQualityReason QualityReasonCode,
        string? MissingFeatureSummary,
        DegradedModeReasonCode MarketDataReasonCode,
        decimal? Ema20,
        decimal? Ema50,
        decimal? Ema200,
        decimal? Alma,
        decimal? Frama,
        decimal? Rsi,
        decimal? MacdLine,
        decimal? MacdSignal,
        decimal? MacdHistogram,
        decimal? KdjK,
        decimal? KdjD,
        decimal? KdjJ,
        decimal? FisherTransform,
        decimal? Atr,
        decimal? BollingerPercentB,
        decimal? BollingerBandWidth,
        decimal? KeltnerChannelRelation,
        decimal? PmaxValue,
        decimal? ChandelierExit,
        decimal? VolumeSpikeRatio,
        decimal? RelativeVolume,
        decimal? Obv,
        decimal? Mfi,
        decimal? KlingerOscillator,
        decimal? KlingerSignal,
        string PrimaryRegime,
        string MomentumBias,
        string VolatilityState,
        string FeatureSummary,
        string TopSignalHints,
        string? NormalizationMeta,
        string? LastDecisionOutcome,
        string? LastDecisionCode,
        string? LastExecutionState,
        string? LastFailureCode,
        string? LastVetoReasonCode,
        ExchangeDataPlane Plane,
        ExecutionEnvironment TradingMode,
        bool HasOpenPosition,
        bool IsInCooldown);

    private sealed record FeatureSnapshotQualityEvaluation(
        FeatureSnapshotQualityReason QualityReasonCode,
        string? MissingFeatureSummary);
}
