using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class AdminCommandRegistryEntry
{
    public Guid Id { get; set; }

    public string CommandId { get; set; } = string.Empty;

    public string CommandType { get; set; } = string.Empty;

    public string ActorUserId { get; set; } = string.Empty;

    public string ScopeKey { get; set; } = string.Empty;

    public string PayloadHash { get; set; } = string.Empty;

    public AdminCommandStatus Status { get; set; } = AdminCommandStatus.Running;

    public DateTime StartedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public string? ResultSummary { get; set; }

    public string? CorrelationId { get; set; }
}
