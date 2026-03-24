using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class SystemStateHistory : BaseEntity
{
    public Guid GlobalSystemStateId { get; set; }

    public string HistoryReference { get; set; } = string.Empty;

    public long Version { get; set; }

    public GlobalSystemStateKind State { get; set; } = GlobalSystemStateKind.Active;

    public string ReasonCode { get; set; } = string.Empty;

    public string? Message { get; set; }

    public string Source { get; set; } = string.Empty;

    public bool IsManualOverride { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }

    public string? CorrelationId { get; set; }

    public string? CommandId { get; set; }

    public Guid? ApprovalQueueId { get; set; }

    public string? ApprovalReference { get; set; }

    public Guid? IncidentId { get; set; }

    public string? IncidentReference { get; set; }

    public Guid? DependencyCircuitBreakerStateId { get; set; }

    public string? DependencyCircuitBreakerStateReference { get; set; }

    public string? BreakerKind { get; set; }

    public string? BreakerStateCode { get; set; }

    public string? UpdatedByUserId { get; set; }

    public string? UpdatedFromIp { get; set; }

    public string? PreviousState { get; set; }

    public string? ChangeSummary { get; set; }
}
