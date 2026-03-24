using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class Incident : BaseEntity
{
    public string IncidentReference { get; set; } = string.Empty;

    public IncidentSeverity Severity { get; set; } = IncidentSeverity.Warning;

    public IncidentStatus Status { get; set; } = IncidentStatus.Open;

    public ApprovalQueueOperationType? OperationType { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    public string? TargetType { get; set; }

    public string? TargetId { get; set; }

    public string? CorrelationId { get; set; }

    public string? CommandId { get; set; }

    public string? DecisionId { get; set; }

    public string? ExecutionAttemptId { get; set; }

    public Guid? ApprovalQueueId { get; set; }

    public string? ApprovalReference { get; set; }

    public Guid? SystemStateHistoryId { get; set; }

    public string? SystemStateHistoryReference { get; set; }

    public Guid? DependencyCircuitBreakerStateId { get; set; }

    public string? DependencyCircuitBreakerStateReference { get; set; }

    public string? CreatedByUserId { get; set; }

    public DateTime? ResolvedAtUtc { get; set; }

    public string? ResolvedByUserId { get; set; }

    public string? ResolvedSummary { get; set; }
}
