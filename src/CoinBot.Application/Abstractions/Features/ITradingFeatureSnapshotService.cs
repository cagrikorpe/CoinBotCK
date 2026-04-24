using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Features;

public interface ITradingFeatureSnapshotService
{
    Task<TradingFeatureSnapshotModel> CaptureAsync(
        TradingFeatureCaptureRequest request,
        CancellationToken cancellationToken = default);

    Task<TradingFeatureSnapshotModel?> GetLatestAsync(
        string userId,
        Guid botId,
        string symbol,
        string timeframe,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<TradingFeatureSnapshotModel>> ListRecentAsync(
        string userId,
        Guid botId,
        string symbol,
        string timeframe,
        int take = 20,
        CancellationToken cancellationToken = default);
}

public sealed record TradingFeatureCaptureRequest(
    string UserId,
    Guid BotId,
    string StrategyKey,
    string Symbol,
    string Timeframe,
    DateTime EvaluatedAtUtc,
    Guid? ExchangeAccountId = null,
    ExchangeDataPlane Plane = ExchangeDataPlane.Futures,
    StrategyIndicatorSnapshot? IndicatorSnapshot = null,
    decimal? ReferencePrice = null,
    IReadOnlyCollection<MarketCandleSnapshot>? HistoricalCandles = null,
    string? CorrelationId = null,
    DateTime? FeatureAnchorTimeUtc = null);

public sealed record TradingFeatureSnapshotModel(
    Guid Id,
    string UserId,
    Guid BotId,
    Guid? ExchangeAccountId,
    string StrategyKey,
    string Symbol,
    string Timeframe,
    DateTime EvaluatedAtUtc,
    DateTime? MarketDataTimestampUtc,
    string FeatureVersion,
    FeatureSnapshotState SnapshotState,
    DegradedModeReasonCode MarketDataReasonCode,
    int SampleCount,
    int RequiredSampleCount,
    decimal? ReferencePrice,
    TradingTrendFeatureSnapshot Trend,
    TradingMomentumFeatureSnapshot Momentum,
    TradingVolatilityFeatureSnapshot Volatility,
    TradingVolumeFeatureSnapshot Volume,
    TradingContextFeatureSnapshot TradingContext,
    string FeatureSummary,
    string TopSignalHints,
    string PrimaryRegime,
    string MomentumBias,
    string VolatilityState,
    string? NormalizationMeta,
    FeatureSnapshotQualityReason QualityReasonCode = FeatureSnapshotQualityReason.None,
    string? MissingFeatureSummary = null,
    DateTime? FeatureAnchorTimeUtc = null,
    string? CorrelationId = null,
    string? SnapshotKey = null);

public sealed record TradingTrendFeatureSnapshot(
    decimal? Ema20,
    decimal? Ema50,
    decimal? Ema200,
    decimal? Alma,
    decimal? Frama);

public sealed record TradingMomentumFeatureSnapshot(
    decimal? Rsi,
    decimal? MacdLine,
    decimal? MacdSignal,
    decimal? MacdHistogram,
    decimal? KdjK,
    decimal? KdjD,
    decimal? KdjJ,
    decimal? FisherTransform);

public sealed record TradingVolatilityFeatureSnapshot(
    decimal? Atr,
    decimal? BollingerPercentB,
    decimal? BollingerBandWidth,
    decimal? KeltnerChannelRelation,
    decimal? PmaxValue,
    decimal? ChandelierExit);

public sealed record TradingVolumeFeatureSnapshot(
    decimal? VolumeSpikeRatio,
    decimal? RelativeVolume,
    decimal? Obv,
    decimal? Mfi = null,
    decimal? KlingerOscillator = null,
    decimal? KlingerSignal = null);

public sealed record TradingContextFeatureSnapshot(
    ExchangeDataPlane Plane,
    ExecutionEnvironment TradingMode,
    bool HasOpenPosition,
    bool IsInCooldown,
    string? LastVetoReasonCode,
    string? LastDecisionOutcome,
    string? LastDecisionCode,
    string? LastExecutionState,
    string? LastFailureCode);

