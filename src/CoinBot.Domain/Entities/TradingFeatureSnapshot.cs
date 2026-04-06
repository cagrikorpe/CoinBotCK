using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class TradingFeatureSnapshot : UserOwnedEntity
{
    public Guid BotId { get; set; }
    public Guid? ExchangeAccountId { get; set; }
    public string StrategyKey { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
    public DateTime EvaluatedAtUtc { get; set; }
    public DateTime? MarketDataTimestampUtc { get; set; }
    public string FeatureVersion { get; set; } = string.Empty;
    public FeatureSnapshotState SnapshotState { get; set; } = FeatureSnapshotState.MissingData;
    public DegradedModeReasonCode MarketDataReasonCode { get; set; } = DegradedModeReasonCode.MarketDataUnavailable;
    public int SampleCount { get; set; }
    public int RequiredSampleCount { get; set; }
    public decimal? ReferencePrice { get; set; }
    public decimal? Ema20 { get; set; }
    public decimal? Ema50 { get; set; }
    public decimal? Ema200 { get; set; }
    public decimal? Alma { get; set; }
    public decimal? Frama { get; set; }
    public decimal? Rsi { get; set; }
    public decimal? MacdLine { get; set; }
    public decimal? MacdSignal { get; set; }
    public decimal? MacdHistogram { get; set; }
    public decimal? KdjK { get; set; }
    public decimal? KdjD { get; set; }
    public decimal? KdjJ { get; set; }
    public decimal? FisherTransform { get; set; }
    public decimal? Atr { get; set; }
    public decimal? BollingerPercentB { get; set; }
    public decimal? BollingerBandWidth { get; set; }
    public decimal? KeltnerChannelRelation { get; set; }
    public decimal? PmaxValue { get; set; }
    public decimal? ChandelierExit { get; set; }
    public decimal? VolumeSpikeRatio { get; set; }
    public decimal? RelativeVolume { get; set; }
    public decimal? Obv { get; set; }
    public ExchangeDataPlane Plane { get; set; } = ExchangeDataPlane.Futures;
    public ExecutionEnvironment TradingMode { get; set; } = ExecutionEnvironment.Demo;
    public bool HasOpenPosition { get; set; }
    public bool IsInCooldown { get; set; }
    public string? LastVetoReasonCode { get; set; }
    public string? LastDecisionOutcome { get; set; }
    public string? LastDecisionCode { get; set; }
    public string? LastExecutionState { get; set; }
    public string? LastFailureCode { get; set; }
    public string FeatureSummary { get; set; } = string.Empty;
    public string TopSignalHints { get; set; } = string.Empty;
    public string PrimaryRegime { get; set; } = string.Empty;
    public string MomentumBias { get; set; } = string.Empty;
    public string VolatilityState { get; set; } = string.Empty;
    public string? NormalizationMeta { get; set; }
}

