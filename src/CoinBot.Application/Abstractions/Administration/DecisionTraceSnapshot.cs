namespace CoinBot.Application.Abstractions.Administration;

public sealed record DecisionTraceSnapshot(
    Guid Id,
    Guid? StrategySignalId,
    string CorrelationId,
    string DecisionId,
    string UserId,
    string Symbol,
    string Timeframe,
    string StrategyVersion,
    string SignalType,
    int? RiskScore,
    string DecisionOutcome,
    string? VetoReasonCode,
    int LatencyMs,
    string SnapshotJson,
    DateTime CreatedAtUtc);
