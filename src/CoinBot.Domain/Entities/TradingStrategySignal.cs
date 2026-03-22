using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class TradingStrategySignal : UserOwnedEntity
{
    public Guid TradingStrategyId { get; set; }

    public Guid TradingStrategyVersionId { get; set; }

    public int StrategyVersionNumber { get; set; }

    public int StrategySchemaVersion { get; set; }

    public StrategySignalType SignalType { get; set; }

    public ExecutionEnvironment ExecutionEnvironment { get; set; }

    public string Symbol { get; set; } = string.Empty;

    public string Timeframe { get; set; } = string.Empty;

    public DateTime IndicatorOpenTimeUtc { get; set; }

    public DateTime IndicatorCloseTimeUtc { get; set; }

    public DateTime IndicatorReceivedAtUtc { get; set; }

    public DateTime GeneratedAtUtc { get; set; }

    public int ExplainabilitySchemaVersion { get; set; } = 1;

    public string IndicatorSnapshotJson { get; set; } = string.Empty;

    public string RuleResultSnapshotJson { get; set; } = string.Empty;

    public string? RiskEvaluationJson { get; set; }
}
