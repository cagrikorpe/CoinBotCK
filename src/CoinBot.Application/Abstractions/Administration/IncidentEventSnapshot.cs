using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Administration;

public sealed record IncidentEventSnapshot(
    IncidentEventType EventType,
    string Message,
    string? ActorUserId,
    string? CorrelationId,
    string? CommandId,
    string? DecisionId,
    string? ExecutionAttemptId,
    string? ApprovalReference,
    string? SystemStateHistoryReference,
    string? DependencyCircuitBreakerStateReference,
    string? PayloadJson,
    DateTime CreatedAtUtc);
