using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Execution;

public sealed record ExecutionOrderTransitionSnapshot(
    Guid ExecutionOrderTransitionId,
    int SequenceNumber,
    ExecutionOrderState State,
    string EventCode,
    string? Detail,
    string CorrelationId,
    string? ParentCorrelationId,
    DateTime OccurredAtUtc);
