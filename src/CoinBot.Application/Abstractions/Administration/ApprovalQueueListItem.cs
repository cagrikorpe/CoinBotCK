using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Administration;

public sealed record ApprovalQueueListItem(
    string ApprovalReference,
    ApprovalQueueOperationType OperationType,
    ApprovalQueueStatus Status,
    IncidentSeverity Severity,
    string Title,
    string Summary,
    string? TargetType,
    string? TargetId,
    string RequestedByUserId,
    int RequiredApprovals,
    int ApprovalCount,
    DateTime ExpiresAtUtc,
    string? CorrelationId,
    string? CommandId,
    string? IncidentReference,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);
