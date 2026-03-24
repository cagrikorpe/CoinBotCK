using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class ApprovalAction : BaseEntity
{
    public Guid ApprovalQueueId { get; set; }

    public string ApprovalReference { get; set; } = string.Empty;

    public ApprovalActionType ActionType { get; set; } = ApprovalActionType.Approved;

    public int Sequence { get; set; }

    public string ActorUserId { get; set; } = string.Empty;

    public string? Reason { get; set; }

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
}
