namespace CoinBot.Domain.Entities;

public sealed class AdminAuditLog
{
    public Guid Id { get; set; }

    public string ActorUserId { get; set; } = string.Empty;

    public string ActionType { get; set; } = string.Empty;

    public string TargetType { get; set; } = string.Empty;

    public string? TargetId { get; set; }

    public string? OldValueSummary { get; set; }

    public string? NewValueSummary { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    public string? CorrelationId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
