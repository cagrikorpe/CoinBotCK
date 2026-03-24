namespace CoinBot.Application.Abstractions.Administration;

public sealed record AdminTraceListItem(
    string CorrelationId,
    string? UserId,
    string? Symbol,
    string? Timeframe,
    string? StrategyVersion,
    string? DecisionOutcome,
    string? VetoReasonCode,
    string? LatestExecutionProvider,
    int DecisionCount,
    int ExecutionCount,
    DateTime LastUpdatedAtUtc);
