using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class IncidentEvent : BaseEntity
{
    public Guid IncidentId { get; set; }

    public string IncidentReference { get; set; } = string.Empty;

    public IncidentEventType EventType { get; set; } = IncidentEventType.IncidentCreated;

    public string Message { get; set; } = string.Empty;

    public string? ActorUserId { get; set; }

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

    public string? PayloadJson { get; set; }
}
