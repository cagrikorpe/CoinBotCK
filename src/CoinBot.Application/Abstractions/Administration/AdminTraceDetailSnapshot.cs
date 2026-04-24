namespace CoinBot.Application.Abstractions.Administration;

public sealed record AdminTraceDetailSnapshot(
    string CorrelationId,
    IReadOnlyCollection<DecisionTraceSnapshot> DecisionTraces,
    IReadOnlyCollection<ExecutionTraceSnapshot> ExecutionTraces,
    IReadOnlyCollection<AdminTraceHandoffAttemptSnapshot>? HandoffAttempts = null,
    IReadOnlyCollection<AdminTraceExecutionTransitionSnapshot>? ExecutionTransitions = null);

public sealed record AdminTraceHandoffAttemptSnapshot(
    Guid HandoffAttemptId,
    Guid ScanCycleId,
    Guid? StrategySignalId,
    string? OwnerUserId,
    Guid? BotId,
    string? Symbol,
    string? Timeframe,
    string StrategyDecisionOutcome,
    string ExecutionRequestStatus,
    string? BlockerCode,
    string? BlockerSummary,
    string? GuardSummary,
    string? ExecutionEnvironment,
    string? ExecutionSide,
    DateTime CompletedAtUtc);

public sealed record AdminTraceExecutionTransitionSnapshot(
    Guid ExecutionOrderTransitionId,
    Guid ExecutionOrderId,
    int SequenceNumber,
    string State,
    string EventCode,
    string? Detail,
    string CorrelationId,
    string? ParentCorrelationId,
    DateTime OccurredAtUtc);
