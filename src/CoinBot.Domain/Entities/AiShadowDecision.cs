using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class AiShadowDecision : UserOwnedEntity
{
    public Guid BotId { get; set; }
    public Guid? ExchangeAccountId { get; set; }
    public Guid? TradingStrategyId { get; set; }
    public Guid? TradingStrategyVersionId { get; set; }
    public Guid? StrategySignalId { get; set; }
    public Guid? StrategySignalVetoId { get; set; }
    public Guid? FeatureSnapshotId { get; set; }
    public Guid? StrategyDecisionTraceId { get; set; }
    public Guid? HypotheticalDecisionTraceId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string StrategyKey { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
    public DateTime EvaluatedAtUtc { get; set; }
    public DateTime? MarketDataTimestampUtc { get; set; }
    public string? FeatureVersion { get; set; }
    public string StrategyDirection { get; set; } = "Neutral";
    public int? StrategyConfidenceScore { get; set; }
    public string? StrategyDecisionOutcome { get; set; }
    public string? StrategyDecisionCode { get; set; }
    public string? StrategySummary { get; set; }
    public string AiDirection { get; set; } = "Neutral";
    public decimal AiConfidence { get; set; }
    public string AiReasonSummary { get; set; } = string.Empty;
    public string AiProviderName { get; set; } = string.Empty;
    public string? AiProviderModel { get; set; }
    public int AiLatencyMs { get; set; }
    public bool AiIsFallback { get; set; }
    public string? AiFallbackReason { get; set; }
    public decimal AiAdvisoryScore { get; set; }
    public string? AiContributionSummary { get; set; }
    public bool RiskVetoPresent { get; set; }
    public string? RiskVetoReason { get; set; }
    public string? RiskVetoSummary { get; set; }
    public bool PilotSafetyBlocked { get; set; }
    public string? PilotSafetyReason { get; set; }
    public string? PilotSafetySummary { get; set; }
    public ExecutionEnvironment TradingMode { get; set; } = ExecutionEnvironment.Demo;
    public ExchangeDataPlane Plane { get; set; } = ExchangeDataPlane.Futures;
    public string FinalAction { get; set; } = "NoSubmit";
    public bool HypotheticalSubmitAllowed { get; set; }
    public string? HypotheticalBlockReason { get; set; }
    public string? HypotheticalBlockSummary { get; set; }
    public string NoSubmitReason { get; set; } = string.Empty;
    public string? FeatureSummary { get; set; }
    public string AgreementState { get; set; } = "NotApplicable";
}
