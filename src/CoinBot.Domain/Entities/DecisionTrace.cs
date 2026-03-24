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

    public string? VetoReasonCode { get; set; }

    public int LatencyMs { get; set; }

    public string SnapshotJson { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
}
