using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Administration;

public sealed record IncidentListItem(
    string IncidentReference,
    IncidentSeverity Severity,
    IncidentStatus Status,
    string Title,
    string Summary,
    ApprovalQueueOperationType? OperationType,
    string? TargetType,
    string? TargetId,
    string? CorrelationId,
    string? CommandId,
    string? ApprovalReference,
    int EventCount,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);
