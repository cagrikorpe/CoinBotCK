using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Administration;

public sealed record ApprovalActionSnapshot(
    int Sequence,
    ApprovalActionType ActionType,
    string ActorUserId,
    string? Reason,
    string? CorrelationId,
    string? CommandId,
    string? DecisionId,
    string? ExecutionAttemptId,
    DateTime CreatedAtUtc);
