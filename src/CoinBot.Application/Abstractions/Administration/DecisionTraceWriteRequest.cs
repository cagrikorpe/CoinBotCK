namespace CoinBot.Application.Abstractions.Administration;

public sealed record DecisionTraceWriteRequest(
    string UserId,
    string Symbol,
    string Timeframe,
    string StrategyVersion,
    string SignalType,
    string DecisionOutcome,
    string SnapshotJson,
    int LatencyMs,
    string? CorrelationId = null,
    string? DecisionId = null,
    int? RiskScore = null,
    string? VetoReasonCode = null,
    Guid? StrategySignalId = null,
    DateTime? CreatedAtUtc = null);
