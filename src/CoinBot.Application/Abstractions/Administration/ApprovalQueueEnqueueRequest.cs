using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Administration;

public sealed record ApprovalQueueEnqueueRequest(
    ApprovalQueueOperationType OperationType,
    IncidentSeverity Severity,
    string Title,
    string Summary,
    string RequestedByUserId,
    string Reason,
    string PayloadJson,
    int RequiredApprovals,
    DateTime ExpiresAtUtc,
    string? TargetType = null,
    string? TargetId = null,
    string? CorrelationId = null,
    string? CommandId = null,
    string? DecisionId = null,
    string? ExecutionAttemptId = null,
    string? ApprovalReference = null,
    string? IncidentReference = null,
    string? SystemStateHistoryReference = null,
    string? DependencyCircuitBreakerStateReference = null);
