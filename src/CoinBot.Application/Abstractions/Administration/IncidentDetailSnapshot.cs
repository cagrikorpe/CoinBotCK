using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Administration;

public sealed record IncidentDetailSnapshot(
    string IncidentReference,
    IncidentSeverity Severity,
    IncidentStatus Status,
    string Title,
    string Summary,
    string Detail,
    ApprovalQueueOperationType? OperationType,
    string? TargetType,
    string? TargetId,
    string? CorrelationId,
    string? CommandId,
    string? DecisionId,
    string? ExecutionAttemptId,
    string? ApprovalReference,
    string? SystemStateHistoryReference,
    string? DependencyCircuitBreakerStateReference,
    string? CreatedByUserId,
    DateTime CreatedAtUtc,
    DateTime? ResolvedAtUtc,
    string? ResolvedByUserId,
    string? ResolvedSummary,
    IReadOnlyCollection<IncidentEventSnapshot> Timeline);
