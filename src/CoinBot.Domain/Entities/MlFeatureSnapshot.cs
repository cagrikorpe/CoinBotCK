using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class MlFeatureSnapshot : BaseEntity
{
    public string Symbol { get; set; } = string.Empty;

    public string Timeframe { get; set; } = string.Empty;

    public string StrategyKey { get; set; } = string.Empty;

    public string? StrategyTemplateKey { get; set; }

    public int? StrategySchemaVersion { get; set; }

    public string SignalDirection { get; set; } = "Unavailable";

    public decimal? ScannerScore { get; set; }

    public decimal? MarketScore { get; set; }

    public int? StrategyScore { get; set; }

    public decimal? RiskPenalty { get; set; }

    public string TrendState { get; set; } = "Unavailable";

    public string VolatilityState { get; set; } = "Unavailable";

    public string LiquidityState { get; set; } = "Unavailable";

    public string MarketFreshnessState { get; set; } = "Unavailable";

    public string? MarketFreshnessReason { get; set; }

    public string? MarketFreshnessSource { get; set; }

    public string HistoricalFallbackState { get; set; } = "None";

    public string PrivatePlaneFreshnessState { get; set; } = "Unavailable";

    public string? PrivatePlaneFreshnessReason { get; set; }

    public string GuardDecision { get; set; } = "NotEvaluated";

    public string? GuardReasonCode { get; set; }

    public string ExecutionDecision { get; set; } = "NotRequested";

    public StrategySignalType? OrderSignalType { get; set; }

    public ExecutionOrderState? OrderState { get; set; }

    public bool? SubmittedToBroker { get; set; }

    public bool? ReduceOnly { get; set; }

    public decimal? PositionUnrealizedPnl { get; set; }

    public decimal? PositionRealizedPnl { get; set; }

    public decimal SignalConfidenceScore { get; set; }

    public decimal TrendAlignmentScore { get; set; }

    public decimal VolatilityScore { get; set; }

    public decimal LiquidityScore { get; set; }

    public decimal RecentPerformanceScore { get; set; }

    public decimal DrawdownPenalty { get; set; }

    public decimal SampleQualityScore { get; set; }

    public decimal FeatureCompletenessScore { get; set; }

    public decimal CombinedBaselineScore { get; set; }

    public string BaselineScoreSummary { get; set; } = string.Empty;

    public decimal? MlShadowScore { get; set; }

    public decimal MlConfidence { get; set; }

    public string MlShadowDecision { get; set; } = "NoDecision";

    public string ModelVersion { get; set; } = string.Empty;

    public string FeatureSchemaVersion { get; set; } = "Unavailable";

    public string ReasonSummary { get; set; } = string.Empty;

    public bool IsDecisionInfluential { get; set; }

    public string FeatureCompletenessState { get; set; } = "Unavailable";

    public string FeatureCompletenessSummary { get; set; } = "Unavailable";

    public string SchemaVersion { get; set; } = string.Empty;

    public DateTime CapturedAtUtc { get; set; }

    public DateTime? FeatureAnchorTimeUtc { get; set; }

    public DateTime? MarketDataTimestampUtc { get; set; }

    public ExecutionEnvironment? ExecutionEnvironment { get; set; }
}
