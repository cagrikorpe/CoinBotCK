using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class ApprovalQueue : BaseEntity
{
    public string ApprovalReference { get; set; } = string.Empty;

    public ApprovalQueueOperationType OperationType { get; set; } = ApprovalQueueOperationType.GlobalSystemStateUpdate;

    public ApprovalQueueStatus Status { get; set; } = ApprovalQueueStatus.Pending;

    public IncidentSeverity Severity { get; set; } = IncidentSeverity.Warning;

    public string Title { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string? TargetType { get; set; }

    public string? TargetId { get; set; }

    public string RequestedByUserId { get; set; } = string.Empty;

    public int RequiredApprovals { get; set; } = 1;

    public int ApprovalCount { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = string.Empty;

    public string PayloadHash { get; set; } = string.Empty;

    public string? CorrelationId { get; set; }

    public string? CommandId { get; set; }

    public string? DecisionId { get; set; }

    public string? ExecutionAttemptId { get; set; }

    public Guid? IncidentId { get; set; }

    public string? IncidentReference { get; set; }

    public Guid? SystemStateHistoryId { get; set; }

    public string? SystemStateHistoryReference { get; set; }

    public Guid? DependencyCircuitBreakerStateId { get; set; }

    public string? DependencyCircuitBreakerStateReference { get; set; }

    public DateTime? ApprovedAtUtc { get; set; }

    public DateTime? ExecutedAtUtc { get; set; }

    public DateTime? RejectedAtUtc { get; set; }

    public DateTime? ExpiredAtUtc { get; set; }

    public string? RejectReason { get; set; }

    public string? ExecutionSummary { get; set; }

    public string? LastActorUserId { get; set; }
}
