namespace CoinBot.Application.Abstractions.Administration;

public sealed record AdminTraceDetailSnapshot(
    string CorrelationId,
    IReadOnlyCollection<DecisionTraceSnapshot> DecisionTraces,
    IReadOnlyCollection<ExecutionTraceSnapshot> ExecutionTraces);
