namespace CoinBot.Domain.Entities;

public sealed class DecisionTrace : BaseEntity
{
    public Guid? StrategySignalId { get; set; }

    public string CorrelationId { get; set; } = string.Empty;

    public string DecisionId { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public string Symbol { get; set; } = string.Empty;

    public string Timeframe { get; set; } = string.Empty;

    public string StrategyVersion { get; set; } = string.Empty;

    public string SignalType { get; set; } = string.Empty;

    public int? RiskScore { get; set; }

    public string DecisionOutcome { get; set; } = string.Empty;

    public string? DecisionReasonType { get; set; }

    public string? DecisionReasonCode { get; set; }

    public string? DecisionSummary { get; set; }

    public DateTime? DecisionAtUtc { get; set; }

    public string? VetoReasonCode { get; set; }

    public int LatencyMs { get; set; }

    public DateTime? LastCandleAtUtc { get; set; }

    public int? DataAgeMs { get; set; }

    public int? StaleThresholdMs { get; set; }

    public string? StaleReason { get; set; }

    public string? ContinuityState { get; set; }

    public int? ContinuityGapCount { get; set; }

    public DateTime? ContinuityGapStartedAtUtc { get; set; }

    public DateTime? ContinuityGapLastSeenAtUtc { get; set; }

    public DateTime? ContinuityRecoveredAtUtc { get; set; }

    public string SnapshotJson { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
}
