using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class ExecutionOrderTransition : UserOwnedEntity
{
    public Guid ExecutionOrderId { get; set; }

    public int SequenceNumber { get; set; }

    public ExecutionOrderState State { get; set; }

    public string EventCode { get; set; } = string.Empty;

    public string? Detail { get; set; }

    public string CorrelationId { get; set; } = string.Empty;

    public string? ParentCorrelationId { get; set; }

    public DateTime OccurredAtUtc { get; set; }
}
